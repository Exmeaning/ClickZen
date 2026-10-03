using ClickZen.Core.Devices;
using ClickZen.Core.Persistence;
using ClickZen.Device.Adb;
using ClickZen.Device.Adb.Parsing;

namespace ClickZen.Device.Tests.Adb;

public sealed class AutoConnectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cz-auto-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Connects_only_autoconnect_devices_and_never_throws()
    {
        var store = new SavedDeviceStore(new AppPaths(_root));
        store.AddOrUpdate(new SavedDevice { Name = "a", Host = "10.0.0.1", Port = 5555, AutoConnect = true });
        store.AddOrUpdate(new SavedDevice { Name = "b", Host = "10.0.0.2", Port = 5555, AutoConnect = false });
        store.AddOrUpdate(new SavedDevice { Name = "c", Host = "10.0.0.3", Port = 5555, AutoConnect = true });

        var (client, fake) = FakeAdbClient.Create();
        fake.Connect = (a, _) => a == "10.0.0.1:5555"
            ? Task.FromResult($"connected to {a}")
            : throw new InvalidOperationException("boom");
        var adb = new AdbService(client, null, null, null);

        var results = await new AutoConnector(adb, store, () => true, null).ConnectSavedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.True(results.Single(r => r.Address == "10.0.0.1:5555").Success);
        Assert.False(results.Single(r => r.Address == "10.0.0.3:5555").Success);
        Assert.DoesNotContain("10.0.0.2:5555", fake.ConnectLog);
    }

    [Fact]
    public async Task Disabled_by_setting()
    {
        var store = new SavedDeviceStore(new AppPaths(_root));
        store.AddOrUpdate(new SavedDevice { Host = "10.0.0.1", AutoConnect = true });
        var (client, fake) = FakeAdbClient.Create();
        var adb = new AdbService(client, null, null, null);

        var results = await new AutoConnector(adb, store, () => false, null).ConnectSavedAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
        Assert.Empty(fake.ConnectLog);
    }

    [Fact]
    public async Task Failure_kind_is_reported()
    {
        var store = new SavedDeviceStore(new AppPaths(_root));
        store.AddOrUpdate(new SavedDevice { Host = "10.0.0.1", AutoConnect = true });
        var (client, _) = FakeAdbClient.Create();
        var adb = new AdbService(client, null, null, null);

        var r = Assert.Single(await new AutoConnector(adb, store, () => true, null).ConnectSavedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ConnectResultKind.Refused, r.Kind);
    }
}
