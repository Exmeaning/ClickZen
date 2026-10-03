using ClickZen.Core.Geometry;
using ClickZen.Core.Input;

namespace ClickZen.Core.Recording;

public sealed record GestureRecognizerOptions
{
    /// <summary>A stroke moving further than this (in dp) is a swipe rather than a tap.</summary>
    public double SwipeThresholdDp { get; init; } = 8;

    /// <summary>A stationary press held at least this long is a long press.</summary>
    public int LongPressThresholdMs { get; init; } = 450;

    /// <summary>Pixels per dp of the device (density / 160).</summary>
    public double DpScale { get; init; } = 1;

    /// <summary>Douglas–Peucker epsilon in dp; 0 keeps every sample.</summary>
    public double SimplifyEpsilonDp { get; init; }
}

/// <summary>
/// Turns a stream of <see cref="RawTouchEvent"/>s into <see cref="Gesture"/>s.
/// A gesture starts when the first finger goes down and ends when the last finger is lifted;
/// if more than one pointer took part it is a <see cref="GestureKind.MultiTouch"/>.
/// Not thread-safe: feed events from one thread.
/// </summary>
public sealed class GestureRecognizer
{
    private readonly GestureRecognizerOptions _options;
    private readonly Dictionary<int, List<TimedPoint>> _active = new();
    private readonly List<List<TimedPoint>> _finished = [];
    private long _gestureStartMs = -1;
    private long _recordingStartMs = -1;

    public GestureRecognizer(GestureRecognizerOptions options) => _options = options;

    /// <summary>Raised when a gesture completes.</summary>
    public event EventHandler<Gesture>? GestureRecognized;

    /// <summary>Gestures recognised so far, in order.</summary>
    public List<Gesture> Gestures { get; } = [];

    /// <summary>Anchor time 0 of the recording; defaults to the first event.</summary>
    public void Start(long recordingStartMs) => _recordingStartMs = recordingStartMs;

    public bool HasActivePointers => _active.Count > 0;

    public void Feed(RawTouchEvent e)
    {
        if (_recordingStartMs < 0)
        {
            _recordingStartMs = e.TimestampMs;
        }

        switch (e.Phase)
        {
            case TouchPhase.Down:
                if (_active.Count == 0 && _finished.Count == 0)
                {
                    _gestureStartMs = e.TimestampMs;
                }

                _active[e.PointerId] = [new TimedPoint(e.Position.X, e.Position.Y, Offset(e.TimestampMs))];
                break;

            case TouchPhase.Move:
                if (_active.TryGetValue(e.PointerId, out var pts))
                {
                    var last = pts[^1];
                    var offset = Offset(e.TimestampMs);
                    if (last.X != e.Position.X || last.Y != e.Position.Y)
                    {
                        if (offset == last.OffsetMs && pts.Count > 1)
                        {
                            pts[^1] = new TimedPoint(e.Position.X, e.Position.Y, offset);
                        }
                        else
                        {
                            pts.Add(new TimedPoint(e.Position.X, e.Position.Y, Math.Max(offset, last.OffsetMs)));
                        }
                    }
                }

                break;

            case TouchPhase.Up:
                if (_active.Remove(e.PointerId, out var done))
                {
                    var off = Math.Max(Offset(e.TimestampMs), done[^1].OffsetMs);
                    if (done[^1].X != e.Position.X || done[^1].Y != e.Position.Y || off != done[^1].OffsetMs)
                    {
                        done.Add(new TimedPoint(e.Position.X, e.Position.Y, off));
                    }

                    _finished.Add(done);
                    if (_active.Count == 0)
                    {
                        Complete();
                    }
                }

                break;
        }
    }

    /// <summary>Records a key press at the given time (keys are not part of touch gestures).</summary>
    public void AddKey(int keyCode, long timestampMs)
    {
        if (_recordingStartMs < 0)
        {
            _recordingStartMs = timestampMs;
        }

        Emit(Gesture.KeyPress(timestampMs - _recordingStartMs, keyCode));
    }

    public void AddText(string text, long timestampMs)
    {
        if (_recordingStartMs < 0)
        {
            _recordingStartMs = timestampMs;
        }

        Emit(Gesture.TextInput(timestampMs - _recordingStartMs, text));
    }

    /// <summary>Cancels any in-progress gesture (e.g. pointer left the view while recording stopped).</summary>
    public void Reset()
    {
        _active.Clear();
        _finished.Clear();
        _gestureStartMs = -1;
    }

    private int Offset(long ts) => _gestureStartMs < 0 ? 0 : (int)(ts - _gestureStartMs);

    private void Complete()
    {
        var strokes = _finished.Select(p => new FingerStroke(Simplify(p))).ToList();
        var startMs = _gestureStartMs - _recordingStartMs;
        _finished.Clear();
        _gestureStartMs = -1;

        Gesture gesture;
        if (strokes.Count > 1)
        {
            gesture = new Gesture { Kind = GestureKind.MultiTouch, StartMs = startMs, Fingers = strokes };
        }
        else
        {
            var s = strokes[0];
            var maxDist = s.Points.Max(p => p.Position.DistanceTo(s.Start));
            var threshold = _options.SwipeThresholdDp * _options.DpScale;
            if (maxDist > threshold)
            {
                gesture = new Gesture { Kind = GestureKind.Swipe, StartMs = startMs, Fingers = strokes };
            }
            else
            {
                // Collapse jitter of a stationary press to a single point held for the duration.
                var dur = s.DurationMs;
                var p = s.Start;
                var stroke = new FingerStroke([new TimedPoint(p.X, p.Y, 0), new TimedPoint(p.X, p.Y, dur)]);
                var kind = dur >= _options.LongPressThresholdMs ? GestureKind.LongPress : GestureKind.Tap;
                gesture = new Gesture { Kind = kind, StartMs = startMs, Fingers = [stroke] };
            }
        }

        Emit(gesture);
    }

    private IReadOnlyList<TimedPoint> Simplify(List<TimedPoint> points)
    {
        // Normalise: first point at offset 0 relative to the gesture start is preserved as-is,
        // other fingers keep their delay relative to the first finger.
        return _options.SimplifyEpsilonDp > 0
            ? Trajectory.Simplify(points, _options.SimplifyEpsilonDp * _options.DpScale)
            : points.ToArray();
    }

    private void Emit(Gesture g)
    {
        Gestures.Add(g);
        GestureRecognized?.Invoke(this, g);
    }
}
