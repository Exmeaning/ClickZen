using System.Text.Json.Serialization;
using ClickZen.Core.Geometry;

namespace ClickZen.Core.Automation;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TapAction), "tap")]
[JsonDerivedType(typeof(LongPressAction), "longPress")]
[JsonDerivedType(typeof(SwipeAction), "swipe")]
[JsonDerivedType(typeof(KeyAction), "key")]
[JsonDerivedType(typeof(TextAction), "text")]
[JsonDerivedType(typeof(WaitAction), "wait")]
[JsonDerivedType(typeof(PlayRecordingAction), "playRecording")]
[JsonDerivedType(typeof(SetVariableAction), "setVariable")]
[JsonDerivedType(typeof(AdbShellAction), "adbShell")]
[JsonDerivedType(typeof(LogAction), "log")]
[JsonDerivedType(typeof(StopAction), "stop")]
public abstract class AutomationAction
{
    public bool Enabled { get; set; } = true;
    /// <summary>Pause after the action; null uses the scheme default.</summary>
    public int? DelayAfterMs { get; set; }
}

public enum TargetKind
{
    /// <summary>A fixed point.</summary>
    Point,
    /// <summary>The centre of the most recent image match in this rule (falls back to <see cref="Target.Point"/>).</summary>
    LastMatch,
    /// <summary>A random point inside <see cref="Target.Area"/>.</summary>
    RandomInArea,
}

/// <summary>Where a pointer action lands.</summary>
public sealed class Target
{
    public TargetKind Kind { get; set; } = TargetKind.Point;
    public PointI Point { get; set; }
    public RectI Area { get; set; }
    /// <summary>Offset added to the resolved point (useful with <see cref="TargetKind.LastMatch"/>).</summary>
    public PointI Offset { get; set; }

    public static Target At(int x, int y) => new() { Point = new PointI(x, y) };
}

public sealed class TapAction : AutomationAction
{
    public Target Target { get; set; } = new();
    public int Count { get; set; } = 1;
    public int IntervalMs { get; set; } = 120;
}

public sealed class LongPressAction : AutomationAction
{
    public Target Target { get; set; } = new();
    public int DurationMs { get; set; } = 800;
}

public sealed class SwipeAction : AutomationAction
{
    public Target From { get; set; } = new();
    public Target To { get; set; } = new();
    public int DurationMs { get; set; } = 300;
}

public sealed class KeyAction : AutomationAction
{
    public int KeyCode { get; set; } = Input.KeyCodes.Back;
}

public sealed class TextAction : AutomationAction
{
    /// <summary>Text to type; <c>{expr}</c> placeholders are evaluated.</summary>
    public string Text { get; set; } = "";
}

public sealed class WaitAction : AutomationAction
{
    public int MinMs { get; set; } = 1000;
    /// <summary>When greater than <see cref="MinMs"/>, a random wait in [Min, Max].</summary>
    public int MaxMs { get; set; }
}

public sealed class PlayRecordingAction : AutomationAction
{
    /// <summary>Id of a recording embedded in the scheme package (recordings/{id}.czrec), or a path relative to the scheme file.</summary>
    public string Recording { get; set; } = "";
    public double Speed { get; set; } = 1.0;
    public int Loops { get; set; } = 1;
}

public sealed class SetVariableAction : AutomationAction
{
    public string Variable { get; set; } = "";
    /// <summary>Expression whose result is assigned, e.g. <c>count + 1</c>.</summary>
    public string Expression { get; set; } = "0";
}

public sealed class AdbShellAction : AutomationAction
{
    public string Command { get; set; } = "";
    public bool AsRoot { get; set; }
    /// <summary>Optional variable receiving the trimmed output.</summary>
    public string? OutputVariable { get; set; }
}

public sealed class LogAction : AutomationAction
{
    /// <summary>Message; <c>{expr}</c> placeholders are evaluated.</summary>
    public string Message { get; set; } = "";
}

public enum StopScope
{
    /// <summary>Disable this task for the rest of the run.</summary>
    Task,
    /// <summary>Stop the whole scheme.</summary>
    Scheme,
}

public sealed class StopAction : AutomationAction
{
    public StopScope Scope { get; set; } = StopScope.Scheme;
}
