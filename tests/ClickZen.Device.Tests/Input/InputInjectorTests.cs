using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Device.Input;

namespace ClickZen.Device.Tests.Input;

public sealed class GeteventDeviceParserTests
{
    private static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "getevent", name));

    [Fact]
    public void Parses_all_device_blocks_of_real_avd_output()
    {
        var devices = GeteventDeviceParser.Parse(Sample("avd_api36_getevent_p.txt"));

        Assert.Contains(devices, d => d.Name == "Power Button" && d.Path == "/dev/input/event0");
        var touch = devices.Where(d => d.IsMultiTouch).ToList();
        Assert.Equal(11, touch.Count);
        var first = touch.First(d => d.Name == "virtio_input_multi_touch_1");
        Assert.Equal("/dev/input/event2", first.Path);
        Assert.Equal(new AbsAxis(0x35, 0, 32767), first.X);
        Assert.Equal(new AbsAxis(0x36, 0, 32767), first.Y);
        Assert.Equal(10, first.MaxSlot);
        Assert.True(first.HasSlots);
        Assert.False(first.IsDirect);
        Assert.Equal(2, first.EventNumber);
    }

    [Fact]
    public void Picks_first_multitouch_device_on_avd()
    {
        var picked = GeteventDeviceParser.PickTouchscreen(GeteventDeviceParser.Parse(Sample("avd_api36_getevent_p.txt")));
        Assert.NotNull(picked);
        Assert.Equal("/dev/input/event2", picked.Path);
    }

    [Fact]
    public void Prefers_direct_touchscreen_with_touch_name()
    {
        var devices = GeteventDeviceParser.Parse(Sample("synthetic_phone_getevent_p.txt"));
        Assert.Equal(3, devices.Count);
        var picked = GeteventDeviceParser.PickTouchscreen(devices);
        Assert.NotNull(picked);
        Assert.Equal("fts_ts", picked.Name);
        Assert.True(picked.IsDirect);
        Assert.Equal(10799, picked.X.Max);
        Assert.Equal(23999, picked.Y.Max);
        Assert.Contains(InputDeviceInfo.BtnTouch, picked.Keys);
    }

    [Fact]
    public void No_touchscreen_gives_null()
    {
        Assert.Null(GeteventDeviceParser.PickTouchscreen(GeteventDeviceParser.Parse("add device 1: /dev/input/event0\n  name: \"keys\"\n")));
        Assert.Empty(GeteventDeviceParser.Parse(""));
    }
}

public sealed class AdbInputInjectorTests
{
    private static TimedPoint P(double x, double y, int t) => new(x, y, t);

    [Fact]
    public void Short_tap_uses_input_tap_long_press_uses_swipe()
    {
        Assert.Equal("input tap 10 20", AdbInputInjector.BuildTap(new PointI(10, 20), 60));
        Assert.Equal("input swipe 10 20 10 20 800", AdbInputInjector.BuildTap(new PointI(10, 20), 800));
    }

    [Fact]
    public void Stroke_on_api30_is_one_continuous_motionevent_gesture()
    {
        var cmd = AdbInputInjector.BuildStroke([P(100, 1000, 0), P(100, 500, 100)], sdk: 34);

        Assert.StartsWith("input motionevent DOWN 100 1000", cmd, StringComparison.Ordinal);
        Assert.EndsWith("input motionevent UP 100 500", cmd, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(cmd, "DOWN"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(cmd, " UP "));
        Assert.True(System.Text.RegularExpressions.Regex.Count(cmd, "MOVE") >= 6);
        Assert.Contains("sleep 0.0", cmd, StringComparison.Ordinal);
    }

    [Fact]
    public void Stroke_before_api30_falls_back_to_swipe()
    {
        Assert.Equal("input swipe 1 2 30 40 250", AdbInputInjector.BuildStroke([P(1, 2, 0), P(9, 9, 100), P(30, 40, 250)], sdk: 29));
    }

    [Theory]
    [InlineData("hello world", "hello%sworld")]
    [InlineData("a&b;c", "a\\&b\\;c")]
    [InlineData("50%", "50\\%")]
    [InlineData("it's", "it\\'s")]
    public void Text_is_escaped_for_input_text(string text, string expected) =>
        Assert.Equal(expected, AdbInputInjector.EscapeInputText(text));

    [Fact]
    public async Task Commands_go_through_the_shell_delegate()
    {
        var sent = new List<string>();
        var inj = new AdbInputInjector((c, _) => { sent.Add(c); return Task.FromResult(""); }, 34);
        await inj.KeyAsync(KeyCodes.Home, TestContext.Current.CancellationToken);
        await inj.MultiStrokeAsync([[P(0, 0, 0), P(5, 5, 50)], [P(9, 9, 0), P(8, 8, 50)]], TestContext.Current.CancellationToken);

        Assert.Equal("input keyevent 3", sent[0]);
        Assert.Contains("DOWN 0 0", sent[1], StringComparison.Ordinal);
        Assert.DoesNotContain("DOWN 9 9", sent[1], StringComparison.Ordinal);
        Assert.False(inj.SupportsMultiTouch);
    }
}

public sealed class RootSendeventInjectorTests
{
    private static InputDeviceInfo Touch(bool slots = true, bool btnTouch = true) => new(
        "/dev/input/event2", "fts_ts",
        new Dictionary<int, AbsAxis>
        {
            [0x35] = new(0x35, 0, 10799),
            [0x36] = new(0x36, 0, 23999),
            [0x39] = new(0x39, 0, 65535),
            [0x2f] = slots ? new(0x2f, 0, 9) : new(0x2f, 0, 0),
        }.Where(kv => slots || kv.Key != 0x2f).ToDictionary(),
        btnTouch ? new HashSet<int> { 0x14a } : new HashSet<int>(),
        true);

    private static RootSendeventInjector Create(InputDeviceInfo touch, DisplayRotation rot = DisplayRotation.Rotation0) =>
        new((_, _) => Task.FromResult(""), touch, () => new SizeI(1080, 2400), () => rot);

    private static string[] Commands(string script) =>
        script.Split(';').Where(c => c.StartsWith("sendevent", StringComparison.Ordinal)).Select(c => c["sendevent /dev/input/event2 ".Length..]).ToArray();

    [Fact]
    public void Tap_writes_protocol_b_down_and_up_with_scaled_axes()
    {
        var script = Create(Touch()).BuildScript([[new TimedPoint(540, 1200, 0), new TimedPoint(540, 1200, 50)]]);
        var cmds = Commands(script);

        // Down batch: slot 0, tracking id, x, y, BTN_TOUCH 1, SYN
        Assert.Equal("3 47 0", cmds[0]);
        Assert.StartsWith("3 57 ", cmds[1], StringComparison.Ordinal);
        Assert.Equal("3 53 5400", cmds[2]);
        Assert.Equal("3 54 12000", cmds[3]);
        Assert.Equal("1 330 1", cmds[4]);
        Assert.Equal("0 0 0", cmds[5]);
        // Up batch ends with tracking id -1, BTN_TOUCH 0, SYN
        Assert.Contains("3 57 -1", cmds);
        Assert.Equal("1 330 0", cmds[^2]);
        Assert.Equal("0 0 0", cmds[^1]);
        Assert.Contains(";sleep 0.0", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_fingers_use_distinct_slots_and_btn_touch_spans_both()
    {
        var script = Create(Touch()).BuildScript(
        [
            [new TimedPoint(100, 100, 0), new TimedPoint(100, 100, 100)],
            [new TimedPoint(900, 100, 20), new TimedPoint(900, 100, 60)],
        ]);
        var cmds = Commands(script);

        Assert.Contains("3 47 1", cmds);
        Assert.Single(cmds, c => c == "1 330 1");
        Assert.Single(cmds, c => c == "1 330 0");
        Assert.Equal(2, cmds.Count(c => c == "3 57 -1"));
        // BTN_TOUCH 0 only after the last finger lifts
        Assert.True(Array.LastIndexOf(cmds, "3 57 -1") < Array.IndexOf(cmds, "1 330 0"));
    }

    [Fact]
    public void Rotated_screen_maps_back_to_natural_axes()
    {
        // Landscape (rotation 90): device point (0, 1079) is natural (0, 0).
        var script = Create(Touch(), DisplayRotation.Rotation90).BuildScript([[new TimedPoint(0, 1079, 0), new TimedPoint(0, 1079, 10)]]);
        var cmds = Commands(script);
        Assert.Equal("3 53 0", cmds[2]);
        Assert.Equal("3 54 0", cmds[3]);
    }

    [Fact]
    public void Device_without_slots_is_single_touch()
    {
        var inj = Create(Touch(slots: false, btnTouch: false));
        Assert.False(inj.SupportsMultiTouch);
        var cmds = Commands(inj.BuildScript([[new TimedPoint(1, 1, 0), new TimedPoint(1, 1, 10)]]));
        Assert.DoesNotContain(cmds, c => c.StartsWith("3 47", StringComparison.Ordinal));
        Assert.DoesNotContain(cmds, c => c.StartsWith("1 330", StringComparison.Ordinal));
    }

    [Fact]
    public void Non_touch_device_is_rejected()
    {
        var keys = new InputDeviceInfo("/dev/input/event0", "keys", new Dictionary<int, AbsAxis>(), new HashSet<int>(), false);
        Assert.Throws<ArgumentException>(() => Create(keys));
    }
}
