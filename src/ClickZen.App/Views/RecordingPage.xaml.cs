using System.ComponentModel;
using ClickZen.App.Controls;
using ClickZen.App.Services;
using ClickZen.Core.Input;
using ClickZen.Core.Recording;
using ClickZen.Core.Settings;
using ClickZen.Device.Scrcpy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;

namespace ClickZen.App.Views;

public sealed partial class RecordingPage : Page
{
    private readonly DeviceHub _hub;
    private readonly ILocalizer _loc;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private DeviceEntry? _entry;
    private bool _syncingSelection;

    public RecordingPage()
    {
        var sp = App.Current.Services;
        _hub = sp.GetRequiredService<DeviceHub>();
        _loc = sp.GetRequiredService<ILocalizer>();
        _settings = sp.GetRequiredService<SettingsService>();
        Recorder = sp.GetRequiredService<RecordingService>();
        InitializeComponent();

        Mirror.HighQuality = _settings.Current.Mirror.HighQualityScaling;
        Mirror.HoverChanged += OnHoverChanged;
        Mirror.PointPicked += OnPointPicked;
        Mirror.InputFailed += (_, msg) => ShowInfo(msg, InfoBarSeverity.Warning);
        Mirror.TouchForwarded += (_, e) => Recorder.FeedTouch(e);
        Mirror.KeyForwarded += (_, code) => RecordKey(code);
        _timer.Tick += (_, _) => UpdateTicker();
    }

    public RecordingService Recorder { get; }

    private IReadOnlyList<Gesture> Selected =>
        Timeline.SelectedItems.OfType<GestureItem>().Select(i => i.Gesture).ToList();

    // ------------------------------------------------------------------ lifecycle

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _hub.PropertyChanged += OnHubChanged;
        Recorder.PropertyChanged += OnRecorderChanged;
        Recorder.GestureAdded += OnGestureAdded;
        Recorder.ItemsReset += OnItemsReset;
        Recorder.Items.CollectionChanged += OnItemsChanged;
        Bind(_hub.Current);
        _timer.Start();
        UpdateDocument();
        UpdateRecordControls();
        UpdatePlaybackText();
        UpdateEditor();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // Mirror input only reaches the recorder through this page.
        if (Recorder.RecordingSource == RecordingInputSource.Mirror)
        {
            Recorder.StopRecording();
        }

        _hub.PropertyChanged -= OnHubChanged;
        Recorder.PropertyChanged -= OnRecorderChanged;
        Recorder.GestureAdded -= OnGestureAdded;
        Recorder.ItemsReset -= OnItemsReset;
        Recorder.Items.CollectionChanged -= OnItemsChanged;
        _timer.Stop();
        Bind(null);
    }

    private void OnHubChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceHub.Current))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Recorder.RecordingSource == RecordingInputSource.Mirror && !ReferenceEquals(_entry, _hub.Current))
                {
                    Recorder.StopRecording();
                }

                Bind(_hub.Current);
            });
        }
    }

    private void Bind(DeviceEntry? entry)
    {
        if (_entry is not null)
        {
            _entry.PropertyChanged -= OnEntryChanged;
        }

        _entry = entry;
        if (_entry is not null)
        {
            _entry.PropertyChanged += OnEntryChanged;
        }

        Mirror.Show(_entry);
        UpdatePlaceholder();
        UpdateTicker();
        UpdateRecordControls();
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!ReferenceEquals(sender, _entry))
        {
            return;
        }

        if (Recorder.RecordingSource == RecordingInputSource.Mirror && _entry?.Frames is null)
        {
            Recorder.StopRecording();
        }

        Mirror.Show(_entry);
        UpdatePlaceholder();
        UpdateRecordControls();
    });

    private void OnRecorderChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RecordingService.IsRecording):
            case nameof(RecordingService.IsPlaying):
                UpdateRecordControls();
                UpdatePlaybackText();
                UpdateEditor();
                break;
            case nameof(RecordingService.FilePath):
            case nameof(RecordingService.IsDirty):
                UpdateDocument();
                break;
            case nameof(RecordingService.PlaybackIndex):
            case nameof(RecordingService.PlaybackLoop):
                UpdatePlaybackText();
                break;
        }
    }

    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateDocument();

    private void OnGestureAdded(object? sender, GestureItem item) => Timeline.ScrollIntoView(item);

    private void OnItemsReset(object? sender, IReadOnlyList<Gesture> select)
    {
        _syncingSelection = true;
        try
        {
            Timeline.SelectedItems.Clear();
            foreach (var item in Recorder.Items.Where(i => select.Any(g => ReferenceEquals(g, i.Gesture))))
            {
                Timeline.SelectedItems.Add(item);
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        if (Timeline.SelectedItems.Count > 0)
        {
            Timeline.ScrollIntoView(Timeline.SelectedItems[0]);
        }

        UpdateEditor();
    }

    // ------------------------------------------------------------------ status

    private void UpdatePlaceholder()
    {
        var entry = _entry;
        if (entry is null)
        {
            Mirror.PlaceholderMessage = _loc["Mirror_NoDevice"];
            Mirror.PlaceholderBusy = false;
            return;
        }

        Mirror.PlaceholderBusy = entry.SessionState is SessionState.Connecting or SessionState.Reconnecting;
        Mirror.PlaceholderMessage = DeviceUi.PlaceholderText(entry);
    }

    private void UpdateTicker()
    {
        var screen = _entry?.Frames?.Latest is null ? default : _entry.ScreenSize;
        InfoText.Text = screen.IsEmpty
            ? _entry?.Info.DisplayName ?? ""
            : _loc.Format("Rec_DeviceInfo", _entry!.Info.DisplayName, screen.Width, screen.Height);

        if (Recorder.IsRecording)
        {
            ElapsedText.Text = Recorder.RecordingSource == RecordingInputSource.Mirror
                ? GestureItem.FormatTime((long)Recorder.RecordingElapsed.TotalMilliseconds)[..^4]
                : _loc["Rec_RecordingOnDevice"];
        }
    }

    private void UpdateDocument()
    {
        DocTitle.Text = Recorder.IsDirty ? Recorder.DisplayName + " *" : Recorder.DisplayName;
        var doc = Recorder.Document;
        var screen = doc.ScreenSize.IsEmpty ? "-" : doc.ScreenSize.ToString();
        DocSummary.Text = _loc.Format("Rec_DocSummary", Recorder.Items.Count, GestureItem.FormatTime(doc.DurationMs), screen);
        EmptyText.Visibility = Recorder.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateEditor();
    }

    private void UpdateRecordControls()
    {
        var recording = Recorder.IsRecording;
        RecordText.Text = recording ? _loc["Rec_StopRecording"] : _loc["Rec_StartRecording"];
        RecordGlyph.Glyph = recording ? "\uE71A" : "\uE7C8";
        RecordOnDeviceButton.Visibility = !recording && Recorder.CanRecordOnDevice(_entry) ? Visibility.Visible : Visibility.Collapsed;
        RecordOnDeviceButton.IsEnabled = !Recorder.IsPlaying;

        // Pointer input during playback would fight the injected gestures.
        Mirror.InputEnabled = !Recorder.IsPlaying;
        if (recording && PickToggle.IsChecked == true)
        {
            PickToggle.IsChecked = false;
            Mirror.PickMode = false;
        }
    }

    private void UpdatePlaybackText()
    {
        var hasItems = Recorder.Items.Count > 0;
        var idle = !Recorder.IsPlaying && !Recorder.IsRecording;
        PlayButton.IsEnabled = idle && hasItems;
        PlayFromButton.IsEnabled = idle && Timeline.SelectedItems.Count > 0;

        if (!Recorder.IsPlaying)
        {
            PlaybackText.Text = "";
            return;
        }

        var index = Recorder.PlaybackIndex;
        var loops = (int)Recorder.Loops;
        var loopText = loops <= 0
            ? _loc.Format("Rec_LoopInfinite", Recorder.PlaybackLoop)
            : _loc.Format("Rec_LoopOf", Recorder.PlaybackLoop, loops);
        PlaybackText.Text = index < 0
            ? _loc["Rec_PlaybackStarting"]
            : _loc.Format("Rec_PlaybackProgress", index + 1, Recorder.Items.Count, loopText);
        if (index >= 0 && index < Recorder.Items.Count)
        {
            Timeline.ScrollIntoView(Recorder.Items[index]);
        }
    }

    private void UpdateEditor()
    {
        var selected = Selected;
        var editable = selected.Count > 0 && !Recorder.IsPlaying && !Recorder.IsRecording;
        var single = selected.Count == 1 ? selected[0] : null;
        var touch = selected.Any(g => GestureItem.IsTouchKind(g.Kind));

        SelectionText.Text = selected.Count == 0 ? _loc["Rec_NoSelection"] : _loc.Format("Rec_SelectionCount", selected.Count);
        StartBox.IsEnabled = SetStartButton.IsEnabled = editable && single is not null;
        DeltaBox.IsEnabled = ShiftButton.IsEnabled = editable;
        RippleCheck.IsEnabled = editable;
        var singleTouch = editable && single is not null && GestureItem.IsTouchKind(single.Kind);
        XBox.IsEnabled = YBox.IsEnabled = MoveToButton.IsEnabled = PickToggle.IsEnabled = singleTouch;
        DxBox.IsEnabled = DyBox.IsEnabled = TranslateButton.IsEnabled = editable && touch;
        DeleteButton.IsEnabled = editable;
        if (!singleTouch && PickToggle.IsChecked == true)
        {
            PickToggle.IsChecked = false;
            Mirror.PickMode = false;
        }

        if (single is not null)
        {
            StartBox.Value = single.StartMs;
            if (single.Fingers.Count > 0)
            {
                var p = single.Fingers[0].Start.Round();
                XBox.Value = p.X;
                YBox.Value = p.Y;
            }
            else
            {
                XBox.Value = YBox.Value = double.NaN;
            }
        }
        else
        {
            StartBox.Value = XBox.Value = YBox.Value = double.NaN;
        }

        UpdatePlaybackText();
    }

    // ------------------------------------------------------------------ recording

    public void ToggleRecording()
    {
        OnRecordClick(this, new RoutedEventArgs());
    }

    public async Task TogglePlaybackAsync()
    {
        if (Recorder.IsPlaying) Recorder.StopPlayback();
        else await PlayAsync(0);
    }

    public Task SaveDocumentAsync() => SaveAsync(saveAs: false);

    public Task OpenDocumentAsync() => OpenAsync();

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (Recorder.IsRecording)
        {
            Recorder.StopRecording();
            return;
        }

        if (_entry is null || _entry.Frames is not { } frames)
        {
            ShowInfo(DeviceUi.PlaceholderText(_entry), InfoBarSeverity.Warning);
            return;
        }

        var screen = _entry.ScreenSize;
        if (frames.Latest is null || screen.IsEmpty)
        {
            ShowInfo(_loc["Mirror_NoFrame"], InfoBarSeverity.Warning);
            return;
        }

        if (Recorder.Items.Count > 0 && Recorder.OrientationDiffers(_entry))
        {
            ShowInfo(_loc["Rec_OrientationWarning"], InfoBarSeverity.Warning);
        }

        Recorder.StartRecording(_entry, screen, RecordingInputSource.Mirror);
        Mirror.Focus(FocusState.Programmatic);
    }

    private async void OnRecordOnDeviceClick(object sender, RoutedEventArgs e)
    {
        if (_entry is not { } entry)
        {
            ShowInfo(_loc["Mirror_NoDevice"], InfoBarSeverity.Warning);
            return;
        }

        try
        {
            ShowInfo(_loc["Rec_RecordOnDeviceHint"]);
            await Recorder.RecordOnDeviceAsync(entry);
        }
        catch (Exception ex)
        {
            ShowInfo(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void RecordKey(int keyCode) => Recorder.FeedKey(keyCode, Environment.TickCount64);

    private async Task SendKeyAndRecord(int code)
    {
        // MirrorView.SendKeyAsync raises KeyForwarded, which records the key.
        await Mirror.SendKeyAsync(code);
    }

    /// <summary>Keyboard -> device (same mapping as the mirror page), recorded while recording.</summary>
    private void OnMirrorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        int? code = e.Key switch
        {
            VirtualKey.Back => KeyCodes.Delete,
            VirtualKey.Enter => KeyCodes.Enter,
            VirtualKey.Escape => KeyCodes.Back,
            VirtualKey.Home => KeyCodes.Home,
            VirtualKey.Left => 21,
            VirtualKey.Right => 22,
            VirtualKey.Up => 19,
            VirtualKey.Down => 20,
            VirtualKey.Tab => 61,
            VirtualKey.Delete => 112,
            _ => null,
        };
        if (code is { } c)
        {
            _ = SendKeyAndRecord(c);
            e.Handled = true;
        }
    }

    private async void OnMirrorCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (char.IsControl(args.Character) || _entry?.Frames is null)
        {
            return;
        }

        args.Handled = true;
        var text = args.Character.ToString();
        Recorder.FeedText(text, Environment.TickCount64);
        await Mirror.SendTextAsync(text); // failures surface through Mirror.InputFailed
    }

    private void OnHoverChanged(object? sender, MirrorPoint? p) =>
        HoverText.Text = p is { } mp ? $"{mp.Device.X}, {mp.Device.Y}" : "";

    // ------------------------------------------------------------------ timeline / editing

    private void OnTimelineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSelection)
        {
            UpdateEditor();
        }
    }

    private void OnTimelineKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete && DeleteButton.IsEnabled)
        {
            DeleteSelected();
            e.Handled = true;
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        var selected = Selected;
        var firstIndex = Timeline.SelectedIndex;
        Recorder.Delete(selected.ToList());
        if (Recorder.Items.Count > 0 && firstIndex >= 0)
        {
            Timeline.SelectedIndex = Math.Min(firstIndex, Recorder.Items.Count - 1);
        }
    }

    private void OnSetStartClick(object sender, RoutedEventArgs e)
    {
        if (Selected is [var g] && double.IsFinite(StartBox.Value))
        {
            Recorder.SetStartTime(g, (long)Math.Round(StartBox.Value), RippleCheck.IsChecked == true);
        }
    }

    private void OnShiftTimeClick(object sender, RoutedEventArgs e)
    {
        if (double.IsFinite(DeltaBox.Value))
        {
            Recorder.ShiftTime(Selected.ToList(), (long)Math.Round(DeltaBox.Value), RippleCheck.IsChecked == true);
        }
    }

    private void OnMoveToClick(object sender, RoutedEventArgs e)
    {
        if (Selected is [var g] && double.IsFinite(XBox.Value) && double.IsFinite(YBox.Value))
        {
            Recorder.MoveTo(g, XBox.Value, YBox.Value);
        }
    }

    private void OnTranslateClick(object sender, RoutedEventArgs e)
    {
        var dx = double.IsFinite(DxBox.Value) ? DxBox.Value : 0;
        var dy = double.IsFinite(DyBox.Value) ? DyBox.Value : 0;
        Recorder.Translate(Selected.ToList(), dx, dy);
    }

    private void OnPickToggle(object sender, RoutedEventArgs e)
    {
        Mirror.PickMode = PickToggle.IsChecked == true;
        if (Mirror.PickMode)
        {
            ShowInfo(_loc["Rec_PickHint"]);
        }
    }

    /// <summary>Picked device point -> X/Y of the selected gesture. Rescaled when the document uses another screen size.</summary>
    private void OnPointPicked(object? sender, MirrorPoint p)
    {
        var docScreen = Recorder.Document.ScreenSize;
        var device = _entry?.ScreenSize ?? default;
        var x = (double)p.Device.X;
        var y = (double)p.Device.Y;
        if (!docScreen.IsEmpty && !device.IsEmpty)
        {
            x = Math.Round(x * docScreen.Width / device.Width);
            y = Math.Round(y * docScreen.Height / device.Height);
        }

        XBox.Value = x;
        YBox.Value = y;
        PickToggle.IsChecked = false;
        Mirror.PickMode = false;
    }

    // ------------------------------------------------------------------ files

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!Recorder.IsDirty || Recorder.Items.Count == 0)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _loc["Rec_UnsavedTitle"],
            Content = _loc.Format("Rec_UnsavedMessage", Recorder.DisplayName),
            PrimaryButtonText = _loc["Rec_SaveChanges"],
            SecondaryButtonText = _loc["Rec_DontSave"],
            CloseButtonText = _loc["Common_Cancel"],
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => await SaveAsync(saveAs: false),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }

    private async void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (Recorder.IsPlaying || !await ConfirmDiscardAsync())
        {
            return;
        }

        Recorder.New();
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e) => await OpenAsync();

    private async Task OpenAsync()
    {
        if (Recorder.IsPlaying || !await ConfirmDiscardAsync())
        {
            return;
        }

        try
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedStartFolder = Recorder.DefaultDirectory,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(RecordingDocument.FileExtension);
            var result = await picker.PickSingleFileAsync();
            if (result is null)
            {
                return;
            }

            Recorder.Open(result.Path);
            ShowInfo(_loc.Format("Rec_Opened", Path.GetFileName(result.Path)), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo(_loc.Format("Rec_OpenFailed", ex.Message), InfoBarSeverity.Error);
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e) => await SaveAsync(saveAs: false);

    private async void OnSaveAsClick(object sender, RoutedEventArgs e) => await SaveAsync(saveAs: true);

    private async Task<bool> SaveAsync(bool saveAs)
    {
        try
        {
            var path = saveAs ? null : Recorder.FilePath;
            if (path is null)
            {
                var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFolder = Recorder.DefaultDirectory,
                    SuggestedFileName = Recorder.FilePath is null
                        ? $"Recording-{DateTime.Now:yyyyMMdd-HHmmss}"
                        : Path.GetFileNameWithoutExtension(Recorder.FilePath),
                    DefaultFileExtension = RecordingDocument.FileExtension,
                };
                picker.FileTypeChoices.Add(_loc["Rec_FileType"], [RecordingDocument.FileExtension]);
                var result = await picker.PickSaveFileAsync();
                if (result is null)
                {
                    return false;
                }

                path = result.Path;
            }

            Recorder.Save(path);
            ShowInfo(_loc.Format("Rec_Saved", path), InfoBarSeverity.Success);
            return true;
        }
        catch (Exception ex)
        {
            ShowInfo(_loc.Format("Rec_SaveFailed", ex.Message), InfoBarSeverity.Error);
            return false;
        }
    }

    // ------------------------------------------------------------------ playback

    private void OnPlayClick(object sender, RoutedEventArgs e) => _ = PlayAsync(0);

    private void OnPlayFromSelectionClick(object sender, RoutedEventArgs e)
    {
        var index = Timeline.SelectedItems.OfType<GestureItem>().Select(i => Recorder.Items.IndexOf(i)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        _ = PlayAsync(index);
    }

    private void OnStopPlaybackClick(object sender, RoutedEventArgs e) => Recorder.StopPlayback();

    private async Task PlayAsync(int startIndex)
    {
        if (_entry is not { } entry)
        {
            ShowInfo(_loc["Mirror_NoDevice"], InfoBarSeverity.Warning);
            return;
        }

        if (Recorder.OrientationDiffers(entry))
        {
            ShowInfo(_loc["Rec_OrientationWarning"], InfoBarSeverity.Warning);
        }

        try
        {
            var outcome = await Recorder.PlayAsync(entry, startIndex);
            ShowInfo(outcome == PlaybackOutcome.Completed ? _loc["Rec_PlaybackDone"] : _loc["Rec_PlaybackStopped"],
                outcome == PlaybackOutcome.Completed ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowInfo(_loc.Format("Rec_PlaybackFailed", ex.Message), InfoBarSeverity.Error);
        }
    }

    private static void ShowInfo(string message, InfoBarSeverity severity = InfoBarSeverity.Informational) =>
        App.Current.MainWindow?.ShowInfo(message, severity);
}
