using System.Diagnostics;
using ClickZen.Core.Persistence;
using ClickZen.Core.Recording;
using ClickZen.Core.Settings;
using ClickZen.Device.Adb;
using ClickZen.Device.Input;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClickZen.Device.Tests.Input;

/// <summary>
/// getevent recording against an Android emulator (Category=Device, skipped in CI): real kernel touch
/// events are injected through the emulator console (<c>adb emu event send</c>) and must come back as gestures.
/// </summary>
[Trait("Category", "Device")]
public sealed class GeteventRecorderDeviceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Records_swipe_and_tap_injected_into_the_emulator_kernel()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ClickZen.slnx")))
        {
            dir = dir.Parent;
        }

        var app = dir is null ? null : Path.Combine(dir.FullName, "src", "ClickZen.App", "bin", "Release", "net10.0-windows10.0.19041.0", "win-x64");
        var adbExe = app is null ? null : Path.Combine(app, "ThirdParty", "adb.exe");
        Assert.SkipWhen(adbExe is null || !File.Exists(adbExe), "Build the app first.");

        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "cz-it-" + Guid.NewGuid().ToString("N")));
        var host = new AdbServerHost(new SettingsService(paths, NullLogger<SettingsService>.Instance), new BundledTools(app!));
        await host.EnsureStartedAsync(Ct);
        var adb = new AdbService(host);
        var device = (await adb.GetDevicesAsync(Ct)).FirstOrDefault(d => d.State == Core.Devices.DeviceAdbState.Online && d.Serial.StartsWith("emulator-", StringComparison.Ordinal));
        Assert.SkipWhen(device is null, "No emulator online.");

        var recorder = new GeteventRecorder(adb);
        var session = await recorder.PrepareAsync(device!.Serial, ct: Ct);
        Assert.SkipWhen(session.Rotation != Core.Geometry.DisplayRotation.Rotation0, "Emulator must be in portrait.");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var gestures = new List<Gesture>();
        var recording = recorder.RecordSessionAsync(device.Serial, session, onGesture: g => { lock (gestures) { gestures.Add(g); } }, stopToken: stop.Token);
        await Task.Delay(1500, Ct);

        var x = session.Touchscreen.X;
        var y = session.Touchscreen.Y;
        int Ax(double fx) => (int)(x.Min + fx * (x.Max - x.Min));
        int Ay(double fy) => (int)(y.Min + fy * (y.Max - y.Min));

        // Swipe: 25% -> 75% of the width at mid height.
        await Emu(adbExe!, device.Serial, $"EV_ABS:ABS_MT_SLOT:0 EV_ABS:ABS_MT_TRACKING_ID:41 EV_ABS:ABS_MT_POSITION_X:{Ax(0.25)} EV_ABS:ABS_MT_POSITION_Y:{Ay(0.5)} EV_KEY:BTN_TOUCH:1 EV_SYN:0:0");
        for (var i = 1; i <= 5; i++)
        {
            await Emu(adbExe!, device.Serial, $"EV_ABS:ABS_MT_POSITION_X:{Ax(0.25 + i * 0.1)} EV_SYN:0:0");
        }

        await Emu(adbExe!, device.Serial, "EV_ABS:ABS_MT_TRACKING_ID:-1 EV_KEY:BTN_TOUCH:0 EV_SYN:0:0");
        await Task.Delay(400, Ct);

        // Tap at 50% / 25%.
        await Emu(adbExe!, device.Serial, $"EV_ABS:ABS_MT_SLOT:0 EV_ABS:ABS_MT_TRACKING_ID:42 EV_ABS:ABS_MT_POSITION_X:{Ax(0.5)} EV_ABS:ABS_MT_POSITION_Y:{Ay(0.25)} EV_KEY:BTN_TOUCH:1 EV_SYN:0:0");
        await Emu(adbExe!, device.Serial, "EV_ABS:ABS_MT_TRACKING_ID:-1 EV_KEY:BTN_TOUCH:0 EV_SYN:0:0");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (gestures)
            {
                if (gestures.Count >= 2)
                {
                    break;
                }
            }

            await Task.Delay(100, Ct);
        }

        await stop.CancelAsync();
        var doc = await recording;

        Assert.Equal(2, doc.Gestures.Count);
        var w = session.ScreenSize.Width;
        var h = session.ScreenSize.Height;
        var swipe = doc.Gestures[0];
        Assert.Equal(GestureKind.Swipe, swipe.Kind);
        Assert.InRange(swipe.Fingers[0].Start.X, w * 0.23, w * 0.27);
        Assert.InRange(swipe.Fingers[0].End.X, w * 0.73, w * 0.77);
        Assert.InRange(swipe.Fingers[0].Start.Y, h * 0.48, h * 0.52);

        var tap = doc.Gestures[1];
        Assert.Contains(tap.Kind, new[] { GestureKind.Tap, GestureKind.LongPress });
        Assert.InRange(tap.Fingers[0].Start.X, w * 0.48, w * 0.52);
        Assert.InRange(tap.Fingers[0].Start.Y, h * 0.23, h * 0.27);
        Assert.True(tap.StartMs > swipe.StartMs);
        Assert.Equal(session.ScreenSize, doc.ScreenSize);
    }

    private static async Task Emu(string adb, string serial, string events)
    {
        var psi = new ProcessStartInfo(adb) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-s", serial, "emu", "event", "send" }.Concat(events.Split(' ')))
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync(Ct);
        await p.WaitForExitAsync(Ct);
        Assert.DoesNotContain("KO", output, StringComparison.Ordinal);
        await Task.Delay(40, Ct);
    }
}
