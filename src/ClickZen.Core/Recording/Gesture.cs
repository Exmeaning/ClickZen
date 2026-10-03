using ClickZen.Core.Geometry;
using ClickZen.Core.Input;

namespace ClickZen.Core.Recording;

public enum GestureKind
{
    Tap,
    LongPress,
    Swipe,
    /// <summary>Several fingers overlapping in time (pinch, two-finger swipe…).</summary>
    MultiTouch,
    Key,
    Text,
}

/// <summary>One finger's stroke inside a gesture. Offsets are relative to the gesture start.</summary>
public sealed record FingerStroke(IReadOnlyList<TimedPoint> Points)
{
    public PointD Start => Points[0].Position;
    public PointD End => Points[^1].Position;
    public int DurationMs => Points[^1].OffsetMs - Points[0].OffsetMs;
}

/// <summary>
/// A recorded gesture. Times are milliseconds from the recording start; coordinates are device
/// pixels in the orientation described by the owning <see cref="RecordingDocument.ScreenSize"/>.
/// </summary>
public sealed record Gesture
{
    public required GestureKind Kind { get; init; }
    public required long StartMs { get; init; }
    public IReadOnlyList<FingerStroke> Fingers { get; init; } = [];
    public int KeyCode { get; init; }
    public string? Text { get; init; }

    public int DurationMs => Fingers.Count == 0 ? 0 : Fingers.Max(f => f.Points[^1].OffsetMs);

    public long EndMs => StartMs + DurationMs;

    public static Gesture KeyPress(long startMs, int keyCode) => new() { Kind = GestureKind.Key, StartMs = startMs, KeyCode = keyCode };

    public static Gesture TextInput(long startMs, string text) => new() { Kind = GestureKind.Text, StartMs = startMs, Text = text };
}
