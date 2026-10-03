using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Device.Scrcpy;
using ClickZen.Device.Scrcpy.Protocol;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Frame = ClickZen.Core.Automation.Frame;

namespace ClickZen.App.Controls;

/// <summary>Where a pointer interaction lands, in all coordinate spaces the UI cares about.</summary>
public readonly record struct MirrorPoint(PointI Device, PointI Frame, (byte R, byte G, byte B)? Color);

/// <summary>
/// Renders a <see cref="ScrcpySession"/>'s frames with Win2D (letterboxed, aspect preserved) and turns
/// mouse / touch / pen input into live multi-touch on the device. Exposes device-space touch events
/// for the recorder and a "pick" mode for choosing coordinates.
/// </summary>
public sealed partial class MirrorView : UserControl
{
    private readonly Lock _frameLock = new();
    private ScrcpySession? _session;
    private Frame? _pendingFrame;
    private Frame? _shownFrame;
    private CanvasBitmap? _bitmap;
    private bool _redrawQueued;
    private readonly Dictionary<uint, int> _fingers = new();
    private readonly Dictionary<uint, PointI> _lastDevicePoint = new();
    private PointD? _hoverFramePoint;

    public MirrorView()
    {
        InitializeComponent();
        Unloaded += (_, _) => Detach();
        UpdatePlaceholder();
    }

    // ------------------------------------------------------------------ public API

    /// <summary>The session to display and control. Null shows the placeholder.</summary>
    public ScrcpySession? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value))
            {
                return;
            }

            Detach();
            _session = value;
            if (_session is not null)
            {
                _session.FrameArrived += OnFrameArrived;
                _session.StateChanged += OnSessionStateChanged;
                lock (_frameLock)
                {
                    _pendingFrame = _session.Latest;
                }

                QueueRedraw();
            }

            UpdatePlaceholder();
        }
    }

    /// <summary>When false, pointer input is not forwarded to the device (view-only).</summary>
    public bool InputEnabled { get; set; } = true;

    /// <summary>Pick mode: clicks raise <see cref="PointPicked"/> instead of touching the device.</summary>
    public bool PickMode { get; set; }

    /// <summary>Text shown when nothing is streaming (set by the page).</summary>
    public string PlaceholderMessage
    {
        get => PlaceholderText.Text;
        set => PlaceholderText.Text = value;
    }

    public bool PlaceholderBusy
    {
        get => PlaceholderProgress.IsActive;
        set => PlaceholderProgress.IsActive = value;
    }

    /// <summary>Raised for every pointer event forwarded to the device (device pixels, monotonic ms).</summary>
    public event EventHandler<RawTouchEvent>? TouchForwarded;

    /// <summary>Raised while hovering, with device coordinates and pixel colour (null when outside the picture).</summary>
    public event EventHandler<MirrorPoint?>? HoverChanged;

    /// <summary>Raised on click in <see cref="PickMode"/>.</summary>
    public event EventHandler<MirrorPoint>? PointPicked;

    /// <summary>Raised when an input could not be delivered (e.g. not connected).</summary>
    public event EventHandler<string>? InputFailed;

    /// <summary>The frame currently displayed (for screenshots).</summary>
    public Frame? CurrentFrame => _shownFrame;

    // ------------------------------------------------------------------ frames

    private void Detach()
    {
        if (_session is not null)
        {
            _session.FrameArrived -= OnFrameArrived;
            _session.StateChanged -= OnSessionStateChanged;
        }

        ReleaseAllFingers();
        _session = null;
        lock (_frameLock)
        {
            _pendingFrame = null;
        }

        _shownFrame = null;
        _bitmap?.Dispose();
        _bitmap = null;
        Canvas.Invalidate();
    }

    private void OnFrameArrived(object? sender, Frame frame)
    {
        lock (_frameLock)
        {
            _pendingFrame = frame;
        }

        QueueRedraw();
    }

    private void OnSessionStateChanged(object? sender, SessionState e) => DispatcherQueue.TryEnqueue(UpdatePlaceholder);

    /// <summary>Coalesces redraw requests: at most one pending UI-thread invalidate at a time.</summary>
    private void QueueRedraw()
    {
        lock (_frameLock)
        {
            if (_redrawQueued)
            {
                return;
            }

            _redrawQueued = true;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            lock (_frameLock)
            {
                _redrawQueued = false;
            }

            Canvas.Invalidate();
        });
    }

    private void UpdatePlaceholder()
    {
        var hasPicture = _shownFrame is not null || _pendingFrame is not null;
        Placeholder.Visibility = hasPicture ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        Frame? next;
        lock (_frameLock)
        {
            next = _pendingFrame;
            _pendingFrame = null;
        }

        if (next is not null)
        {
            if (_bitmap is null || _bitmap.SizeInPixels.Width != next.Width || _bitmap.SizeInPixels.Height != next.Height)
            {
                _bitmap?.Dispose();
                _bitmap = CanvasBitmap.CreateFromBytes(sender, next.Bgra, next.Width, next.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Ignore);
            }
            else
            {
                _bitmap.SetPixelBytes(next.Bgra);
            }

            if (_shownFrame is null)
            {
                DispatcherQueue.TryEnqueue(UpdatePlaceholder);
            }

            _shownFrame = next;
        }

        if (_bitmap is null || _shownFrame is null)
        {
            return;
        }

        var fit = Fit();
        var dest = new Rect(fit.OffsetX, fit.OffsetY, fit.ContentWidth, fit.ContentHeight);
        var ds = args.DrawingSession;
        ds.DrawImage(_bitmap, dest, new Rect(0, 0, _shownFrame.Width, _shownFrame.Height), 1f,
            HighQuality ? CanvasImageInterpolation.HighQualityCubic : CanvasImageInterpolation.Linear);

        if (PickMode && _hoverFramePoint is { } hp)
        {
            var v = fit.SourceToViewport(hp);
            var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
            ds.DrawLine((float)v.X, (float)dest.Top, (float)v.X, (float)dest.Bottom, accent, 1f);
            ds.DrawLine((float)dest.Left, (float)v.Y, (float)dest.Right, (float)v.Y, accent, 1f);
            ds.DrawCircle((float)v.X, (float)v.Y, 6f, Colors.White, 2f);
            ds.DrawCircle((float)v.X, (float)v.Y, 6f, accent, 1f);
        }
    }

    /// <summary>Use high-quality cubic scaling (setting).</summary>
    public bool HighQuality { get; set; } = true;

    private LetterboxFit Fit() =>
        new(_shownFrame?.Size ?? default, Canvas.ActualWidth, Canvas.ActualHeight);

    // ------------------------------------------------------------------ coordinates

    private bool TryMap(Point viewport, bool clamp, out MirrorPoint point)
    {
        point = default;
        var frame = _shownFrame;
        if (frame is null)
        {
            return false;
        }

        var fit = Fit();
        PointD framePt;
        if (clamp)
        {
            framePt = fit.ViewportToSourceClamped(new PointD(viewport.X, viewport.Y));
        }
        else if (fit.ViewportToSource(new PointD(viewport.X, viewport.Y)) is { } p)
        {
            framePt = p;
        }
        else
        {
            return false;
        }

        var deviceSize = _session?.DeviceSize ?? frame.Size;
        var map = new FrameToDevice(frame.Size, deviceSize.IsEmpty ? frame.Size : deviceSize);
        var device = map.ClampToDevice(map.FrameToDevicePoint(framePt));
        var fi = framePt.Round();
        var color = fi.X >= 0 && fi.Y >= 0 && fi.X < frame.Width && fi.Y < frame.Height ? frame.PixelAt(fi.X, fi.Y) : ((byte, byte, byte)?)null;
        point = new MirrorPoint(device, fi, color);
        return true;
    }

    // ------------------------------------------------------------------ input

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        var pt = e.GetCurrentPoint(Canvas);
        if (pt.PointerDeviceType == PointerDeviceType.Mouse && !pt.Properties.IsLeftButtonPressed)
        {
            // Right click = Back, middle click = Home (like scrcpy).
            if (pt.Properties.IsRightButtonPressed)
            {
                _ = SendKeyAsync(KeyCodes.Back);
            }
            else if (pt.Properties.IsMiddleButtonPressed)
            {
                _ = SendKeyAsync(KeyCodes.Home);
            }

            e.Handled = true;
            return;
        }

        if (HandlePress(pt.Position, e.Pointer.PointerId))
        {
            if (!PickMode)
            {
                Canvas.CapturePointer(e.Pointer);
            }

            e.Handled = true;
        }
    }

    /// <summary>Press at a viewport point. Returns true when the event was consumed.</summary>
    internal bool HandlePress(Point viewport, uint pointerId)
    {
        if (!TryMap(viewport, clamp: false, out var mp))
        {
            return false;
        }

        if (PickMode)
        {
            PointPicked?.Invoke(this, mp);
            return true;
        }

        if (!InputEnabled || _session is null || _fingers.Count >= 10)
        {
            return false;
        }

        var finger = Enumerable.Range(0, 10).First(i => !_fingers.ContainsValue(i));
        _fingers[pointerId] = finger;
        Forward(MotionAction.Down, TouchPhase.Down, finger, pointerId, mp.Device);
        return true;
    }

    /// <summary>Drag of a pressed pointer. Returns true when the pointer is down.</summary>
    internal bool HandleMove(Point viewport, uint pointerId)
    {
        if (!_fingers.TryGetValue(pointerId, out var finger))
        {
            return false;
        }

        if (TryMap(viewport, clamp: true, out var mp) && _lastDevicePoint.GetValueOrDefault(pointerId) != mp.Device)
        {
            Forward(MotionAction.Move, TouchPhase.Move, finger, pointerId, mp.Device);
        }

        return true;
    }

    /// <summary>Release of a pressed pointer. Returns true when it was down.</summary>
    internal bool HandleRelease(Point viewport, uint pointerId)
    {
        if (!_fingers.Remove(pointerId, out var finger))
        {
            return false;
        }

        var device = TryMap(viewport, clamp: true, out var mp) ? mp.Device : _lastDevicePoint.GetValueOrDefault(pointerId);
        Forward(MotionAction.Up, TouchPhase.Up, finger, pointerId, device);
        _lastDevicePoint.Remove(pointerId);
        return true;
    }

    /// <summary>Viewport point for a frame pixel (used by the self-test).</summary>
    internal Point? FrameToViewport(PointD frame) =>
        _shownFrame is null ? null : Fit().SourceToViewport(frame) is var v ? new Point(v.X, v.Y) : null;

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(Canvas);
        if (HandleMove(pt.Position, e.Pointer.PointerId))
        {
            e.Handled = true;
            return;
        }

        if (TryMap(pt.Position, clamp: false, out var hover))
        {
            _hoverFramePoint = new PointD(hover.Frame.X, hover.Frame.Y);
            HoverChanged?.Invoke(this, hover);
        }
        else
        {
            _hoverFramePoint = null;
            HoverChanged?.Invoke(this, null);
        }

        if (PickMode)
        {
            Canvas.Invalidate();
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (HandleRelease(e.GetCurrentPoint(Canvas).Position, e.Pointer.PointerId))
        {
            Canvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_fingers.Remove(e.Pointer.PointerId, out var finger))
        {
            Forward(MotionAction.Up, TouchPhase.Up, finger, e.Pointer.PointerId, _lastDevicePoint.GetValueOrDefault(e.Pointer.PointerId));
            _lastDevicePoint.Remove(e.Pointer.PointerId);
        }
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_fingers.ContainsKey(e.Pointer.PointerId))
        {
            _hoverFramePoint = null;
            HoverChanged?.Invoke(this, null);
            if (PickMode)
            {
                Canvas.Invalidate();
            }
        }
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!InputEnabled || _session is null || PickMode)
        {
            return;
        }

        var pt = e.GetCurrentPoint(Canvas);
        if (!TryMap(pt.Position, clamp: false, out var mp))
        {
            return;
        }

        var delta = pt.Properties.MouseWheelDelta / 120f;
        var horizontal = pt.Properties.IsHorizontalMouseWheel;
        _ = Guard(_session.Injector.ScrollAsync(mp.Device, horizontal ? delta : 0, horizontal ? 0 : delta, CancellationToken.None));
        e.Handled = true;
    }

    private void Forward(MotionAction action, TouchPhase phase, int finger, uint pointerId, PointI device)
    {
        _lastDevicePoint[pointerId] = device;
        var session = _session;
        if (session is null)
        {
            return;
        }

        _ = Guard(session.Injector.SendRawTouchAsync(action, finger, device, CancellationToken.None));
        TouchForwarded?.Invoke(this, new RawTouchEvent(finger, phase, device, Environment.TickCount64));
    }

    private void ReleaseAllFingers()
    {
        foreach (var (pointerId, finger) in _fingers.ToArray())
        {
            Forward(MotionAction.Up, TouchPhase.Up, finger, pointerId, _lastDevicePoint.GetValueOrDefault(pointerId));
        }

        _fingers.Clear();
        _lastDevicePoint.Clear();
    }

    /// <summary>Sends a key press to the device (used by the toolbar and right/middle click).</summary>
    public Task SendKeyAsync(int keyCode) => _session is null ? Task.CompletedTask : Guard(_session.Injector.KeyAsync(keyCode, CancellationToken.None));

    private async Task Guard(Task t)
    {
        try
        {
            await t;
        }
        catch (Exception ex)
        {
            InputFailed?.Invoke(this, ex.Message);
        }
    }
}
