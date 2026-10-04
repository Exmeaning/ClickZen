using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Device.Scrcpy;
using ClickZen.Device.Scrcpy.Protocol;

namespace ClickZen.App.Services;

/// <summary>
/// Live input from the mirror view: one call per pointer event (device pixels of the shown device),
/// plus keys, text and wheel scrolling.
/// </summary>
public interface IMirrorInput
{
    Task TouchAsync(TouchPhase phase, int finger, PointI device, CancellationToken ct);

    Task KeyAsync(int keyCode, CancellationToken ct);

    Task TextAsync(string text, CancellationToken ct);

    Task ScrollAsync(PointI device, float hscroll, float vscroll, CancellationToken ct);
}

/// <summary>Mirror input over a scrcpy control channel: every pointer event is forwarded as it happens.</summary>
public sealed class ScrcpyMirrorInput(ScrcpyTouchInjector injector) : IMirrorInput
{
    public Task TouchAsync(TouchPhase phase, int finger, PointI device, CancellationToken ct) =>
        injector.SendRawTouchAsync(phase switch
        {
            TouchPhase.Down => MotionAction.Down,
            TouchPhase.Up => MotionAction.Up,
            _ => MotionAction.Move,
        }, finger, device, ct);

    public Task KeyAsync(int keyCode, CancellationToken ct) => injector.KeyAsync(keyCode, ct);

    public Task TextAsync(string text, CancellationToken ct) => injector.TextAsync(text, ct);

    public Task ScrollAsync(PointI device, float hscroll, float vscroll, CancellationToken ct) =>
        injector.ScrollAsync(device, hscroll, vscroll, ct);
}

/// <summary>
/// Mirror input over any <see cref="ITouchInjector"/> (used by emulator-window devices, whose input goes through
/// the linked adb device). Backends such as <c>adb shell input</c> cannot hold a finger down between calls, so each
/// finger's events are buffered and replayed on release: a short, still press becomes a tap, a still press of
/// 300 ms or more a long press, anything else a timed stroke along the recorded path.
/// </summary>
public sealed class InjectorMirrorInput : IMirrorInput
{
    private const int TapSlopPx = 12;
    private const int LongPressMs = 300;

    private readonly Func<CancellationToken, Task<ITouchInjector>> _resolve;
    private readonly Func<SizeI> _screen;
    private readonly Dictionary<int, (long Start, List<TimedPoint> Path)> _strokes = new();

    /// <param name="resolve">Returns the injector to use (called per gesture, so a newly linked device is picked up).</param>
    /// <param name="screen">Device screen size (for the scroll distance).</param>
    public InjectorMirrorInput(Func<CancellationToken, Task<ITouchInjector>> resolve, Func<SizeI> screen)
    {
        _resolve = resolve;
        _screen = screen;
    }

    public Task TouchAsync(TouchPhase phase, int finger, PointI device, CancellationToken ct)
    {
        var now = Environment.TickCount64;
        switch (phase)
        {
            case TouchPhase.Down:
                _strokes[finger] = (now, [new TimedPoint(device.X, device.Y, 0)]);
                return Task.CompletedTask;
            case TouchPhase.Move:
                if (_strokes.TryGetValue(finger, out var s))
                {
                    s.Path.Add(new TimedPoint(device.X, device.Y, (int)(now - s.Start)));
                }

                return Task.CompletedTask;
            default:
                if (!_strokes.Remove(finger, out var stroke))
                {
                    return Task.CompletedTask;
                }

                stroke.Path.Add(new TimedPoint(device.X, device.Y, (int)(now - stroke.Start)));
                return ReplayAsync(stroke.Path, ct);
        }
    }

    /// <summary>The gesture a buffered path stands for (pure; unit-test friendly).</summary>
    internal static (bool IsTap, PointI Point, int DurationMs) Classify(IReadOnlyList<TimedPoint> path)
    {
        var first = path[0];
        var duration = Math.Max(1, path[^1].OffsetMs - first.OffsetMs);
        var still = path.All(p => Math.Abs(p.X - first.X) <= TapSlopPx && Math.Abs(p.Y - first.Y) <= TapSlopPx);
        var point = new PointI((int)Math.Round(first.X), (int)Math.Round(first.Y));
        return still ? (true, point, duration < LongPressMs ? 50 : duration) : (false, point, duration);
    }

    private async Task ReplayAsync(List<TimedPoint> path, CancellationToken ct)
    {
        var injector = await _resolve(ct);
        var (isTap, point, duration) = Classify(path);
        if (isTap)
        {
            await injector.TapAsync(point, duration, ct);
        }
        else
        {
            await injector.StrokeAsync(path, ct);
        }
    }

    public async Task KeyAsync(int keyCode, CancellationToken ct) => await (await _resolve(ct)).KeyAsync(keyCode, ct);

    public async Task TextAsync(string text, CancellationToken ct) => await (await _resolve(ct)).TextAsync(text, ct);

    /// <summary>One wheel notch = a quick swipe of an eighth of the screen height (wheel up scrolls content down).</summary>
    public async Task ScrollAsync(PointI device, float hscroll, float vscroll, CancellationToken ct)
    {
        var screen = _screen();
        var step = Math.Max(40, (screen.IsEmpty ? 1600 : Math.Max(screen.Width, screen.Height)) / 8.0);
        var dx = Math.Clamp(hscroll, -3, 3) * step;
        var dy = Math.Clamp(vscroll, -3, 3) * step;
        var end = new PointD(device.X + dx, device.Y + dy);
        if (!screen.IsEmpty)
        {
            end = new PointD(Math.Clamp(end.X, 0, screen.Width - 1), Math.Clamp(end.Y, 0, screen.Height - 1));
        }

        var injector = await _resolve(ct);
        await injector.StrokeAsync([new TimedPoint(device.X, device.Y, 0), new TimedPoint(end.X, end.Y, 150)], ct);
    }
}

/// <summary>
/// Mirror input of an emulator-window device. When the linked adb device has a live scrcpy control channel
/// (and the user prefers it) every pointer event is forwarded immediately, rescaled from the window device's
/// screen to the adb device's screen; otherwise gestures go through <paramref name="fallback"/> (buffered replay).
/// The choice is made per gesture (at finger down), so a finger never switches backend mid-stroke.
/// </summary>
public sealed class WindowMirrorInput(
    Func<ScrcpyTouchInjector?> live, Func<SizeI> from, Func<SizeI> to, IMirrorInput fallback) : IMirrorInput
{
    private readonly Dictionary<int, ScrcpyTouchInjector?> _fingerLive = new();

    private PointI Scale(PointI p) =>
        ScaledTouchInjector.Factors(from(), to()) is { } f
            ? new PointI((int)Math.Round((double)p.X * f.To.Width / f.From.Width), (int)Math.Round((double)p.Y * f.To.Height / f.From.Height))
            : p;

    public Task TouchAsync(TouchPhase phase, int finger, PointI device, CancellationToken ct)
    {
        if (phase == TouchPhase.Down)
        {
            _fingerLive[finger] = live();
        }

        var injector = _fingerLive.GetValueOrDefault(finger);
        if (phase == TouchPhase.Up)
        {
            _fingerLive.Remove(finger);
        }

        return injector is null
            ? fallback.TouchAsync(phase, finger, device, ct)
            : new ScrcpyMirrorInput(injector).TouchAsync(phase, finger, Scale(device), ct);
    }

    public Task KeyAsync(int keyCode, CancellationToken ct) =>
        live() is { } injector ? injector.KeyAsync(keyCode, ct) : fallback.KeyAsync(keyCode, ct);

    public Task TextAsync(string text, CancellationToken ct) =>
        live() is { } injector ? injector.TextAsync(text, ct) : fallback.TextAsync(text, ct);

    public Task ScrollAsync(PointI device, float hscroll, float vscroll, CancellationToken ct) =>
        live() is { } injector ? injector.ScrollAsync(Scale(device), hscroll, vscroll, ct) : fallback.ScrollAsync(device, hscroll, vscroll, ct);
}

/// <summary>
/// Rescales coordinates from one screen size to another (window device reference resolution → linked adb device
/// screen). Pass-through when both sizes agree; when only the orientation differs the target is transposed first.
/// </summary>
public sealed class ScaledTouchInjector : ITouchInjector
{
    private readonly ITouchInjector _inner;
    private readonly Func<SizeI> _from;
    private readonly Func<SizeI> _to;

    public ScaledTouchInjector(ITouchInjector inner, Func<SizeI> from, Func<SizeI> to)
    {
        _inner = inner;
        _from = from;
        _to = to;
    }

    public ITouchInjector Inner => _inner;

    public string Name => _inner.Name;

    public bool SupportsMultiTouch => _inner.SupportsMultiTouch;

    /// <summary>The (from, to) pair to scale with, or null when no scaling is needed.</summary>
    internal static (SizeI From, SizeI To)? Factors(SizeI from, SizeI to)
    {
        if (from.IsEmpty || to.IsEmpty)
        {
            return null;
        }

        if (to.IsLandscape != from.IsLandscape && to.Width != to.Height)
        {
            to = to.Transposed;
        }

        return from == to ? null : (from, to);
    }

    private PointD Map(PointD p, (SizeI From, SizeI To)? f) =>
        f is { } v ? new PointD(p.X * v.To.Width / v.From.Width, p.Y * v.To.Height / v.From.Height) : p;

    private IReadOnlyList<TimedPoint> Map(IReadOnlyList<TimedPoint> path, (SizeI From, SizeI To)? f) =>
        f is null ? path : path.Select(t => Map(t.Position, f) is var m ? new TimedPoint(m.X, m.Y, t.OffsetMs) : t).ToArray();

    public Task TapAsync(PointI point, int durationMs, CancellationToken ct)
    {
        var f = Factors(_from(), _to());
        return _inner.TapAsync(Map(new PointD(point.X, point.Y), f).Round(), durationMs, ct);
    }

    public Task StrokeAsync(IReadOnlyList<TimedPoint> path, CancellationToken ct) =>
        _inner.StrokeAsync(Map(path, Factors(_from(), _to())), ct);

    public Task MultiStrokeAsync(IReadOnlyList<IReadOnlyList<TimedPoint>> fingers, CancellationToken ct)
    {
        var f = Factors(_from(), _to());
        return _inner.MultiStrokeAsync(fingers.Select(path => Map(path, f)).ToArray(), ct);
    }

    public Task KeyAsync(int keyCode, CancellationToken ct) => _inner.KeyAsync(keyCode, ct);

    public Task TextAsync(string text, CancellationToken ct) => _inner.TextAsync(text, ct);
}

/// <summary>Input backend of a capture-only window device: every call fails with an explanatory message.</summary>
public sealed class UnavailableTouchInjector(string message) : ITouchInjector
{
    public string Name => "none";

    public bool SupportsMultiTouch => false;

    public string Message => message;

    public Task TapAsync(PointI point, int durationMs, CancellationToken ct) => Fail();

    public Task StrokeAsync(IReadOnlyList<TimedPoint> path, CancellationToken ct) => Fail();

    public Task MultiStrokeAsync(IReadOnlyList<IReadOnlyList<TimedPoint>> fingers, CancellationToken ct) => Fail();

    public Task KeyAsync(int keyCode, CancellationToken ct) => Fail();

    public Task TextAsync(string text, CancellationToken ct) => Fail();

    private Task Fail() => Task.FromException(new InvalidOperationException(message));
}
