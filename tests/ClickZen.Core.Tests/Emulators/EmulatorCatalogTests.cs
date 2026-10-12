using ClickZen.Core.Automation;
using ClickZen.Core.Devices;
using ClickZen.Core.Emulators;
using ClickZen.Core.Geometry;

namespace ClickZen.Core.Tests.Emulators;

public sealed class EmulatorCatalogTests
{
    [Theory]
    [InlineData(EmulatorKind.MuMu, 0, 16384)]
    [InlineData(EmulatorKind.MuMu, 1, 16416)]
    [InlineData(EmulatorKind.MuMu, 3, 16480)]
    [InlineData(EmulatorKind.LDPlayer, 0, 5555)]
    [InlineData(EmulatorKind.LDPlayer, 2, 5559)]
    [InlineData(EmulatorKind.Nox, 0, 62001)]
    [InlineData(EmulatorKind.Nox, 1, 62025)]
    [InlineData(EmulatorKind.Nox, 2, 62026)]
    [InlineData(EmulatorKind.MEmu, 0, 21503)]
    [InlineData(EmulatorKind.MEmu, 1, 21513)]
    [InlineData(EmulatorKind.BlueStacks, 0, 5555)]
    [InlineData(EmulatorKind.AndroidStudio, 1, 5557)]
    public void Port_formulas(EmulatorKind kind, int index, int port)
    {
        Assert.Equal(port, EmulatorCatalog.Get(kind).PortForIndex(index));
    }

    [Fact]
    public void Negative_index_has_no_port()
    {
        Assert.All(EmulatorCatalog.All, d => Assert.Null(d.PortForIndex(-1)));
    }

    [Theory]
    [InlineData("MuMuNxDevice", EmulatorKind.MuMu)]
    [InlineData("mumuplayer", EmulatorKind.MuMu)]
    [InlineData("MuMuVMMHeadless", EmulatorKind.MuMu)]
    [InlineData("dnplayer", EmulatorKind.LDPlayer)]
    [InlineData("Ld9BoxHeadless", EmulatorKind.LDPlayer)]
    [InlineData("Nox", EmulatorKind.Nox)]
    [InlineData("NoxVMHandle", EmulatorKind.Nox)]
    [InlineData("MEmu", EmulatorKind.MEmu)]
    [InlineData("MEmuHeadless", EmulatorKind.MEmu)]
    [InlineData("HD-Player", EmulatorKind.BlueStacks)]
    [InlineData("qemu-system-x86_64", EmulatorKind.AndroidStudio)]
    [InlineData("qemu-system-aarch64-headless", EmulatorKind.AndroidStudio)]
    public void Process_names(string process, EmulatorKind kind)
    {
        Assert.Equal(kind, EmulatorCatalog.ForProcess(process)?.Kind);
        Assert.True(EmulatorCatalog.IsEmulatorProcess(process));
    }

    [Theory]
    [InlineData("MuMuNxMain")] // the multi-instance manager owns no instance window
    [InlineData("MEmuConsole")]
    [InlineData("explorer")]
    [InlineData("")]
    public void Other_processes_are_not_emulators(string process)
    {
        Assert.Null(EmulatorCatalog.ForProcess(process));
    }

    [Fact]
    public void Ids_are_unique_and_every_kind_is_covered()
    {
        Assert.Equal(EmulatorCatalog.All.Count, EmulatorCatalog.All.Select(d => d.Id).Distinct().Count());
        Assert.Equal(Enum.GetValues<EmulatorKind>().Order(), EmulatorCatalog.All.Select(d => d.Kind).Order());
        Assert.All(EmulatorCatalog.All, d => Assert.NotEmpty(d.ScanPorts));
    }

    [Theory]
    [InlineData(EmulatorKind.MuMu, "MuMuPlayer-12.0-1", "mumu:MuMuPlayer-12.0-1")]
    [InlineData(EmulatorKind.AndroidStudio, "Pixel_8", "androidstudio:Pixel_8")]
    public void Keys_round_trip(EmulatorKind kind, string instance, string key)
    {
        Assert.Equal(key, EmulatorCatalog.KeyOf(kind, instance));
        Assert.True(EmulatorCatalog.TryParseKey(key, out var k, out var i));
        Assert.Equal((kind, instance), (k, i));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mumu")]
    [InlineData("mumu:")]
    [InlineData(":x")]
    [InlineData("toaster:x")]
    [InlineData("42:x")]
    public void Bad_keys(string? key)
    {
        Assert.False(EmulatorCatalog.TryParseKey(key, out _, out _));
    }
}

public sealed class EmulatorSerialTests
{
    [Theory]
    [InlineData("127.0.0.1:16384", 16384)]
    [InlineData("localhost:5555", 5555)]
    [InlineData("[::1]:62001", 62001)]
    [InlineData("emulator-5554", 5555)]
    [InlineData("EMULATOR-5560", 5561)]
    [InlineData("192.168.1.5:5555", null)]
    [InlineData("R5CT123", null)]
    [InlineData("emulator-x", null)]
    [InlineData("127.0.0.1:99999", null)]
    [InlineData(null, null)]
    public void Local_adb_port(string? serial, int? port)
    {
        Assert.Equal(port, EmulatorSerials.LocalAdbPort(serial));
    }

    [Fact]
    public void Instance_owns_its_serials()
    {
        var mumu = new EmulatorInstance { Kind = EmulatorKind.MuMu, InstanceKey = "MuMuPlayer-12.0-0", AdbPort = 16384, FallbackPorts = [16385, 16386] };
        Assert.True(mumu.OwnsSerial("127.0.0.1:16384"));
        Assert.True(mumu.OwnsSerial("127.0.0.1:16385"));
        Assert.False(mumu.OwnsSerial("127.0.0.1:16416"));
        Assert.Equal("127.0.0.1:16384", mumu.AdbAddress);
        Assert.Equal("mumu:MuMuPlayer-12.0-0", mumu.Key);
        Assert.Equal("MuMuPlayer-12.0-0", mumu.Name);

        var avd = new EmulatorInstance { Kind = EmulatorKind.AndroidStudio, InstanceKey = "Pixel", DisplayName = "Pixel 8", AdbPort = 5555 };
        Assert.True(avd.OwnsSerial("emulator-5554"));
        Assert.True(avd.OwnsSerial("127.0.0.1:5555"));
        Assert.Equal("Pixel 8", avd.Name);
        Assert.Equal("emulator-5554", EmulatorSerials.ConsoleSerial(5555));

        var unknown = mumu with { AdbPort = null, FallbackPorts = [] };
        Assert.False(unknown.OwnsSerial("127.0.0.1:16384"));
        Assert.Null(unknown.AdbAddress);
    }
}

public sealed class EmulatorProfileFactoryTests
{
    private static EmulatorInstance MuMu() => new()
    {
        Kind = EmulatorKind.MuMu,
        InstanceKey = "MuMuPlayerGlobal-12.0-0",
        DisplayName = "Android Device",
        ProcessName = "MuMuNxDevice",
        AdbPort = 16384,
        WindowHandle = 42,
        WindowTitle = "Android Device",
        WindowClass = "Qt5156QWindowIcon",
    };

    [Fact]
    public void Creates_a_profile_for_an_instance()
    {
        var p = EmulatorProfileFactory.Create(MuMu(), "", new RectI(0, 40, 1280, 720), new SizeI(1280, 760), new SizeI(1920, 1080), "127.0.0.1:16384");
        Assert.Equal("Android Device", p.Name);
        Assert.Equal("MuMuNxDevice", p.Match.ProcessName);
        Assert.Equal("Qt5156QWindowIcon", p.Match.ClassName);
        Assert.Equal("Android Device", p.Match.Title);
        Assert.Equal(new RectI(0, 40, 1280, 720), p.CropRect);
        Assert.Equal(new SizeI(1280, 760), p.ClientSize);
        Assert.Equal(new SizeI(1920, 1080), p.ReferenceSize);
        Assert.Equal("127.0.0.1:16384", p.AdbSerial);
        Assert.Equal("mumu:MuMuPlayerGlobal-12.0-0", p.EmulatorKey);
        Assert.Null(p.Match.Validate());
    }

    [Fact]
    public void Updates_the_existing_profile_of_the_same_instance()
    {
        var old = EmulatorProfileFactory.Create(MuMu(), "Old", null, default, default, null);
        old.CaptureMethod = WindowCaptureMethod.PrintWindow;
        var others = new[] { new EmulatorProfile { Name = "Hand made" }, old };

        var found = EmulatorProfileFactory.FindExisting(MuMu(), others);
        Assert.Same(old, found);

        var updated = EmulatorProfileFactory.Create(MuMu(), "New", null, default, default, "127.0.0.1:16384", found);
        Assert.Equal(old.Id, updated.Id);
        Assert.Equal(WindowCaptureMethod.PrintWindow, updated.CaptureMethod);
        Assert.Equal("New", updated.Name);
        Assert.Equal("Old", old.Name); // the original is not modified

        Assert.Null(EmulatorProfileFactory.FindExisting(MuMu() with { InstanceKey = "MuMuPlayerGlobal-12.0-1" }, others));
    }

    [Fact]
    public void Avd_rule_ignores_the_port_in_the_title()
    {
        var avd = new EmulatorInstance
        {
            Kind = EmulatorKind.AndroidStudio,
            InstanceKey = "Pico_API_30",
            ProcessName = "qemu-system-x86_64",
            WindowTitle = "Android Emulator - Pico_API_30:5554",
            WindowClass = "Qt672QWindowIcon",
        };
        var rule = EmulatorProfileFactory.MatchRule(avd);
        Assert.Equal("Pico_API_30", rule.Title);
        Assert.Equal(TitleMatchMode.Contains, rule.TitleMode);
    }

    [Theory]
    [InlineData(1080, 1920, 1280, 720, 1920, 1080)]
    [InlineData(1080, 1920, 720, 1280, 1080, 1920)]
    [InlineData(1080, 1920, 0, 0, 1080, 1920)]
    [InlineData(1080, 1080, 1280, 720, 1080, 1080)]
    public void Orient_like(int w, int h, int pw, int ph, int ew, int eh)
    {
        Assert.Equal(new SizeI(ew, eh), ScreenOrientation.OrientLike(new SizeI(w, h), new SizeI(pw, ph)));
    }

    [Fact]
    public void Aspect_difference()
    {
        Assert.Equal(0, ScreenOrientation.AspectDifference(new SizeI(1280, 720), new SizeI(1920, 1080)), 6);
        Assert.True(ScreenOrientation.AspectDifference(new SizeI(1280, 760), new SizeI(1920, 1080)) > 0.05);
        Assert.Equal(double.PositiveInfinity, ScreenOrientation.AspectDifference(default, new SizeI(1, 1)));
    }
}

public sealed class ScreenAreaLocatorTests
{
    private static readonly SizeI Device = new(1920, 1080);

    [Fact]
    public void Client_area_with_the_device_aspect_needs_no_crop()
    {
        var a = ScreenAreaLocator.FromAspect(new SizeI(1280, 720), Device);
        Assert.NotNull(a);
        Assert.Null(a.Crop);
        Assert.Equal(ScreenAreaMethod.AspectMatch, a.Method);
        Assert.Null(ScreenAreaLocator.FromAspect(new SizeI(1280, 770), Device));
        Assert.NotNull(ScreenAreaLocator.FromAspect(new SizeI(720, 1280), Device)); // portrait window, landscape "wm size"
    }

    [Fact]
    public void Render_window_inside_the_client_area()
    {
        var client = new SizeI(1330, 760);
        var a = ScreenAreaLocator.FromRenderWindow(new RectI(0, 40, 1280, 720), client, Device);
        Assert.Equal(new RectI(0, 40, 1280, 720), a!.Crop);
        Assert.Equal(ScreenAreaMethod.RenderWindow, a.Method);
        Assert.True(a.Confidence >= ScreenAreaLocator.ConfidentEnough);

        // Tiny child (a button), wrong aspect, outside: rejected.
        Assert.Null(ScreenAreaLocator.FromRenderWindow(new RectI(0, 0, 40, 40), client, Device));
        Assert.Null(ScreenAreaLocator.FromRenderWindow(new RectI(0, 0, 1000, 760), client, Device));
        Assert.Null(ScreenAreaLocator.FromRenderWindow(new RectI(2000, 0, 1280, 720), client, Device));

        // Whole client area = no crop.
        Assert.Null(ScreenAreaLocator.FromRenderWindow(new RectI(0, 0, 1280, 720), new SizeI(1280, 720), Device)!.Crop);
    }

    [Fact]
    public void Screenshot_match_needs_a_good_score()
    {
        var client = new SizeI(1330, 760);
        Assert.Equal(ScreenAreaMethod.ScreenshotMatch, ScreenAreaLocator.FromScreenshotMatch(new RectI(0, 40, 1280, 720), 0.93, client)!.Method);
        Assert.Null(ScreenAreaLocator.FromScreenshotMatch(new RectI(0, 40, 1280, 720), 0.5, client));
        Assert.Null(ScreenAreaLocator.FromScreenshotMatch(new RectI(0, 40, 100, 100), 0.99, client));
    }

    [Fact]
    public void Edges_trim_a_flat_title_bar_and_side_bar()
    {
        // 1330×760 window: 40 px grey title bar on top, 50 px dark tool bar on the right, noisy 1280×720 "screen".
        var frame = MakeFrame(1330, 760, (x, y) =>
            y < 40 ? ((byte)200, (byte)200, (byte)200)
            : x >= 1280 ? ((byte)30, (byte)30, (byte)30)
            : ((byte)((x * 7 + y * 3) % 251), (byte)((x * 13) % 241), (byte)((y * 17) % 239)));
        var a = ScreenAreaLocator.FromEdges(frame, Device);
        Assert.NotNull(a);
        Assert.Equal(new RectI(0, 40, 1280, 720), a.Crop);
        Assert.Equal(ScreenAreaMethod.EdgeDetection, a.Method);
    }

    [Fact]
    public void Edges_reject_a_result_with_the_wrong_aspect()
    {
        var noisy = MakeFrame(1000, 1000, (x, y) => ((byte)((x * 7 + y * 3) % 251), (byte)((x * 13) % 241), (byte)((y * 17) % 239)));
        Assert.Null(ScreenAreaLocator.FromEdges(noisy, Device));

        var full = MakeFrame(1280, 720, (x, y) => ((byte)((x * 7 + y * 3) % 251), (byte)((x * 13) % 241), (byte)((y * 17) % 239)));
        var a = ScreenAreaLocator.FromEdges(full, Device);
        Assert.Null(a!.Crop);
        Assert.Equal(ScreenAreaMethod.AspectMatch, a.Method);
    }

    private static Frame MakeFrame(int w, int h, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var bgra = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = pixel(x, y);
                var i = (y * w + x) * 4;
                bgra[i] = b;
                bgra[i + 1] = g;
                bgra[i + 2] = r;
                bgra[i + 3] = 255;
            }
        }

        return new Frame(w, h, bgra);
    }
}
