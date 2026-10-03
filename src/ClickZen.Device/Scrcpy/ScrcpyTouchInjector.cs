using ClickZen.Core;
using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Device.Scrcpy.Protocol;

namespace ClickZen.Device.Scrcpy;

/// <summary>
/// <see cref="ITouchInjector"/> over the scrcpy control socket. Strokes are real continuous gestures
/// (DOWN → MOVE… → UP with one pointer id per finger) played against a monotonic clock, so
/// timing matches the recording to within a frame. Coordinates are device pixels; they are
/// converted to video-frame pixels because the server maps positions relative to the video size
/// (and drops events whose screen size does not match the current video size).
/// </summary>
public sealed class ScrcpyTouchInjector : ITouchInjector
{
    /// <summary>Interval between MOVE events when interpolating strokes.</summary>
    public const int MoveIntervalMs = 8;

    private readonly Func<Func<byte[], int>, CancellationToken, Task> _send;
    private readonly Func<SizeI> _videoSize;
    private readonly Func<SizeI> _deviceSize;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gestureLock = new(1, 1);

    /// <param name="send">Sends one serialised control message (typically <see cref="ScrcpyConnection.SendAsync"/>).</param>
    /// <param name="videoSize">Current video size from the last session packet. Empty in control-only mode.</param>
    /// <param name="deviceSize">Current device screen size (orientation-aware).</param>
    public ScrcpyTouchInjector(Func<Func<byte[], int>, CancellationToken, Task> send, Func<SizeI> videoSize, Func<SizeI> deviceSize, IClock? clock = null)
    {
        _send = send;
        _videoSize = videoSize;
        _deviceSize = deviceSize;
        _clock = clock ?? SystemClock.Instance;
    }

    public string Name => "scrcpy";

    public bool SupportsMultiTouch => true;

    public async Task TapAsync(PointI point, int durationMs, CancellationToken ct)
    {
        await _gestureLock.WaitAsync(ct);
        try
        {
            var p = ToVideo(point);
            await TouchAsync(MotionAction.Down, 0, p, ct);
            try
            {
                await _clock.DelayAsync(TimeSpan.FromMilliseconds(Math.Max(1, durationMs)), ct);
            }
            finally
            {
                // Always release the finger, even when cancelled mid-press.
                await TouchAsync(MotionAction.Up, 0, p, CancellationToken.None);
            }
        }
        finally
        {
            _gestureLock.Release();
        }
    }

    public Task StrokeAsync(IReadOnlyList<TimedPoint> path, CancellationToken ct) => MultiStrokeAsync([path], ct);

    public async Task MultiStrokeAsync(IReadOnlyList<IReadOnlyList<TimedPoint>> fingers, CancellationToken ct)
    {
        var strokes = fingers.Where(f => f.Count > 0).Take(10).ToArray();
        if (strokes.Length == 0)
        {
            return;
        }

        await _gestureLock.WaitAsync(ct);
        try
        {
            await PlayAsync(BuildTimeline(strokes), ct);
        }
        finally
        {
            _gestureLock.Release();
        }
    }

    public async Task KeyAsync(int keyCode, CancellationToken ct)
    {
        await _send(b => ControlMessageWriter.WriteKeycode(b, KeyAction.Down, keyCode), ct);
        await _send(b => ControlMessageWriter.WriteKeycode(b, KeyAction.Up, keyCode), CancellationToken.None);
    }

    public Task TextAsync(string text, CancellationToken ct) =>
        string.IsNullOrEmpty(text) ? Task.CompletedTask : _send(b => ControlMessageWriter.WriteText(b, text), ct);

    /// <summary>Mouse-wheel style scroll at a device point.</summary>
    public Task ScrollAsync(PointI point, float hscroll, float vscroll, CancellationToken ct)
    {
        var p = ToVideo(point);
        var size = TargetSize();
        return _send(b => ControlMessageWriter.WriteScroll(b, p.X, p.Y, size.Width, size.Height, hscroll, vscroll), ct);
    }

    /// <summary>Turns the device screen on/off while keeping mirroring alive.</summary>
    public Task SetDisplayPowerAsync(bool on, CancellationToken ct) => _send(b => ControlMessageWriter.WriteSetDisplayPower(b, on), ct);

    public Task RotateAsync(CancellationToken ct) => _send(b => ControlMessageWriter.WriteSimple(b, ControlMessageType.RotateDevice), ct);

    public Task SetClipboardAsync(string text, bool paste, CancellationToken ct) =>
        _send(b => ControlMessageWriter.WriteSetClipboard(b, Environment.TickCount64, paste, text), ct);

    /// <summary>Low-level: one raw touch event (used by the mirror view for live input).</summary>
    public Task SendRawTouchAsync(MotionAction action, int pointerId, PointI devicePoint, CancellationToken ct) =>
        TouchAsync(action, pointerId, ToVideo(devicePoint), ct);

    // ------------------------------------------------------------------ timeline

    internal readonly record struct TouchStep(int AtMs, MotionAction Action, int Pointer, PointD Device);

    /// <summary>Merges all fingers into one time-ordered event list (MOVEs resampled every ~8 ms).</summary>
    internal static List<TouchStep> BuildTimeline(IReadOnlyList<IReadOnlyList<TimedPoint>> fingers)
    {
        var steps = new List<TouchStep>();
        var origin = fingers.Min(f => f[0].OffsetMs);
        for (var id = 0; id < fingers.Count; id++)
        {
            var path = Trajectory.ResampleByTime(fingers[id], MoveIntervalMs);
            steps.Add(new TouchStep(path[0].OffsetMs - origin, MotionAction.Down, id, path[0].Position));
            for (var i = 1; i < path.Count; i++)
            {
                steps.Add(new TouchStep(path[i].OffsetMs - origin, MotionAction.Move, id, path[i].Position));
            }

            var last = path[^1];
            steps.Add(new TouchStep(last.OffsetMs - origin, MotionAction.Up, id, last.Position));
        }

        // Stable order: by time, then DOWN before MOVE before UP so a finger never moves before it is down.
        return steps.Select((s, i) => (s, i))
            .OrderBy(x => x.s.AtMs)
            .ThenBy(x => x.s.Action switch { MotionAction.Down => 0, MotionAction.Move => 1, _ => 2 })
            .ThenBy(x => x.i)
            .Select(x => x.s)
            .ToList();
    }

    private async Task PlayAsync(List<TouchStep> steps, CancellationToken ct)
    {
        var down = new HashSet<int>();
        var lastPos = new Dictionary<int, PointI>();
        var t0 = _clock.ElapsedMs;
        try
        {
            foreach (var step in steps)
            {
                var wait = t0 + step.AtMs - _clock.ElapsedMs;
                if (wait > 0)
                {
                    await _clock.DelayAsync(TimeSpan.FromMilliseconds(wait), ct);
                }

                var p = ToVideo(step.Device.Round());
                await TouchAsync(step.Action, step.Pointer, p, ct);
                lastPos[step.Pointer] = p;
                if (step.Action == MotionAction.Down)
                {
                    down.Add(step.Pointer);
                }
                else if (step.Action == MotionAction.Up)
                {
                    down.Remove(step.Pointer);
                }
            }
        }
        finally
        {
            // Never leave a finger pressed on the device (cancellation, socket error…).
            foreach (var id in down)
            {
                try
                {
                    await TouchAsync(MotionAction.Up, id, lastPos[id], CancellationToken.None);
                }
                catch (Exception)
                {
                    // Connection gone: nothing more we can do.
                }
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private SizeI TargetSize()
    {
        var v = _videoSize();
        return v.IsEmpty ? _deviceSize() : v;
    }

    private PointI ToVideo(PointI device)
    {
        var video = _videoSize();
        var dev = _deviceSize();
        if (video.IsEmpty || dev.IsEmpty || video == dev)
        {
            return device;
        }

        var p = new FrameToDevice(video, dev).DeviceToFramePoint(device);
        return new PointI(Math.Clamp((int)Math.Round(p.X), 0, video.Width - 1), Math.Clamp((int)Math.Round(p.Y), 0, video.Height - 1));
    }

    private Task TouchAsync(MotionAction action, int pointerId, PointI p, CancellationToken ct)
    {
        var size = TargetSize();
        var pressure = action == MotionAction.Up ? 0f : 1f;
        return _send(b => ControlMessageWriter.WriteTouch(b, action, pointerId, p.X, p.Y, size.Width, size.Height, pressure), ct);
    }
}
