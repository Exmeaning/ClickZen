using System.Globalization;
using System.Text;
using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Device.Adb;

namespace ClickZen.Device.Input;

/// <summary>
/// Root-only injector writing raw multi-touch events (Linux MT protocol B) to the touchscreen node under
/// <c>su</c>. Supports real multi-touch and does not depend on the Android input stack, at the cost of needing
/// root and one su invocation per gesture. Device-pixel coordinates are converted to the panel's natural
/// orientation and axis range.
/// <para>
/// Two script flavours: <c>sendevent</c> (one process per event – portable but ~50 ms each, so a 400 ms swipe
/// takes seconds) and, when the device's <c>sh</c> is mksh on a 64-bit kernel, binary <c>input_event</c>
/// records written by the shell builtin <c>print</c> to one open descriptor, with sleeps done by builtin
/// <c>read -t</c> on a FIFO – no process per event, so gestures keep their real timing.
/// </para>
/// </summary>
public sealed class RootSendeventInjector : ITouchInjector
{
    private const int EvSyn = 0, EvKey = 1, EvAbs = 3;
    private const int SynReport = 0;

    /// <summary>Prints "mksh64" when raw event writing is possible (mksh + 64-bit userland).</summary>
    internal const string ProbeCommand = "[ -n \"$KSH_VERSION\" ] && [ \"$(od -An -tx1 -j4 -N1 /system/bin/sh)\" = \" 02\" ] && echo mksh64 || echo sendevent";

    private readonly Func<string, CancellationToken, Task<string>> _rootShell;
    private readonly InputDeviceInfo _touch;
    private readonly Func<SizeI> _naturalSize;
    private readonly Func<DisplayRotation> _rotation;
    private int _nextTrackingId = 100;

    /// <param name="rootShell">Runs a command as root (the command is passed to <c>su -c</c>).</param>
    /// <param name="touch">Touchscreen picked by <see cref="GeteventDeviceParser.PickTouchscreen"/>.</param>
    /// <param name="naturalSize">Screen size in the natural (rotation 0) orientation.</param>
    /// <param name="rotation">Current display rotation.</param>
    /// <param name="rawEvents">Write binary input_event records (see the class remarks); false = sendevent.</param>
    public RootSendeventInjector(Func<string, CancellationToken, Task<string>> rootShell, InputDeviceInfo touch,
        Func<SizeI> naturalSize, Func<DisplayRotation> rotation, bool rawEvents = false)
    {
        if (!touch.IsMultiTouch)
        {
            throw new ArgumentException("The input device does not report ABS_MT_POSITION_X/Y.", nameof(touch));
        }

        _rootShell = rootShell;
        _touch = touch;
        _naturalSize = naturalSize;
        _rotation = rotation;
        RawEvents = rawEvents;
    }

    /// <summary>Probes the device: finds the touchscreen with <c>getevent -p</c>. Null if there is none.</summary>
    public static async Task<InputDeviceInfo?> DetectTouchscreenAsync(AdbService adb, string serial, CancellationToken ct)
    {
        var output = await adb.RootShellAsync(serial, "getevent -p", ct);
        return GeteventDeviceParser.PickTouchscreen(GeteventDeviceParser.Parse(output));
    }

    /// <summary>Whether the device can take raw events written by the shell (mksh on a 64-bit kernel).</summary>
    public static async Task<bool> SupportsRawEventsAsync(AdbService adb, string serial, CancellationToken ct)
    {
        try
        {
            var output = await adb.RootShellAsync(serial, ProbeCommand, ct);
            return output.Contains("mksh64", StringComparison.Ordinal);
        }
        catch (AdbException)
        {
            return false;
        }
    }

    public string Name => RawEvents ? "root" : "root (sendevent)";

    public bool SupportsMultiTouch => _touch.HasSlots;

    public InputDeviceInfo Device => _touch;

    /// <summary>True when gestures are written as binary events (fast path).</summary>
    public bool RawEvents { get; }

    public Task TapAsync(PointI point, int durationMs, CancellationToken ct) =>
        Run([[new TimedPoint(point.X, point.Y, 0), new TimedPoint(point.X, point.Y, Math.Max(1, durationMs))]], ct);

    public Task StrokeAsync(IReadOnlyList<TimedPoint> path, CancellationToken ct) =>
        path.Count == 0 ? Task.CompletedTask : Run([path], ct);

    public Task MultiStrokeAsync(IReadOnlyList<IReadOnlyList<TimedPoint>> fingers, CancellationToken ct)
    {
        var usable = fingers.Where(f => f.Count > 0).Take(SupportsMultiTouch ? _touch.MaxSlot + 1 : 1).ToArray();
        return usable.Length == 0 ? Task.CompletedTask : Run(usable, ct);
    }

    public Task KeyAsync(int keyCode, CancellationToken ct) =>
        _rootShell("input keyevent " + keyCode.ToString(CultureInfo.InvariantCulture), ct);

    public Task TextAsync(string text, CancellationToken ct) =>
        string.IsNullOrEmpty(text) ? Task.CompletedTask : _rootShell("input text " + AdbInputInjector.EscapeInputText(text), ct);

    private Task Run(IReadOnlyList<IReadOnlyList<TimedPoint>> fingers, CancellationToken ct) =>
        _rootShell(RawEvents ? BuildRawScript(fingers) : BuildScript(fingers), ct);

    // ------------------------------------------------------------------ script building (pure, tested)

    /// <summary>One step of the gesture timeline: a batch of events at <paramref name="AtMs"/>, or a pause.</summary>
    internal readonly record struct TimelineItem(int AtMs, int SleepMs, IReadOnlyList<(int Type, int Code, int Value)> Events);

    /// <summary>
    /// Builds one shell script that replays all fingers on a shared timeline:
    /// per event batch "sendevent dev 3 47 slot; 3 57 id; 3 53 x; 3 54 y; [1 330 1]; 0 0 0", with sleeps
    /// between batches. Lifting writes tracking id -1 (0xffffffff as a signed int).
    /// </summary>
    internal string BuildScript(IReadOnlyList<IReadOnlyList<TimedPoint>> fingers)
    {
        var dev = _touch.Path;
        var sb = new StringBuilder();
        foreach (var item in Timeline(fingers))
        {
            if (item.SleepMs > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $";sleep {item.SleepMs / 1000.0:0.###}");
                continue;
            }

            foreach (var (type, code, value) in item.Events)
            {
                if (sb.Length > 0)
                {
                    sb.Append(';');
                }

                sb.Append(CultureInfo.InvariantCulture, $"sendevent {dev} {type} {code} {value}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Same timeline as <see cref="BuildScript"/>, written as binary <c>struct input_event</c> records (64-bit:
    /// 16-byte zero timestamp, u16 type, u16 code, s32 value, little endian) by mksh's builtin <c>print</c> to
    /// one descriptor, so each batch is a single write the kernel stamps itself. Pauses use builtin
    /// <c>read -t</c> on a private FIFO (falls back to <c>sleep</c> if no FIFO can be made).
    /// </summary>
    internal string BuildRawScript(IReadOnlyList<IReadOnlyList<TimedPoint>> fingers)
    {
        var sb = new StringBuilder();
        sb.Append("f=/data/local/tmp/.czev$$;if mkfifo $f 2>/dev/null&&exec 3<>$f;then rm -f $f;w(){ read -t $1 -u3 _;};else rm -f $f;w(){ sleep $1;};fi;");
        sb.Append(CultureInfo.InvariantCulture, $"exec 4>{_touch.Path}||exit 1;z='\\0\\0\\0\\0\\0\\0\\0\\0\\0\\0\\0\\0\\0\\0\\0\\0'");
        foreach (var item in Timeline(fingers))
        {
            if (item.SleepMs > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $";w {item.SleepMs / 1000.0:0.###}");
                continue;
            }

            sb.Append(";print -n \"");
            foreach (var (type, code, value) in item.Events)
            {
                sb.Append("$z");
                AppendLe(sb, type, 2);
                AppendLe(sb, code, 2);
                AppendLe(sb, value, 4);
            }

            sb.Append("\">&4");
        }

        sb.Append(";exec 4>&- 3>&-");
        return sb.ToString();

        static void AppendLe(StringBuilder sb, int value, int bytes)
        {
            var u = unchecked((uint)value);
            for (var i = 0; i < bytes; i++)
            {
                var b = (u >> (8 * i)) & 0xFF;
                // \0 for zero keeps scripts short; \xHH is mksh's escape (two hex digits, no ambiguity).
                sb.Append(b == 0 ? "\\0" : string.Create(CultureInfo.InvariantCulture, $"\\x{b:x2}"));
            }
        }
    }

    /// <summary>
    /// The gesture as event batches (each ends with SYN_REPORT) and pauses of at least 5 ms between them.
    /// Fingers are resampled to 16 ms and merged on one clock; ids, slots, BTN_TOUCH and axes as in protocol B.
    /// </summary>
    internal IEnumerable<TimelineItem> Timeline(IReadOnlyList<IReadOnlyList<TimedPoint>> fingers)
    {
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
        var lastAt = 0;
        var batch = new List<(int, int, int)>();

        foreach (var (at, finger, kind, p) in steps)
        {
            if (at > lastAt)
            {
                if (batch.Count > 0)
                {
                    batch.Add((EvSyn, SynReport, 0));
                    yield return new TimelineItem(lastAt, 0, batch);
                    batch = [];
                }

                var gap = at - lastAt;
                if (gap >= 5)
                {
                    yield return new TimelineItem(lastAt, gap, []);
                }

                lastAt = at;
            }

            if (_touch.HasSlots)
            {
                batch.Add((EvAbs, InputDeviceInfo.AbsMtSlot, finger));
            }

            switch (kind)
            {
                case 0:
                    ids[finger] = _nextTrackingId++ & 0xFFFF;
                    batch.Add((EvAbs, InputDeviceInfo.AbsMtTrackingId, ids[finger]));
                    AddPosition(batch, p);
                    if (_touch.Axes.ContainsKey(InputDeviceInfo.AbsMtPressure))
                    {
                        batch.Add((EvAbs, InputDeviceInfo.AbsMtPressure, Math.Max(1, _touch.Axes[InputDeviceInfo.AbsMtPressure].Max / 2)));
                    }

                    if (down++ == 0 && _touch.Keys.Contains(InputDeviceInfo.BtnTouch))
                    {
                        batch.Add((EvKey, InputDeviceInfo.BtnTouch, 1));
                    }

                    break;
                case 1:
                    AddPosition(batch, p);
                    break;
                default:
                    batch.Add((EvAbs, InputDeviceInfo.AbsMtTrackingId, -1));
                    if (--down == 0 && _touch.Keys.Contains(InputDeviceInfo.BtnTouch))
                    {
                        batch.Add((EvKey, InputDeviceInfo.BtnTouch, 0));
                    }

                    break;
            }
        }

        if (batch.Count > 0)
        {
            batch.Add((EvSyn, SynReport, 0));
            yield return new TimelineItem(lastAt, 0, batch);
        }

        void AddPosition(List<(int, int, int)> b, PointD device)
        {
            var nat = RotationMapper.RotatedToNatural(device, natural, rotation);
            b.Add((EvAbs, InputDeviceInfo.AbsMtPositionX, RotationMapper.PixelsToAxis(nat.X, _touch.X.Min, _touch.X.Max, natural.Width)));
            b.Add((EvAbs, InputDeviceInfo.AbsMtPositionY, RotationMapper.PixelsToAxis(nat.Y, _touch.Y.Min, _touch.Y.Max, natural.Height)));
        }
    }
    }
