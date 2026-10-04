using System.ComponentModel;
using System.Globalization;
using ClickZen.App.Controls;
using ClickZen.App.Services;
using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;
using ClickZen.Device.Scrcpy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;

namespace ClickZen.App.Views;

/// <summary>
/// Automation page: scheme document and task list (left), task editor (centre), picture workbench (right),
/// run bar and run log (bottom). Hosts the task editor through <see cref="IAutomationEditorHost"/>.
/// </summary>
public sealed partial class AutomationPage : Page, IAutomationEditorHost
{
    private readonly DeviceHub _hub;
    private readonly ILocalizer _loc;
    private readonly IImageMatcher _matcher;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private DeviceEntry? _entry;
    private TaskItem? _dragged;
    private bool _summariesStale;
    private bool _syncingInterval;
    private bool _busyDialog;

    public AutomationPage()
    {
        var sp = App.Current.Services;
        _hub = sp.GetRequiredService<DeviceHub>();
        _loc = sp.GetRequiredService<ILocalizer>();
        _matcher = sp.GetRequiredService<IImageMatcher>();
        Service = sp.GetRequiredService<AutomationService>();
        InitializeComponent();

        Bench.Localizer = _loc;
        Bench.ScreenProvider = () => _entry is null ? default : RecordingService.CurrentScreen(_entry);
        _timer.Tick += (_, _) => OnTick();
        TaskList.DragItemsStarting += (_, e) => _dragged = e.Items.OfType<TaskItem>().FirstOrDefault();
        TaskList.DragItemsCompleted += (_, _) =>
        {
            if (_dragged is { } d)
            {
                TaskList.SelectedItem = d;
            }

            _dragged = null;
        };

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            if (Bench.Mode != WorkbenchMode.View)
            {
                Bench.CancelPick();
                e.Handled = true;
            }
        };
        KeyboardAccelerators.Add(escape);
    }

    public AutomationService Service { get; }

    /// <summary>The picture workbench (used by the self-test).</summary>
    internal Workbench Workbench => Bench;

    /// <summary>When set, <see cref="CaptureTemplateAsync"/> uses this name instead of asking (self-test).</summary>
    internal string? AutoTemplateName { get; set; }

    private TaskItem? SelectedTask => TaskList.SelectedItem as TaskItem;

    // ------------------------------------------------------------------ lifecycle

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _hub.PropertyChanged += OnHubChanged;
        Service.PropertyChanged += OnServiceChanged;
        Service.DocumentReplaced += OnDocumentReplaced;
        Service.TaskFired += OnTaskFired;
        Service.Log.CollectionChanged += OnLogChanged;
        Service.Tasks.CollectionChanged += OnTasksChanged;
        Service.Templates.CollectionChanged += OnTemplatesChanged;
        Bind(_hub.Current);
        _timer.Start();
        OnDocumentReplaced(this, EventArgs.Empty);
        UpdateRunControls();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Bench.CancelPick();
        CommitSchemeName();
        _hub.PropertyChanged -= OnHubChanged;
        Service.PropertyChanged -= OnServiceChanged;
        Service.DocumentReplaced -= OnDocumentReplaced;
        Service.TaskFired -= OnTaskFired;
        Service.Log.CollectionChanged -= OnLogChanged;
        Service.Tasks.CollectionChanged -= OnTasksChanged;
        Service.Templates.CollectionChanged -= OnTemplatesChanged;
        _timer.Stop();
        Bind(null);
    }

    private void OnHubChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceHub.Current))
        {
            DispatcherQueue.TryEnqueue(() => Bind(_hub.Current));
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

        Bench.Source = _entry?.Frames;
        UpdatePlaceholder();
        UpdateTargetText();
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (ReferenceEquals(sender, _entry))
        {
            Bench.Source = _entry?.Frames;
            UpdatePlaceholder();
        }
    });

    private void UpdatePlaceholder()
    {
        var entry = _entry;
        Bench.PlaceholderMessage = DeviceUi.PlaceholderText(entry);
    }

    private void OnTick()
    {
        if (Service.IsRunning)
        {
            Service.PollRounds();
        }

        if (_summariesStale)
        {
            _summariesStale = false;
            Service.RefreshSummaries();
            UpdateDocument();
        }

        UpdateRunStatus();
    }

    // ------------------------------------------------------------------ document display

    private void OnDocumentReplaced(object? sender, EventArgs e)
    {
        Bench.CancelPick();
        Bench.ClearOverlays();
        HideTest();
        SchemeNameBox.Text = Service.Scheme.Name;
        Bench.RefSize = Service.Scheme.RefSize;
        SyncInterval();
        TaskList.SelectedItem = Service.Tasks.FirstOrDefault();
        ShowTask(SelectedTask);
        UpdateDocument();
    }

    private void OnServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AutomationService.FilePath):
            case nameof(AutomationService.IsDirty):
            case nameof(AutomationService.DisplayName):
                UpdateDocument();
                break;
            case nameof(AutomationService.IsRunning):
            case nameof(AutomationService.IsStepping):
            case nameof(AutomationService.StatusText):
                UpdateRunControls();
                break;
            case nameof(AutomationService.CheckIntervalMs):
                SyncInterval();
                break;
        }
    }

    private void OnTasksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateDocument();

    private void OnTemplatesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateDocument();

    private void UpdateDocument()
    {
        DocTitle.Text = Service.IsDirty ? Service.DisplayName + " *" : Service.DisplayName;
        var s = Service.Scheme;
        var where = Service.FilePath ?? _loc["Auto_NotSaved"];
        var refText = s.RefSize.IsEmpty ? "-" : s.RefSize.ToString();
        DocSummary.Text = _loc.Format("Auto_DocSummary", s.Tasks.Count, s.Templates.Count, refText, where);
        NoTasksText.Visibility = Service.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoTemplatesText.Visibility = Service.Templates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var hasTask = SelectedTask is not null;
        DuplicateTaskButton.IsEnabled = DeleteTaskButton.IsEnabled = ExportButton.IsEnabled = hasTask;
        var hasTemplate = TemplateList.SelectedItem is TemplateItem;
        RenameTemplateButton.IsEnabled = DeleteTemplateButton.IsEnabled = hasTemplate;
        if (Bench.RefSize != s.RefSize)
        {
            Bench.RefSize = s.RefSize;
        }
    }

    private void OnSchemeNameCommitted(object sender, RoutedEventArgs e) => CommitSchemeName();

    private void OnSchemeNameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            CommitSchemeName();
            e.Handled = true;
        }
    }

    private void CommitSchemeName() => Service.Rename(SchemeNameBox.Text);

    // ------------------------------------------------------------------ tasks

    private void OnTaskSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_dragged is not null && SelectedTask is null)
        {
            return; // Mid drag-reorder: the item is removed and re-inserted.
        }

        ShowTask(SelectedTask);
        UpdateDocument();
    }

    private void ShowTask(TaskItem? item)
    {
        NoTaskSelected.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        ShowInEditor(item?.Task);
    }

    private void OnAddTaskClick(object sender, RoutedEventArgs e)
    {
        var item = Service.AddTask(index: SelectedTask is { } s ? Service.Tasks.IndexOf(s) + 1 : -1);
        TaskList.SelectedItem = item;
        TaskList.ScrollIntoView(item);
    }

    private void OnDuplicateTaskClick(object sender, RoutedEventArgs e)
    {
        if (SelectedTask is { } s)
        {
            var copy = Service.DuplicateTask(s);
            TaskList.SelectedItem = copy;
            TaskList.ScrollIntoView(copy);
        }
    }

    private async void OnDeleteTaskClick(object sender, RoutedEventArgs e) => await DeleteSelectedTaskAsync();

    private void OnTaskListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete && SelectedTask is not null)
        {
            _ = DeleteSelectedTaskAsync();
            e.Handled = true;
        }
    }

    private async Task DeleteSelectedTaskAsync()
    {
        if (SelectedTask is not { } s)
        {
            return;
        }

        if (!await ConfirmAsync(_loc["Auto_DeleteTaskTitle"], _loc.Format("Auto_DeleteTaskMessage", s.Name), _loc["Auto_Delete"]))
        {
            return;
        }

        var index = Service.Tasks.IndexOf(s);
        Service.DeleteTask(s);
        TaskList.SelectedItem = Service.Tasks.Count == 0 ? null : Service.Tasks[Math.Min(index, Service.Tasks.Count - 1)];
    }

    // ------------------------------------------------------------------ templates

    private TemplateItem? SelectedTemplate => TemplateList.SelectedItem as TemplateItem;

    private void OnTemplateSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDocument();
        if (SelectedTemplate is { } t && !t.Template.SourceRect.IsEmpty)
        {
            Bench.Highlight(t.Template.SourceRect, null);
        }
    }

    private async void OnCaptureTemplateClick(object sender, RoutedEventArgs e)
    {
        var result = await CaptureTemplateAsync();
        if (result is { } r)
        {
            ShowMessage(_loc.Format("Auto_TemplateCaptured", r.Template.Name, r.Template.Size.Width, r.Template.Size.Height));
        }
    }

    private async void OnRenameTemplateClick(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is { } t)
        {
            await RenameTemplateAsync(t.Template);
        }
    }

    private async void OnTemplateDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (SelectedTemplate is { } t)
        {
            await RenameTemplateAsync(t.Template);
        }
    }

    private async Task RenameTemplateAsync(TemplateAsset template)
    {
        var name = await PromptAsync(_loc["Auto_RenameTemplateTitle"], template.Name);
        if (name is not null)
        {
            Service.RenameTemplate(template, name);
            ReloadEditor();
        }
    }

    private async void OnDeleteTemplateClick(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { } t)
        {
            return;
        }

        var users = SchemeTools.TasksUsingTemplate(Service.Scheme, t.Template.Id);
        var message = users.Count == 0
            ? _loc.Format("Auto_DeleteTemplateMessage", t.Name)
            : _loc.Format("Auto_DeleteTemplateInUse", t.Name, users.Count, string.Join(", ", users.Take(5).Select(u => u.Name)));
        if (await ConfirmAsync(_loc["Auto_DeleteTemplateTitle"], message, _loc["Auto_Delete"]))
        {
            Service.DeleteTemplate(t.Template);
            Bench.ClearOverlays();
            ReloadEditor();
        }
    }

    // ------------------------------------------------------------------ IAutomationEditorHost

    public Scheme Scheme => Service.Scheme;

    public IReadOnlyCollection<string> EmbeddedRecordings => Service.Recordings.Keys;

    public void MarkDirty()
    {
        Service.MarkDirty();
        _summariesStale = true;
    }

    public async Task<PointI?> PickPointAsync()
    {
        if (!EnsurePicture())
        {
            return null;
        }

        ShowMessage(_loc["Workbench_PickPointHint"]);
        var pick = await Bench.PickPointAsync();
        if (pick is null || !PrepareAuthoring(pick.Screen))
        {
            return null;
        }

        var p = AutomationCoords.DeviceToRef(pick.DevicePoint, pick.Screen, Scheme.RefSize);
        Bench.Highlight(null, p);
        return p;
    }

    public async Task<RectI?> PickAreaAsync()
    {
        if (!EnsurePicture())
        {
            return null;
        }

        ShowMessage(_loc["Workbench_PickAreaHint"]);
        var pick = await Bench.PickAreaAsync();
        if (pick is null || !PrepareAuthoring(pick.Screen))
        {
            return null;
        }

        var r = AutomationCoords.DeviceToRef(pick.DeviceRect, pick.Screen, Scheme.RefSize);
        Bench.Highlight(r, null);
        return r;
    }

    public async Task<(TemplateAsset Template, RectI Area)?> CaptureTemplateAsync()
    {
        if (!EnsurePicture())
        {
            return null;
        }

        ShowMessage(_loc["Workbench_CaptureHint"]);
        var pick = await Bench.PickAreaAsync();
        if (pick is null || !PrepareAuthoring(pick.Screen))
        {
            return null;
        }

        var area = AutomationCoords.DeviceToRef(pick.DeviceRect, pick.Screen, Scheme.RefSize);
        if (area.Width < 4 || area.Height < 4)
        {
            ShowMessage(_loc["Auto_TemplateTooSmall"], isError: true);
            return null;
        }

        byte[] png;
        var size = AutomationCoords.TemplateSize(area);
        try
        {
            png = await Task.Run(() => TemplateImaging.CropScaled(pick.Frame, pick.FrameRect, size));
        }
        catch (Exception ex)
        {
            ShowMessage(_loc.Format("Auto_TemplateFailed", ex.Message), isError: true);
            return null;
        }

        var defaultName = SchemeTools.UniqueName(Scheme.Templates.Select(t => t.Name), _loc["Auto_TemplateDefaultName"]);
        var name = AutoTemplateName ?? await PromptAsync(_loc["Auto_NameTemplateTitle"], defaultName);
        if (name is null)
        {
            return null;
        }

        var template = new TemplateAsset
        {
            Id = SchemeTools.NewId(),
            Name = string.IsNullOrWhiteSpace(name) ? defaultName : name.Trim(),
            Size = size,
            SourceRect = area,
            Png = png,
        };
        Service.AddTemplate(template);
        Bench.Highlight(area, null);
        TemplateList.SelectedItem = Service.Templates.FirstOrDefault(t => t.Template == template);
        return (template, area);
    }

    public async Task<ConditionTestResult> TestConditionAsync(Condition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var frame = Bench.CurrentFrame;
        if (frame is null)
        {
            var msg = _loc["Auto_TestNoPicture"];
            ShowTest(false, msg);
            return new ConditionTestResult(false, double.NaN, null, msg);
        }

        var screen = Bench.Screen;
        var refSize = Scheme.RefSize;
        if (RefScaling.OrientationDiffers(refSize, screen))
        {
            ShowMessage(_loc["Auto_OrientationMismatch"], isError: true);
        }

        switch (condition)
        {
            case ImageCondition ic:
            {
                var template = Scheme.Templates.FirstOrDefault(t => t.Id == ic.TemplateId);
                var r = await Task.Run(() => ConditionProbe.ProbeImage(frame, screen, refSize, template, ic, _matcher));
                string text;
                if (r.Problem == "template")
                {
                    text = _loc["Auto_TestTemplateMissing"];
                }
                else if (r.Problem == "area")
                {
                    text = _loc["Auto_TestAreaOutside"];
                }
                else
                {
                    var score = double.IsNaN(r.Score) ? "-" : r.Score.ToString("0.000", CultureInfo.InvariantCulture);
                    var threshold = ic.Threshold.ToString("0.00", CultureInfo.InvariantCulture);
                    text = _loc.Format(r.Found ? "Auto_TestFound" : "Auto_TestNotFound", score, threshold);
                }

                Bench.Highlight(ic.Area.IsEmpty ? null : ic.Area, null);
                Bench.ShowResult(r.RefLocation, double.IsNaN(r.Score) ? null : r.Score.ToString("0.000", CultureInfo.InvariantCulture), r.Found);
                ShowTest(r.Matched, text + "  " + (r.Matched ? _loc["Auto_TestConditionTrue"] : _loc["Auto_TestConditionFalse"]));
                return new ConditionTestResult(r.Matched, r.Score, r.RefLocation, r.Problem is null ? null : text);
            }

            case ColorCondition cc:
            {
                var r = ConditionProbe.ProbeColor(frame, screen, refSize, cc);
                var text = r.Problem switch
                {
                    "color" => _loc.Format("Auto_TestBadColor", cc.Color),
                    "bounds" => _loc["Auto_TestPointOutside"],
                    _ => _loc.Format("Auto_TestColor", r.Actual ?? "-", cc.Color, r.Distance, cc.Tolerance),
                };
                Bench.Highlight(null, cc.Point);
                Bench.ShowResult(new RectI(cc.Point.X, cc.Point.Y, 0, 0), r.Actual, r.Matched);
                ShowTest(r.Matched, text + "  " + (r.Matched ? _loc["Auto_TestConditionTrue"] : _loc["Auto_TestConditionFalse"]));
                return new ConditionTestResult(r.Matched, r.Distance < 0 ? double.NaN : r.Distance, null, r.Problem is null ? null : text);
            }

            default:
            {
                var msg = _loc["Auto_TestUnsupported"];
                ShowTest(false, msg);
                return new ConditionTestResult(false, double.NaN, null, msg);
            }
        }
    }

    public void Highlight(RectI? area, PointI? point = null) => Bench.Highlight(area, point);

    public void ShowMessage(string message, bool isError = false) =>
        App.Current.MainWindow?.ShowInfo(message, isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational);

    /// <summary>True when the workbench has a picture; otherwise tells the user why not.</summary>
    private bool EnsurePicture()
    {
        if (Bench.CurrentFrame is not null)
        {
            return true;
        }

        ShowMessage(_entry is null ? _loc["Mirror_NoDevice"] : _loc["Auto_TestNoPicture"], isError: true);
        return false;
    }

    /// <summary>
    /// Adopts the current screen as the scheme's RefSize when it has none; refuses when the scheme was
    /// authored in the other orientation (coordinates would be distorted).
    /// </summary>
    private bool PrepareAuthoring(SizeI screen)
    {
        if (screen.IsEmpty)
        {
            return false;
        }

        if (Scheme.RefSize.IsEmpty)
        {
            Scheme.RefSize = screen;
            Bench.RefSize = screen;
            MarkDirty();
            UpdateDocument();
            return true;
        }

        if (!AutomationCoords.CanAuthor(Scheme.RefSize, screen))
        {
            ShowMessage(_loc.Format("Auto_OrientationAuthoring", Scheme.RefSize, screen), isError: true);
            return false;
        }

        return true;
    }

    private void ShowTest(bool ok, string text)
    {
        TestCard.Visibility = Visibility.Visible;
        TestGlyph.Glyph = ok ? "\uE73E" : "\uE711";
        TestGlyph.Foreground = AutomationUi.Resource(ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
        TestText.Text = text;
    }

    private void HideTest() => TestCard.Visibility = Visibility.Collapsed;

    private void OnClearTestClick(object sender, RoutedEventArgs e)
    {
        HideTest();
        Bench.ClearOverlays();
    }

    // ------------------------------------------------------------------ run bar

    private void SyncInterval()
    {
        _syncingInterval = true;
        IntervalBox.Value = Service.CheckIntervalMs;
        _syncingInterval = false;
    }

    private void OnIntervalChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingInterval)
        {
            return;
        }

        if (double.IsFinite(args.NewValue))
        {
            Service.CheckIntervalMs = (int)Math.Round(args.NewValue);
        }
        else
        {
            SyncInterval();
        }
    }

    private void UpdateRunControls()
    {
        var running = Service.IsRunning;
        RunText.Text = running ? _loc["Auto_Stop"] : _loc["Auto_Run"];
        RunGlyph.Glyph = running ? "\uE71A" : "\uE768";
        RunButton.IsEnabled = !Service.IsStepping;
        StepButton.IsEnabled = !running && !Service.IsStepping;
        TargetButton.IsEnabled = !running;
        UpdateRunStatus();
    }

    private void UpdateRunStatus()
    {
        if (Service.IsRunning)
        {
            var devices = Service.Runs.Count(r => r.State == EngineState.Running);
            RunStatusText.Text = _loc.Format("Auto_RunStatus", devices, Service.TotalRounds, Service.LastFired ?? "-");
        }
        else if (Service.IsStepping)
        {
            RunStatusText.Text = _loc["Auto_Stepping"];
        }
        else
        {
            RunStatusText.Text = Service.Runs.Count > 0
                ? _loc.Format("Auto_RunFinished", Service.TotalRounds, Service.LastFired ?? "-")
                : _loc["Auto_Ready"];
        }
    }

    private void UpdateTargetText()
    {
        var targets = Service.ResolveTargets();
        TargetText.Text = Service.TargetSerials.Count == 0
            ? _loc["Auto_TargetCurrent"]
            : targets.Count == 1 ? targets[0].Info.DisplayName : _loc.Format("Auto_TargetCount", targets.Count);
    }

    private void OnTargetFlyoutOpening(object sender, object e)
    {
        TargetFlyout.Items.Clear();
        var current = new ToggleMenuFlyoutItem { Text = _loc["Auto_TargetCurrent"], IsChecked = Service.TargetSerials.Count == 0 };
        current.Click += (_, _) =>
        {
            Service.TargetSerials.Clear();
            UpdateTargetText();
        };
        TargetFlyout.Items.Add(current);
        TargetFlyout.Items.Add(new MenuFlyoutSeparator());
        foreach (var d in _hub.Devices)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = d.Frames is null ? _loc.Format("Auto_TargetNotMirroring", d.Info.DisplayName) : d.Info.DisplayName,
                IsChecked = Service.TargetSerials.Contains(d.Serial),
                IsEnabled = d.Frames is not null,
            };
            var serial = d.Serial;
            item.Click += (s, _) =>
            {
                if (((ToggleMenuFlyoutItem)s).IsChecked)
                {
                    Service.TargetSerials.Add(serial);
                }
                else
                {
                    Service.TargetSerials.Remove(serial);
                }

                UpdateTargetText();
            };
            TargetFlyout.Items.Add(item);
        }
    }

    private async void OnRunClick(object sender, RoutedEventArgs e) => await ToggleRunAsync();

    internal async Task ToggleRunAsync()
    {
        if (Service.IsRunning)
        {
            await Service.StopAsync();
            return;
        }

        if (Service.IsStepping)
        {
            return;
        }

        CommitSchemeName();
        Bench.CancelPick();
        if (Service.Scheme.Tasks.Count == 0)
        {
            ShowMessage(_loc["Auto_NothingToRun"], isError: true);
            return;
        }

        var problems = await Service.StartAsync(Service.ResolveTargets());
        if (problems.Count > 0)
        {
            ShowMessage(string.Join("\n", problems), isError: true);
        }

        if (Service.Validate().Count > 0)
        {
            ShowLog(true);
        }
    }

    private async void OnStepClick(object sender, RoutedEventArgs e)
    {
        Bench.CancelPick();
        var problems = await Service.StepAsync(Service.ResolveTargets());
        if (problems.Count > 0)
        {
            ShowMessage(string.Join("\n", problems), isError: true);
        }

        ShowLog(true);
        Bench.Refresh();
    }

    private void OnTaskFired(object? sender, TaskFiredInfo e)
    {
        // Show where a match was clicked on the workbench (the engine reports device pixels).
        if (e.Args.Match is { } m && ReferenceEquals(e.Run.Entry, _entry))
        {
            var screen = Bench.Screen;
            var p = AutomationCoords.DeviceToRef(new PointI(m.Location.X, m.Location.Y), screen, Bench.OverlaySpace);
            Bench.Highlight(null, p);
        }
    }

    private void OnLogToggleClick(object sender, RoutedEventArgs e) => ShowLog(LogToggle.IsChecked == true);

    private void ShowLog(bool show)
    {
        LogToggle.IsChecked = show;
        LogPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && Service.Log.Count > 0)
        {
            LogList.ScrollIntoView(Service.Log[^1]);
        }
    }

    private void OnLogChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (LogPanel.Visibility == Visibility.Visible && Service.Log.Count > 0
            && e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
        {
            LogList.ScrollIntoView(Service.Log[^1]);
        }
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e) => Service.ClearLog();

    // ------------------------------------------------------------------ files

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!Service.IsDirty || (Service.Scheme.Tasks.Count == 0 && Service.Scheme.Templates.Count == 0))
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _loc["Auto_UnsavedTitle"],
            Content = _loc.Format("Auto_UnsavedMessage", Service.DisplayName),
            PrimaryButtonText = _loc["Auto_SaveChanges"],
            SecondaryButtonText = _loc["Auto_DontSave"],
            CloseButtonText = _loc["Common_Cancel"],
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowDialogAsync(dialog) switch
        {
            ContentDialogResult.Primary => await SaveAsync(saveAs: false),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }

    private async void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (Service.IsRunning)
        {
            ShowMessage(_loc["Auto_StopFirst"], isError: true);
            return;
        }

        CommitSchemeName();
        if (await ConfirmDiscardAsync())
        {
            Service.New();
        }
    }

    public Task SaveDocumentAsync() => SaveAsync(saveAs: false);

    public Task OpenDocumentAsync() => OpenAsync();

    private async void OnOpenClick(object sender, RoutedEventArgs e) => await OpenAsync();

    private async Task OpenAsync()
    {
        if (Service.IsRunning)
        {
            ShowMessage(_loc["Auto_StopFirst"], isError: true);
            return;
        }

        CommitSchemeName();
        if (!await ConfirmDiscardAsync())
        {
            return;
        }

        var path = await PickOpenAsync();
        if (path is null)
        {
            return;
        }

        try
        {
            Service.Open(path);
            App.Current.MainWindow?.ShowInfo(_loc.Format("Auto_Opened", Path.GetFileName(path)), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage(_loc.Format("Auto_OpenFailed", ex.Message), isError: true);
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e) => await SaveAsync(saveAs: false);

    private async void OnSaveAsClick(object sender, RoutedEventArgs e) => await SaveAsync(saveAs: true);

    private async Task<bool> SaveAsync(bool saveAs)
    {
        CommitSchemeName();
        var path = saveAs ? null : Service.FilePath;
        path ??= await PickSaveAsync(Service.FilePath is null
            ? (string.IsNullOrWhiteSpace(Service.Scheme.Name) ? $"Scheme-{DateTime.Now:yyyyMMdd-HHmmss}" : Service.Scheme.Name)
            : Path.GetFileNameWithoutExtension(Service.FilePath));
        if (path is null)
        {
            return false;
        }

        try
        {
            Service.Save(path);
            SchemeNameBox.Text = Service.Scheme.Name;
            App.Current.MainWindow?.ShowInfo(_loc.Format("Auto_Saved", Service.FilePath), InfoBarSeverity.Success);
            return true;
        }
        catch (Exception ex)
        {
            ShowMessage(_loc.Format("Auto_SaveFailed", ex.Message), isError: true);
            return false;
        }
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var path = await PickOpenAsync();
        if (path is null)
        {
            return;
        }

        try
        {
            var r = Service.ImportTasks(path);
            if (r.Tasks.Count > 0)
            {
                TaskList.SelectedItem = Service.FindTask(r.Tasks[0].Id);
            }

            var msg = _loc.Format("Auto_Imported", r.Tasks.Count, r.Templates);
            App.Current.MainWindow?.ShowInfo(r.Rescaled ? msg + " " + _loc["Auto_ImportRescaled"] : msg, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage(_loc.Format("Auto_ImportFailed", ex.Message), isError: true);
        }
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (SelectedTask is not { } task)
        {
            return;
        }

        var path = await PickSaveAsync(string.IsNullOrWhiteSpace(task.Task.Name) ? "Task" : task.Task.Name);
        if (path is null)
        {
            return;
        }

        try
        {
            Service.ExportTask(task, path);
            App.Current.MainWindow?.ShowInfo(_loc.Format("Auto_Exported", task.Name, path), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage(_loc.Format("Auto_SaveFailed", ex.Message), isError: true);
        }
    }

    private async Task<string?> PickOpenAsync()
    {
        try
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedStartFolder = Service.DefaultDirectory,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(SchemePackage.FileExtension);
            var result = await picker.PickSingleFileAsync();
            return result?.Path;
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, isError: true);
            return null;
        }
    }

    private async Task<string?> PickSaveAsync(string suggestedName)
    {
        try
        {
            var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFolder = Service.DefaultDirectory,
                SuggestedFileName = Sanitize(suggestedName),
                DefaultFileExtension = SchemePackage.FileExtension,
            };
            picker.FileTypeChoices.Add(_loc["Auto_FileType"], [SchemePackage.FileExtension]);
            var result = await picker.PickSaveFileAsync();
            return result?.Path;
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, isError: true);
            return null;
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "Scheme" : s;
    }

    // ------------------------------------------------------------------ dialogs

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_busyDialog)
        {
            return ContentDialogResult.None;
        }

        _busyDialog = true;
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            App.Current.Services.GetRequiredService<ILogger<AutomationPage>>().LogWarning(ex, "Dialog could not be shown");
            return ContentDialogResult.None;
        }
        finally
        {
            _busyDialog = false;
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = _loc["Common_Cancel"],
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private async Task<string?> PromptAsync(string title, string initial)
    {
        var box = new TextBox { Text = initial, SelectionStart = 0, SelectionLength = initial.Length, MinWidth = 280 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = _loc["Common_Ok"],
            CloseButtonText = _loc["Common_Cancel"],
            DefaultButton = ContentDialogButton.Primary,
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                dialog.Hide();
                e.Handled = true;
                _enterPressed = true;
            }
        };
        _enterPressed = false;
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        var result = await ShowDialogAsync(dialog);
        return result == ContentDialogResult.Primary || _enterPressed ? box.Text.Trim() : null;
    }

    private bool _enterPressed;
}
