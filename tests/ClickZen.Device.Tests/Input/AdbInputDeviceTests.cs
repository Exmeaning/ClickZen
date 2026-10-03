using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Core.Persistence;
using ClickZen.Core.Settings;
using ClickZen.Device.Adb;
using ClickZen.Device.Input;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClickZen.Device.Tests.Input;

/// <summary>adb input fallback against a real device (Category=Device, skipped in CI).</summary>
[Trait("Category", "Device")]
public sealed class AdbInputDeviceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Motionevent_stroke_opens_the_notification_shade()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ClickZen.slnx")))
        {
            dir = dir.Parent;
        }

        var app = dir is null ? null : Path.Combine(dir.FullName, "src", "ClickZen.App", "bin", "Release", "net10.0-windows10.0.19041.0", "win-x64");
        Assert.SkipWhen(app is null || !File.Exists(Path.Combine(app, "ThirdParty", "adb.exe")), "Build the app first.");

        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "cz-it-" + Guid.NewGuid().ToString("N")));
        var host = new AdbServerHost(new SettingsService(paths, NullLogger<SettingsService>.Instance), new BundledTools(app));
        await host.EnsureStartedAsync(Ct);
        var adb = new AdbService(host);
        var wanted = Environment.GetEnvironmentVariable("CLICKZEN_TEST_SERIAL");
        var device = (await adb.GetDevicesAsync(Ct)).FirstOrDefault(d => d.State == Core.Devices.DeviceAdbState.Online && (wanted is null || d.Serial == wanted));
        Assert.SkipWhen(device is null, "No online device.");
        Assert.SkipWhen(device!.SdkLevel < AdbInputInjector.MotionEventMinSdk, "Needs Android 11+.");

        var inj = AdbInputInjector.For(adb, device.Serial, device.SdkLevel);
        await inj.KeyAsync(KeyCodes.Home, Ct);
        await Task.Delay(800, Ct);

        var w = device.PhysicalSize.Width;
        var h = device.PhysicalSize.Height;
        await inj.StrokeAsync(Trajectory.Line(new PointD(w / 2.0, 5), new PointD(w / 2.0, h * 0.6), 300, 20), Ct);
        await Task.Delay(1200, Ct);
        var focus = await adb.ShellAsync(device.Serial, "dumpsys window | grep -E 'mCurrentFocus|NotificationShade' | head -3", Ct);
        await inj.KeyAsync(KeyCodes.Back, Ct);

        Assert.Contains("NotificationShade", focus, StringComparison.OrdinalIgnoreCase);
    }
}
