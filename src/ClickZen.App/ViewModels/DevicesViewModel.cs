using System.Collections.ObjectModel;
using ClickZen.App.Services;
using ClickZen.Core.Devices;
using ClickZen.Device.Adb;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ClickZen.App.ViewModels;

/// <summary>A saved wireless device row.</summary>
public sealed partial class SavedDeviceItem : ObservableObject
{
    public SavedDeviceItem(SavedDevice model) => Model = model;

    public SavedDevice Model { get; }

    public string Name => Model.Name;

    public string Address => Model.Address;

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }
}

/// <summary>A saved emulator window profile row.</summary>
public sealed class EmulatorProfileItem(EmulatorProfile model, string description)
{
    public EmulatorProfile Model { get; } = model;

    public string Name => Model.Name;

    public string Description { get; } = description;
}

public sealed partial class DevicesViewModel : ObservableObject
{
    private readonly DeviceHub _hub;
    private readonly AdbService _adb;
    private readonly SavedDeviceStore _saved;
    private readonly EmulatorService _emulators;
    private readonly ILocalizer _loc;
    private readonly ILogger<DevicesViewModel> _log;

    public DevicesViewModel(DeviceHub hub, AdbService adb, SavedDeviceStore saved, EmulatorService emulators, ILocalizer loc, ILogger<DevicesViewModel> log)
    {
        _hub = hub;
        _adb = adb;
        _saved = saved;
        _emulators = emulators;
        _loc = loc;
        _log = log;
        _hub.Devices.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDevices));
            RefreshSavedConnectionState();
        };
        _saved.Changed += (_, _) => App.Current.MainWindow?.DispatcherQueue.TryEnqueue(LoadSaved);
        _hub.Profiles.Changed += (_, _) => App.Current.MainWindow?.DispatcherQueue.TryEnqueue(LoadProfiles);
        _emulators.Instances.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasEmulators));
        _emulators.Installed.CollectionChanged += (_, _) => OnPropertyChanged(nameof(InstalledText));
        LoadSaved();
        LoadProfiles();
    }

    // ------------------------------------------------------------------ detected emulators

    public EmulatorService Emulators => _emulators;

    public ObservableCollection<DetectedEmulator> EmulatorInstances => _emulators.Instances;

    public bool HasEmulators => _emulators.Instances.Count > 0;

    /// <summary>"Installed: MuMu, LDPlayer" (registry) for the empty state; empty when nothing is installed.</summary>
    public string InstalledText => _emulators.Installed.Count == 0
        ? ""
        : _loc.Format("Emu_Installed", string.Join(", ", _emulators.Installed.Select(i => EmulatorUi.KindName(i.Kind)).Distinct()));

    [RelayCommand]
    private async Task DetectEmulatorsAsync()
    {
        var found = await _emulators.DetectAsync();
        Report(found.Count == 0 ? _loc["Emu_NoneRunning"] : _loc.Format("Emu_Found", found.Count), false);
    }

    /// <summary>Quiet detection when the page opens (no message).</summary>
    public Task DetectQuietlyAsync() => _emulators.DetectAsync();

    [RelayCommand]
    private async Task ConnectEmulatorAsync(DetectedEmulator row)
    {
        var r = await _emulators.ConnectAsync(row);
        Report(r.Message, !r.Success);
    }

    [RelayCommand]
    private async Task ConnectAllEmulatorsAsync()
    {
        if (!HasEmulators)
        {
            await _emulators.DetectAsync();
        }

        var results = await _emulators.ConnectAllAsync();
        var ok = results.Count(r => r.Success);
        var failed = results.Where(r => !r.Success).Select(r => r.Message).ToList();
        if (results.Count == 0)
        {
            Report(HasEmulators ? _loc["Emu_AllConnected"] : _loc["Emu_NoneRunning"], false);
        }
        else
        {
            Report(failed.Count == 0 ? _loc.Format("Emu_ConnectedCount", ok) : string.Join("\n", failed), failed.Count > 0);
        }
    }

    /// <summary>
    /// One-click binding of the instance's window as a window device. When the Android screen could not be located
    /// with confidence the outcome asks for review and carries the prefilled profile for the wizard.
    /// </summary>
    public async Task<EmulatorBindResult> BindEmulatorWindowAsync(DetectedEmulator row)
    {
        var r = await _emulators.BindWindowAsync(row);
        if (r.NeedsReview)
        {
            Report(r.Message, false);
        }
        else
        {
            Report(r.Message, !r.Success);
        }

        return r;
    }

    public ObservableCollection<EmulatorProfileItem> Profiles { get; } = [];

    public bool HasProfiles => Profiles.Count > 0;

    private void LoadProfiles()
    {
        Profiles.Clear();
        try
        {
            foreach (var p in _hub.Profiles.GetAll())
            {
                var parts = new List<string> { p.Match.Title ?? p.Match.ProcessName ?? "" };
                parts.Add(p.CropRect is { } c ? $"{c.Width}×{c.Height} @ {c.X},{c.Y}" : _loc["Window_ClientArea"]);
                if (!p.ReferenceSize.IsEmpty)
                {
                    parts.Add($"{p.ReferenceSize.Width}×{p.ReferenceSize.Height}");
                }

                parts.Add(p.AdbSerial ?? _loc["Window_AdbNone"]);
                Profiles.Add(new EmulatorProfileItem(p, string.Join("  ·  ", parts.Where(s => s.Length > 0))));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not load emulator profiles");
            Report(ex.Message, true);
        }

        OnPropertyChanged(nameof(HasProfiles));
    }

    [RelayCommand]
    private void RemoveProfile(EmulatorProfileItem item)
    {
        try
        {
            _hub.Profiles.Remove(item.Model.Id);
            Report(_loc.Format("Window_ProfileRemoved", item.Name), false);
        }
        catch (Exception ex)
        {
            Report(ex.Message, true);
        }
    }

    /// <summary>After the wizard saved a profile: report it and make its device current.</summary>
    public void OnProfileSaved(EmulatorProfile profile)
    {
        Report(_loc.Format("Window_ProfileSaved", profile.Name), false);
        if (_hub.FindWindowDevice(profile.Id) is { } entry)
        {
            _hub.Current = entry;
        }
    }

    public ObservableCollection<DeviceEntry> Devices => _hub.Devices;

    public ObservableCollection<SavedDeviceItem> Saved { get; } = [];

    public bool HasDevices => _hub.Devices.Count > 0;

    public bool HasSaved => Saved.Count > 0;

    public DeviceHub Hub => _hub;

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool MessageIsError { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    private void LoadSaved()
    {
        Saved.Clear();
        foreach (var d in _saved.GetAll())
        {
            Saved.Add(new SavedDeviceItem(d));
        }

        RefreshSavedConnectionState();
        OnPropertyChanged(nameof(HasSaved));
    }

    private void RefreshSavedConnectionState()
    {
        foreach (var s in Saved)
        {
            s.IsConnected = _hub.Devices.Any(d => string.Equals(d.Serial, s.Address, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ------------------------------------------------------------------ device actions

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunAsync(async () =>
        {
            _adb.InvalidateCache();
            await _hub.RefreshAsync();
        });
    }

    [RelayCommand]
    private void SetCurrent(DeviceEntry entry) => _hub.Current = entry;

    [RelayCommand]
    private async Task StartMirrorAsync(DeviceEntry entry)
    {
        await RunAsync(() => _hub.ConnectAsync(entry));
        _hub.Current = entry;
        if (entry.IsWindow && entry.Frames is null && entry.SessionError is { } error)
        {
            Report(error, true);
        }
    }

    [RelayCommand]
    private Task StopMirrorAsync(DeviceEntry entry) => _hub.DisconnectAsync(entry);

    [RelayCommand]
    private async Task DisconnectWirelessAsync(DeviceEntry entry)
    {
        await RunAsync(async () =>
        {
            await _hub.DisconnectAsync(entry);
            var r = await _adb.DisconnectAsync(entry.Serial);
            Report(r.Success ? _loc.Format("Devices_Disconnected", entry.Serial) : r.Message, !r.Success);
        });
    }

    [RelayCommand]
    private async Task ConnectSavedAsync(SavedDeviceItem item)
    {
        item.IsBusy = true;
        try
        {
            var r = await _adb.ConnectAsync(item.Address);
            Report(r.Success ? _loc.Format("Devices_Connected", item.Address) : r.Message, !r.Success);
        }
        catch (Exception ex)
        {
            Report(ex.Message, true);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    [RelayCommand]
    private void RemoveSaved(SavedDeviceItem item) => _saved.Remove(item.Address);

    [RelayCommand]
    private void ToggleAutoConnect(SavedDeviceItem item)
    {
        item.Model.AutoConnect = !item.Model.AutoConnect;
        _saved.AddOrUpdate(item.Model);
    }

    // ------------------------------------------------------------------ wireless

    /// <summary>adb connect host[:port] (port defaults to 5555). Returns the result for the dialog.</summary>
    public async Task<AdbConnectResult> ConnectAddressAsync(string address, string? saveAs, CancellationToken ct = default)
    {
        var normalized = NormalizeAddress(address, 5555);
        var r = await _adb.ConnectAsync(normalized, ct);
        if (r.Success && !string.IsNullOrWhiteSpace(saveAs) && TrySplit(r.Address, out var host, out var port))
        {
            _saved.AddOrUpdate(new SavedDevice { Name = saveAs.Trim(), Host = host, Port = port });
        }

        Report(r.Success ? _loc.Format("Devices_Connected", r.Address) : r.Message, !r.Success);
        return r;
    }

    /// <summary>Android 11+ pairing. Does not connect: the connect port differs from the pairing port.</summary>
    public async Task<AdbConnectResult> PairAsync(string address, string code, CancellationToken ct = default)
    {
        var r = await _adb.PairAsync(NormalizeAddress(address, 0), code.Trim(), ct);
        Report(r.Success ? _loc["Devices_Paired"] : r.Message, !r.Success);
        return r;
    }

    /// <summary>
    /// The scan tab: connect every detected emulator (exact ports from process detection), then try the port table for
    /// emulators detection does not know. Returns the addresses / serials now connected.
    /// </summary>
    public async Task<IReadOnlyList<string>> ScanEmulatorsAsync(CancellationToken ct = default)
    {
        await _emulators.DetectAsync(ct);
        var detected = await _emulators.ConnectAllAsync(ct);
        var already = _emulators.Instances.Where(i => i.IsConnected).Select(i => i.Serial).OfType<string>();
        var known = _emulators.Instances.SelectMany(i => new[] { i.Instance.AdbPort }.Concat(i.Instance.FallbackPorts.Select(p => (int?)p))).OfType<int>().ToHashSet();
        var scanned = await _adb.ScanEmulatorsAsync(EmulatorPresets.AllPorts.Where(p => !known.Contains(p)), ct);
        var found = detected.Where(r => r.Success).Select(r => r.Serial).OfType<string>().Concat(already).Concat(scanned)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Report(found.Count == 0 ? _loc["Devices_ScanNone"] : _loc.Format("Devices_ScanFound", found.Count), false);
        return found;
    }

    internal static string NormalizeAddress(string input, int defaultPort)
    {
        var s = input.Trim();
        if (s.Length == 0 || s.Contains(':', StringComparison.Ordinal) || defaultPort <= 0)
        {
            return s;
        }

        return $"{s}:{defaultPort}";
    }

    private static bool TrySplit(string address, out string host, out int port)
    {
        host = "";
        port = 0;
        var i = address.LastIndexOf(':');
        return i > 0 && int.TryParse(address[(i + 1)..], out port) && (host = address[..i]).Length > 0;
    }

    // ------------------------------------------------------------------ helpers

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        catch (AdbException ex)
        {
            _log.LogWarning(ex, "Device action failed");
            Report(AdbErrorText(ex), true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Device action failed");
            Report(ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string AdbErrorText(AdbException ex) => ex.Kind switch
    {
        AdbErrorKind.Unauthorized => _loc["AdbError_Unauthorized"],
        AdbErrorKind.Offline => _loc["AdbError_Offline"],
        AdbErrorKind.DeviceNotFound => _loc["AdbError_DeviceNotFound"],
        AdbErrorKind.Timeout => _loc["AdbError_Timeout"],
        AdbErrorKind.ServerStartFailed => _loc["AdbError_ServerStartFailed"],
        AdbErrorKind.ConnectionRefused => _loc["AdbError_ConnectionRefused"],
        _ => ex.Message,
    };

    private void Report(string message, bool isError)
    {
        Message = message;
        MessageIsError = isError;
    }
}
