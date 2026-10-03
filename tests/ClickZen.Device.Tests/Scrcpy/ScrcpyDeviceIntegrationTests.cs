using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Core.Persistence;
using ClickZen.Core.Settings;
using ClickZen.Device.Adb;
using ClickZen.Device.Decoding;
using ClickZen.Device.Scrcpy;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClickZen.Device.Tests.Scrcpy;

/// <summary>
/// End-to-end against a real device or emulator. Skipped by default in CI (Category=Device).
/// Target device: env CLICKZEN_TEST_SERIAL, else the first online device.
/// Requires a built App (for ThirdParty/adb.exe, scrcpy-server and FFmpeg).
/// </summary>
[Trait("Category", "Device")]
public sealed class ScrcpyDeviceIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string? ThirdParty()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ClickZen.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            return null;
        }

        var app = Path.Combine(dir.FullName, "src", "ClickZen.App", "bin", "Release", "net10.0-windows10.0.19041.0", "win-x64");
        return File.Exists(Path.Combine(app, "ThirdParty", "scrcpy-server")) ? app : null;
    }

    private static async Task<(AdbService Adb, BundledTools Tools, string Serial)?> SetupAsync()
    {
        var appDir = ThirdParty();
        if (appDir is null)
        {
            return null;
        }

        var tools = new BundledTools(appDir);
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "cz-it-" + Guid.NewGuid().ToString("N")));
        var settings = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        var host = new AdbServerHost(settings, tools);
        await host.EnsureStartedAsync(Ct);
        var adb = new AdbService(host);
        var devices = await adb.GetDevicesAsync(Ct);
        var wanted = Environment.GetEnvironmentVariable("CLICKZEN_TEST_SERIAL");
        var device = devices.FirstOrDefault(d => d.State == Core.Devices.DeviceAdbState.Online && (wanted is null || d.Serial == wanted));
        return device is null ? null : (adb, tools, device.Serial);
    }

    [Fact]
    public async Task Mirrors_decodes_and_injects_on_a_real_device()
    {
        var setup = await SetupAsync();
        Assert.SkipWhen(setup is null, "No built app or no online device.");
        var (adb, tools, serial) = setup!.Value;
        FfmpegVideoDecoder.Initialize(tools.FfmpegDirectory);

        var info = (await adb.GetDevicesAsync(Ct)).First(d => d.Serial == serial);
        Assert.False(info.PhysicalSize.IsEmpty);

        await adb.ShellAsync(serial, "input keyevent 224; wm dismiss-keyguard; input keyevent 3", Ct); // wake, unlock, home
        await using var session = new ScrcpySession(serial, new AdbScrcpyTransport(adb, tools), new ScrcpySessionOptions { MaxSize = 1024, AutoReconnect = false })
        {
            DeviceSizeOverride = info.PhysicalSize,
        };

        await session.StartAsync(Ct);
        Assert.True(session.State == SessionState.Streaming, $"State {session.State}: {session.LastError}");
        Assert.False(string.IsNullOrEmpty(session.DeviceName));

        var first = await session.WaitForFrameAsync(-1, TimeSpan.FromSeconds(10), Ct);
        Assert.NotNull(first);
        // max_size=1024 keeps the aspect ratio of the device (portrait 1080x2400 → ~460x1024).
        Assert.Equal(1024, Math.Max(first.Width, first.Height));
        var devAspect = (double)info.PhysicalSize.Width / info.PhysicalSize.Height;
        var frameAspect = (double)Math.Min(first.Width, first.Height) / Math.Max(first.Width, first.Height);
        Assert.InRange(frameAspect, Math.Min(devAspect, 1 / devAspect) - 0.02, Math.Min(devAspect, 1 / devAspect) + 0.02);
        Assert.True(first.Bgra.Any(b => b != 0), "Frame is all black.");

        // Open the notification shade with a real swipe through the control channel and check the system saw it.
        var size = session.DeviceSize;
        var before = await adb.ShellAsync(serial, "dumpsys window | grep -E 'mCurrentFocus' | head -1", Ct);
        await session.Injector.StrokeAsync(Trajectory.Line(new PointD(size.Width / 2.0, 5), new PointD(size.Width / 2.0, size.Height * 0.6), 300, 30), Ct);
        await Task.Delay(1200, Ct);
        var shade = await adb.ShellAsync(serial, "dumpsys window | grep -E 'mCurrentFocus|NotificationShade' | head -3", Ct);
        await session.Injector.KeyAsync(KeyCodes.Back, Ct);
        await Task.Delay(500, Ct);

        // The shade (or quick settings) must have taken focus, proving touch injection works.
        Assert.Contains("NotificationShade", shade, StringComparison.OrdinalIgnoreCase);

        // Frames keep flowing.
        var later = await session.WaitForFrameAsync(first.Sequence, TimeSpan.FromSeconds(5), Ct);
        Assert.NotNull(later);
        Assert.True(later.Sequence > first.Sequence);
        _ = before;
    }

    [Fact]
    public async Task Server_process_is_gone_after_dispose()
    {
        var setup = await SetupAsync();
        Assert.SkipWhen(setup is null, "No built app or no online device.");
        var (adb, tools, serial) = setup!.Value;
        FfmpegVideoDecoder.Initialize(tools.FfmpegDirectory);

        var session = new ScrcpySession(serial, new AdbScrcpyTransport(adb, tools), new ScrcpySessionOptions { Video = false, AutoReconnect = false });
        await session.StartAsync(Ct);
        Assert.Equal(SessionState.Connected, session.State);
        var running = await adb.ShellAsync(serial, "ps -A -o ARGS | grep -c 'com.genymobile.scrcpy.Server' || true", Ct);
        Assert.True(int.Parse(running.Trim().Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture) >= 1);

        await session.DisposeAsync();
        string remaining = "";
        for (var i = 0; i < 20; i++)
        {
            remaining = await adb.ShellAsync(serial, "ps -A -o ARGS | grep 'com.genymobile.scrcpy.Server' | grep -v grep || true", Ct);
            if (string.IsNullOrWhiteSpace(remaining))
            {
                break;
            }

            await Task.Delay(250, Ct);
        }

        Assert.True(string.IsNullOrWhiteSpace(remaining), $"Server still running: {remaining}");
    }
}
