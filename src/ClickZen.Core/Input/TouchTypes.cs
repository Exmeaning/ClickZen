using ClickZen.Core.Geometry;

namespace ClickZen.Core.Input;

public enum TouchPhase
{
    Down,
    Move,
    Up,
}

/// <summary>
/// One raw pointer event in device pixels. Produced by the mirror view, getevent parsing
/// or any other recorder, consumed by <see cref="Recording.GestureRecognizer"/>.
/// </summary>
/// <param name="TimestampMs">Monotonic milliseconds (any epoch, only differences matter).</param>
public readonly record struct RawTouchEvent(int PointerId, TouchPhase Phase, PointD Position, long TimestampMs);

/// <summary>A point along a gesture path, with its time offset from the gesture start.</summary>
public readonly record struct TimedPoint(double X, double Y, int OffsetMs)
{
    public PointD Position => new(X, Y);
}

/// <summary>Android key codes used by the UI and actions (subset).</summary>
public static class KeyCodes
{
    public const int Home = 3;
    public const int Back = 4;
    public const int VolumeUp = 24;
    public const int VolumeDown = 25;
    public const int Power = 26;
    public const int Enter = 66;
    public const int Delete = 67;
    public const int Menu = 82;
    public const int AppSwitch = 187;
    public const int Wakeup = 224;
    public const int Sleep = 223;
}

/// <summary>
/// The capability every input backend (scrcpy control channel, adb input, root sendevent)
/// implements. Coordinates are device pixels in the current orientation.
/// </summary>
public interface ITouchInjector
{
    /// <summary>Short identifier for logs ("scrcpy", "adb", "root").</summary>
    string Name { get; }

    /// <summary>True when the backend can hold several pointers down at once.</summary>
    bool SupportsMultiTouch { get; }

    Task TapAsync(PointI point, int durationMs, CancellationToken ct);

    /// <summary>Plays one continuous finger stroke along <paramref name="path"/> honouring each point's offset.</summary>
    Task StrokeAsync(IReadOnlyList<TimedPoint> path, CancellationToken ct);

    /// <summary>Plays several simultaneous strokes (fingers); backends without multi-touch play the first one.</summary>
    Task MultiStrokeAsync(IReadOnlyList<IReadOnlyList<TimedPoint>> fingers, CancellationToken ct);

    Task KeyAsync(int keyCode, CancellationToken ct);

    Task TextAsync(string text, CancellationToken ct);
}
