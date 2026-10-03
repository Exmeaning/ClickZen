using ClickZen.Core.Geometry;
using ClickZen.Core.Input;

namespace ClickZen.Device.Input;

/// <summary>
/// Turns raw kernel events of one touchscreen into <see cref="RawTouchEvent"/>s in device pixels of the
/// current orientation. Implements Linux multi-touch protocol B (slots + tracking ids, the norm on every
/// modern Android device) and, for devices without <c>ABS_MT_SLOT</c>, a best-effort protocol A
/// (<c>SYN_MT_REPORT</c>-separated anonymous contacts).
/// <para>
/// Slot state is accumulated between <c>SYN_REPORT</c>s and only emitted on a report, so a frame that
/// changes X and Y of a contact produces one move. The kernel suppresses unchanged values per slot, which
/// is why X/Y survive a lift: a new contact at the same column re-uses the previous X.
/// Events after <c>SYN_DROPPED</c> are discarded up to the next report.
/// </para>
/// Pointer ids are slot indices (protocol B) or the lowest free id (protocol A). Not thread-safe.
/// </summary>
public sealed class GeteventTouchDecoder
{
    /// <summary>Upper bound on tracked slots, whatever the device claims.</summary>
    public const int MaxSupportedSlots = 64;

    private static readonly IReadOnlyList<RawTouchEvent> None = [];

    private readonly InputDeviceInfo _device;
    private readonly SizeI _naturalSize;
    private readonly DisplayRotation _rotation;
    private readonly Func<long> _clockMs;
    private readonly Slot[] _slots;
    private readonly bool _protocolB;
    private int _currentSlot;
    private bool _dropping;
    private long _lastTimestampMs = long.MinValue;

    // Protocol A
    private readonly List<ContactA> _frameA = [];
    private ContactA _pendingA = ContactA.Empty;
    private readonly Dictionary<int, (int PointerId, PointD Position)> _activeA = new();

    /// <param name="device">The touchscreen (must report ABS_MT_POSITION_X/Y).</param>
    /// <param name="naturalSize">Screen size in the natural (rotation 0) orientation; empty keeps axis units.</param>
    /// <param name="rotation">Display rotation used to map natural coordinates to the current orientation.</param>
    /// <param name="clockMs">Clock for events without a <c>-t</c> timestamp. Defaults to <see cref="Environment.TickCount64"/>.</param>
    public GeteventTouchDecoder(InputDeviceInfo device, SizeI naturalSize, DisplayRotation rotation, Func<long>? clockMs = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!device.IsMultiTouch)
        {
            throw new ArgumentException("The input device does not report ABS_MT_POSITION_X/Y.", nameof(device));
        }

        _device = device;
        // Without a known screen size the coordinates stay in axis units (offset to start at 0).
        _naturalSize = naturalSize.IsEmpty ? new SizeI(Math.Max(1, device.X.Span), Math.Max(1, device.Y.Span)) : naturalSize;
        _rotation = rotation;
        _clockMs = clockMs ?? (() => Environment.TickCount64);
        _protocolB = device.HasSlots;
        var count = _protocolB ? Math.Clamp(device.MaxSlot + 1, 1, MaxSupportedSlots) : 0;
        _slots = new Slot[count];
        for (var i = 0; i < count; i++)
        {
            _slots[i] = new Slot();
        }
    }

    public InputDeviceInfo Device => _device;

    /// <summary>Screen size in the orientation of the emitted coordinates.</summary>
    public SizeI ScreenSize => RotationMapper.RotatedSize(_naturalSize, _rotation);

    public DisplayRotation Rotation => _rotation;

    /// <summary>True while at least one contact has been reported down and not yet lifted.</summary>
    public bool HasActiveContacts => _protocolB ? _slots.Any(s => s.Reported) : _activeA.Count > 0;

    /// <summary>Timestamp (ms) of the last processed event, or null before the first one.</summary>
    public long? LastTimestampMs => _lastTimestampMs == long.MinValue ? null : _lastTimestampMs;

    /// <summary>
    /// Processes one event of this device. Returns the pointer events completed by it – non-empty only for
    /// <c>SYN_REPORT</c>. Events of other devices (when the line carried a device path) are ignored.
    /// </summary>
    public IReadOnlyList<RawTouchEvent> Feed(GeteventEvent e)
    {
        if (e.DevicePath is not null && !string.Equals(e.DevicePath, _device.Path, StringComparison.Ordinal))
        {
            return None;
        }

        var ts = e.TimestampUs is { } us ? us / 1000 : _clockMs();
        _lastTimestampMs = _lastTimestampMs == long.MinValue ? ts : Math.Max(_lastTimestampMs, ts);

        if (e.Type == GeteventEvent.EvSyn)
        {
            switch (e.Code)
            {
                case GeteventEvent.SynDropped:
                    _dropping = true;
                    return None;
                case GeteventEvent.SynReport when _dropping:
                    _dropping = false;
                    DiscardPartialFrame();
                    return None;
                case GeteventEvent.SynReport:
                    return _protocolB ? ReportB(_lastTimestampMs) : ReportA(_lastTimestampMs);
                case GeteventEvent.SynMtReport when !_protocolB && !_dropping:
                    CommitContactA();
                    return None;
                default:
                    return None;
            }
        }

        if (_dropping || e.Type != GeteventEvent.EvAbs)
        {
            return None;
        }

        if (_protocolB)
        {
            AbsB(e.Code, e.Value);
        }
        else
        {
            AbsA(e.Code, e.Value);
        }

        return None;
    }

    /// <summary>Lifts every contact still down (e.g. when recording stops mid-gesture) at the last event time.</summary>
    public IReadOnlyList<RawTouchEvent> Finish()
    {
        var ts = LastTimestampMs ?? _clockMs();
        var output = new List<RawTouchEvent>();
        if (_protocolB)
        {
            for (var i = 0; i < _slots.Length; i++)
            {
                var s = _slots[i];
                if (s.Reported)
                {
                    output.Add(new RawTouchEvent(i, TouchPhase.Up, s.LastPosition, ts));
                }

                s.Reset();
            }
        }
        else
        {
            foreach (var (pointerId, position) in _activeA.Values.OrderBy(v => v.PointerId))
            {
                output.Add(new RawTouchEvent(pointerId, TouchPhase.Up, position, ts));
            }

            _activeA.Clear();
            _frameA.Clear();
            _pendingA = ContactA.Empty;
        }

        return output;
    }

    // ------------------------------------------------------------------ protocol B

    private void AbsB(int code, int value)
    {
        if (code == InputDeviceInfo.AbsMtSlot)
        {
            _currentSlot = value >= 0 && value < _slots.Length ? value : -1;
            return;
        }

        if (_currentSlot < 0)
        {
            return;
        }

        var slot = _slots[_currentSlot];
        switch (code)
        {
            case InputDeviceInfo.AbsMtTrackingId:
                if (value < 0)
                {
                    slot.TrackingId = -1;
                }
                else if (value != slot.TrackingId)
                {
                    if (slot.TrackingId >= 0 || slot.Reported)
                    {
                        // A new contact replaced the previous one without an explicit lift in between.
                        slot.Replaced = true;
                    }

                    slot.TrackingId = value;
                }

                break;
            case InputDeviceInfo.AbsMtPositionX:
                slot.RawX = value;
                slot.Changed = true;
                break;
            case InputDeviceInfo.AbsMtPositionY:
                slot.RawY = value;
                slot.Changed = true;
                break;
        }
    }

    private List<RawTouchEvent> ReportB(long ts)
    {
        var output = new List<RawTouchEvent>();

        // Lifts first, so a finger lifted and another put down in the same frame do not merge into one gesture.
        for (var i = 0; i < _slots.Length; i++)
        {
            var s = _slots[i];
            if (s.Reported && (s.TrackingId < 0 || s.Replaced))
            {
                // The last move of the frame (if any) belongs to the old contact unless it was replaced.
                var position = !s.Replaced && s.Changed && s.HasPosition ? Map(s.RawX!.Value, s.RawY!.Value) : s.LastPosition;
                output.Add(new RawTouchEvent(i, TouchPhase.Up, position, ts));
                s.Reported = false;
            }
        }

        for (var i = 0; i < _slots.Length; i++)
        {
            var s = _slots[i];
            var position = s.HasPosition ? Map(s.RawX!.Value, s.RawY!.Value) : s.LastPosition;
            if (s.TrackingId >= 0 && s.HasPosition)
            {
                if (!s.Reported)
                {
                    output.Add(new RawTouchEvent(i, TouchPhase.Down, position, ts));
                    s.Reported = true;
                    s.LastPosition = position;
                }
                else if (s.Changed && position != s.LastPosition)
                {
                    output.Add(new RawTouchEvent(i, TouchPhase.Move, position, ts));
                    s.LastPosition = position;
                }
            }

            s.Changed = false;
            s.Replaced = false;
        }

        return output;
    }

    // ------------------------------------------------------------------ protocol A

    private void AbsA(int code, int value)
    {
        switch (code)
        {
            case InputDeviceInfo.AbsMtTrackingId:
                _pendingA = _pendingA with { TrackingId = value };
                break;
            case InputDeviceInfo.AbsMtPositionX:
                _pendingA = _pendingA with { RawX = value };
                break;
            case InputDeviceInfo.AbsMtPositionY:
                _pendingA = _pendingA with { RawY = value };
                break;
        }
    }

    private void CommitContactA()
    {
        if (_pendingA is { RawX: not null, RawY: not null } && _pendingA.TrackingId is not < 0)
        {
            _frameA.Add(_pendingA);
        }

        _pendingA = ContactA.Empty;
    }

    private List<RawTouchEvent> ReportA(long ts)
    {
        // A trailing contact without SYN_MT_REPORT still counts.
        CommitContactA();
        var output = new List<RawTouchEvent>();
        var useTrackingIds = _frameA.Count > 0 && _frameA.All(c => c.TrackingId is not null);
        var seen = new HashSet<int>();
        var current = new List<(int Key, PointD Position)>();
        for (var i = 0; i < _frameA.Count; i++)
        {
            var c = _frameA[i];
            var key = useTrackingIds ? c.TrackingId!.Value : i;
            if (seen.Add(key))
            {
                current.Add((key, Map(c.RawX!.Value, c.RawY!.Value)));
            }
        }

        _frameA.Clear();

        foreach (var key in _activeA.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            var (pointerId, position) = _activeA[key];
            output.Add(new RawTouchEvent(pointerId, TouchPhase.Up, position, ts));
            _activeA.Remove(key);
        }

        foreach (var (key, position) in current)
        {
            if (_activeA.TryGetValue(key, out var active))
            {
                if (active.Position != position)
                {
                    output.Add(new RawTouchEvent(active.PointerId, TouchPhase.Move, position, ts));
                    _activeA[key] = (active.PointerId, position);
                }
            }
            else
            {
                var pointerId = 0;
                while (_activeA.Values.Any(v => v.PointerId == pointerId))
                {
                    pointerId++;
                }

                _activeA[key] = (pointerId, position);
                output.Add(new RawTouchEvent(pointerId, TouchPhase.Down, position, ts));
            }
        }

        return output;
    }

    // ------------------------------------------------------------------ helpers

    private void DiscardPartialFrame()
    {
        foreach (var s in _slots)
        {
            s.Changed = false;
        }

        _frameA.Clear();
        _pendingA = ContactA.Empty;
    }

    private PointD Map(int rawX, int rawY)
    {
        var x = _device.X;
        var y = _device.Y;
        var natural = new PointD(
            RotationMapper.AxisToPixels(rawX, x.Min, x.Max, _naturalSize.Width),
            RotationMapper.AxisToPixels(rawY, y.Min, y.Max, _naturalSize.Height));
        return RotationMapper.NaturalToRotated(natural, _naturalSize, _rotation);
    }

    private sealed class Slot
    {
        public int TrackingId = -1;
        public int? RawX;
        public int? RawY;
        public bool Changed;
        public bool Replaced;
        public bool Reported;
        public PointD LastPosition;

        public bool HasPosition => RawX is not null && RawY is not null;

        public void Reset()
        {
            TrackingId = -1;
            Changed = false;
            Replaced = false;
            Reported = false;
        }
    }

    private readonly record struct ContactA(int? TrackingId, int? RawX, int? RawY)
    {
        public static ContactA Empty => default;
    }
}
