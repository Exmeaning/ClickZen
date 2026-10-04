using System.Globalization;
using System.Numerics;
using ClickZen.App.Services;
using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.System;
using Windows.UI;
using Frame = ClickZen.Core.Automation.Frame;

namespace ClickZen.App.Controls;

public enum WorkbenchMode
{
    /// <summary>Shows the picture and overlays; clicks do nothing.</summary>
    View,

    /// <summary>The next click picks a point.</summary>
    PickPoint,

    /// <summary>A drag selects a rectangle.</summary>
    PickArea,
}

/// <summary>What the user picked, in every space the page needs. <see cref="Frame"/> is the picture it was picked on.</summary>
public sealed record WorkbenchPick(Frame Frame, SizeI Screen, RectI FrameRect, RectI DeviceRect)
{
    public PointI DevicePoint => new(DeviceRect.X, DeviceRect.Y);
}

/// <summary>
/// The automation page's picture: the current device's live (or frozen) frame, letterboxed with Win2D,
/// with overlays for highlighted areas / points and test results, and point / area picking.
/// Overlay coordinates are in <see cref="RefSize"/> pixels (the scheme's space); when RefSize is empty,
/// current device pixels.
/// </summary>
public sealed partial class Workbench : UserControl
{
    private const float LabelFontSize = 12;

    private readonly Lock _frameLock = new();
    private ILiveFrameSource? _session;
    private Frame? _pendingFrame;
    private Frame? _shownFrame;
    private CanvasBitmap? _bitmap;
    private bool _redrawQueued;
    private bool _frozen;
    private PointD? _hoverFrame;
    private PointD? _dragStart;
    private PointD? _dragEnd;
    private uint? _dragPointer;
    private TaskCompletionSource<WorkbenchPick?>? _pick;
    private RectI? _highlightArea;
    private PointI? _highlightPoint;
    private RectI? _resultBox;
    private string? _resultLabel;
    private bool _resultOk;

    public Workbench()
    {
        InitializeComponent();
        Unloaded += (_, _) => Detach();
        Loaded += (_, _) => UpdateModeText();
        UpdatePlaceholder();
    }

    /// <summary>Localiser for the mode text (set by the host).</summary>
    public ILocalizer? Localizer { get; set; }

    /// <summary>Device screen size of the shown picture (current orientation). Defaults to the frame size.</summary>
    public Func<SizeI>? ScreenProvider { get; set; }

    /// <summary>Space of the overlay and hover coordinates (the scheme's RefSize); empty = device pixels.</summary>
    public SizeI RefSize
    {
        get => _refSize;
        set
        {
            _refSize = value;
            Canvas.Invalidate();
        }
    }

    private SizeI _refSize;

    public WorkbenchMode Mode { get; private set; } = WorkbenchMode.View;

    /// <summary>The picture currently shown (frozen or newest); null when there is none.</summary>
    public Frame? CurrentFrame
    {
        get
        {
            lock (_frameLock)
            {
                return _pendingFrame ?? _shownFrame;
            }
        }
    }

    public bool IsFrozen => _frozen;

    /// <summary>Device size for the shown picture.</summary>
    public SizeI Screen
    {
        get
        {
            var s = ScreenProvider?.Invoke() ?? default;
            return s.IsEmpty ? CurrentFrame?.Size ?? default : s;
        }
    }

    /// <summary>The effective overlay space: <see cref="RefSize"/>, or the device when it is empty.</summary>
    public SizeI OverlaySpace => _refSize.IsEmpty ? Screen : _refSize;

    /// <summary>Text shown when there is no picture.</summary>
    public string PlaceholderMessage
    {
        get => PlaceholderText.Text;
        set => PlaceholderText.Text = value;
    }

    /// <summary>Raised when the mode changes (pick started / finished).</summary>
    public event EventHandler<WorkbenchMode>? ModeChanged;

    /// <summary>The live picture to show (scrcpy video or window capture); null shows the placeholder.</summary>
    public ILiveFrameSource? Source
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
                lock (_frameLock)
                {
                    _pendingFrame = _session.Latest;
                }

                QueueRedraw();
            }

            UpdatePlaceholder();
        }
    }

    // ------------------------------------------------------------------ public API

    /// <summary>Freezes / unfreezes the picture.</summary>
    public void SetFrozen(bool frozen)
    {
        _frozen = frozen;
        FreezeToggle.IsChecked = frozen;
        if (!frozen)
        {
            Refresh();
        }

        UpdateModeText();
    }

    /// <summary>Shows the newest frame (also while frozen: it becomes the new frozen picture).</summary>
    public void Refresh()
    {
        if (_session?.Latest is { } f)
        {
            lock (_frameLock)
            {
                _pendingFrame = f;
            }

            QueueRedraw();
        }
    }

    /// <summary>Highlights an area and/or a point (RefSize pixels). Nulls clear.</summary>
    public void Highlight(RectI? area, PointI? point)
    {
        _highlightArea = area is { IsEmpty: false } ? area : null;
        _highlightPoint = point;
        Canvas.Invalidate();
    }

    /// <summary>Shows a test result box (RefSize pixels) with a caption; null box clears.</summary>
    public void ShowResult(RectI? box, string? label, bool ok)
    {
        _resultBox = box;
        _resultLabel = label;
        _resultOk = ok;
        Canvas.Invalidate();
    }

    public void ClearOverlays()
    {
        _highlightArea = null;
        _highlightPoint = null;
        _resultBox = null;
        _resultLabel = null;
        Canvas.Invalidate();
    }

    /// <summary>Lets the user click a point. Null when cancelled or there is no picture.</summary>
    public Task<WorkbenchPick?> PickPointAsync() => BeginPick(WorkbenchMode.PickPoint);

    /// <summary>Lets the user drag a rectangle. Null when cancelled or there is no picture.</summary>
    public Task<WorkbenchPick?> PickAreaAsync() => BeginPick(WorkbenchMode.PickArea);

    /// <summary>Ends a running pick with no result.</summary>
    public void CancelPick() => EndPick(null);

    private Task<WorkbenchPick?> BeginPick(WorkbenchMode mode)
    {
        CancelPick();
        if (CurrentFrame is null)
        {
            return Task.FromResult<WorkbenchPick?>(null);
        }

        var tcs = new TaskCompletionSource<WorkbenchPick?>();
        _pick = tcs;
        SetMode(mode);
        Focus(FocusState.Programmatic);
        return tcs.Task;
    }

    private void EndPick(WorkbenchPick? result)
    {
        var tcs = _pick;
        _pick = null;
        _dragStart = _dragEnd = null;
        _dragPointer = null;
        SetMode(WorkbenchMode.View);
        tcs?.TrySetResult(result);
    }

    private void SetMode(WorkbenchMode mode)
    {
        if (Mode == mode)
        {
            return;
        }

        Mode = mode;
        CancelPickButton.Visibility = mode == WorkbenchMode.View ? Visibility.Collapsed : Visibility.Visible;
        UpdateModeText();
        Canvas.Invalidate();
        ModeChanged?.Invoke(this, mode);
    }

    private void UpdateModeText()
    {
        var loc = Localizer;
        if (loc is null)
        {
            return;
        }

        (ModeGlyph.Glyph, ModeText.Text) = Mode switch
        {
            WorkbenchMode.PickPoint => ("\uEF3C", loc["Workbench_ModePoint"]),
            WorkbenchMode.PickArea => ("\uF407", loc["Workbench_ModeArea"]),
            _ => ("\uE7B3", _frozen ? loc["Workbench_ModeFrozen"] : loc["Workbench_ModeLive"]),
        };
        ModeGlyph.Foreground = AutomationUi.Resource(Mode == WorkbenchMode.View ? "TextFillColorSecondaryBrush" : "AccentTextFillColorPrimaryBrush");
    }

    // ------------------------------------------------------------------ frames

    private void Detach()
    {
        if (_session is not null)
        {
            _session.FrameArrived -= OnFrameArrived;
        }

        _session = null;
        lock (_frameLock)
        {
            _pendingFrame = null;
        }

        _shownFrame = null;
        _bitmap?.Dispose();
        _bitmap = null;
        CancelPick();
        Canvas.Invalidate();
        UpdatePlaceholder();
    }

    private void OnFrameArrived(object? sender, Frame frame)
    {
        if (_frozen && _shownFrame is not null)
        {
            return;
        }

        lock (_frameLock)
        {
            _pendingFrame = frame;
        }

        QueueRedraw();
    }

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

    private void UpdatePlaceholder() =>
        Placeholder.Visibility = CurrentFrame is null ? Visibility.Visible : Visibility.Collapsed;

    private LetterboxFit Fit() => new(_shownFrame?.Size ?? default, Canvas.ActualWidth, Canvas.ActualHeight);

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

            var first = _shownFrame is null;
            _shownFrame = next;
            if (first)
            {
                DispatcherQueue.TryEnqueue(UpdatePlaceholder);
            }
        }

        if (_bitmap is null || _shownFrame is null)
        {
            return;
        }

        var fit = Fit();
        var dest = new Rect(fit.OffsetX, fit.OffsetY, fit.ContentWidth, fit.ContentHeight);
        var ds = args.DrawingSession;
        ds.DrawImage(_bitmap, dest, new Rect(0, 0, _shownFrame.Width, _shownFrame.Height), 1f, CanvasImageInterpolation.HighQualityCubic);

        var accent = ThemeColor("SystemAccentColor", Colors.DodgerBlue);
        var success = BrushColor("SystemFillColorSuccessBrush", Colors.SeaGreen);
        var critical = BrushColor("SystemFillColorCriticalBrush", Colors.Crimson);
        var contrast = BrushColor("TextOnAccentFillColorPrimaryBrush", Colors.White);

        if (_highlightArea is { } ha && ToViewport(ha) is { } hv)
        {
            ds.FillRectangle(hv, WithAlpha(accent, 40));
            ds.DrawRectangle(hv, contrast, 3f);
            ds.DrawRectangle(hv, accent, 1.5f);
        }

        if (_highlightPoint is { } hp && ToViewport(new RectI(hp.X, hp.Y, 0, 0)) is { } pv)
        {
            DrawMarker(ds, new Vector2((float)pv.X, (float)pv.Y), accent, contrast);
        }

        if (_resultBox is { } rb && ToViewport(rb) is { } rv)
        {
            var c = _resultOk ? success : critical;
            ds.DrawRectangle(rv, contrast, 3.5f);
            ds.DrawRectangle(rv, c, 2f);
            if (!string.IsNullOrEmpty(_resultLabel))
            {
                DrawLabel(ds, _resultLabel, new Vector2((float)rv.X, (float)rv.Y), c, contrast);
            }
        }
        else if (_resultBox is null && !string.IsNullOrEmpty(_resultLabel))
        {
            DrawLabel(ds, _resultLabel, new Vector2((float)dest.X + 6, (float)dest.Y + 26), _resultOk ? success : critical, contrast);
        }

        if (_dragStart is { } a && _dragEnd is { } b)
        {
            var va = fit.SourceToViewport(a);
            var vb = fit.SourceToViewport(b);
            var r = new Rect(new Point(va.X, va.Y), new Point(vb.X, vb.Y));
            ds.FillRectangle(r, WithAlpha(accent, 50));
            ds.DrawRectangle(r, contrast, 3f);
            using var dash = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
            ds.DrawRectangle(r, accent, 1.5f, dash);
        }

        if (Mode != WorkbenchMode.View && _hoverFrame is { } hf)
        {
            var v = fit.SourceToViewport(hf);
            ds.DrawLine((float)v.X, (float)dest.Top, (float)v.X, (float)dest.Bottom, WithAlpha(accent, 180), 1f);
            ds.DrawLine((float)dest.Left, (float)v.Y, (float)dest.Right, (float)v.Y, WithAlpha(accent, 180), 1f);
        }
    }

    private static void DrawMarker(CanvasDrawingSession ds, Vector2 c, Color color, Color contrast)
    {
        ds.DrawCircle(c, 9f, contrast, 3f);
        ds.DrawCircle(c, 9f, color, 1.5f);
        ds.DrawLine(c.X - 14, c.Y, c.X - 4, c.Y, color, 1.5f);
        ds.DrawLine(c.X + 4, c.Y, c.X + 14, c.Y, color, 1.5f);
        ds.DrawLine(c.X, c.Y - 14, c.X, c.Y - 4, color, 1.5f);
        ds.DrawLine(c.X, c.Y + 4, c.X, c.Y + 14, color, 1.5f);
    }

    private static void DrawLabel(CanvasDrawingSession ds, string text, Vector2 anchor, Color background, Color foreground)
    {
        using var format = new CanvasTextFormat { FontSize = LabelFontSize, WordWrapping = CanvasWordWrapping.NoWrap };
        using var layout = new CanvasTextLayout(ds, text, format, 0, 0);
        var w = (float)layout.LayoutBounds.Width + 8;
        var h = (float)layout.LayoutBounds.Height + 4;
        var y = Math.Max(0, anchor.Y - h - 2);
        ds.FillRoundedRectangle(anchor.X, y, w, h, 3, 3, background);
        ds.DrawTextLayout(layout, anchor.X + 4, y + 2, foreground);
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color ThemeColor(string key, Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is Color c ? c : fallback;

    private static Color BrushColor(string key, Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is SolidColorBrush b ? b.Color : fallback;

    // ------------------------------------------------------------------ coordinates

    /// <summary>Overlay rectangle (RefSize pixels) → viewport rectangle.</summary>
    private Rect? ToViewport(RectI overlay)
    {
        var frame = _shownFrame;
        if (frame is null)
        {
            return null;
        }

        var screen = Screen;
        var device = AutomationCoords.RefToDevice(overlay, OverlaySpace, screen);
        var map = new FrameToDevice(frame.Size, screen);
        var a = map.DeviceToFramePoint(new PointD(device.X, device.Y));
        var b = map.DeviceToFramePoint(new PointD(device.Right, device.Bottom));
        var fit = Fit();
        var va = fit.SourceToViewport(a);
        var vb = fit.SourceToViewport(b);
        return new Rect(new Point(va.X, va.Y), new Point(vb.X, vb.Y));
    }

    /// <summary>Viewport point for a frame pixel (used by the self-test).</summary>
    internal Point? FrameToViewport(PointD frame) =>
        _shownFrame is null ? null : Fit().SourceToViewport(frame) is var v ? new Point(v.X, v.Y) : null;

    private WorkbenchPick? MakePick(Frame frame, RectI frameRect)
    {
        var screen = Screen;
        if (screen.IsEmpty)
        {
            return null;
        }

        var map = new FrameToDevice(frame.Size, screen);
        RectI deviceRect;
        if (frameRect.IsEmpty)
        {
            var p = map.ClampToDevice(map.FrameToDevicePoint(new PointD(frameRect.X + 0.5, frameRect.Y + 0.5)));
            deviceRect = new RectI(p.X, p.Y, 0, 0);
        }
        else
        {
            deviceRect = map.FrameToDeviceRect(frameRect);
        }

        return new WorkbenchPick(frame, screen, frameRect, deviceRect);
    }

    // ------------------------------------------------------------------ input

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        var pt = e.GetCurrentPoint(Canvas);
        if (pt.Properties.IsRightButtonPressed)
        {
            CancelPick();
            e.Handled = true;
            return;
        }

        if (HandlePress(pt.Position, e.Pointer.PointerId))
        {
            if (Mode == WorkbenchMode.PickArea)
            {
                Canvas.CapturePointer(e.Pointer);
            }

            e.Handled = true;
        }
    }

    /// <summary>Press at a viewport point. Returns true when consumed.</summary>
    internal bool HandlePress(Point viewport, uint pointerId)
    {
        var frame = _shownFrame;
        if (frame is null || Mode == WorkbenchMode.View)
        {
            return false;
        }

        var fit = Fit();
        if (fit.ViewportToSource(new PointD(viewport.X, viewport.Y)) is not { } p)
        {
            return false;
        }

        if (Mode == WorkbenchMode.PickPoint)
        {
            var fp = new PointI(Math.Clamp((int)p.X, 0, frame.Width - 1), Math.Clamp((int)p.Y, 0, frame.Height - 1));
            EndPick(MakePick(frame, new RectI(fp.X, fp.Y, 0, 0)));
            return true;
        }

        _dragStart = p;
        _dragEnd = p;
        _dragPointer = pointerId;
        Canvas.Invalidate();
        return true;
    }

    internal bool HandleMove(Point viewport, uint pointerId)
    {
        if (_dragPointer != pointerId || _dragStart is null)
        {
            return false;
        }

        _dragEnd = Fit().ViewportToSourceClamped(new PointD(viewport.X, viewport.Y));
        Canvas.Invalidate();
        return true;
    }

    internal bool HandleRelease(Point viewport, uint pointerId)
    {
        if (_dragPointer != pointerId || _dragStart is not { } a || _shownFrame is not { } frame)
        {
            return false;
        }

        var b = Fit().ViewportToSourceClamped(new PointD(viewport.X, viewport.Y));
        _dragPointer = null;
        var rect = RectI.FromCorners(a.Round(), b.Round()).ClampTo(frame.Size);
        if (rect.Width < 3 || rect.Height < 3)
        {
            // A click, not a drag: keep waiting for a real selection.
            _dragStart = _dragEnd = null;
            Canvas.Invalidate();
            return true;
        }

        EndPick(MakePick(frame, rect));
        return true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(Canvas);
        HandleMove(pt.Position, e.Pointer.PointerId);
        UpdateHover(pt.Position);
        e.Handled = true;
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
        if (_dragPointer == e.Pointer.PointerId)
        {
            _dragPointer = null;
            _dragStart = _dragEnd = null;
            Canvas.Invalidate();
        }
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer is null)
        {
            _hoverFrame = null;
            HoverText.Text = "";
            HoverSwatch.Visibility = Visibility.Collapsed;
            Canvas.Invalidate();
        }
    }

    private void UpdateHover(Point viewport)
    {
        var frame = _shownFrame;
        if (frame is null || Fit().ViewportToSource(new PointD(viewport.X, viewport.Y)) is not { } p)
        {
            _hoverFrame = null;
            HoverText.Text = "";
            HoverSwatch.Visibility = Visibility.Collapsed;
            if (Mode != WorkbenchMode.View)
            {
                Canvas.Invalidate();
            }

            return;
        }

        _hoverFrame = p;
        var screen = Screen;
        var map = new FrameToDevice(frame.Size, screen);
        var device = map.ClampToDevice(map.FrameToDevicePoint(p));
        var space = OverlaySpace;
        var refPt = AutomationCoords.DeviceToRef(device, screen, space);
        var fx = Math.Clamp((int)p.X, 0, frame.Width - 1);
        var fy = Math.Clamp((int)p.Y, 0, frame.Height - 1);
        var (r, g, b) = frame.PixelAt(fx, fy);
        var hex = ConditionProbe.FormatColor(r, g, b);
        HoverText.Text = string.Create(CultureInfo.InvariantCulture, $"{refPt.X}, {refPt.Y}   {hex}");
        HoverSwatch.Background = new SolidColorBrush(Color.FromArgb(255, r, g, b));
        HoverSwatch.Visibility = Visibility.Visible;
        if (Mode != WorkbenchMode.View)
        {
            Canvas.Invalidate();
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && Mode != WorkbenchMode.View)
        {
            CancelPick();
            e.Handled = true;
        }
    }

    private void OnCancelPickClick(object sender, RoutedEventArgs e) => CancelPick();

    private void OnFreezeClick(object sender, RoutedEventArgs e) => SetFrozen(FreezeToggle.IsChecked == true);

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Refresh();
}
