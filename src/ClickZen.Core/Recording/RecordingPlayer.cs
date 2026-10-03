using ClickZen.Core.Geometry;
using ClickZen.Core.Input;

namespace ClickZen.Core.Recording;

public sealed record PlaybackOptions
{
    /// <summary>1 = original speed, 2 = twice as fast.</summary>
    public double Speed { get; init; } = 1.0;

    /// <summary>0 = loop forever.</summary>
    public int Loops { get; init; } = 1;

    /// <summary>Index of the first gesture to play (for "play from selection").</summary>
    public int StartIndex { get; init; }

    /// <summary>Pause between loops.</summary>
    public int LoopGapMs { get; init; } = 500;

    public HumanizeOptions Humanize { get; init; } = HumanizeOptions.None;
}

/// <summary>Progress notification during playback.</summary>
public readonly record struct PlaybackProgress(int Loop, int GestureIndex, int GestureCount);

/// <summary>
/// Plays a <see cref="RecordingDocument"/> through an <see cref="ITouchInjector"/>, keeping the
/// original timeline (scaled by speed) against a monotonic clock so drift never accumulates.
/// Cancellation is honoured immediately, including during waits.
/// </summary>
public sealed class RecordingPlayer
{
    private readonly ITouchInjector _injector;
    private readonly IClock _clock;
    private readonly Humanizer _humanizer;

    public RecordingPlayer(ITouchInjector injector, IClock? clock = null, Humanizer? humanizer = null)
    {
        _injector = injector;
        _clock = clock ?? SystemClock.Instance;
        _humanizer = humanizer ?? new Humanizer();
    }

    public event EventHandler<PlaybackProgress>? Progress;

    /// <param name="currentScreen">Current device screen; coordinates are rescaled from the recording's screen.</param>
    /// <param name="dpScale">Device density scale used by jitter.</param>
    public async Task PlayAsync(RecordingDocument doc, PlaybackOptions options, SizeI currentScreen, double dpScale, CancellationToken ct)
    {
        var gestures = doc.Gestures.OrderBy(g => g.StartMs).Skip(Math.Max(0, options.StartIndex)).ToList();
        if (gestures.Count == 0)
        {
            return;
        }

        var speed = Math.Clamp(options.Speed, 0.05, 20);
        for (var loop = 1; options.Loops <= 0 || loop <= options.Loops; loop++)
        {
            var origin = gestures[0].StartMs;
            var t0 = _clock.ElapsedMs;
            for (var i = 0; i < gestures.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var g = gestures[i];
                var due = t0 + (long)((g.StartMs - origin) / speed);
                var wait = due - _clock.ElapsedMs;
                if (wait > 0)
                {
                    await _clock.DelayAsync(TimeSpan.FromMilliseconds(wait), ct);
                }

                Progress?.Invoke(this, new PlaybackProgress(loop, options.StartIndex + i, doc.Gestures.Count));
                await PlayGestureAsync(g, doc.ScreenSize, currentScreen, dpScale, speed, options.Humanize, ct);
            }

            if (options.Loops > 0 && loop >= options.Loops)
            {
                break;
            }

            await _clock.DelayAsync(TimeSpan.FromMilliseconds(Math.Max(0, options.LoopGapMs)), ct);
        }
    }

    private async Task PlayGestureAsync(Gesture g, SizeI recorded, SizeI current, double dpScale, double speed, HumanizeOptions h, CancellationToken ct)
    {
        switch (g.Kind)
        {
            case GestureKind.Key:
                await _injector.KeyAsync(g.KeyCode, ct);
                return;
            case GestureKind.Text:
                await _injector.TextAsync(g.Text ?? "", ct);
                return;
            case GestureKind.Tap:
            case GestureKind.LongPress:
            {
                var p = RefScaling.Scale(g.Fingers[0].Start.Round(), recorded, current);
                p = _humanizer.Jitter(p, h, dpScale, current);
                var dur = _humanizer.Duration((int)(g.DurationMs / speed), h);
                await _injector.TapAsync(p, Math.Max(g.Kind == GestureKind.Tap ? 1 : 50, dur), ct);
                return;
            }

            default:
            {
                // Same random offset for the whole stroke so its shape is preserved.
                var fingers = g.Fingers.Select(f => Transform(f.Points, recorded, current, dpScale, speed, h)).ToList();
                if (fingers.Count == 1)
                {
                    await _injector.StrokeAsync(fingers[0], ct);
                }
                else
                {
                    await _injector.MultiStrokeAsync(fingers, ct);
                }

                return;
            }
        }
    }

    private IReadOnlyList<TimedPoint> Transform(IReadOnlyList<TimedPoint> pts, SizeI recorded, SizeI current, double dpScale, double speed, HumanizeOptions h)
    {
        var start = RefScaling.Scale(pts[0].Position.Round(), recorded, current);
        var jittered = _humanizer.Jitter(start, h, dpScale, current);
        var dx = jittered.X - start.X;
        var dy = jittered.Y - start.Y;
        var scaleT = _humanizer.Duration(1000, h) / 1000.0 / speed;
        var sx = recorded.IsEmpty || current.IsEmpty ? 1.0 : (double)current.Width / recorded.Width;
        var sy = recorded.IsEmpty || current.IsEmpty ? 1.0 : (double)current.Height / recorded.Height;
        return pts.Select(p => new TimedPoint(
            Math.Clamp(p.X * sx + dx, 0, Math.Max(0, current.Width - 1)),
            Math.Clamp(p.Y * sy + dy, 0, Math.Max(0, current.Height - 1)),
            (int)Math.Round(p.OffsetMs * scaleT))).ToArray();
    }
}
