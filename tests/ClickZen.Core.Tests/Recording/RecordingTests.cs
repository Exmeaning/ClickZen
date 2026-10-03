using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Core.Recording;
using ClickZen.Core.Tests.Fakes;

namespace ClickZen.Core.Tests.Recording;

public sealed class GestureRecognizerTests
{
    private static GestureRecognizer Create(double dpScale = 1) =>
        new(new GestureRecognizerOptions { SwipeThresholdDp = 8, LongPressThresholdMs = 450, DpScale = dpScale });

    private static RawTouchEvent E(int id, TouchPhase ph, double x, double y, long t) => new(id, ph, new PointD(x, y), t);

    [Fact]
    public void Quick_press_is_a_tap_with_jitter_collapsed()
    {
        var r = Create();
        r.Start(1000);
        r.Feed(E(0, TouchPhase.Down, 100, 200, 1100));
        r.Feed(E(0, TouchPhase.Move, 102, 201, 1130));
        r.Feed(E(0, TouchPhase.Up, 101, 199, 1180));

        var g = Assert.Single(r.Gestures);
        Assert.Equal(GestureKind.Tap, g.Kind);
        Assert.Equal(100, g.StartMs);
        Assert.Equal(80, g.DurationMs);
        Assert.Equal(new PointD(100, 200), g.Fingers[0].Start);
        Assert.Equal(new PointD(100, 200), g.Fingers[0].End);
    }

    [Fact]
    public void Held_press_is_a_long_press()
    {
        var r = Create();
        r.Feed(E(0, TouchPhase.Down, 10, 10, 0));
        r.Feed(E(0, TouchPhase.Up, 10, 10, 600));
        Assert.Equal(GestureKind.LongPress, Assert.Single(r.Gestures).Kind);
    }

    [Fact]
    public void Swipe_threshold_scales_with_density()
    {
        // 15 px movement: swipe at dpScale 1 (threshold 8 px), tap at dpScale 3 (threshold 24 px).
        foreach (var (scale, expected) in new[] { (1.0, GestureKind.Swipe), (3.0, GestureKind.Tap) })
        {
            var r = Create(scale);
            r.Feed(E(0, TouchPhase.Down, 0, 0, 0));
            r.Feed(E(0, TouchPhase.Move, 15, 0, 50));
            r.Feed(E(0, TouchPhase.Up, 15, 0, 100));
            Assert.Equal(expected, Assert.Single(r.Gestures).Kind);
        }
    }

    [Fact]
    public void Swipe_keeps_full_path_and_timing()
    {
        var r = Create();
        r.Feed(E(0, TouchPhase.Down, 500, 2000, 0));
        for (var i = 1; i <= 20; i++)
        {
            r.Feed(E(0, TouchPhase.Move, 500, 2000 - i * 50, i * 10));
        }

        r.Feed(E(0, TouchPhase.Up, 500, 1000, 220));

        var g = Assert.Single(r.Gestures);
        Assert.Equal(GestureKind.Swipe, g.Kind);
        Assert.Equal(22, g.Fingers[0].Points.Count);
        Assert.Equal(220, g.DurationMs);
        Assert.Equal(new PointD(500, 1000), g.Fingers[0].End);
    }

    [Fact]
    public void Overlapping_fingers_form_one_multitouch_gesture()
    {
        var r = Create();
        r.Feed(E(0, TouchPhase.Down, 400, 1000, 0));
        r.Feed(E(1, TouchPhase.Down, 600, 1000, 20));
        r.Feed(E(0, TouchPhase.Move, 300, 1000, 100));
        r.Feed(E(1, TouchPhase.Move, 700, 1000, 100));
        r.Feed(E(0, TouchPhase.Up, 300, 1000, 150));
        Assert.Empty(r.Gestures);
        r.Feed(E(1, TouchPhase.Up, 700, 1000, 160));

        var g = Assert.Single(r.Gestures);
        Assert.Equal(GestureKind.MultiTouch, g.Kind);
        Assert.Equal(2, g.Fingers.Count);
        Assert.Equal(20, g.Fingers[1].Points[0].OffsetMs);
    }

    [Fact]
    public void Sequential_gestures_have_increasing_start_times()
    {
        var r = Create();
        r.Start(0);
        r.Feed(E(0, TouchPhase.Down, 1, 1, 100));
        r.Feed(E(0, TouchPhase.Up, 1, 1, 150));
        r.AddKey(KeyCodes.Back, 300);
        r.Feed(E(0, TouchPhase.Down, 1, 1, 500));
        r.Feed(E(0, TouchPhase.Up, 1, 1, 1100));

        Assert.Equal(new long[] { 100, 300, 500 }, r.Gestures.Select(g => g.StartMs));
        Assert.Equal(GestureKind.Key, r.Gestures[1].Kind);
        Assert.Equal(GestureKind.LongPress, r.Gestures[2].Kind);
    }

    [Fact]
    public void Events_for_unknown_pointer_are_ignored()
    {
        var r = Create();
        r.Feed(E(5, TouchPhase.Move, 1, 1, 0));
        r.Feed(E(5, TouchPhase.Up, 1, 1, 10));
        Assert.Empty(r.Gestures);
        Assert.False(r.HasActivePointers);
    }
}

public sealed class RecordingDocumentTests
{
    [Fact]
    public void Json_round_trip_preserves_gestures()
    {
        var doc = new RecordingDocument
        {
            Name = "demo",
            ScreenSize = new SizeI(2400, 1080),
            Gestures =
            [
                new Gesture { Kind = GestureKind.Tap, StartMs = 0, Fingers = [new FingerStroke([new TimedPoint(1, 2, 0), new TimedPoint(1, 2, 60)])] },
                new Gesture { Kind = GestureKind.Swipe, StartMs = 500, Fingers = [new FingerStroke([new TimedPoint(10, 20, 0), new TimedPoint(30.27, 40, 120)])] },
                Gesture.KeyPress(900, KeyCodes.Home),
                Gesture.TextInput(1000, "hi"),
            ],
        };

        var json = doc.ToJson();
        Assert.Contains("[2400,1080]", json.Replace(" ", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal), StringComparison.Ordinal);

        var back = RecordingDocument.FromJson(json);
        Assert.Equal(new SizeI(2400, 1080), back.ScreenSize);
        Assert.Equal(4, back.Gestures.Count);
        Assert.Equal(GestureKind.Swipe, back.Gestures[1].Kind);
        Assert.Equal(30.3, back.Gestures[1].Fingers[0].End.X, 6);
        Assert.Equal(KeyCodes.Home, back.Gestures[2].KeyCode);
        Assert.Equal("hi", back.Gestures[3].Text);
        Assert.Equal(1000, back.DurationMs);
    }

    [Fact]
    public void Newer_format_is_rejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cz-{Guid.NewGuid():N}.czrec");
        File.WriteAllText(path, """{ "formatVersion": 99, "gestures": [] }""");
        try
        {
            Assert.Throws<InvalidDataException>(() => RecordingDocument.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class RecordingPlayerTests
{
    private static RecordingDocument Doc() => new()
    {
        ScreenSize = new SizeI(1000, 2000),
        Gestures =
        [
            new Gesture { Kind = GestureKind.Tap, StartMs = 1000, Fingers = [new FingerStroke([new TimedPoint(100, 200, 0), new TimedPoint(100, 200, 50)])] },
            new Gesture { Kind = GestureKind.Swipe, StartMs = 3000, Fingers = [new FingerStroke([new TimedPoint(500, 1500, 0), new TimedPoint(500, 500, 400)])] },
            Gesture.KeyPress(4000, KeyCodes.Back),
        ],
    };

    [Fact]
    public async Task Plays_on_original_timeline_relative_to_first_gesture()
    {
        var clock = new FakeClock();
        var inj = new RecordingInjector(clock);
        await new RecordingPlayer(inj, clock).PlayAsync(Doc(), new PlaybackOptions(), new SizeI(1000, 2000), 1, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "tap", "stroke", "key" }, inj.Calls.Select(c => c.Kind));
        Assert.Equal(new long[] { 0, 2000, 3000 }, inj.Calls.Select(c => c.AtMs));
        Assert.Equal(new PointI(100, 200), inj.Calls[0].Point);
        Assert.Equal(50, inj.Calls[0].DurationMs);
    }

    [Fact]
    public async Task Speed_compresses_timeline_and_durations()
    {
        var clock = new FakeClock();
        var inj = new RecordingInjector(clock);
        await new RecordingPlayer(inj, clock).PlayAsync(Doc(), new PlaybackOptions { Speed = 2 }, new SizeI(1000, 2000), 1, TestContext.Current.CancellationToken);

        Assert.Equal(new long[] { 0, 1000, 1500 }, inj.Calls.Select(c => c.AtMs));
        Assert.Equal(200, inj.Calls[1].Strokes![0][^1].OffsetMs);
    }

    [Fact]
    public async Task Coordinates_rescale_to_current_screen()
    {
        var clock = new FakeClock();
        var inj = new RecordingInjector(clock);
        await new RecordingPlayer(inj, clock).PlayAsync(Doc(), new PlaybackOptions(), new SizeI(500, 1000), 1, TestContext.Current.CancellationToken);

        Assert.Equal(new PointI(50, 100), inj.Calls[0].Point);
        Assert.Equal(250, inj.Calls[1].Strokes![0][^1].X, 6);
    }

    [Fact]
    public async Task Loops_repeat_and_start_index_skips()
    {
        var clock = new FakeClock();
        var inj = new RecordingInjector(clock);
        await new RecordingPlayer(inj, clock).PlayAsync(Doc(), new PlaybackOptions { Loops = 2, StartIndex = 1 }, new SizeI(1000, 2000), 1, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "stroke", "key", "stroke", "key" }, inj.Calls.Select(c => c.Kind));
    }

    [Fact]
    public async Task Cancellation_stops_playback()
    {
        var clock = new FakeClock();
        var inj = new RecordingInjector(clock);
        using var cts = new CancellationTokenSource();
        var player = new RecordingPlayer(inj, clock);
        player.Progress += (_, p) =>
        {
            if (p.GestureIndex == 1)
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            player.PlayAsync(Doc(), new PlaybackOptions { Loops = 0 }, new SizeI(1000, 2000), 1, cts.Token));
        Assert.Single(inj.Calls);
    }
}
