using System.Globalization;
using System.Text;
using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Device.Adb;

namespace ClickZen.Device.Input;

/// <summary>
/// Fallback injector over <c>adb shell input</c>. Works on any device but each call costs one adb
/// round-trip (tens of milliseconds). On Android 11+ (API 30) strokes are played as one continuous
/// gesture with <c>input motionevent DOWN/MOVE/UP</c> in a single shell command – the 1.x version
/// chopped trajectories into separate <c>input swipe</c> calls, lifting the finger between segments.
/// Multi-touch is not possible through <c>input</c>; only the first finger is played.
/// </summary>
public sealed class AdbInputInjector : ITouchInjector
{
    /// <summary>API level that added <c>input -d &lt;displayId&gt;</c>.</summary>
    public const int DisplayIdMinSdk = 29;

    /// <summary>API level that added <c>input motionevent</c>.</summary>
    public const int MotionEventMinSdk = 30;

    private readonly Func<string, CancellationToken, Task<string>> _shell;
    private readonly int _sdk;
    private readonly Func<int>? _displayId;

    /// <param name="shell">Runs a shell command on the device (e.g. <see cref="AdbService.ShellAsync(string, string, CancellationToken)"/> bound to a serial).</param>
    /// <param name="sdkLevel">Android API level of the device.</param>
    /// <param name="displayId">Optional active Android display id provider (&gt; 0 routes commands via <c>input -d &lt;id&gt;</c>).</param>
    public AdbInputInjector(Func<string, CancellationToken, Task<string>> shell, int sdkLevel, Func<int>? displayId = null)
    {
        _shell = shell;
        _sdk = sdkLevel;
        _displayId = displayId;
    }

    public static AdbInputInjector For(AdbService adb, string serial, int sdkLevel, bool asRoot = false, Func<int>? displayId = null) =>
        new(asRoot ? (cmd, ct) => adb.RootShellAsync(serial, cmd, ct) : (cmd, ct) => adb.ShellAsync(serial, cmd, ct), sdkLevel, displayId);

    public string Name => "adb";

    public bool SupportsMultiTouch => false;

    private int TargetDisplayId => _sdk >= DisplayIdMinSdk && _displayId is not null ? Math.Max(0, _displayId()) : 0;

    public Task TapAsync(PointI point, int durationMs, CancellationToken ct) =>
        _shell(BuildTap(point, durationMs, TargetDisplayId), ct);

    public Task StrokeAsync(IReadOnlyList<TimedPoint> path, CancellationToken ct) =>
        path.Count == 0 ? Task.CompletedTask : _shell(BuildStroke(path, _sdk, TargetDisplayId), ct);

    public Task MultiStrokeAsync(IReadOnlyList<IReadOnlyList<TimedPoint>> fingers, CancellationToken ct) =>
        fingers.Count == 0 ? Task.CompletedTask : StrokeAsync(fingers[0], ct);

    public Task KeyAsync(int keyCode, CancellationToken ct) =>
        _shell(InputPrefix(TargetDisplayId) + " keyevent " + keyCode.ToString(CultureInfo.InvariantCulture), ct);

    public Task TextAsync(string text, CancellationToken ct) =>
        string.IsNullOrEmpty(text) ? Task.CompletedTask : _shell(InputPrefix(TargetDisplayId) + " text " + EscapeInputText(text), ct);

    // ------------------------------------------------------------------ command builders (pure, tested)

    private static string InputPrefix(int displayId) =>
        displayId > 0 ? string.Create(CultureInfo.InvariantCulture, $"input -d {displayId}") : "input";

    internal static string BuildTap(PointI p, int durationMs, int displayId = 0) =>
        durationMs <= 100
            ? string.Create(CultureInfo.InvariantCulture, $"{InputPrefix(displayId)} tap {p.X} {p.Y}")
            : string.Create(CultureInfo.InvariantCulture, $"{InputPrefix(displayId)} swipe {p.X} {p.Y} {p.X} {p.Y} {durationMs}");

    internal static string BuildStroke(IReadOnlyList<TimedPoint> path, int sdk, int displayId = 0)
    {
        var prefix = InputPrefix(displayId);
        var first = path[0];
        var last = path[^1];
        var duration = Math.Max(1, last.OffsetMs - first.OffsetMs);
        if (sdk < MotionEventMinSdk || path.Count == 1)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"{prefix} swipe {R(first.X)} {R(first.Y)} {R(last.X)} {R(last.Y)} {duration}");
        }

        // One shell invocation: DOWN, MOVEs at ~16 ms cadence with sleeps, UP.
        var points = Trajectory.ResampleByTime(path, 16);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{prefix} motionevent DOWN {R(first.X)} {R(first.Y)}");
        var prevT = first.OffsetMs;
        for (var i = 1; i < points.Count; i++)
        {
            var p = points[i];
            var gap = p.OffsetMs - prevT;
            if (gap >= 5)
            {
                sb.Append(CultureInfo.InvariantCulture, $"; sleep {gap / 1000.0:0.###}");
            }

            sb.Append(CultureInfo.InvariantCulture, $"; {prefix} motionevent MOVE {R(p.X)} {R(p.Y)}");
            prevT = p.OffsetMs;
        }

        sb.Append(CultureInfo.InvariantCulture, $"; {prefix} motionevent UP {R(last.X)} {R(last.Y)}");
        return sb.ToString();
    }

    /// <summary>
    /// <c>input text</c> treats spaces as argument separators and %s as a space; shell metacharacters
    /// must be escaped. Non-ASCII text cannot be typed through <c>input text</c> at all.
    /// </summary>
    internal static string EscapeInputText(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        foreach (var c in text)
        {
            switch (c)
            {
                case ' ':
                    sb.Append("%s");
                    break;
                case '\'' or '"' or '\\' or '`' or '$' or '&' or '|' or ';' or '<' or '>' or '(' or ')' or '*' or '?' or '~' or '#' or '!' or '[' or ']' or '{' or '}':
                    sb.Append('\\').Append(c);
                    break;
                case '%':
                    sb.Append("\\%");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    private static int R(double v) => (int)Math.Round(v);
}
