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

public sealed partial class DevicesViewModel : ObservableObject
{
    private readonly DeviceHub _hub;
    private readonly AdbService _adb;
    private readonly SavedDeviceStore _saved;
    private readonly ILocalizer _loc;
    private readonly ILogger<DevicesViewModel> _log;

    public DevicesViewModel(DeviceHub hub, AdbService adb, SavedDeviceStore saved, ILocalizer loc, ILogger<DevicesViewModel> log)
    {
        _hub = hub;
        _adb = adb;
        _saved = saved;
        _loc = loc;
        _log = log;
        _hub.Devices.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDevices));
            RefreshSavedConnectionState();
        };
        _saved.Changed += (_, _) => App.Current.MainWindow?.DispatcherQueue.TryEnqueue(LoadSaved);
        LoadSaved();
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

    public async Task<IReadOnlyList<string>> ScanEmulatorsAsync(CancellationToken ct = default)
    {
        var found = await _adb.ScanEmulatorsAsync(ct);
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
