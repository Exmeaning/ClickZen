using ClickZen.Core.Devices;
using ClickZen.Core.Geometry;
using ClickZen.Core.Persistence;

namespace ClickZen.Platform.Tests;

public sealed class EmulatorProfileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ClickZenTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static EmulatorProfile Sample() => new()
    {
        Name = "MuMu 12",
        Match = new WindowMatchRule { ProcessName = "MuMuPlayer", ClassName = "Qt5156QWindowIcon", Title = "MuMu模拟器12" },
        CropRect = new RectI(0, 48, 1280, 720),
        ClientSize = new SizeI(1280, 768),
        ReferenceSize = new SizeI(1920, 1080),
        AdbSerial = "127.0.0.1:16384",
        CaptureMethod = WindowCaptureMethod.Wgc,
    };

    [Fact]
    public void Uses_app_paths_file()
    {
        var paths = new AppPaths(_dir);
        var store = new EmulatorProfileStore(paths);
        Assert.Equal(Path.Combine(_dir, "emulator-profiles.json"), store.FilePath);
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Round_trip_through_file()
    {
        var path = Path.Combine(_dir, "emulator-profiles.json");
        var store = new EmulatorProfileStore(path);
        var p = Sample();
        var avd = new EmulatorProfile
        {
            Name = "AVD",
            Match = new WindowMatchRule { Title = @"Android Emulator - .*", TitleMode = TitleMatchMode.Regex },
        };
        store.Upsert(p);
        store.Upsert(avd);

        var json = File.ReadAllText(path);
        Assert.Contains("\"formatVersion\": 1", json);
        Assert.Contains("\"cropRect\": [", json);
        Assert.Contains("\"captureMethod\": \"wgc\"", json);
        Assert.Contains("\"titleMode\": \"regex\"", json);

        var reread = new EmulatorProfileStore(path).GetAll();
        Assert.Equal(2, reread.Count);
        var q = reread[0];
        Assert.Equal(p.Id, q.Id);
        Assert.Equal("MuMu 12", q.Name);
        Assert.Equal("MuMuPlayer", q.Match.ProcessName);
        Assert.Equal("Qt5156QWindowIcon", q.Match.ClassName);
        Assert.Equal("MuMu模拟器12", q.Match.Title);
        Assert.Equal(TitleMatchMode.Contains, q.Match.TitleMode);
        Assert.Equal(new RectI(0, 48, 1280, 720), q.CropRect);
        Assert.Equal(new SizeI(1280, 768), q.ClientSize);
        Assert.Equal(new SizeI(1920, 1080), q.ReferenceSize);
        Assert.Equal("127.0.0.1:16384", q.AdbSerial);
        Assert.Equal(WindowCaptureMethod.Wgc, q.CaptureMethod);

        var a = reread[1];
        Assert.Null(a.CropRect);
        Assert.Null(a.AdbSerial);
        Assert.Equal(TitleMatchMode.Regex, a.Match.TitleMode);
        Assert.Equal(WindowCaptureMethod.Auto, a.CaptureMethod);
    }

    [Fact]
    public void Upsert_replaces_and_remove_deletes()
    {
        var store = new EmulatorProfileStore(Path.Combine(_dir, "p.json"));
        var p = Sample();
        store.Upsert(p);
        p.Name = "Renamed";
        store.Upsert(p);
        Assert.Single(store.GetAll());
        Assert.Equal("Renamed", store.Get(p.Id)!.Name);

        // Returned objects are copies.
        store.Get(p.Id)!.Match.Title = "mutated";
        Assert.Equal("MuMu模拟器12", store.Get(p.Id)!.Match.Title);

        Assert.True(store.Remove(p.Id));
        Assert.False(store.Remove(p.Id));
        Assert.Empty(new EmulatorProfileStore(Path.Combine(_dir, "p.json")).GetAll());
    }

    [Fact]
    public void Corrupt_file_is_moved_aside()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, "{ not json");
        var store = new EmulatorProfileStore(path);
        Assert.Empty(store.GetAll());
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_dir, "bad.json.corrupt-*"));

        store.Upsert(Sample());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Newer_format_is_read_only()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "new.json");
        File.WriteAllText(path, """{ "formatVersion": 2, "profiles": [ { "id": "x", "name": "Future", "match": { "title": "a" } } ] }""");
        var store = new EmulatorProfileStore(path);
        Assert.Equal("Future", Assert.Single(store.GetAll()).Name);
        Assert.Throws<InvalidOperationException>(() => store.Upsert(Sample()));
    }

    [Fact]
    public void Frame_mapping_uses_reference_size()
    {
        var p = Sample();
        var map = p.CreateFrameMapping(new SizeI(1280, 720));
        Assert.Equal(new PointD(1920, 1080), map.FrameToDevicePoint(new PointD(1280, 720)));
        Assert.Equal(new PointD(960, 540), map.FrameToDevicePoint(new PointD(640, 360)));

        p.ReferenceSize = default;
        var identity = p.CreateFrameMapping(new SizeI(1280, 720));
        Assert.Equal(new PointD(10, 20), identity.FrameToDevicePoint(new PointD(10, 20)));

        Assert.True(p.ClientSizeChanged(new SizeI(1000, 768)));
        Assert.False(p.ClientSizeChanged(new SizeI(1280, 768)));
    }

    [Fact]
    public void Serialize_static_helpers_round_trip()
    {
        var doc = new EmulatorProfilesDocument { Profiles = [Sample()] };
        var back = EmulatorProfileStore.Deserialize(EmulatorProfileStore.Serialize(doc));
        Assert.Equal(1, back.FormatVersion);
        Assert.Equal(doc.Profiles[0].CropRect, back.Profiles[0].CropRect);
    }
}
