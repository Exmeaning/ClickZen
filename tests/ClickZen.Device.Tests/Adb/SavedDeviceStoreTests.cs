using ClickZen.Core.Devices;
using ClickZen.Core.Persistence;
using ClickZen.Device.Adb;

namespace ClickZen.Device.Tests.Adb;

public sealed class SavedDeviceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cz-saved-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppPaths Paths => new(_root);

    [Fact]
    public void Empty_when_file_missing()
    {
        var store = new SavedDeviceStore(Paths);
        Assert.Empty(store.GetAll());
        Assert.False(File.Exists(Paths.DevicesFile));
    }

    [Fact]
    public void Add_persists_and_reloads()
    {
        var store = new SavedDeviceStore(Paths);
        Assert.True(store.AddOrUpdate(new SavedDevice { Name = "Pixel", Host = "192.168.1.23", Port = 5555, AutoConnect = true }));
        Assert.True(File.Exists(Paths.DevicesFile));

        var reloaded = new SavedDeviceStore(Paths).GetAll();
        var d = Assert.Single(reloaded);
        Assert.Equal("Pixel", d.Name);
        Assert.Equal("192.168.1.23:5555", d.Address);
        Assert.True(d.AutoConnect);
    }

    [Fact]
    public void Same_host_and_port_updates_name_and_autoconnect()
    {
        var store = new SavedDeviceStore(Paths);
        store.AddOrUpdate(new SavedDevice { Name = "Old", Host = "Phone.LAN", Port = 5555, AutoConnect = true });
        Assert.False(store.AddOrUpdate(new SavedDevice { Name = "New", Host = "phone.lan", Port = 5555, AutoConnect = false }));

        var d = Assert.Single(store.GetAll());
        Assert.Equal("New", d.Name);
        Assert.False(d.AutoConnect);
        Assert.Equal("Phone.LAN", d.Host);
    }

    [Fact]
    public void Different_port_is_a_different_device()
    {
        var store = new SavedDeviceStore(Paths);
        store.AddOrUpdate(new SavedDevice { Host = "10.0.0.2", Port = 5555 });
        store.AddOrUpdate(new SavedDevice { Host = "10.0.0.2", Port = 40001 });
        Assert.Equal(2, store.GetAll().Count);
    }

    [Fact]
    public void Remove_by_address()
    {
        var store = new SavedDeviceStore(Paths);
        store.AddOrUpdate(new SavedDevice { Host = "10.0.0.2", Port = 5555 });
        store.AddOrUpdate(new SavedDevice { Host = "10.0.0.3", Port = 5555 });

        Assert.True(store.Remove("10.0.0.2:5555"));
        Assert.False(store.Remove("10.0.0.2:5555"));
        Assert.False(store.Remove("not an address"));
        Assert.True(store.Remove("10.0.0.3")); // default port
        Assert.Empty(new SavedDeviceStore(Paths).GetAll());
    }

    [Fact]
    public void Find_by_address()
    {
        var store = new SavedDeviceStore(Paths);
        store.AddOrUpdate(new SavedDevice { Name = "A", Host = "10.0.0.2", Port = 5555 });
        Assert.Equal("A", store.Find("10.0.0.2:5555")?.Name);
        Assert.Equal("A", store.Find("10.0.0.2")?.Name);
        Assert.Null(store.Find("10.0.0.2:5556"));
    }

    [Fact]
    public void Changed_is_raised_only_on_real_changes()
    {
        var store = new SavedDeviceStore(Paths);
        var count = 0;
        store.Changed += (_, _) => count++;

        store.AddOrUpdate(new SavedDevice { Host = "10.0.0.2", Port = 5555 });
        store.AddOrUpdate(new SavedDevice { Host = "10.0.0.2", Port = 5555, Name = "x" });
        store.Remove("10.0.0.2:5555");
        store.Remove("10.0.0.2:5555");

        Assert.Equal(3, count);
    }

    [Fact]
    public void Returned_items_are_copies()
    {
        var store = new SavedDeviceStore(Paths);
        var input = new SavedDevice { Name = "A", Host = "10.0.0.2", Port = 5555 };
        store.AddOrUpdate(input);
        input.Name = "mutated";
        store.GetAll()[0].Name = "mutated too";
        Assert.Equal("A", store.GetAll()[0].Name);
    }

    [Fact]
    public void Rejects_invalid_entries()
    {
        var store = new SavedDeviceStore(Paths);
        Assert.Throws<ArgumentException>(() => store.AddOrUpdate(new SavedDevice { Host = " " }));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.AddOrUpdate(new SavedDevice { Host = "a", Port = 0 }));
    }

    [Fact]
    public void Hand_edited_file_is_cleaned()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Paths.DevicesFile, """
            {
              "formatVersion": 1,
              "saved": [
                { "name": "A", "host": " 10.0.0.2 ", "port": 5555, "autoConnect": true },
                { "name": "dup", "host": "10.0.0.2", "port": 5555 },
                { "name": "bad", "host": "", "port": 5555 },
                { "name": "badport", "host": "10.0.0.9", "port": 0 },
                { "name": "B", "host": "10.0.0.3", "port": 5556, "autoConnect": false },
              ]
            }
            """);

        var all = new SavedDeviceStore(Paths).GetAll();
        Assert.Equal(["A", "B"], all.Select(d => d.Name));
        Assert.Equal("10.0.0.2", all[0].Host);
    }

    [Fact]
    public void Corrupt_file_yields_empty_list()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Paths.DevicesFile, "{ not json");
        Assert.Empty(new SavedDeviceStore(Paths).GetAll());
    }

    [Fact]
    public async Task Concurrent_adds_are_all_kept()
    {
        var store = new SavedDeviceStore(Paths);
        await Task.WhenAll(Enumerable.Range(1, 40).Select(i =>
            Task.Run(() => store.AddOrUpdate(new SavedDevice { Host = $"10.0.0.{i}", Port = 5555 }), TestContext.Current.CancellationToken)));

        Assert.Equal(40, store.GetAll().Count);
        Assert.Equal(40, new SavedDeviceStore(Paths).GetAll().Count);
    }
}
