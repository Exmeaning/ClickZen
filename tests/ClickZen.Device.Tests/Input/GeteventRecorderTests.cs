using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Core.Recording;
using ClickZen.Device.Adb;
using ClickZen.Device.Input;
using ClickZen.Device.Tests.Adb;

namespace ClickZen.Device.Tests.Input;

internal static class GeteventSamples
{
    public static string Read(string name) => SampleFiles.Read(Path.Combine("getevent", name));

    public static IEnumerable<string> Lines(string text) =>
        text.Replace("\r", "", StringComparison.Ordinal).Split('\n');

    /// <summary>The AVD's first virtio touchscreen (/dev/input/event2, axes 0..32767, 11 slots).</summary>
    public static InputDeviceInfo AvdTouch() =>
        GeteventDeviceParser.PickTouchscreen(GeteventDeviceParser.Parse(Read("avd_api36_getevent_p.txt")))!;

    public static readonly SizeI AvdNatural = new(1080, 2400);

    public static double Px(int raw, int pixels) => raw * (double)pixels / 32768;
}

public sealed class GeteventEventParserTests
{
    [Fact]
    public void Parses_timestamped_numeric_line()
    {
        Assert.True(GeteventEventParser.TryParse("[     789.870862] 0003 0035 00001f40", out var e));
        Assert.Equal(789_870_862L, e.TimestampUs);
        Assert.Null(e.DevicePath);
        Assert.Equal(GeteventEvent.EvAbs, e.Type);
        Assert.Equal(InputDeviceInfo.AbsMtPositionX, e.Code);
        Assert.Equal(8000, e.Value);
    }

    [Fact]
    public void Negative_values_are_sign_extended()
    {
        Assert.True(GeteventEventParser.TryParse("[  791.498729] 0003 0039 ffffffff", out var e));
        Assert.Equal(-1, e.Value);
    }

    [Fact]
    public void Parses_device_path_and_labels()
    {
        Assert.True(GeteventEventParser.TryParse("[   12.000500] /dev/input/event2: EV_ABS       ABS_MT_TRACKING_ID   ffffffff", out var e));
        Assert.Equal(12_000_500L, e.TimestampUs);
        Assert.Equal("/dev/input/event2", e.DevicePath);
        Assert.Equal(InputDeviceInfo.AbsMtTrackingId, e.Code);
        Assert.Equal(-1, e.Value);

        Assert.True(GeteventEventParser.TryParse("/dev/input/event2: EV_KEY       BTN_TOUCH            DOWN", out e));
        Assert.Null(e.TimestampUs);
        Assert.Equal(GeteventEvent.EvKey, e.Type);
        Assert.Equal(InputDeviceInfo.BtnTouch, e.Code);
        Assert.Equal(1, e.Value);

        Assert.True(GeteventEventParser.TryParse("/dev/input/event2: EV_SYN       SYN_REPORT           00000000", out e));
        Assert.Equal(GeteventEvent.EvSyn, e.Type);
        Assert.Equal(GeteventEvent.SynReport, e.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("add device 3: /dev/input/event2")]
    [InlineData("  name:     \"virtio_input_multi_touch_1\"")]
    [InlineData("[  1.0] 0003 0035")]
    [InlineData("/dev/input/event2: EV_ABS ABS_MT_UNKNOWN_THING 00000001")]
    [InlineData("could not open /dev/input/event9, Permission denied")]
    public void Rejects_non_event_lines(string line) => Assert.False(GeteventEventParser.TryParse(line, out _));
}

public sealed class GeteventTouchDecoderTests
{
    private static List<RawTouchEvent> Decode(GeteventTouchDecoder decoder, IEnumerable<string> lines)
    {
        var all = new List<RawTouchEvent>();
        foreach (var line in lines)
        {
            if (GeteventEventParser.TryParse(line, out var e))
            {
                all.AddRange(decoder.Feed(e));
            }
        }

        return all;
    }

    [Fact]
    public void Merges_slot_state_of_real_pinch_and_tap()
    {
        var decoder = new GeteventTouchDecoder(GeteventSamples.AvdTouch(), GeteventSamples.AvdNatural, DisplayRotation.Rotation0);
        var events = Decode(decoder, GeteventSamples.Lines(GeteventSamples.Read("avd_api36_getevent_t_pinch_tap.txt")));

        // Two fingers down, five frames moving both, two lifts, then a tap with the first free slot.
        Assert.Equal(2 + 5 * 2 + 2 + 2, events.Count);
        Assert.Equal(new RawTouchEvent(0, TouchPhase.Down, new PointD(GeteventSamples.Px(8000, 1080), GeteventSamples.Px(16000, 2400)), 789_870), events[0]);
        Assert.Equal(new RawTouchEvent(1, TouchPhase.Down, new PointD(GeteventSamples.Px(24000, 1080), GeteventSamples.Px(16000, 2400)), 790_015), events[1]);

        // Only X changes in the move frames: Y is carried over from the slot state.
        var moves = events.Where(e => e.Phase == TouchPhase.Move).ToList();
        Assert.Equal(10, moves.Count);
        Assert.All(moves, m => Assert.Equal(GeteventSamples.Px(16000, 2400), m.Position.Y));
        Assert.Equal(GeteventSamples.Px(3000, 1080), moves.Last(m => m.PointerId == 0).Position.X, 6);
        Assert.Equal(GeteventSamples.Px(29000, 1080), moves.Last(m => m.PointerId == 1).Position.X, 6);

        var ups = events.Where(e => e.Phase == TouchPhase.Up).ToList();
        Assert.Equal([0, 1, 0], ups.Select(u => u.PointerId));
        Assert.Equal(791_498, ups[0].TimestampMs);
        Assert.Equal(GeteventSamples.Px(3000, 1080), ups[0].Position.X, 6);

        Assert.Equal(new RawTouchEvent(0, TouchPhase.Down, new PointD(540, 600), 792_233), events[^2]);
        Assert.Equal(new RawTouchEvent(0, TouchPhase.Up, new PointD(540, 600), 792_523), events[^1]);
        Assert.False(decoder.HasActiveContacts);
    }

    [Theory]
    [InlineData(DisplayRotation.Rotation0, 540, 600)]
    [InlineData(DisplayRotation.Rotation90, 600, 539)]
    [InlineData(DisplayRotation.Rotation180, 539, 1799)]
    [InlineData(DisplayRotation.Rotation270, 1799, 540)]
    public void Maps_axes_to_rotated_pixels(DisplayRotation rotation, double x, double y)
    {
        var decoder = new GeteventTouchDecoder(GeteventSamples.AvdTouch(), GeteventSamples.AvdNatural, rotation);
        var events = Decode(decoder, ["[ 1.000000] 0003 0039 00000001", "[ 1.000000] 0003 0035 00004000", "[ 1.000000] 0003 0036 00002000", "[ 1.000000] 0000 0000 00000000"]);
        Assert.Equal(new PointD(x, y), Assert.Single(events).Position);
        Assert.Equal(RotationMapper.RotatedSize(GeteventSamples.AvdNatural, rotation), decoder.ScreenSize);
    }

    [Fact]
    public void Uses_device_axis_range_and_minimum()
    {
        var touch = new InputDeviceInfo("/dev/input/event1", "ts", new Dictionary<int, AbsAxis>
        {
            [InputDeviceInfo.AbsMtSlot] = new(InputDeviceInfo.AbsMtSlot, 0, 9),
            [InputDeviceInfo.AbsMtPositionX] = new(InputDeviceInfo.AbsMtPositionX, 100, 2259),
            [InputDeviceInfo.AbsMtPositionY] = new(InputDeviceInfo.AbsMtPositionY, 0, 4799),
        }, new HashSet<int>(), true);
        var decoder = new GeteventTouchDecoder(touch, GeteventSamples.AvdNatural, DisplayRotation.Rotation0);
        // x = (1180 - 100) * 1080 / 2160 = 540, y = 2400 * 2400 / 4800 = 1200
        var events = Decode(decoder, ["0003 0039 00000005", "0003 0035 0000049c", "0003 0036 00000960", "0000 0000 00000000"]);
        Assert.Equal(new PointD(540, 1200), Assert.Single(events).Position);
    }

    [Fact]
    public void Ignores_other_devices_and_events_after_syn_dropped()
    {
        var decoder = new GeteventTouchDecoder(GeteventSamples.AvdTouch(), GeteventSamples.AvdNatural, DisplayRotation.Rotation0);
        var events = Decode(decoder,
        [
            "[ 1.000000] /dev/input/event3: 0003 0039 00000001",
            "[ 1.000000] /dev/input/event3: 0003 0035 00004000",
            "[ 1.000000] /dev/input/event3: 0003 0036 00004000",
            "[ 1.000000] /dev/input/event3: 0000 0000 00000000",
            "[ 1.000000] /dev/input/event2: 0003 0039 00000002",
            "[ 1.000000] /dev/input/event2: 0003 0035 00004000",
            "[ 1.000000] /dev/input/event2: 0003 0036 00004000",
            "[ 1.000000] /dev/input/event2: 0000 0000 00000000",
            "[ 1.010000] /dev/input/event2: 0000 0003 00000000",
            "[ 1.010000] /dev/input/event2: 0003 0035 00000000",
            "[ 1.010000] /dev/input/event2: 0000 0000 00000000",
            "[ 1.020000] /dev/input/event2: 0003 0039 ffffffff",
            "[ 1.020000] /dev/input/event2: 0000 0000 00000000",
        ]);

        Assert.Equal([TouchPhase.Down, TouchPhase.Up], events.Select(e => e.Phase));
        Assert.Equal(new PointD(540, 1200), events[1].Position);
    }

    [Fact]
    public void New_tracking_id_without_lift_splits_the_contact()
    {
        var decoder = new GeteventTouchDecoder(GeteventSamples.AvdTouch(), GeteventSamples.AvdNatural, DisplayRotation.Rotation0);
        var events = Decode(decoder,
        [
            "[ 1.000000] 0003 0039 00000001", "[ 1.000000] 0003 0035 00004000", "[ 1.000000] 0003 0036 00004000", "[ 1.000000] 0000 0000 00000000",
            "[ 1.050000] 0003 0039 00000002", "[ 1.050000] 0003 0035 00002000", "[ 1.050000] 0000 0000 00000000",
        ]);

        Assert.Equal([TouchPhase.Down, TouchPhase.Up, TouchPhase.Down], events.Select(e => e.Phase));
        Assert.Equal(new PointD(540, 1200), events[1].Position);
        Assert.Equal(new PointD(270, 1200), events[2].Position);
    }

    [Fact]
    public void Finish_lifts_open_contacts_at_last_time()
    {
        var decoder = new GeteventTouchDecoder(GeteventSamples.AvdTouch(), GeteventSamples.AvdNatural, DisplayRotation.Rotation0);
        Decode(decoder, ["[ 2.000000] 0003 0039 00000001", "[ 2.000000] 0003 0035 00004000", "[ 2.000000] 0003 0036 00004000", "[ 2.000000] 0000 0000 00000000", "[ 2.300000] 0000 0000 00000000"]);
        Assert.True(decoder.HasActiveContacts);
        var up = Assert.Single(decoder.Finish());
        Assert.Equal(new RawTouchEvent(0, TouchPhase.Up, new PointD(540, 1200), 2300), up);
        Assert.False(decoder.HasActiveContacts);
    }

    [Fact]
    public void Protocol_a_devices_are_tracked_per_contact()
    {
        var touch = new InputDeviceInfo("/dev/input/event1", "old_ts", new Dictionary<int, AbsAxis>
        {
            [InputDeviceInfo.AbsMtPositionX] = new(InputDeviceInfo.AbsMtPositionX, 0, 1079),
            [InputDeviceInfo.AbsMtPositionY] = new(InputDeviceInfo.AbsMtPositionY, 0, 2399),
        }, new HashSet<int>(), true);
        var decoder = new GeteventTouchDecoder(touch, GeteventSamples.AvdNatural, DisplayRotation.Rotation0);
        var events = Decode(decoder,
        [
            "[ 1.000000] 0003 0035 00000064", "[ 1.000000] 0003 0036 000000c8", "[ 1.000000] 0000 0002 00000000", "[ 1.000000] 0000 0000 00000000",
            "[ 1.010000] 0003 0035 0000006e", "[ 1.010000] 0003 0036 000000c8", "[ 1.010000] 0000 0002 00000000",
            "[ 1.010000] 0003 0035 000001f4", "[ 1.010000] 0003 0036 000001f4", "[ 1.010000] 0000 0002 00000000", "[ 1.010000] 0000 0000 00000000",
            "[ 1.020000] 0000 0002 00000000", "[ 1.020000] 0000 0000 00000000",
        ]);

        Assert.Equal(
            [(0, TouchPhase.Down), (0, TouchPhase.Move), (1, TouchPhase.Down), (0, TouchPhase.Up), (1, TouchPhase.Up)],
            events.Select(e => (e.PointerId, e.Phase)));
        Assert.Equal(new PointD(110, 200), events[1].Position);
    }
}

public sealed class DisplayRotationParserTests
{
    [Theory]
    [InlineData("avd_api36_input_orientation_rot0.txt", DisplayRotation.Rotation0)]
    [InlineData("avd_api36_input_orientation_rot1.txt", DisplayRotation.Rotation90)]
    public void Reads_default_display_viewport(string file, DisplayRotation expected)
    {
        Assert.True(DisplayRotationParser.TryParse(SampleFiles.Read(Path.Combine("dumpsys", file)), out var r));
        Assert.Equal(expected, r);
    }

    [Theory]
    [InlineData("    SurfaceOrientation: 3", DisplayRotation.Rotation270)]
    [InlineData("  mCurrentRotation=ROTATION_180", DisplayRotation.Rotation180)]
    [InlineData("  mRotation=1 mAltOrientation=false", DisplayRotation.Rotation90)]
    public void Reads_other_formats(string output, DisplayRotation expected)
    {
        Assert.True(DisplayRotationParser.TryParse(output, out var r));
        Assert.Equal(expected, r);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Viewport INTERNAL: displayId=-1, orientation=2")]
    [InlineData("SurfaceOrientation: 7")]
    public void Unknown_output_is_rotation_0(string output)
    {
        Assert.False(DisplayRotationParser.TryParse(output, out var r));
        Assert.Equal(DisplayRotation.Rotation0, r);
    }
}

public sealed class GeteventRecorderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GeteventSession AvdSession(DisplayRotation rotation = DisplayRotation.Rotation0) =>
        new(GeteventSamples.AvdTouch(), GeteventSamples.AvdNatural, rotation, 420);

    [Fact]
    public void Builder_turns_sample_into_pinch_and_tap()
    {
        var builder = new GeteventRecordingBuilder(AvdSession(), new GeteventRecordingOptions { Name = "pinch", DeviceModel = "AVD" });
        foreach (var line in GeteventSamples.Lines(GeteventSamples.Read("avd_api36_getevent_t_pinch_tap.txt")))
        {
            builder.FeedLine(line);
        }

        var doc = builder.Build();
        Assert.Equal("pinch", doc.Name);
        Assert.Equal("AVD", doc.DeviceModel);
        Assert.Equal(new SizeI(1080, 2400), doc.ScreenSize);
        Assert.Equal(2, doc.Gestures.Count);

        var pinch = doc.Gestures[0];
        Assert.Equal(GestureKind.MultiTouch, pinch.Kind);
        Assert.Equal(0, pinch.StartMs);
        Assert.Equal(2, pinch.Fingers.Count);
        Assert.Equal(GeteventSamples.Px(8000, 1080), pinch.Fingers[0].Start.X, 6);
        Assert.Equal(GeteventSamples.Px(3000, 1080), pinch.Fingers[0].End.X, 6);
        Assert.Equal(145, pinch.Fingers[1].Points[0].OffsetMs);
        Assert.Equal(GeteventSamples.Px(29000, 1080), pinch.Fingers[1].End.X, 6);
        Assert.Equal(791_654 - 789_870, pinch.DurationMs);

        var tap = doc.Gestures[1];
        Assert.Equal(GestureKind.Tap, tap.Kind);
        Assert.Equal(792_233 - 789_870, tap.StartMs);
        Assert.Equal(290, tap.DurationMs);
        Assert.Equal(new PointD(540, 600), tap.Fingers[0].Start);

        // The document survives a save/load round trip.
        var back = RecordingDocument.FromJson(doc.ToJson());
        Assert.Equal(2, back.Gestures.Count);
        Assert.Same(doc, builder.Build());
    }

    [Fact]
    public void Builder_completes_or_drops_open_gesture()
    {
        string[] lines = ["[ 5.000000] 0003 0039 00000001", "[ 5.000000] 0003 0035 00004000", "[ 5.000000] 0003 0036 00004000", "[ 5.000000] 0000 0000 00000000", "[ 5.600000] 0000 0000 00000000"];

        var complete = new GeteventRecordingBuilder(AvdSession());
        var dropped = new GeteventRecordingBuilder(AvdSession(), new GeteventRecordingOptions { CompleteOpenGestures = false });
        foreach (var l in lines)
        {
            complete.FeedLine(l);
            dropped.FeedLine(l);
        }

        var g = Assert.Single(complete.Build().Gestures);
        Assert.Equal(GestureKind.LongPress, g.Kind);
        Assert.Equal(600, g.DurationMs);
        Assert.Empty(dropped.Build().Gestures);
    }

    [Fact]
    public async Task Prepare_detects_touchscreen_size_density_and_rotation()
    {
        var commands = new List<string>();
        var recorder = new GeteventRecorder(
            (_, cmd, _) =>
            {
                commands.Add(cmd);
                return Task.FromResult(cmd switch
                {
                    "getevent -p" => GeteventSamples.Read("avd_api36_getevent_p.txt"),
                    _ => "Physical size: 1080x2400\nPhysical density: 420\nOverride density: 480",
                });
            },
            (_, _, _, _) => Task.CompletedTask,
            new FixedRotationResolver(DisplayRotation.Rotation270));

        var s = await recorder.PrepareAsync("emu", null, Ct);
        Assert.Equal("/dev/input/event2", s.Touchscreen.Path);
        Assert.Equal(new SizeI(1080, 2400), s.NaturalSize);
        Assert.Equal(new SizeI(2400, 1080), s.ScreenSize);
        Assert.Equal(480, s.Density);
        Assert.Equal(3.0, s.DpScale);
        Assert.Equal(DisplayRotation.Rotation270, s.Rotation);
        Assert.Equal("getevent -t '/dev/input/event2'", s.Command);
        Assert.Equal(["getevent -p", "wm size; wm density"], commands);

        // Supplied values skip the device queries.
        commands.Clear();
        await recorder.PrepareAsync("emu", new GeteventRecordingOptions { Touchscreen = s.Touchscreen, NaturalSize = s.NaturalSize, Density = 420 }, Ct);
        Assert.Empty(commands);
    }

    [Fact]
    public async Task Prepare_without_touchscreen_fails()
    {
        var recorder = new GeteventRecorder((_, _, _) => Task.FromResult("add device 1: /dev/input/event0\n  name: \"keys\"\n"),
            (_, _, _, _) => Task.CompletedTask, FixedRotationResolver.Natural);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.PrepareAsync("emu", null, Ct));
    }

    [Fact]
    public async Task Cancelling_stops_recording_and_returns_document()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        string? streamed = null;
        var recorder = new GeteventRecorder(
            (_, _, _) => Task.FromResult(""),
            async (_, cmd, onLine, ct) =>
            {
                streamed = cmd;
                await Task.Yield();
                foreach (var line in GeteventSamples.Lines(GeteventSamples.Read("avd_api36_getevent_t_pinch_tap.txt")))
                {
                    onLine(line);
                }

                await Task.Delay(Timeout.Infinite, ct);
            },
            FixedRotationResolver.Natural);

        var seen = new List<Gesture>();
        var options = new GeteventRecordingOptions { Touchscreen = GeteventSamples.AvdTouch(), NaturalSize = GeteventSamples.AvdNatural, Density = 160 };
        var doc = await recorder.RecordAsync("emu", options, g =>
        {
            seen.Add(g);
            if (seen.Count == 2)
            {
                stop.Cancel();
            }
        }, stop.Token);

        Assert.Equal("getevent -t '/dev/input/event2'", streamed);
        Assert.Equal([GestureKind.MultiTouch, GestureKind.Tap], doc.Gestures.Select(g => g.Kind));
        Assert.Equal(seen, doc.Gestures);
    }

    [Fact]
    public async Task Cancelling_before_recording_throws()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        var recorder = new GeteventRecorder((_, _, ct) => Task.FromCanceled<string>(ct), (_, _, _, _) => Task.CompletedTask, FixedRotationResolver.Natural);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recorder.RecordAsync("emu", null, null, stop.Token));
    }

    [Fact]
    public async Task Stream_failures_are_rethrown()
    {
        var recorder = new GeteventRecorder((_, _, _) => Task.FromResult(""),
            (_, _, _, _) => Task.FromException(new AdbException(AdbErrorKind.DeviceNotFound, "gone")), FixedRotationResolver.Natural);
        var ex = await Assert.ThrowsAsync<AdbException>(() => recorder.RecordSessionAsync("emu", AvdSession(), null, null, Ct));
        Assert.Equal(AdbErrorKind.DeviceNotFound, ex.Kind);
    }

    [Fact]
    public async Task Capture_streams_raw_touches_until_cancelled()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var recorder = new GeteventRecorder((_, _, _) => Task.FromResult(""),
            async (_, _, onLine, ct) =>
            {
                foreach (var line in GeteventSamples.Lines(GeteventSamples.Read("avd_api36_getevent_t_pinch_tap.txt")))
                {
                    onLine(line);
                }

                await Task.Delay(Timeout.Infinite, ct);
            }, FixedRotationResolver.Natural);

        var touches = new List<RawTouchEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recorder.CaptureAsync("emu", AvdSession(DisplayRotation.Rotation90), t =>
        {
            touches.Add(t);
            if (touches.Count == 16)
            {
                stop.Cancel();
            }
        }, stop.Token));

        Assert.Equal(16, touches.Count);
        Assert.Equal(new PointD(600, 539), touches[^2].Position);
    }

    [Fact]
    public async Task Records_through_adb_service_with_dumpsys_rotation()
    {
        var (client, fake) = FakeAdbClient.Create();
        fake.Shell = (_, cmd, _) => Task.FromResult(cmd switch
        {
            "getevent -p" => GeteventSamples.Lines(GeteventSamples.Read("avd_api36_getevent_p.txt")),
            "wm size; wm density" => ["Physical size: 1080x2400", "Physical density: 420"],
            DumpsysRotationResolver.InputCommand => GeteventSamples.Lines(SampleFiles.Read(Path.Combine("dumpsys", "avd_api36_input_orientation_rot1.txt"))),
            "getevent -t '/dev/input/event2'" => GeteventSamples.Lines(GeteventSamples.Read("avd_api36_getevent_t_pinch_tap.txt")),
            _ => [],
        });
        using var adb = new AdbService(client, null, null, null);

        var doc = await new GeteventRecorder(adb).RecordAsync("emulator-5554", stopToken: Ct);

        Assert.Equal(new SizeI(2400, 1080), doc.ScreenSize);
        Assert.Equal([GestureKind.MultiTouch, GestureKind.Tap], doc.Gestures.Select(g => g.Kind));
        Assert.Equal(new PointD(600, 539), doc.Gestures[1].Fingers[0].Start);
        Assert.Contains("emulator-5554: getevent -t '/dev/input/event2'", fake.ShellLog);
    }
}
