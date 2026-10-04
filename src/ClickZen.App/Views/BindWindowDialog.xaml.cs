using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using ClickZen.App.Services;
using ClickZen.Core.Devices;
using ClickZen.Core.Geometry;
using ClickZen.Platform.Capture;
using ClickZen.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Frame = ClickZen.Core.Automation.Frame;

namespace ClickZen.App.Views;

/// <summary>A row of the window list.</summary>
public sealed class WindowChoice(DesktopWindowInfo window)
{
    public DesktopWindowInfo Window { get; } = window;

    public string Title => Window.Title;

    public string Details => string.IsNullOrEmpty(Window.ProcessName) ? Window.ClassName : $"{Window.ProcessName}  ·  {Window.ClassName}";

    public string Glyph => "\uE737";
}

/// <summary>A row of the linked-adb-device picker (null serial = capture only).</summary>
internal sealed record AdbChoice(string? Serial, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// "Bind emulator window" wizard: choose a window (live thumbnail) → drag the Android screen area on a capture
/// (or take the whole client area) → linked adb device (optional, for input) and reference resolution (read from
/// the device's <c>wm size</c>, transposed to the picture's orientation, editable) → name and match rule → saved
/// as an <see cref="EmulatorProfile"/>. Opened with an existing profile it edits it (prefilled).
/// </summary>
public sealed partial class BindWindowDialog : ContentDialog
{
    private const int MinCropPx = 16;

    private readonly DeviceHub _hub;
    private readonly ILocalizer _loc;
    private readonly ILogger _log;
    private readonly EmulatorProfile? _existing;
    private readonly DispatcherQueueTimer _thumbTimer;
    private readonly List<WindowChoice> _allWindows = [];
    private readonly string[] _stepKeys = ["Window_StepWindow", "Window_StepArea", "Window_StepDevice", "Window_StepName"];
    private int _step;
    private WindowFrameSource? _live;
    private long _thumbSequence = -1;
    private WriteableBitmap? _thumbBitmap;
    private DesktopWindowInfo? _window;
    private Frame? _areaFrame;
    private CanvasBitmap? _areaBitmap;
    private RectI? _crop;
    private PointD? _dragStart;
    private uint? _dragPointer;
    private bool _syncingRef;
    private bool _refEdited;

    public BindWindowDialog(DeviceHub hub, EmulatorProfile? existing = null)
    {
        _hub = hub;
        _existing = existing?.Clone();
        var sp = App.Current.Services;
        _loc = sp.GetRequiredService<ILocalizer>();
        _log = sp.GetRequiredService<ILoggerFactory>().CreateLogger<BindWindowDialog>();
        InitializeComponent();

        Title = _loc[existing is null ? "Window_DialogTitle" : "Window_DialogEditTitle"];
        CloseButtonText = _loc["Common_Cancel"];
        SecondaryButtonText = _loc["Window_Back"];
        Steps.ItemsSource = _stepKeys.Select(k => _loc[k]).ToList();

        _thumbTimer = DispatcherQueue.CreateTimer();
        _thumbTimer.Interval = TimeSpan.FromMilliseconds(250);
        _thumbTimer.Tick += (_, _) => UpdateThumbnail();

        if (_existing is { } p)
        {
            _crop = p.CropRect;
            NameBox.Text = p.Name;
            MatchTitle.Text = p.Match.Title ?? "";
            MatchProcess.Text = p.Match.ProcessName ?? "";
            MatchClass.Text = p.Match.ClassName ?? "";
            SetRef(p.ReferenceSize);
            _refEdited = !p.ReferenceSize.IsEmpty; // keep a saved resolution: do not overwrite it from adb
        }

        LoadAdbDevices();
        RefreshWindows();
        ShowStep(0);
    }

    /// <summary>The profile that was saved, or null when the wizard was cancelled.</summary>
    public EmulatorProfile? SavedProfile { get; private set; }

    // ------------------------------------------------------------------ self-test hooks

    internal int Step => _step;

    /// <summary>True once the live thumbnail showed a frame.</summary>
    internal bool HasPreview => _thumbSequence >= 0;

    /// <summary>Does what "Next" does; returns the problem shown, or null when it advanced.</summary>
    internal string? TryAdvance()
    {
        var problem = Validate(_step);
        if (problem is null && _step < _stepKeys.Length - 1)
        {
            ShowStep(_step + 1);
        }

        return problem;
    }

    /// <summary>The profile the wizard would save now (not saved).</summary>
    internal EmulatorProfile PreviewProfile => BuildProfile();

    // ------------------------------------------------------------------ navigation

    private void ShowStep(int step)
    {
        _step = step;
        WindowStep.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        AreaStep.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        DeviceStep.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        NameStep.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Steps.ItemsSource = _stepKeys.Take(step + 1).Select(k => _loc[k]).ToList();
        StepHint.Text = _loc[_stepKeys[step] + "Hint"];
        PrimaryButtonText = _loc[step == _stepKeys.Length - 1 ? "Window_Save" : "Window_Next"];
        IsSecondaryButtonEnabled = step > 0;
        Problem.IsOpen = false;

        if (step == 1)
        {
            EnterAreaStep();
        }
        else if (step == 2)
        {
            UpdateRefInfo();
        }
        else if (step == 3)
        {
            EnterNameStep();
        }
    }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var problem = Validate(_step);
        if (problem is not null)
        {
            args.Cancel = true;
            ShowProblem(problem);
            return;
        }

        if (_step < _stepKeys.Length - 1)
        {
            args.Cancel = true;
            ShowStep(_step + 1);
            return;
        }

        try
        {
            SavedProfile = BuildProfile();
            _hub.Profiles.Upsert(SavedProfile);
            _log.LogInformation("Emulator profile {Name} saved: crop {Crop}, reference {Ref}, adb {Adb}",
                SavedProfile.Name, SavedProfile.CropRect?.ToString() ?? "client area", SavedProfile.ReferenceSize, SavedProfile.AdbSerial ?? "none");
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            SavedProfile = null;
            ShowProblem(ex.Message);
        }
    }

    private void OnSecondaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_step > 0)
        {
            ShowStep(_step - 1);
        }
    }

    private void OnStepClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index < _step)
        {
            ShowStep(args.Index);
        }
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        _thumbTimer.Stop();
        StopLive();
        _areaBitmap?.Dispose();
        _areaBitmap = null;
    }

    private string? Validate(int step) => step switch
    {
        0 when _window is null => _loc["Window_PickWindowFirst"],
        0 when !WindowEnumerator.IsAlive(_window.Handle) => _loc["Window_Closed"],
        1 when _areaFrame is null => _loc["Window_AreaMissing"],
        1 when _crop is { } c && (c.Width < MinCropPx || c.Height < MinCropPx) => _loc["Window_AreaTooSmall"],
        3 when string.IsNullOrWhiteSpace(NameBox.Text) => _loc["Window_NameRequired"],
        3 => BuildRule().Validate() is { } bad ? _loc.Format("Window_RuleInvalid", bad) : null,
        _ => null,
    };

    private void ShowProblem(string message)
    {
        Problem.Message = message;
        Problem.IsOpen = true;
    }

    // ------------------------------------------------------------------ step 1: window

    private void OnRefreshWindows(object sender, RoutedEventArgs e) => RefreshWindows();

    private void RefreshWindows()
    {
        var selected = _window?.Handle ?? 0;
        _allWindows.Clear();
        _allWindows.AddRange(WindowEnumerator.GetVisibleWindows((uint)Environment.ProcessId).Select(w => new WindowChoice(w)));
        ApplyFilter();

        if (selected == 0 && _existing is { } p)
        {
            selected = WindowMatcher.FindBestMatch(p, _allWindows.Select(c => c.Window))?.Handle ?? 0;
        }

        if (selected != 0 && ((IEnumerable<WindowChoice>)WindowList.ItemsSource).FirstOrDefault(c => c.Window.Handle == selected) is { } again)
        {
            WindowList.SelectedItem = again;
            WindowList.ScrollIntoView(again);
        }
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var f = WindowFilter.Text.Trim();
        var keep = WindowList.SelectedItem;
        WindowList.ItemsSource = f.Length == 0
            ? _allWindows.ToList()
            : _allWindows.Where(c => c.Title.Contains(f, StringComparison.CurrentCultureIgnoreCase)
                || c.Window.ProcessName.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        if (keep is not null && ((List<WindowChoice>)WindowList.ItemsSource).Contains(keep))
        {
            WindowList.SelectedItem = keep;
        }
    }

    private async void OnWindowSelected(object sender, SelectionChangedEventArgs e)
    {
        if (WindowList.SelectedItem is not WindowChoice choice || choice.Window.Handle == _window?.Handle)
        {
            return;
        }

        var previous = _window;
        _window = choice.Window;
        if (previous is not null)
        {
            _crop = null; // a crop belongs to the window it was drawn on
        }

        Problem.IsOpen = false;
        await StartLiveAsync(choice.Window);
    }

    private async Task StartLiveAsync(DesktopWindowInfo window)
    {
        StopLive();
        _thumbSequence = -1;
        Thumbnail.Source = null;
        _thumbBitmap = null;
        ThumbnailPlaceholder.Text = _loc["Window_PreviewLoading"];
        ThumbnailPlaceholder.Visibility = Visibility.Visible;
        ThumbnailInfo.Text = "";

        var method = _existing?.CaptureMethod ?? WindowCaptureMethod.Auto;
        var live = new WindowFrameSource(window.Handle, new WindowFrameSourceOptions { Method = method });
        _live = live;
        try
        {
            await live.StartAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Preview capture of {Hwnd:X} failed", window.Handle);
        }

        if (!ReferenceEquals(_live, live))
        {
            await live.DisposeAsync();
            return;
        }

        if (live.State == WindowCaptureState.Faulted)
        {
            ThumbnailPlaceholder.Text = _loc.Format("Window_PreviewFailed", live.LastError ?? "");
            return;
        }

        _thumbTimer.Start();
        UpdateThumbnail();
    }

    private void StopLive()
    {
        _thumbTimer.Stop();
        var live = _live;
        _live = null;
        if (live is not null)
        {
            _ = live.DisposeAsync().AsTask();
        }
    }

    private void UpdateThumbnail()
    {
        var live = _live;
        if (live is null)
        {
            return;
        }

        if (live.State == WindowCaptureState.Faulted)
        {
            _thumbTimer.Stop();
            ThumbnailPlaceholder.Text = _loc.Format("Window_PreviewFailed", live.LastError ?? "");
            ThumbnailPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        if (live.Latest is not { } frame || frame.Sequence == _thumbSequence)
        {
            return;
        }

        _thumbSequence = frame.Sequence;
        if (_thumbBitmap is null || _thumbBitmap.PixelWidth != frame.Width || _thumbBitmap.PixelHeight != frame.Height)
        {
            _thumbBitmap = new WriteableBitmap(frame.Width, frame.Height);
            Thumbnail.Source = _thumbBitmap;
        }

        using (var stream = _thumbBitmap.PixelBuffer.AsStream())
        {
            stream.Write(frame.Bgra, 0, frame.Width * frame.Height * 4);
        }

        _thumbBitmap.Invalidate();
        ThumbnailPlaceholder.Visibility = Visibility.Collapsed;
        ThumbnailInfo.Text = string.Create(CultureInfo.InvariantCulture,
            $"{frame.Width}×{frame.Height}  ·  {live.ActiveMethod}  ·  {Math.Round(live.Fps)} fps");
    }

    // ------------------------------------------------------------------ step 2: area

    private void EnterAreaStep()
    {
        if (_areaFrame is null || _areaFrame.Size != _live?.Latest?.Size)
        {
            TakeAreaPicture();
        }

        AreaCanvas.Invalidate();
        UpdateAreaInfo();
    }

    private void OnRecapture(object sender, RoutedEventArgs e) => TakeAreaPicture();

    private void TakeAreaPicture()
    {
        _areaFrame = _live?.Latest;
        _areaBitmap?.Dispose();
        _areaBitmap = null;
        if (_areaFrame is not null && _crop is { } c && !CaptureGeometry.EffectiveCrop(c, _areaFrame.Size).Equals(c))
        {
            _crop = null; // the window was resized since the crop was chosen
        }

        AreaPlaceholder.Visibility = _areaFrame is null ? Visibility.Visible : Visibility.Collapsed;
        AreaCanvas.Invalidate();
        UpdateAreaInfo();
    }

    private void OnUseClientArea(object sender, RoutedEventArgs e)
    {
        _crop = null;
        AreaCanvas.Invalidate();
        UpdateAreaInfo();
    }

    private LetterboxFit AreaFit() => new(_areaFrame?.Size ?? default, AreaCanvas.ActualWidth, AreaCanvas.ActualHeight);

    private void OnAreaDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var frame = _areaFrame;
        if (frame is null)
        {
            return;
        }

        _areaBitmap ??= CanvasBitmap.CreateFromBytes(sender, frame.Bgra, frame.Width, frame.Height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Ignore);
        var fit = AreaFit();
        var dest = new Rect(fit.OffsetX, fit.OffsetY, fit.ContentWidth, fit.ContentHeight);
        var ds = args.DrawingSession;
        ds.DrawImage(_areaBitmap, dest, new Rect(0, 0, frame.Width, frame.Height), 1f, CanvasImageInterpolation.HighQualityCubic);

        if (_crop is not { } crop)
        {
            return;
        }

        var a = fit.SourceToViewport(new PointD(crop.X, crop.Y));
        var b = fit.SourceToViewport(new PointD(crop.Right, crop.Bottom));
        var sel = new Rect(a.X, a.Y, b.X - a.X, b.Y - a.Y);
        var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        var smoke = Application.Current.Resources.TryGetValue("SmokeFillColorDefault", out var s) && s is Windows.UI.Color sc
            ? sc
            : accent with { A = 0x40 };
        ds.FillRectangle(new Rect(dest.Left, dest.Top, dest.Width, Math.Max(0, sel.Top - dest.Top)), smoke);
        ds.FillRectangle(new Rect(dest.Left, sel.Bottom, dest.Width, Math.Max(0, dest.Bottom - sel.Bottom)), smoke);
        ds.FillRectangle(new Rect(dest.Left, sel.Top, Math.Max(0, sel.Left - dest.Left), sel.Height), smoke);
        ds.FillRectangle(new Rect(sel.Right, sel.Top, Math.Max(0, dest.Right - sel.Right), sel.Height), smoke);
        ds.DrawRectangle(sel, accent, 2f);
    }

    private void OnAreaPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_areaFrame is null)
        {
            return;
        }

        var p = e.GetCurrentPoint(AreaCanvas).Position;
        if (AreaFit().ViewportToSource(new PointD(p.X, p.Y)) is not { } start)
        {
            return;
        }

        _dragStart = start;
        _dragPointer = e.Pointer.PointerId;
        AreaCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnAreaMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is not { } start || _dragPointer != e.Pointer.PointerId || _areaFrame is null)
        {
            return;
        }

        var p = e.GetCurrentPoint(AreaCanvas).Position;
        var end = AreaFit().ViewportToSourceClamped(new PointD(p.X, p.Y));
        _crop = RectI.FromCorners(start.Round(), end.Round()).ClampTo(_areaFrame.Size);
        AreaCanvas.Invalidate();
        UpdateAreaInfo();
        e.Handled = true;
    }

    private void OnAreaReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer != e.Pointer.PointerId)
        {
            return;
        }

        _dragStart = null;
        _dragPointer = null;
        AreaCanvas.ReleasePointerCapture(e.Pointer);
        if (_crop is { } c && (c.Width < 4 || c.Height < 4))
        {
            _crop = null; // a click, not a drag
            AreaCanvas.Invalidate();
            UpdateAreaInfo();
        }
    }

    /// <summary>Size of the picture the device will show (crop, or the whole client area).</summary>
    private SizeI PictureSize => _crop is { } c ? c.Size : _areaFrame?.Size ?? _live?.Latest?.Size ?? default;

    private void UpdateAreaInfo()
    {
        var client = _areaFrame?.Size ?? default;
        AreaInfo.Text = _crop is { } c
            ? _loc.Format("Window_AreaInfoCrop", c.X, c.Y, c.Width, c.Height, client.Width, client.Height)
            : _loc.Format("Window_AreaInfoClient", client.Width, client.Height);
    }

    // ------------------------------------------------------------------ step 3: linked device and reference resolution

    private void LoadAdbDevices()
    {
        var choices = new List<AdbChoice> { new(null, _loc["Window_AdbNone"]) };
        choices.AddRange(_hub.Devices.Where(d => !d.IsWindow && d.Info.State == DeviceAdbState.Online)
            .Select(d => new AdbChoice(d.Serial, $"{d.Info.DisplayName}  ({d.Serial})")));
        var wanted = _existing?.AdbSerial;
        if (wanted is not null && choices.All(c => c.Serial != wanted))
        {
            choices.Add(new AdbChoice(wanted, _loc.Format("Window_AdbOfflineChoice", wanted)));
        }

        AdbCombo.ItemsSource = choices;
        AdbCombo.SelectedItem = _existing is not null
            ? choices.First(c => c.Serial == wanted)
            : choices.Count(c => IsEmulatorSerial(c.Serial)) == 1 ? choices.First(c => IsEmulatorSerial(c.Serial)) : choices[0];
    }

    private bool IsEmulatorSerial(string? serial) =>
        serial is not null && _hub.Devices.Any(d => d.Serial == serial && d.Info.Kind == ConnectionKind.Emulator);

    private string? SelectedAdb => (AdbCombo.SelectedItem as AdbChoice)?.Serial;

    private async void OnAdbSelected(object sender, SelectionChangedEventArgs e)
    {
        RefFromAdb.IsEnabled = SelectedAdb is not null;
        if (SelectedAdb is not null && !_refEdited)
        {
            await ReadRefFromAdbAsync();
        }

        UpdateRefInfo();
    }

    private async void OnRefFromAdb(object sender, RoutedEventArgs e) => await ReadRefFromAdbAsync();

    /// <summary>Reads <c>wm size</c> of the linked device and orients it like the picture.</summary>
    private async Task ReadRefFromAdbAsync()
    {
        if (SelectedAdb is not { } serial)
        {
            return;
        }

        try
        {
            var size = (await _hub.Adb.GetDisplaySizeAsync(serial)).Effective;
            if (size.IsEmpty)
            {
                return;
            }

            SetRef(OrientLike(size, PictureSize));
            _refEdited = false;
        }
        catch (Exception ex)
        {
            ShowProblem(_loc.Format("Window_RefReadFailed", ex.Message));
        }

        UpdateRefInfo();
    }

    /// <summary>Transposes <paramref name="size"/> when the picture has the other orientation.</summary>
    internal static SizeI OrientLike(SizeI size, SizeI picture) =>
        !picture.IsEmpty && picture.Width != picture.Height && size.Width != size.Height && picture.IsLandscape != size.IsLandscape
            ? size.Transposed
            : size;

    private void OnRefFromFrame(object sender, RoutedEventArgs e)
    {
        SetRef(PictureSize);
        _refEdited = true;
        UpdateRefInfo();
    }

    private void OnRefChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_syncingRef)
        {
            _refEdited = true;
            UpdateRefInfo();
        }
    }

    private void SetRef(SizeI size)
    {
        _syncingRef = true;
        RefWidth.Value = size.Width;
        RefHeight.Value = size.Height;
        _syncingRef = false;
    }

    private SizeI RefSize
    {
        get
        {
            var w = double.IsFinite(RefWidth.Value) ? (int)Math.Round(RefWidth.Value) : 0;
            var h = double.IsFinite(RefHeight.Value) ? (int)Math.Round(RefHeight.Value) : 0;
            return w > 0 && h > 0 ? new SizeI(w, h) : default;
        }
    }

    private void UpdateRefInfo()
    {
        var picture = PictureSize;
        var r = RefSize;
        if (r.IsEmpty)
        {
            RefInfo.Text = _loc.Format("Window_RefNone", picture.Width, picture.Height);
            return;
        }

        var text = _loc.Format("Window_RefInfo", picture.Width, picture.Height, r.Width, r.Height);
        if (!picture.IsEmpty)
        {
            var pa = (double)picture.Width / picture.Height;
            var ra = (double)r.Width / r.Height;
            if (Math.Abs(pa - ra) / ra > 0.03)
            {
                text += "  " + _loc["Window_RefAspectWarning"];
            }
        }

        RefInfo.Text = text;
    }

    // ------------------------------------------------------------------ step 4: name and match rule

    private void EnterNameStep()
    {
        if (_window is { } w)
        {
            var suggested = WindowMatcher.SuggestRule(w);
            var sameWindowAsExisting = _existing is not null && WindowMatcher.IsMatch(_existing.Match, w);
            if (!sameWindowAsExisting)
            {
                MatchTitle.Text = suggested.Title ?? "";
                MatchProcess.Text = suggested.ProcessName ?? "";
                MatchClass.Text = suggested.ClassName ?? "";
            }

            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                NameBox.Text = w.Title;
            }
        }

        var p = BuildProfile();
        Summary.Text = _loc.Format("Window_Summary",
            p.CropRect is { } c ? $"{c.X}, {c.Y}, {c.Width}×{c.Height}" : _loc["Window_ClientArea"],
            p.ReferenceSize.IsEmpty ? "-" : $"{p.ReferenceSize.Width}×{p.ReferenceSize.Height}",
            p.AdbSerial ?? _loc["Window_AdbNone"]);
    }

    private WindowMatchRule BuildRule() => new()
    {
        Title = string.IsNullOrWhiteSpace(MatchTitle.Text) ? null : MatchTitle.Text.Trim(),
        ProcessName = string.IsNullOrWhiteSpace(MatchProcess.Text) ? null : MatchProcess.Text.Trim(),
        ClassName = string.IsNullOrWhiteSpace(MatchClass.Text) ? null : MatchClass.Text.Trim(),
        TitleMode = TitleMatchMode.Contains,
    };

    private EmulatorProfile BuildProfile()
    {
        var p = _existing?.Clone() ?? new EmulatorProfile();
        p.Name = NameBox.Text.Trim();
        p.Match = BuildRule();
        if (_existing is not null && _existing.Match.TitleMode == TitleMatchMode.Regex && p.Match.Title == _existing.Match.Title)
        {
            p.Match.TitleMode = TitleMatchMode.Regex;
        }

        p.CropRect = _crop;
        p.ClientSize = _areaFrame?.Size ?? _live?.Latest?.Size ?? p.ClientSize;
        p.ReferenceSize = RefSize;
        p.AdbSerial = SelectedAdb;
        return p;
    }
}
