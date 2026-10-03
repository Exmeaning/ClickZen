using ClickZen.Device.Adb;

namespace ClickZen.Device.Tests.Adb;

/// <summary>
/// Talks to the real adb.exe / adb server on this machine. Skipped in CI (<c>--filter-not-trait Category=Device</c>);
/// needs no phone, only an adb executable.
/// </summary>
[Trait("Category", "Device")]
public sealed class AdbIntegrationTests
{
    private static string? FindAdb()
    {
        var bundled = new BundledTools().AdbPath;
        if (File.Exists(bundled))
        {
            return bundled;
        }

        return File.Exists(@"C:\adb\adb.exe") ? @"C:\adb\adb.exe" : null;
    }

    [Fact]
    public async Task Ensure_started_then_list_devices()
    {
        var adb = FindAdb();
        Assert.SkipWhen(adb is null, "No adb.exe available");
        var ct = TestContext.Current.CancellationToken;

        var host = new AdbServerHost(() => adb, new BundledTools(), null, AdbServerHost.DefaultEndPoint);
        var status = await host.EnsureStartedAsync(ct);
        Assert.True(status.ServerVersion >= 20);
        Assert.NotNull(status.ExecutableVersion);

        // Second call must reuse the server we just ensured.
        var again = await host.EnsureStartedAsync(ct);
        Assert.Equal(AdbServerStartMode.ReusedExisting, again.Mode);

        using var service = new AdbService(host);
        var devices = await service.GetDevicesAsync(ct);
        Assert.NotNull(devices);

        var bad = await service.ConnectAsync("127.0.0.1:1", ct);
        Assert.False(bad.Success);

        await using var watcher = new DeviceWatcher(service, host);
        await watcher.StartAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.True(watcher.IsTracking);
    }
}
