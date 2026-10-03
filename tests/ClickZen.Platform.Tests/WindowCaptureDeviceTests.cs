using System.Diagnostics;
using System.Runtime.InteropServices;
using ClickZen.Core.Automation;
using ClickZen.Core.Devices;
using ClickZen.Core.Geometry;
using ClickZen.Platform.Capture;
using ClickZen.Platform.Windows;

namespace ClickZen.Platform.Tests;

/// <summary>
/// Real window capture against a freshly started Notepad (Category=Device: needs an interactive desktop, skipped in CI).
/// Skips when Notepad cannot be started or only reuses an existing window (Windows 11 tabbed Notepad may
/// open a tab in a window that already exists – that window is never touched).
/// </summary>
[Trait("Category", "Device")]
public sealed partial class WindowCaptureDeviceTests
{
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    private const uint WM_CLOSE = 0x0010;

    private static bool IsNotepad(DesktopWindowInfo w) =>
        w.Title.Contains("记事本", StringComparison.Ordinal) || w.Title.Contains("Notepad", StringComparison.OrdinalIgnoreCase);

    private sealed class NotepadWindow : IAsyncDisposable
    {
        public required DesktopWindowInfo Window { get; init; }

        public async ValueTask DisposeAsync()
        {
            if (WindowEnumerator.IsAlive(Window.Handle))
            {
                _ = PostMessage(Window.Handle, WM_CLOSE, 0, 0);
                for (var i = 0; i < 30 && WindowEnumerator.IsAlive(Window.Handle); i++)
                {
                    await Task.Delay(100);
                }
            }
        }
    }

    private static async Task<NotepadWindow?> StartNotepadAsync(CancellationToken ct)
    {
        var before = WindowEnumerator.GetVisibleWindows().Where(IsNotepad).Select(w => w.Handle).ToHashSet();
        try
        {
            using var p = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(200, ct);
            var fresh = WindowEnumerator.GetVisibleWindows().FirstOrDefault(w => IsNotepad(w) && !before.Contains(w.Handle));
            if (fresh is not null)
            {
                await Task.Delay(700, ct); // let it paint
                return new NotepadWindow { Window = fresh };
            }
        }

        return null;
    }

    /// <summary>Writes the frame as a 32-bpp BMP when CLICKZEN_CAPTURE_DUMP names a directory (manual visual check).</summary>
    private static void Dump(Frame frame, string name)
    {
        var dir = Environment.GetEnvironmentVariable("CLICKZEN_CAPTURE_DUMP");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        using var fs = File.Create(Path.Combine(dir, name));
        using var w = new BinaryWriter(fs);
        var size = frame.Width * frame.Height * 4;
        w.Write((ushort)0x4D42);
        w.Write(54 + size);
        w.Write(0);
        w.Write(54);
        w.Write(40);
        w.Write(frame.Width);
        w.Write(-frame.Height); // top-down
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(0);
        w.Write(size);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(frame.Bgra, 0, size);
    }

    private static void AssertPlausible(Frame? frame, SizeI expected)
    {
        Assert.NotNull(frame);
        Assert.Equal(expected, frame.Size);
        Assert.Equal(frame.Width * frame.Height * 4, frame.Bgra.Length);
        Assert.False(BgraBuffer.IsLikelyBlack(frame.Bgra, frame.Width, frame.Height), "Capture is all black.");
        Assert.Equal(255, frame.Bgra[3]); // opaque
    }

    [Fact]
    public async Task Wgc_backend_delivers_frames()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.SkipUnless(WgcWindowCapture.IsSupported, "WGC not supported.");
        await using var np = await StartNotepadAsync(ct);
        Assert.SkipWhen(np is null, "Could not start a new Notepad window.");

        using var wgc = new WgcWindowCapture(np!.Window.Handle);
        var got = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        wgc.FrameReady += f => got.TrySetResult(f);
        wgc.Start();
        try
        {
            var frame = await got.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.True(WindowMetrics.TryQuery(np.Window.Handle, out var m));
            AssertPlausible(frame, m.ClientSize);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"No WGC frame. received={wgc.FramesReceived} error={wgc.LastFrameError}");
        }
    }

    [Fact]
    public async Task Captures_running_android_emulator_window_if_any()
    {
        var ct = TestContext.Current.CancellationToken;
        var profile = new EmulatorProfile
        {
            Name = "AVD",
            Match = new WindowMatchRule { ProcessName = "qemu-system-x86_64", Title = "Android Emulator", TitleMode = TitleMatchMode.Contains },
        };
        var window = WindowMatcher.FindBestMatch(profile, WindowEnumerator.GetVisibleWindows());
        Assert.SkipWhen(window is null, "No Android Emulator (AVD) window is open.");
        Assert.True(WindowMetrics.TryQuery(window!.Handle, out var m));

        // Read-only: the emulator window is only captured, never resized or closed.
        foreach (var method in new[] { WindowCaptureMethod.Wgc, WindowCaptureMethod.PrintWindow })
        {
            if (method == WindowCaptureMethod.Wgc && !WgcWindowCapture.IsSupported)
            {
                continue;
            }

            var full = await WindowFrameSource.CaptureOnceAsync(window.Handle, null, method, TimeSpan.FromSeconds(5), ct);
            Assert.NotNull(full);
            Dump(full, $"avd-{method}.bmp");
            Assert.Equal(m.ClientSize, full.Size);
            if (method == WindowCaptureMethod.Wgc)
            {
                // GPU-rendered emulator surface: WGC must see real content (PrintWindow may legitimately be black).
                Assert.False(BgraBuffer.IsLikelyBlack(full.Bgra, full.Width, full.Height), "AVD capture is black.");
            }

            var crop = new RectI(m.ClientSize.Width / 4, m.ClientSize.Height / 4, m.ClientSize.Width / 2, m.ClientSize.Height / 2);
            var part = await WindowFrameSource.CaptureOnceAsync(window.Handle, crop, method, TimeSpan.FromSeconds(5), ct);
            Assert.Equal(crop.Size, part!.Size);
        }
    }

    [Fact]
    public async Task Enumerator_reports_process_name()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var np = await StartNotepadAsync(ct);
        Assert.SkipWhen(np is null, "Could not start a new Notepad window.");

        Assert.Equal(np!.Window.ProcessName, WindowEnumerator.GetProcessName(np.Window.ProcessId));
        Assert.Contains("notepad", np.Window.ProcessName, StringComparison.OrdinalIgnoreCase);

        // Notepad changes its title while starting ("记事本" → "无标题 - Notepad"): build the rule from a fresh snapshot.
        var current = WindowEnumerator.GetVisibleWindows().First(w => w.Handle == np.Window.Handle);
        var rule = WindowMatcher.SuggestRule(current);
        var windows = WindowEnumerator.GetVisibleWindows();
        var found = WindowMatcher.FindBestMatch(rule, windows, np.Window.Handle);
        Assert.True(np.Window.Handle == found?.Handle,
            $"rule={rule.ProcessName}|{rule.ClassName}|{rule.Title}; now: " +
            string.Join("; ", windows.Where(w => w.Handle == np.Window.Handle).Select(w => $"{w.ProcessName}|{w.ClassName}|{w.Title}")));
    }

    [Fact]
    public async Task Wgc_and_PrintWindow_capture_client_area_and_crop()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var np = await StartNotepadAsync(ct);
        Assert.SkipWhen(np is null, "Could not start a new Notepad window.");
        var hwnd = np!.Window.Handle;
        Assert.True(WindowMetrics.TryQuery(hwnd, out var m));
        Assert.True(m.ClientSize.Width > 100 && m.ClientSize.Height > 100, $"client {m.ClientSize}");

        // Whole client area, both backends.
        if (WgcWindowCapture.IsSupported)
        {
            var wgc = await WindowFrameSource.CaptureOnceAsync(hwnd, null, WindowCaptureMethod.Wgc, TimeSpan.FromSeconds(5), ct);
            AssertPlausible(wgc, m.ClientSize);
        }

        var pw = await WindowFrameSource.CaptureOnceAsync(hwnd, null, WindowCaptureMethod.PrintWindow, TimeSpan.FromSeconds(5), ct);
        AssertPlausible(pw, m.ClientSize);

        // Cropped.
        var crop = new RectI(20, 30, 200, 120);
        var cropped = await WindowFrameSource.CaptureOnceAsync(hwnd, crop, WindowCaptureMethod.Auto, TimeSpan.FromSeconds(5), ct);
        Assert.NotNull(cropped);
        Assert.Equal(crop.Size, cropped.Size);
    }

    [Fact]
    public async Task Wgc_and_PrintWindow_agree_on_client_content()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.SkipUnless(WgcWindowCapture.IsSupported, "WGC not supported.");
        await using var np = await StartNotepadAsync(ct);
        Assert.SkipWhen(np is null, "Could not start a new Notepad window.");
        var hwnd = np!.Window.Handle;

        var wgc = await WindowFrameSource.CaptureOnceAsync(hwnd, null, WindowCaptureMethod.Wgc, TimeSpan.FromSeconds(5), ct);
        var pw = await WindowFrameSource.CaptureOnceAsync(hwnd, null, WindowCaptureMethod.PrintWindow, TimeSpan.FromSeconds(5), ct);
        Assert.NotNull(wgc);
        Assert.NotNull(pw);
        Dump(wgc, "notepad-wgc.bmp");
        Dump(pw, "notepad-printwindow.bmp");
        Assert.Equal(wgc.Size, pw.Size);

        // If the client offsets were wrong the images would be shifted by the title-bar height; compare a grid.
        var diff = 0.0;
        var n = 0;
        for (var y = 2; y < wgc.Height - 2; y += 7)
        {
            for (var x = 2; x < wgc.Width - 2; x += 7)
            {
                var a = wgc.PixelAt(x, y);
                var b = pw.PixelAt(x, y);
                diff += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                n++;
            }
        }

        Assert.True(diff / n < 20, $"mean abs diff {diff / n:F1}");
    }

    [Theory]
    [InlineData(WindowCaptureMethod.Auto)]
    [InlineData(WindowCaptureMethod.PrintWindow)]
    public async Task Frame_source_streams_then_faults_when_window_closes(WindowCaptureMethod method)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var np = await StartNotepadAsync(ct);
        Assert.SkipWhen(np is null, "Could not start a new Notepad window.");
        var hwnd = np!.Window.Handle;

        await using var source = new WindowFrameSource(hwnd, new WindowFrameSourceOptions { Method = method, CropRect = new RectI(0, 0, 160, 90) });
        var arrived = 0;
        source.FrameArrived += (_, _) => Interlocked.Increment(ref arrived);
        await source.StartAsync(ct);
        Assert.Equal(WindowCaptureState.Capturing, source.State);
        if (method == WindowCaptureMethod.PrintWindow)
        {
            Assert.Equal(WindowCaptureMethod.PrintWindow, source.ActiveMethod);
        }

        var first = await source.WaitForFrameAsync(0, TimeSpan.FromSeconds(5), ct);
        Assert.NotNull(first);
        Assert.Equal(new SizeI(160, 90), first.Size);
        Assert.True(first.Sequence >= 1);
        Assert.True(arrived >= 1);

        if (source.ActiveMethod == WindowCaptureMethod.PrintWindow)
        {
            var next = await source.WaitForFrameAsync(first.Sequence, TimeSpan.FromSeconds(2), ct);
            Assert.True(next!.Sequence > first.Sequence);
        }

        // Crop can change on the fly.
        source.CropRect = new RectI(10, 10, 50, 40);
        if (source.ActiveMethod == WindowCaptureMethod.PrintWindow)
        {
            var resized = await source.WaitForFrameAsync(source.Latest!.Sequence, TimeSpan.FromSeconds(2), ct);
            Assert.Equal(new SizeI(50, 40), resized!.Size);
        }

        _ = PostMessage(hwnd, WM_CLOSE, 0, 0);
        for (var i = 0; i < 50 && source.State != WindowCaptureState.Faulted; i++)
        {
            await Task.Delay(100, ct);
        }

        Assert.Equal(WindowCaptureState.Faulted, source.State);
        Assert.NotNull(source.LastError);
        Assert.NotNull(source.Latest); // last frame is kept
    }
}
