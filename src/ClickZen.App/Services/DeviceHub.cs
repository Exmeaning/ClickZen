using System.Collections.ObjectModel;
using ClickZen.Core.Devices;
using ClickZen.Core.Settings;
using ClickZen.Device;
using ClickZen.Device.Adb;
using ClickZen.Device.Decoding;
using ClickZen.Device.Scrcpy;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace ClickZen.App.Services;

/// <summary>
/// UI-facing model of one device: adb info plus its (optional) scrcpy session.
/// All property changes are raised on the UI thread.
/// </summary>
public sealed partial class DeviceEntry : ObservableObject
{
    public DeviceEntry(DeviceInfo info) => Info = info;

    [ObservableProperty]
    public partial DeviceInfo Info { get; set; }

    [ObservableProperty]
    public partial ScrcpySession? Session { get; set; }

    [ObservableProperty]
    public partial SessionState SessionState { get; set; } = SessionState.Disconnected;

    [ObservableProperty]
    public partial string? SessionError { get; set; }

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    public string Serial => Info.Serial;

    partial void OnInfoChanged(DeviceInfo value)
    {
        OnPropertyChanged(nameof(Serial));
        if (Session is not null)
        {
            Session.DeviceSizeOverride = value.PhysicalSize;
        }
    }
}

/// <summary>
/// Owns the adb server, the device list and one scrcpy session per connected device;
/// tracks the "current" device shown in the title bar. Singleton.
/// </summary>
public sealed partial class DeviceHub : ObservableObject, IAsyncDisposable
{
    private readonly AdbServerHost _server;
    private readonly AdbService _adb;
    private readonly DeviceWatcher _watcher;
    private readonly AutoConnector _autoConnector;
    private readonly SettingsService _settings;
    private readonly BundledTools _tools;
    private readonly IScrcpyTransport _transport;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<DeviceHub> _log;
    private DispatcherQueue? _ui;
    private bool _ffmpegReady;

    public DeviceHub(AdbServerHost server, AdbService adb, DeviceWatcher watcher, AutoConnector autoConnector,
        SettingsService settings, BundledTools tools, IScrcpyTransport transport, ILoggerFactory loggers)
    {
        _server = server;
        _adb = adb;
        _watcher = watcher;
        _autoConnector = autoConnector;
        _settings = settings;
        _tools = tools;
        _transport = transport;
        _loggers = loggers;
        _log = loggers.CreateLogger<DeviceHub>();
        _watcher.DevicesChanged += OnDevicesChanged;
        _watcher.TrackingChanged += (_, tracking) => Ui(() => IsAdbAvailable = tracking);
    }

    public ObservableCollection<DeviceEntry> Devices { get; } = [];

    [ObservableProperty]
    public partial DeviceEntry? Current { get; set; }

    [ObservableProperty]
    public partial bool IsAdbAvailable { get; set; }

    [ObservableProperty]
    public partial string? StartupError { get; set; }

    public AdbService Adb => _adb;

    partial void OnCurrentChanged(DeviceEntry? oldValue, DeviceEntry? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsCurrent = false;
        }

        if (newValue is not null)
        {
            newValue.IsCurrent = true;
        }
    }

    /// <summary>Starts adb and device tracking. Call once from the UI thread after the window exists.</summary>
    public async Task StartAsync(DispatcherQueue ui, CancellationToken ct = default)
    {
        _ui = ui;
        try
        {
            var status = await _server.EnsureStartedAsync(ct);
            _log.LogInformation("adb server {Mode} (version {Version}) at {Path}", status.Mode, status.ServerVersion, status.AdbPath);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "adb server could not be started");
            StartupError = ex.Message;
        }

        await _watcher.StartAsync(ct);
        Sync(_watcher.Devices);

        if (_settings.Current.Devices.AutoConnectSaved)
        {
            _ = Task.Run(() => _autoConnector.ConnectSavedAsync(CancellationToken.None), CancellationToken.None);
        }
    }

    /// <summary>Forces a fresh device list (the list normally updates by itself).</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _watcher.RefreshAsync(ct);
        Ui(() => Sync(_watcher.Devices));
    }

    /// <summary>
    /// The input backend for a device, following the user's preference with automatic fallback:
    /// scrcpy control channel when connected, otherwise <c>adb shell input</c>; Root uses sendevent
    /// when a touchscreen was detected (falls back to adb input as root).
    /// </summary>
    public async Task<ClickZen.Core.Input.ITouchInjector> GetInjectorAsync(DeviceEntry entry, CancellationToken ct = default)
    {
        var pref = _settings.Current.Devices.InputMethod;
        if (pref == InputMethodPreference.Root)
        {
            var touch = await ClickZen.Device.Input.RootSendeventInjector.DetectTouchscreenAsync(_adb, entry.Serial, ct);
            if (touch is not null)
            {
                return new ClickZen.Device.Input.RootSendeventInjector(
                    (cmd, c) => _adb.RootShellAsync(entry.Serial, cmd, c), touch,
                    () => entry.Info.PhysicalSize,
                    () => entry.Session is { } s && !s.VideoSize.IsEmpty && s.VideoSize.IsLandscape != entry.Info.PhysicalSize.IsLandscape
                        ? ClickZen.Core.Geometry.DisplayRotation.Rotation90
                        : ClickZen.Core.Geometry.DisplayRotation.Rotation0);
            }

            _log.LogWarning("No touchscreen found for root input on {Serial}; using adb input as root", entry.Serial);
            return ClickZen.Device.Input.AdbInputInjector.For(_adb, entry.Serial, entry.Info.SdkLevel, asRoot: true);
        }

        if (pref == InputMethodPreference.Scrcpy && entry.Session is { IsControlAvailable: true } session)
        {
            return session.Injector;
        }

        return ClickZen.Device.Input.AdbInputInjector.For(_adb, entry.Serial, entry.Info.SdkLevel);
    }

    /// <summary>Starts the scrcpy session for a device (no-op if already running).</summary>
    public async Task ConnectAsync(DeviceEntry entry, CancellationToken ct = default)
    {
        if (entry.Session is not null || entry.Info.State != DeviceAdbState.Online)
        {
            return;
        }

        EnsureFfmpeg();
        var d = _settings.Current.Devices;
        var m = _settings.Current.Mirror;
        var session = new ScrcpySession(entry.Serial, _transport, new ScrcpySessionOptions
        {
            MaxSize = d.MaxSize,
            VideoBitRate = d.VideoBitRateMbps * 1_000_000,
            MaxFps = d.MaxFps,
            StayAwake = m.StayAwake,
            ShowTouches = m.ShowTouches,
            AutoReconnect = d.AutoReconnect,
        }, _loggers.CreateLogger<ScrcpySession>())
        {
            DeviceSizeOverride = entry.Info.PhysicalSize,
        };
        session.StateChanged += (_, s) => Ui(() =>
        {
            entry.SessionState = s;
            entry.SessionError = session.LastError;
        });
        entry.Session = session;
        Current ??= entry;
        await session.StartAsync(ct);
        entry.SessionState = session.State;
        entry.SessionError = session.LastError;
    }

    public async Task DisconnectAsync(DeviceEntry entry)
    {
        var session = entry.Session;
        if (session is null)
        {
            return;
        }

        entry.Session = null;
        await session.DisposeAsync();
        entry.SessionState = SessionState.Disconnected;
    }

    private void EnsureFfmpeg()
    {
        if (_ffmpegReady)
        {
            return;
        }

        FfmpegVideoDecoder.Initialize(_tools.FfmpegDirectory);
        _ffmpegReady = true;
    }

    private void OnDevicesChanged(object? sender, DeviceListChangedEventArgs e) => Ui(() => Sync(e.Devices));

    /// <summary>Reconciles <see cref="Devices"/> with the adb list (UI thread).</summary>
    private void Sync(IReadOnlyList<DeviceInfo> list)
    {
        var bySerial = list.ToDictionary(d => d.Serial, StringComparer.Ordinal);
        foreach (var gone in Devices.Where(e => !bySerial.ContainsKey(e.Serial)).ToList())
        {
            Devices.Remove(gone);
            _ = DisconnectAsync(gone);
            if (Current == gone)
            {
                Current = Devices.FirstOrDefault();
            }
        }

        foreach (var info in list)
        {
            var existing = Devices.FirstOrDefault(e => e.Serial == info.Serial);
            if (existing is null)
            {
                var entry = new DeviceEntry(info);
                Devices.Add(entry);
                Current ??= entry;
                if (info.State == DeviceAdbState.Online && _settings.Current.Mirror.OpenMirrorOnConnect)
                {
                    _ = ConnectQuietlyAsync(entry);
                }
            }
            else if (existing.Info != info)
            {
                var cameOnline = existing.Info.State != DeviceAdbState.Online && info.State == DeviceAdbState.Online;
                existing.Info = info;
                if (cameOnline && _settings.Current.Mirror.OpenMirrorOnConnect)
                {
                    _ = ConnectQuietlyAsync(existing);
                }
            }
        }
    }

    private async Task ConnectQuietlyAsync(DeviceEntry entry)
    {
        try
        {
            await ConnectAsync(entry);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not start mirroring for {Serial}", entry.Serial);
            entry.SessionError = ex.Message;
        }
    }

    private void Ui(Action action)
    {
        if (_ui is null || _ui.HasThreadAccess)
        {
            action();
        }
        else
        {
            _ui.TryEnqueue(() => action());
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var e in Devices.ToList())
        {
            await DisconnectAsync(e);
        }

        await _watcher.DisposeAsync();
    }
}
