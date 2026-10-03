using System.ComponentModel;
using ClickZen.App.Services;
using ClickZen.Core;
using ClickZen.Device.Scrcpy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinUIEx;

namespace ClickZen.App.Shell;

public sealed partial class MainWindow : Window
{
    private readonly ILocalizer _loc;
    private readonly DeviceHub _hub;
    private readonly RecordingService _recording;
    private readonly AutomationService _automation;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DeviceEntry? _observed;
    private bool _syncingSwitcher;

    public MainWindow(IServiceProvider services)
    {
        _loc = services.GetRequiredService<ILocalizer>();
        _hub = services.GetRequiredService<DeviceHub>();
        _recording = services.GetRequiredService<RecordingService>();
        _automation = services.GetRequiredService<AutomationService>();
        InitializeComponent();

        Title = AppInfo.Name;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ClickZen.ico"));

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var manager = WindowManager.Get(this);
        manager.MinWidth = 1100;
        manager.MinHeight = 700;
        manager.PersistenceId = "ClickZenMainWindow";

        ThemeService.ApplyBackdrop(this);
        services.GetRequiredService<ThemeService>().Register(this);

        BuildNavigation();
        StatusVersion.Text = $"{AppInfo.Name} {AppInfo.Version}";
        StatusEngine.Text = _automation.StatusText;
        _automation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AutomationService.StatusText) or nameof(AutomationService.IsRunning))
            {
                DispatcherQueue.TryEnqueue(() => StatusEngine.Text = _automation.StatusText);
            }
        };
        UpdateRecordingStatus();
        _recording.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateRecordingStatus);
        StatusSync.Text = _loc["Status_SyncOff"];

        DeviceSwitcher.ItemsSource = _hub.Devices;
        DeviceSwitcher.PlaceholderText = _loc["TitleBar_NoDevice"];
        _hub.PropertyChanged += OnHubPropertyChanged;
        _hub.Devices.CollectionChanged += (_, _) => SyncSwitcher();
        SyncSwitcher();

        _statusTimer.Tick += (_, _) => UpdateDeviceStatus();
        _statusTimer.Start();
        Closed += (_, _) => _statusTimer.Stop();
    }

    public Frame Frame => ContentFrame;

    private void UpdateRecordingStatus()
    {
        StatusRecording.Text = _recording.IsRecording
            ? _loc["Status_RecordingActive"]
            : _recording.IsPlaying && _recording.PlaybackIndex >= 0
                ? _loc.Format("Status_RecordingPlaying", _recording.PlaybackIndex + 1, _recording.PlaybackLoop)
                : _loc["Status_RecordingIdle"];
    }

    /// <summary>Shows a message at the top of the content area.</summary>
    public void ShowInfo(string message, InfoBarSeverity severity = InfoBarSeverity.Informational, string? title = null)
    {
        GlobalInfoBar.Title = title ?? "";
        GlobalInfoBar.Message = message;
        GlobalInfoBar.Severity = severity;
        GlobalInfoBar.IsOpen = true;
    }

    private void BuildNavigation()
    {
        foreach (var page in NavigationPages.All)
        {
            var item = new NavigationViewItem
            {
                Content = _loc[page.ResourceKey],
                Tag = page.Tag,
                Icon = new FontIcon { Glyph = page.Glyph ?? "\uE8A5" },
            };
            ToolTipService.SetToolTip(item, item.Content);
            if (page.IsFooter)
            {
                NavView.FooterMenuItems.Add(item);
            }
            else
            {
                NavView.MenuItems.Add(item);
            }
        }

        NavView.SelectedItem = NavView.MenuItems[0];
    }

    public void NavigateTo(string tag)
    {
        var item = NavView.MenuItems.Concat(NavView.FooterMenuItems).OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null)
        {
            NavView.SelectedItem = item;
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && NavigationPages.ByTag(tag) is { } page
            && ContentFrame.CurrentSourcePageType != page.PageType)
        {
            ContentFrame.Navigate(page.PageType, null, new EntranceNavigationTransitionInfo());
        }
    }

    // ------------------------------------------------------------------ device switcher

    private void OnHubPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceHub.Current))
        {
            DispatcherQueue.TryEnqueue(SyncSwitcher);
        }
        else if (e.PropertyName == nameof(DeviceHub.StartupError) && _hub.StartupError is { } err)
        {
            DispatcherQueue.TryEnqueue(() => ShowInfo(err, InfoBarSeverity.Error, _loc["Devices_AdbStartFailed"]));
        }
    }

    private void SyncSwitcher()
    {
        _syncingSwitcher = true;
        DeviceSwitcher.SelectedItem = _hub.Current;
        _syncingSwitcher = false;
        Observe(_hub.Current);
        UpdateDeviceStatus();
    }

    private void OnDeviceSwitcherSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSwitcher && DeviceSwitcher.SelectedItem is DeviceEntry entry)
        {
            _hub.Current = entry;
        }
    }

    private void Observe(DeviceEntry? entry)
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnCurrentDeviceChanged;
        }

        _observed = entry;
        if (entry is not null)
        {
            entry.PropertyChanged += OnCurrentDeviceChanged;
        }
    }

    private void OnCurrentDeviceChanged(object? sender, PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(UpdateDeviceStatus);

    private void UpdateDeviceStatus()
    {
        var entry = _hub.Current;
        if (entry is null)
        {
            DeviceStateText.Text = "";
            DeviceStateDot.Fill = (Brush)Application.Current.Resources["SystemFillColorNeutralBrush"];
            StatusDevice.Text = _loc["TitleBar_NoDevice"];
            return;
        }

        var (key, brush) = entry.SessionState switch
        {
            SessionState.Streaming => ("SessionState_Streaming", "SystemFillColorSuccessBrush"),
            SessionState.Connected => ("SessionState_Connected", "SystemFillColorSuccessBrush"),
            SessionState.Connecting => ("SessionState_Connecting", "SystemFillColorCautionBrush"),
            SessionState.Reconnecting => ("SessionState_Reconnecting", "SystemFillColorCautionBrush"),
            SessionState.Faulted => ("SessionState_Faulted", "SystemFillColorCriticalBrush"),
            _ => ("SessionState_Disconnected", "SystemFillColorNeutralBrush"),
        };
        DeviceStateText.Text = _loc[key];
        DeviceStateDot.Fill = (Brush)Application.Current.Resources[brush];

        var session = entry.Session;
        StatusDevice.Text = session is { State: SessionState.Streaming }
            ? _loc.Format("Status_DeviceStreaming", entry.Info.DisplayName, session.VideoSize.Width, session.VideoSize.Height, Math.Round(session.Fps))
            : entry.Info.DisplayName;
    }
}
