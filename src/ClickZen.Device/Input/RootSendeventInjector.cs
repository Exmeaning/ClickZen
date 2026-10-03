using System.Globalization;
using System.Text;
using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Device.Adb;

namespace ClickZen.Device.Input;

/// <summary>
/// Root-only injector writing raw multi-touch events (Linux MT protocol B) to the touchscreen node
/// with <c>sendevent</c> under <c>su</c>. Supports real multi-touch and does not depend on the
/// Android input stack, at the cost of needing root and one su invocation per gesture.
/// Device-pixel coordinates are converted to the panel's natural orientation and axis range.
/// </summary>
public sealed class RootSendeventInjector : ITouchInjector
{
    private const int EvSyn = 0, EvKey = 1, EvAbs = 3;
    private const int SynReport = 0;

    private readonly Func<string, CancellationToken, Task<string>> _rootShell;
    private readonly InputDeviceInfo _touch;
    private readonly Func<SizeI> _naturalSize;
    private readonly Func<DisplayRotation> _rotation;
    private int _nextTrackingId = 100;

    /// <param name="rootShell">Runs a command as root (the command is passed to <c>su -c</c>).</param>
    /// <param name="touch">Touchscreen picked by <see cref="GeteventDeviceParser.PickTouchscreen"/>.</param>
    /// <param name="naturalSize">Screen size in the natural (rotation 0) orientation.</param>
    /// <param name="rotation">Current display rotation.</param>
    public RootSendeventInjector(Func<string, CancellationToken, Task<string>> rootShell, InputDeviceInfo touch,
        Func<SizeI> naturalSize, Func<DisplayRotation> rotation)
    {
        if (!touch.IsMultiTouch)
        {
            throw new ArgumentException("The input device does not report ABS_MT_POSITION_X/Y.", nameof(touch));
        }

        _rootShell = rootShell;
        _touch = touch;
        _naturalSize = naturalSize;
        _rotation = rotation;
    }

    /// <summary>Probes the device: finds the touchscreen with <c>getevent -p</c>. Null if there is none.</summary>
    public static async Task<InputDeviceInfo?> DetectTouchscreenAsync(AdbService adb, string serial, CancellationToken ct)
    {
        var output = await adb.RootShellAsync(serial, "getevent -p", ct);
        return GeteventDeviceParser.PickTouchscreen(GeteventDeviceParser.Parse(output));
    }

    public string Name => "root";

    public bool SupportsMultiTouch => _touch.HasSlots;

    public InputDeviceInfo Device => _touch;

    public Task TapAsync(PointI point, int durationMs, CancellationToken ct) =>
        Run(BuildScript([[new TimedPoint(point.X, point.Y, 0), new TimedPoint(point.X, point.Y, Math.Max(1, durationMs))]]), ct);

    public Task StrokeAsync(IReadOnlyList<TimedPoint> path, CancellationToken ct) =>
        path.Count == 0 ? Task.CompletedTask : Run(BuildScript([path]), ct);

    public Task MultiStrokeAsync(IReadOnlyList<IReadOnlyList<TimedPoint>> fingers, CancellationToken ct)
    {
        var usable = fingers.Where(f => f.Count > 0).Take(SupportsMultiTouch ? _touch.MaxSlot + 1 : 1).ToArray();
        return usable.Length == 0 ? Task.CompletedTask : Run(BuildScript(usable), ct);
    }

    public Task KeyAsync(int keyCode, CancellationToken ct) =>
        _rootShell("input keyevent " + keyCode.ToString(CultureInfo.InvariantCulture), ct);

    public Task TextAsync(string text, CancellationToken ct) =>
        string.IsNullOrEmpty(text) ? Task.CompletedTask : _rootShell("input text " + AdbInputInjector.EscapeInputText(text), ct);

    private Task Run(string script, CancellationToken ct) => _rootShell(script, ct);

    // ------------------------------------------------------------------ script building (pure, tested)

    /// <summary>
    /// Builds one shell script that replays all fingers on a shared timeline:
    /// per event batch "sendevent dev 3 47 slot; 3 57 id; 3 53 x; 3 54 y; [1 330 1]; 0 0 0", with sleeps
    /// between batches. Lifting writes tracking id -1 (0xffffffff as a signed int).
    /// </summary>
    internal string BuildScript(IReadOnlyList<IReadOnlyList<TimedPoint>> fingers)
    {
        var dev = _touch.Path;
        var natural = _naturalSize();
        var rotation = _rotation();
        var steps = new List<(int At, int Finger, int Kind, PointD P)>(); // Kind: 0 down, 1 move, 2 up
        var origin = fingers.Min(f => f[0].OffsetMs);
        for (var f = 0; f < fingers.Count; f++)
        {
            var path = Trajectory.ResampleByTime(fingers[f], 16);
            steps.Add((path[0].OffsetMs - origin, f, 0, path[0].Position));
            for (var i = 1; i < path.Count; i++)
            {
                steps.Add((path[i].OffsetMs - origin, f, 1, path[i].Position));
            }

            steps.Add((path[^1].OffsetMs - origin, f, 2, path[^1].Position));
        }

        steps = steps.OrderBy(s => s.At).ThenBy(s => s.Kind).ToList();
        var ids = new int[fingers.Count];
        var down = 0;
        var sb = new StringBuilder();
        var lastAt = 0;
        var batchOpen = false;

        void Cmd(int type, int code, int value)
        {
            if (sb.Length > 0)
            {
                sb.Append(';');
            }

            sb.Append(CultureInfo.InvariantCulture, $"sendevent {dev} {type} {code} {value}");
        }

        void Sync()
        {
            Cmd(EvSyn, SynReport, 0);
            batchOpen = false;
        }

        foreach (var (at, finger, kind, p) in steps)
        {
            if (at > lastAt)
            {
                if (batchOpen)
                {
                    Sync();
                }

                var gap = at - lastAt;
                if (gap >= 5)
                {
                    sb.Append(CultureInfo.InvariantCulture, $";sleep {gap / 1000.0:0.###}");
                }

                lastAt = at;
            }

            if (_touch.HasSlots)
            {
                Cmd(EvAbs, InputDeviceInfo.AbsMtSlot, finger);
            }

            switch (kind)
            {
                case 0:
                    ids[finger] = _nextTrackingId++ & 0xFFFF;
                    Cmd(EvAbs, InputDeviceInfo.AbsMtTrackingId, ids[finger]);
                    WritePosition(p);
                    if (_touch.Axes.ContainsKey(InputDeviceInfo.AbsMtPressure))
                    {
                        Cmd(EvAbs, InputDeviceInfo.AbsMtPressure, Math.Max(1, _touch.Axes[InputDeviceInfo.AbsMtPressure].Max / 2));
                    }

                    if (down++ == 0 && _touch.Keys.Contains(InputDeviceInfo.BtnTouch))
                    {
                        Cmd(EvKey, InputDeviceInfo.BtnTouch, 1);
                    }

                    break;
                case 1:
                    WritePosition(p);
                    break;
                default:
                    Cmd(EvAbs, InputDeviceInfo.AbsMtTrackingId, -1);
                    if (--down == 0 && _touch.Keys.Contains(InputDeviceInfo.BtnTouch))
                    {
                        Cmd(EvKey, InputDeviceInfo.BtnTouch, 0);
                    }

                    break;
            }

            batchOpen = true;
        }

        if (batchOpen)
        {
            Sync();
        }

        return sb.ToString();

        void WritePosition(PointD device)
        {
            var nat = RotationMapper.RotatedToNatural(device, natural, rotation);
            Cmd(EvAbs, InputDeviceInfo.AbsMtPositionX, RotationMapper.PixelsToAxis(nat.X, _touch.X.Min, _touch.X.Max, natural.Width));
            Cmd(EvAbs, InputDeviceInfo.AbsMtPositionY, RotationMapper.PixelsToAxis(nat.Y, _touch.Y.Min, _touch.Y.Max, natural.Height));
        }
    }
}
