using System.Collections.ObjectModel;
using ClickZen.Core.Automation;
using ClickZen.Core.Devices;
using ClickZen.Core.Emulators;
using ClickZen.Core.Geometry;
using ClickZen.Device.Adb;
using ClickZen.Platform.Capture;
using ClickZen.Platform.Emulators;
using ClickZen.Platform.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace ClickZen.App.Services;

/// <summary>Connection state of a detected emulator instance, as shown on the Devices page.</summary>
public enum EmulatorConnectionState
{
    /// <summary>The port is known and nothing is connected to it.</summary>
    NotConnected,
    /// <summary>An adb device with the instance's serial (or an equivalent one) is online.</summary>
    Connected,
    /// <summary>adb knows the device but it is offline / unauthorized.</summary>
    Offline,
    /// <summary>The emulator's own ADB switch is off (BlueStacks, LDPlayer): connecting cannot work.</summary>
    AdbDisabled,
    /// <summary>The adb port could not be determined.</summary>
    PortUnknown,
}

/// <summary>One detected emulator instance with its live connection and binding state (UI thread).</summary>
public sealed partial class DetectedEmulator : ObservableObject
{
    public DetectedEmulator(EmulatorInstance instance) => Instance = instance;

    [ObservableProperty]
    public partial EmulatorInstance Instance { get; set; }

    [ObservableProperty]
    public partial EmulatorConnectionState State { get; set; }

    /// <summary>The adb serial ClickZen sees for the instance when connected (e.g. "emulator-5554" or "127.0.0.1:16384").</summary>
    [ObservableProperty]
    public partial string? ConnectedSerial { get; set; }

    /// <summary>The window device bound to this instance ("window:…"), if any.</summary>
    [ObservableProperty]
    public partial string? BoundDevice { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public string Key => Instance.Key;

    /// <summary>The serial to use for this instance: the connected one, else the address to connect to.</summary>
    public string? Serial => ConnectedSerial ?? Instance.AdbAddress;

    public bool IsConnected => State == EmulatorConnectionState.Connected;

    public bool IsBound => BoundDevice is not null;

    partial void OnStateChanged(EmulatorConnectionState value) => OnPropertyChanged(nameof(IsConnected));

    partial void OnBoundDeviceChanged(string? value) => OnPropertyChanged(nameof(IsBound));

    partial void OnConnectedSerialChanged(string? value) => OnPropertyChanged(nameof(Serial));

    partial void OnInstanceChanged(EmulatorInstance value)
    {
        OnPropertyChanged(nameof(Key));
        OnPropertyChanged(nameof(Serial));
    }
}

/// <summary>Outcome of connecting a detected emulator.</summary>
public sealed record EmulatorConnectResult(bool Success, string Message, string? Serial);

/// <summary>Outcome of one-click window binding.</summary>
/// <param name="Success">A profile was saved and its window device is capturing.</param>
/// <param name="NeedsReview">The screen area is a guess: <see cref="Profile"/> is prefilled for the wizard and was not saved.</param>
/// <param name="DeviceId">The window device ("window:…") when saved.</param>
public sealed record EmulatorBindResult(bool Success, bool NeedsReview, string Message, EmulatorProfile? Profile, string? DeviceId, ScreenAreaMethod Method);

/// <summary>
/// Detects running emulators (<see cref="EmulatorDetector"/>), keeps <see cref="Instances"/> in sync with the adb device
/// list and connects them with one click. Detection runs on demand (Devices page, the scan tab, MCP) on a background
/// thread; it never talks to adb, connecting does. Singleton; collections change on the UI thread only.
/// </summary>
public sealed partial class EmulatorService : ObservableObject
{
    private readonly DeviceHub _hub;
    private readonly AdbService _adb;
    private readonly IImageMatcher _matcher;
    private readonly ILocalizer _loc;
    private readonly ILogger<EmulatorService> _log;
    private readonly EmulatorDetector _detector;
    private readonly SemaphoreSlim _detectGate = new(1, 1);
    private DispatcherQueue? _ui;

    public EmulatorService(DeviceHub hub, AdbService adb, IImageMatcher matcher, ILocalizer loc, ILoggerFactory loggers)
        : this(hub, adb, matcher, loc, loggers, new SystemEmulatorEnvironment((uint)Environment.ProcessId))
    {
    }

    internal EmulatorService(DeviceHub hub, AdbService adb, IImageMatcher matcher, ILocalizer loc, ILoggerFactory loggers, IEmulatorEnvironment environment)
    {
        _hub = hub;
        _adb = adb;
        _matcher = matcher;
        _loc = loc;
        _log = loggers.CreateLogger<EmulatorService>();
        _detector = new EmulatorDetector(environment, loggers.CreateLogger<EmulatorDetector>());
        // Rows follow the adb list: devices coming and going, and a listed device going online / offline (Info changes).
        _hub.Devices.CollectionChanged += OnHubDevicesChanged;
        foreach (var d in _hub.Devices)
        {
            d.PropertyChanged += OnHubDeviceChanged;
        }

        _hub.Profiles.Changed += (_, _) => Ui(RefreshStates);
    }

    private void OnHubDevicesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (var d in e.OldItems?.OfType<DeviceEntry>() ?? [])
        {
            d.PropertyChanged -= OnHubDeviceChanged;
        }

        foreach (var d in e.NewItems?.OfType<DeviceEntry>() ?? [])
        {
            d.PropertyChanged += OnHubDeviceChanged;
        }

        Ui(RefreshStates);
    }

    private void OnHubDeviceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceEntry.Info))
        {
            Ui(RefreshStates);
        }
    }

    /// <summary>Running instances from the last detection.</summary>
    public ObservableCollection<DetectedEmulator> Instances { get; } = [];

    /// <summary>Emulators installed on this PC (from the registry), running or not.</summary>
    public ObservableCollection<InstalledEmulator> Installed { get; } = [];

    [ObservableProperty]
    public partial bool IsDetecting { get; set; }

    /// <summary>When the last detection finished; null before the first one.</summary>
    [ObservableProperty]
    public partial DateTimeOffset? LastDetected { get; set; }

    public bool HasInstances => Instances.Count > 0;

    // ------------------------------------------------------------------ detection

    /// <summary>
    /// Runs detection on a background thread and updates <see cref="Instances"/> (on the UI thread). Concurrent calls
    /// share one run. Returns the instances found. Never throws for detection failures (they are logged).
    /// </summary>
    public async Task<IReadOnlyList<DetectedEmulator>> DetectAsync(CancellationToken ct = default)
    {
        _ui ??= App.Current.MainWindow?.DispatcherQueue;
        await _detectGate.WaitAsync(ct);
        try
        {
            await RunOnUiAsync(() => IsDetecting = true);
            EmulatorDetection result;
            try
            {
                result = await Task.Run(_detector.Detect, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Emulator detection failed");
                result = EmulatorDetection.Empty;
            }

            return await OnUiAsync(() =>
            {
                Apply(result);
                LastDetected = DateTimeOffset.Now;
                return (IReadOnlyList<DetectedEmulator>)Instances.ToList();
            });
        }
        finally
        {
            await RunOnUiAsync(() => IsDetecting = false);
            _detectGate.Release();
        }
    }

    /// <summary>Merges a detection into <see cref="Instances"/>, keeping existing rows (and their busy state) by key.</summary>
    private void Apply(EmulatorDetection result)
    {
        var keys = result.Running.Select(i => i.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in Instances.Where(i => !keys.Contains(i.Key)).ToList())
        {
            Instances.Remove(gone);
        }

        for (var n = 0; n < result.Running.Count; n++)
        {
            var instance = result.Running[n];
            var existing = Instances.FirstOrDefault(i => string.Equals(i.Key, instance.Key, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                Instances.Insert(Math.Min(n, Instances.Count), new DetectedEmulator(instance));
            }
            else
            {
                existing.Instance = instance;
                var at = Instances.IndexOf(existing);
                if (at != n && n < Instances.Count)
                {
                    Instances.Move(at, n);
                }
            }
        }

        Installed.Clear();
        foreach (var i in result.Installed)
        {
            Installed.Add(i);
        }

        RefreshStates();
        OnPropertyChanged(nameof(HasInstances));
    }

    /// <summary>Recomputes every row's connection state and bound window device from the hub (UI thread).</summary>
    private void RefreshStates()
    {
        var devices = _hub.Devices.Where(d => !d.IsWindow).ToList();
        var profiles = SafeProfiles();
        foreach (var row in Instances)
        {
            var i = row.Instance;
            var device = devices.Where(d => i.OwnsSerial(d.Serial))
                .OrderByDescending(d => d.Info.State == DeviceAdbState.Online)
                .ThenByDescending(d => d.Serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            row.ConnectedSerial = device?.Info.State == DeviceAdbState.Online ? device.Serial : null;
            row.State = device switch
            {
                { Info.State: DeviceAdbState.Online } => EmulatorConnectionState.Connected,
                not null => EmulatorConnectionState.Offline,
                _ when i.AdbEnabled == false => EmulatorConnectionState.AdbDisabled,
                _ when i.AdbPort is null => EmulatorConnectionState.PortUnknown,
                _ => EmulatorConnectionState.NotConnected,
            };
            row.BoundDevice = EmulatorProfileFactory.FindExisting(i, profiles) is { } p ? DeviceHub.WindowSerial(p.Id) : null;
        }
    }

    private IReadOnlyList<EmulatorProfile> SafeProfiles()
    {
        try
        {
            return _hub.Profiles.GetAll();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not read emulator profiles");
            return [];
        }
    }

    // ------------------------------------------------------------------ lookup (MCP and UI)

    /// <summary>
    /// Finds a detected instance by key ("mumu:MuMuPlayer-12.0-0"), instance name, display name, adb serial / port,
    /// or position in the list ("0", "1"…). Null when nothing (or more than one thing) matches.
    /// </summary>
    public DetectedEmulator? Find(string? query)
    {
        var q = (query ?? "").Trim();
        var list = Instances.ToList();
        if (q.Length == 0)
        {
            return list.Count == 1 ? list[0] : null;
        }

        var exact = list.FirstOrDefault(i => i.Key.Equals(q, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(i => i.Instance.InstanceKey.Equals(q, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(i => i.Instance.Name.Equals(q, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(i => string.Equals(i.Serial, q, StringComparison.OrdinalIgnoreCase) || i.Instance.OwnsSerial(q))
            ?? (int.TryParse(q, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n < list.Count ? list[n] : null);
        if (exact is not null)
        {
            return exact;
        }

        var partial = list.Where(i => i.Instance.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || i.Instance.InstanceKey.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0] : null;
    }

    // ------------------------------------------------------------------ connect

    /// <summary>
    /// Connects an instance over adb: nothing to do when it is already online (adb finds AVD / LDPlayer instances by
    /// itself as emulator-NNNN); otherwise <c>adb connect 127.0.0.1:port</c>, then the fallback ports (MuMu moves to the
    /// next free port when its own is taken). On success the device becomes the current device when
    /// <paramref name="makeCurrent"/>.
    /// </summary>
    public async Task<EmulatorConnectResult> ConnectAsync(DetectedEmulator row, bool makeCurrent = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        var i = row.Instance;
        if (row.State == EmulatorConnectionState.Connected && row.ConnectedSerial is { } already)
        {
            if (makeCurrent)
            {
                await RunOnUiAsync(() => SelectDevice(already));
            }

            return new EmulatorConnectResult(true, _loc.Format("Emu_AlreadyConnected", i.Name, already), already);
        }

        if (i.AdbPort is not int port)
        {
            return new EmulatorConnectResult(false, _loc.Format("Emu_PortUnknown", i.Name), null);
        }

        await RunOnUiAsync(() => row.IsBusy = true);
        try
        {
            AdbConnectResult? last = null;
            foreach (var p in new[] { port }.Concat(i.FallbackPorts))
            {
                ct.ThrowIfCancellationRequested();
                last = await _adb.ConnectAsync(EmulatorSerials.Address(p), ct);
                if (last.Success)
                {
                    _log.LogInformation("Emulator {Key} connected at {Address} ({Kind})", i.Key, last.Address, last.Kind);
                    var serial = await WaitForDeviceAsync(i, last.Address, ct);
                    if (makeCurrent && serial is not null)
                    {
                        await RunOnUiAsync(() => SelectDevice(serial));
                    }

                    return new EmulatorConnectResult(true, _loc.Format("Emu_Connected", i.Name, serial ?? last.Address), serial ?? last.Address);
                }

                _log.LogInformation("Emulator {Key}: adb connect {Address} failed [{Kind}] {Message}", i.Key, last.Address, last.Kind, last.Message);
            }

            var hint = i.AdbEnabled == false ? _loc["Emu_HintEnableAdb"] : i.Kind == EmulatorKind.LDPlayer ? _loc["Emu_HintLdAdb"] : _loc["Emu_HintStarting"];
            return new EmulatorConnectResult(false, _loc.Format("Emu_ConnectFailed", i.Name, last?.Message ?? "", hint), null);
        }
        catch (AdbException ex)
        {
            _log.LogWarning(ex, "Connecting emulator {Key} failed", i.Key);
            return new EmulatorConnectResult(false, _loc.Format("Emu_ConnectFailed", i.Name, ex.Message, ""), null);
        }
        finally
        {
            await RunOnUiAsync(() =>
            {
                row.IsBusy = false;
                RefreshStates();
            });
        }
    }

    /// <summary>Connects every detected instance that is not connected yet; returns one result per attempt.</summary>
    public async Task<IReadOnlyList<EmulatorConnectResult>> ConnectAllAsync(CancellationToken ct = default)
    {
        var rows = await OnUiAsync(() => Instances.Where(i => i.State is EmulatorConnectionState.NotConnected or EmulatorConnectionState.Offline).ToList());
        var results = await Task.WhenAll(rows.Select(r => ConnectAsync(r, makeCurrent: false, ct)));
        if (results.FirstOrDefault(r => r.Success)?.Serial is { } first && await OnUiAsync(() => _hub.Current is null))
        {
            await RunOnUiAsync(() => SelectDevice(first));
        }

        return results;
    }

    /// <summary>
    /// After <c>adb connect</c> the device appears through device tracking a moment later; waits (up to 5 s) for an
    /// entry owned by the instance and returns its serial ("emulator-5554" wins over "127.0.0.1:5555" for AVDs).
    /// </summary>
    private async Task<string?> WaitForDeviceAsync(EmulatorInstance instance, string address, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 25; attempt++)
        {
            var serial = await OnUiAsync(() => _hub.Devices
                .Where(d => !d.IsWindow && d.Info.State == DeviceAdbState.Online && (instance.OwnsSerial(d.Serial) || d.Serial == address))
                .OrderByDescending(d => d.Serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Serial)
                .FirstOrDefault());
            if (serial is not null)
            {
                return serial;
            }

            await Task.Delay(200, ct);
        }

        return null;
    }

    private void SelectDevice(string serial)
    {
        if (_hub.Devices.FirstOrDefault(d => !d.IsWindow && d.Serial == serial) is { } entry)
        {
            _hub.Current = entry;
        }
    }

    // ------------------------------------------------------------------ one-click window binding

    /// <summary>
    /// Binds the instance's window as a window device without the wizard:
    /// <list type="number">
    /// <item>connects adb when needed (reference resolution and input come from it);</item>
    /// <item>captures the window and locates the Android screen in it – client area with the device's aspect ratio,
    /// the emulator's render child window, flat bars trimmed off the picture, or the device's own screenshot found in
    /// the picture (template matching);</item>
    /// <item>saves an <see cref="EmulatorProfile"/> (updating the one bound to this instance before) and starts capturing.</item>
    /// </list>
    /// When the screen area is only a guess the profile is returned unsaved with <see cref="EmulatorBindResult.NeedsReview"/>
    /// so the UI can open the prefilled wizard (MCP reports it instead).
    /// </summary>
    public async Task<EmulatorBindResult> BindWindowAsync(DetectedEmulator row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        var i = row.Instance;
        if (!i.HasWindow || !WindowEnumerator.IsAlive(i.WindowHandle))
        {
            return Failed(_loc.Format("Emu_BindNoWindow", i.Name));
        }

        if (i.WindowMinimized || Platform.Windows.WindowMetrics.TryQuery(i.WindowHandle, out var m) && m.IsMinimized)
        {
            return Failed(_loc.Format("Emu_BindMinimized", i.Name));
        }

        await RunOnUiAsync(() => row.IsBusy = true);
        try
        {
            // 1. adb (optional: without it the window device can still be captured, just not tapped).
            var serial = row.IsConnected ? row.ConnectedSerial : null;
            if (serial is null && i.AdbPort is not null)
            {
                await RunOnUiAsync(() => row.IsBusy = false);
                var c = await ConnectAsync(row, makeCurrent: false, ct);
                await RunOnUiAsync(() => row.IsBusy = true);
                serial = c.Success ? c.Serial : null;
            }

            var device = default(SizeI);
            if (serial is not null)
            {
                try
                {
                    device = (await _adb.GetDisplaySizeAsync(serial, ct)).Effective;
                }
                catch (AdbException ex)
                {
                    _log.LogWarning(ex, "Could not read the screen size of {Serial}", serial);
                }
            }

            // 2. A picture of the window (client area).
            var frame = await WindowFrameSource.CaptureOnceAsync(i.WindowHandle, null, WindowCaptureMethod.Auto, TimeSpan.FromSeconds(4), ct);
            if (frame is null)
            {
                return Failed(_loc.Format("Emu_BindCaptureFailed", i.Name));
            }

            // 3. Where the Android screen is.
            var area = await LocateScreenAsync(i, frame, device, serial, ct);
            var reference = device.IsEmpty ? default : ScreenOrientation.OrientLike(device, area.Crop?.Size ?? frame.Size);

            var existing = EmulatorProfileFactory.FindExisting(i, SafeProfiles());
            var name = existing?.Name is { Length: > 0 } kept ? kept : EmulatorUi.KindName(i.Kind) + " · " + i.Name;
            var profile = EmulatorProfileFactory.Create(i, name, area.Crop, frame.Size, reference, serial, existing);
            _log.LogInformation("One-click binding of {Key}: area {Area} via {Method} ({Confidence:0.00}), client {Client}, reference {Ref}, adb {Adb}",
                i.Key, area.Crop?.ToString() ?? "client area", area.Method, area.Confidence, frame.Size, reference, serial ?? "none");

            if (area.Confidence < ScreenAreaLocator.ConfidentEnough)
            {
                return new EmulatorBindResult(false, true, _loc.Format("Emu_BindReview", i.Name), profile, null, area.Method);
            }

            // 4. Save and start capturing.
            var deviceId = await OnUiAsync(() =>
            {
                _hub.Profiles.Upsert(profile);
                return DeviceHub.WindowSerial(profile.Id);
            });
            await RunOnUiAsync(async () =>
            {
                if (_hub.FindWindowDevice(profile.Id) is { } entry)
                {
                    _hub.Current = entry;
                    await _hub.ConnectAsync(entry, ct);
                }
            }).Unwrap();

            var how = _loc["Emu_Method_" + area.Method];
            var message = serial is null
                ? _loc.Format("Emu_BoundNoAdb", i.Name, how)
                : _loc.Format("Emu_Bound", i.Name, how);
            return new EmulatorBindResult(true, false, message, profile, deviceId, area.Method);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "One-click binding of {Key} failed", i.Key);
            return Failed(_loc.Format("Emu_BindFailed", i.Name, ex.Message));
        }
        finally
        {
            await RunOnUiAsync(() =>
            {
                row.IsBusy = false;
                RefreshStates();
            });
        }

        EmulatorBindResult Failed(string message) => new(false, false, message, null, null, ScreenAreaMethod.ClientArea);
    }

    /// <summary>The best screen area among the locator methods, most reliable first.</summary>
    private async Task<ScreenArea> LocateScreenAsync(EmulatorInstance i, Frame frame, SizeI device, string? serial, CancellationToken ct)
    {
        var client = frame.Size;
        if (!device.IsEmpty && ScreenAreaLocator.FromAspect(client, device) is { } aspect)
        {
            return aspect;
        }

        // The emulator's render child window.
        var def = EmulatorCatalog.Get(i.Kind);
        var children = ChildWindows.Enumerate(i.WindowHandle).Where(c => c.Visible).ToList();
        foreach (var pattern in def.RenderWindows)
        {
            foreach (var c in children.Where(c => (pattern.ClassName is null || c.ClassName == pattern.ClassName) && (pattern.Title is null || c.Title == pattern.Title)))
            {
                if (ScreenAreaLocator.FromRenderWindow(c.Rect, client, device) is { } render)
                {
                    return render;
                }
            }
        }

        // The device's screenshot found in the window picture (most general, needs adb).
        if (serial is not null && !device.IsEmpty)
        {
            try
            {
                var png = await _adb.ScreencapPngAsync(serial, ct);
                var shot = Vision.ImageCodec.MeasurePng(png);
                if (await Task.Run(() => ScreenAreaLocator.FromScreenshot(frame, png, shot, _matcher), ct) is { } matched)
                {
                    return matched;
                }
            }
            catch (Exception ex) when (ex is AdbException or IOException or InvalidOperationException or ArgumentException)
            {
                _log.LogInformation(ex, "Screenshot matching for {Key} not possible", i.Key);
            }
        }

        // Flat title bar / tool bar / borders around the picture.
        if (!device.IsEmpty && ScreenAreaLocator.FromEdges(frame, device) is { } edges)
        {
            return edges;
        }

        // The single large child window as a last structured guess (unknown emulator layouts).
        var big = children.Where(c => c.Rect.Width * c.Rect.Height >= client.Width * client.Height / 2).OrderByDescending(c => c.Rect.Width * c.Rect.Height).ToList();
        if (big.Count == 1 && ScreenAreaLocator.FromRenderWindow(big[0].Rect, client, device) is { } child)
        {
            return child with { Confidence = Math.Min(child.Confidence, 0.6) };
        }

        return ScreenArea.WholeClient;
    }

    // ------------------------------------------------------------------ threading

    private void Ui(Action action)
    {
        var ui = _ui ??= App.Current.MainWindow?.DispatcherQueue;
        if (ui is null || ui.HasThreadAccess)
        {
            action();
        }
        else
        {
            ui.TryEnqueue(() => action());
        }
    }

    private Task RunOnUiAsync(Action action) => OnUiAsync(() =>
    {
        action();
        return true;
    });

    /// <summary>Starts <paramref name="action"/> on the UI thread; the returned task completes with the task it returned.</summary>
    private Task<Task> RunOnUiAsync(Func<Task> action) => OnUiAsync(action);

    private Task<T> OnUiAsync<T>(Func<T> func)
    {
        var ui = _ui ??= App.Current.MainWindow?.DispatcherQueue;
        if (ui is null || ui.HasThreadAccess)
        {
            return Task.FromResult(func());
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!ui.TryEnqueue(() =>
            {
                try
                {
                    tcs.SetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
        {
            tcs.SetException(new InvalidOperationException("The UI thread is not available."));
        }

        return tcs.Task;
    }
}
