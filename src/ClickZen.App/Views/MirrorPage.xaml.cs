using System.ComponentModel;
using ClickZen.App.Controls;
using ClickZen.App.Services;
using ClickZen.Core.Input;
using ClickZen.Core.Persistence;
using ClickZen.Core.Settings;
using ClickZen.Device.Scrcpy;
using ClickZen.Vision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using Windows.System;

namespace ClickZen.App.Views;

public sealed partial class MirrorPage : Page
{
    private readonly DeviceHub _hub;
    private readonly ILocalizer _loc;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _infoTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private DeviceEntry? _entry;

    public MirrorPage()
    {
        var sp = App.Current.Services;
        _hub = sp.GetRequiredService<DeviceHub>();
        _loc = sp.GetRequiredService<ILocalizer>();
        _paths = sp.GetRequiredService<AppPaths>();
        _settings = sp.GetRequiredService<SettingsService>();
        InitializeComponent();

        Mirror.HighQuality = _settings.Current.Mirror.HighQualityScaling;
        Mirror.HoverChanged += OnHoverChanged;
        Mirror.PointPicked += OnPointPicked;
        Mirror.InputFailed += (_, msg) => ShowInfo(msg, InfoBarSeverity.Warning);
        _infoTimer.Tick += (_, _) => UpdateInfo();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _hub.PropertyChanged += OnHubChanged;
        Bind(_hub.Current);
        _infoTimer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _hub.PropertyChanged -= OnHubChanged;
        _infoTimer.Stop();
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

        Mirror.Show(_entry);
        UpdatePlaceholder();
        UpdateInfo();
        UpdateScrcpyTools();
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        Mirror.Show(_entry);
        UpdatePlaceholder();
        UpdateScrcpyTools();
    });

    /// <summary>Rotate / screen off / paste need the scrcpy control channel; window devices do not have one.</summary>
    private void UpdateScrcpyTools()
    {
        var scrcpy = _entry is not { IsWindow: true };
        RotateButton.IsEnabled = scrcpy;
        ScreenOffToggle.IsEnabled = scrcpy;
        PasteButton.IsEnabled = scrcpy;
    }

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

    private void UpdateInfo()
    {
        var entry = _entry;
        var frames = entry?.Frames;
        var picture = entry?.Session is { } s ? s.VideoSize : frames?.Latest?.Size ?? default;
        if (entry is null || frames is null || picture.IsEmpty)
        {
            InfoText.Text = entry?.Info.DisplayName ?? "";
            return;
        }

        var dev = entry.ScreenSize;
        InfoText.Text = _loc.Format("Mirror_Info", entry.Info.DisplayName, dev.Width, dev.Height,
            picture.Width, picture.Height, Math.Round(frames.Fps));
    }

    // ------------------------------------------------------------------ hover / pick

    private void OnHoverChanged(object? sender, MirrorPoint? p)
    {
        if (p is not { } mp)
        {
            HoverText.Text = "";
            ColorSwatch.Visibility = Visibility.Collapsed;
            return;
        }

        HoverText.Text = mp.Color is { } c
            ? $"{mp.Device.X}, {mp.Device.Y}   #{c.R:X2}{c.G:X2}{c.B:X2}"
            : $"{mp.Device.X}, {mp.Device.Y}";
        if (mp.Color is { } col)
        {
            ColorSwatch.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, col.R, col.G, col.B));
            ColorSwatch.Visibility = Visibility.Visible;
        }
    }

    private void OnPickToggle(object sender, RoutedEventArgs e)
    {
        Mirror.PickMode = PickToggle.IsChecked == true;
        ShowInfo(Mirror.PickMode ? _loc["Mirror_PickHint"] : null);
    }

    private void OnPointPicked(object? sender, MirrorPoint p)
    {
        var text = $"{p.Device.X}, {p.Device.Y}";
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        ShowInfo(_loc.Format("Mirror_PointCopied", text), InfoBarSeverity.Success);
    }

    // ------------------------------------------------------------------ keys and buttons

    private Task Key(int code) => Mirror.SendKeyAsync(code);

    private void OnBack(object sender, RoutedEventArgs e) => _ = Key(KeyCodes.Back);

    private void OnHome(object sender, RoutedEventArgs e) => _ = Key(KeyCodes.Home);

    private void OnRecents(object sender, RoutedEventArgs e) => _ = Key(KeyCodes.AppSwitch);

    private void OnVolumeUp(object sender, RoutedEventArgs e) => _ = Key(KeyCodes.VolumeUp);

    private void OnVolumeDown(object sender, RoutedEventArgs e) => _ = Key(KeyCodes.VolumeDown);

    private void OnPower(object sender, RoutedEventArgs e) => _ = Key(KeyCodes.Power);

    private async void OnRotate(object sender, RoutedEventArgs e) => await Guard(s => s.Injector.RotateAsync(CancellationToken.None));

    private async void OnScreenOffToggle(object sender, RoutedEventArgs e)
    {
        var off = ScreenOffToggle.IsChecked == true;
        await Guard(s => s.Injector.SetDisplayPowerAsync(!off, CancellationToken.None));
        ShowInfo(off ? _loc["Mirror_ScreenOffHint"] : null);
    }

    private async void OnPasteText(object sender, RoutedEventArgs e)
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text))
        {
            ShowInfo(_loc["Mirror_ClipboardEmpty"], InfoBarSeverity.Warning);
            return;
        }

        var text = await content.GetTextAsync();
        await Guard(s => s.Injector.SetClipboardAsync(text, paste: true, CancellationToken.None));
    }

    private async Task Guard(Func<ScrcpySession, Task> action)
    {
        var session = _entry?.Session;
        if (session is null)
        {
            ShowInfo(_entry is { IsWindow: true } ? _loc["Window_NeedsScrcpy"] : _loc["Mirror_NotStarted"], InfoBarSeverity.Warning);
            return;
        }

        try
        {
            await action(session);
        }
        catch (Exception ex)
        {
            ShowInfo(ex.Message, InfoBarSeverity.Error);
        }
    }

    /// <summary>Keyboard → device. Special keys go as key codes, printable text via CharacterReceived.</summary>
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
            _ = Key(c);
            e.Handled = true;
        }
    }

    private async void OnMirrorCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (char.IsControl(args.Character))
        {
            return;
        }

        args.Handled = true;
        await Mirror.SendTextAsync(args.Character.ToString());
    }

    // ------------------------------------------------------------------ screenshots

    private byte[]? CapturePng()
    {
        var frame = Mirror.CurrentFrame;
        if (frame is null)
        {
            ShowInfo(_loc["Mirror_NoFrame"], InfoBarSeverity.Warning);
            return null;
        }

        return ImageCodec.ToPng(frame);
    }

    private async void OnScreenshotSave(object sender, RoutedEventArgs e)
    {
        var png = CapturePng();
        if (png is null)
        {
            return;
        }

        Directory.CreateDirectory(_paths.ScreenshotsDirectory);
        var file = Path.Combine(_paths.ScreenshotsDirectory, $"ClickZen-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        await File.WriteAllBytesAsync(file, png);
        ShowInfo(_loc.Format("Mirror_ScreenshotSaved", file), InfoBarSeverity.Success);
    }

    private async void OnScreenshotCopy(object sender, RoutedEventArgs e)
    {
        var png = CapturePng();
        if (png is null)
        {
            return;
        }

        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var package = new DataPackage();
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        Clipboard.SetContent(package);
        ShowInfo(_loc["Mirror_ScreenshotCopied"], InfoBarSeverity.Success);
    }

    // ------------------------------------------------------------------ pop-out

    private void OnPopOut(object sender, RoutedEventArgs e)
    {
        if (_entry?.Frames is null)
        {
            ShowInfo(DeviceUi.PlaceholderText(_entry), InfoBarSeverity.Warning);
            return;
        }

        var window = new Shell.MirrorWindow(_entry);
        window.Activate();
    }

    private void ShowInfo(string? message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        if (App.Current.MainWindow is not { } w)
        {
            return;
        }

        if (message is null)
        {
            return;
        }

        w.ShowInfo(message, severity);
    }
}
