using ClickZen.Core.Devices;
using ClickZen.Core.Input;
using ClickZen.Core.Settings;
using ClickZen.Device.Scrcpy;
using ClickZen.Platform.Capture;
using ClickZen.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace ClickZen.App.Services;

/// <summary>
/// Emulator window devices: every saved <see cref="EmulatorProfile"/> appears in <see cref="DeviceHub.Devices"/>
/// as a <see cref="ConnectionKind.Window"/> entry whose picture is a <see cref="WindowFrameSource"/> (the cropped
/// window) and whose input goes to the linked adb device, rescaled from the profile's reference resolution.
/// A closed window turns the entry Faulted; it is reattached when the window reappears (auto reconnect) or on demand.
/// </summary>
public sealed partial class DeviceHub
{
    public const string WindowSerialPrefix = "window:";

    private static readonly TimeSpan WindowMonitorInterval = TimeSpan.FromSeconds(2);

    private CancellationTokenSource? _windowMonitor;

    /// <summary>Saved emulator profiles (emulator-profiles.json).</summary>
    public EmulatorProfileStore Profiles => _profiles;

    public static string WindowSerial(string profileId) => WindowSerialPrefix + profileId;

    /// <summary>The window device of a profile, if it is in the list.</summary>
    public DeviceEntry? FindWindowDevice(string profileId) =>
        Devices.FirstOrDefault(d => d.IsWindow && d.Profile!.Id == profileId);

    /// <summary>Reconciles the window devices with the saved profiles (UI thread).</summary>
    private void SyncProfiles()
    {
        IReadOnlyList<EmulatorProfile> profiles;
        try
        {
            profiles = _profiles.GetAll();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read emulator profiles");
            return;
        }

        var ids = profiles.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in Devices.Where(e => e.IsWindow && !ids.Contains(e.Profile!.Id)).ToList())
        {
            Devices.Remove(gone);
            _ = StopWindowAsync(gone);
            if (Current == gone)
            {
                Current = Devices.FirstOrDefault();
            }
        }

        foreach (var p in profiles)
        {
            var existing = FindWindowDevice(p.Id);
            if (existing is null)
            {
                var entry = new DeviceEntry(BuildWindowInfo(p)) { Profile = p };
                entry.WindowInputFactory = () => CreateWindowMirrorInput(entry);
                Devices.Add(entry);
                Current ??= entry;
                if (_settings.Current.Mirror.OpenMirrorOnConnect)
                {
                    entry.WindowWanted = true;
                    _ = StartWindowQuietlyAsync(entry);
                }

                continue;
            }

            var old = existing.Profile!;
            existing.Profile = p;
            existing.Info = BuildWindowInfo(p);
            if (existing.WindowSource is { } source)
            {
                var sameWindow = old.CaptureMethod == p.CaptureMethod
                    && old.Match.ProcessName == p.Match.ProcessName && old.Match.ClassName == p.Match.ClassName
                    && old.Match.Title == p.Match.Title && old.Match.TitleMode == p.Match.TitleMode;
                if (sameWindow)
                {
                    source.CropRect = p.CropRect;
                }
                else
                {
                    _ = RestartWindowAsync(existing);
                }
            }
        }
    }

    /// <summary>Device info shown for a window device (name, reference resolution, linked device's density).</summary>
    private DeviceInfo BuildWindowInfo(EmulatorProfile p)
    {
        var linked = string.IsNullOrWhiteSpace(p.AdbSerial)
            ? null
            : Devices.FirstOrDefault(d => !d.IsWindow && d.Serial == p.AdbSerial.Trim())?.Info;
        return new DeviceInfo
        {
            Serial = WindowSerial(p.Id),
            State = DeviceAdbState.Online,
            Kind = ConnectionKind.Window,
            Model = string.IsNullOrWhiteSpace(p.Name) ? p.Match.Title ?? p.Id : p.Name,
            PhysicalSize = p.ReferenceSize,
            Density = linked?.Density ?? 0,
            SdkLevel = linked?.SdkLevel ?? 0,
        };
    }

    /// <summary>Linked adb devices may come and go: refresh what window entries show of them.</summary>
    private void RefreshWindowInfos()
    {
        foreach (var e in Devices.Where(d => d.IsWindow))
        {
            e.Info = BuildWindowInfo(e.Profile!);
        }
    }

    // ------------------------------------------------------------------ input

    /// <summary>
    /// Input of a window device: the linked adb device's injector (same preference/fallback rules as for that
    /// device), with coordinates rescaled from the window device's screen (reference resolution) to the adb
    /// device's screen. Without a usable linked device every call fails with a localised explanation.
    /// </summary>
    private async Task<ITouchInjector> GetWindowInjectorAsync(DeviceEntry entry, CancellationToken ct)
    {
        var serial = entry.AdbSerial;
        if (serial is null)
        {
            return new UnavailableTouchInjector(_loc["Window_NoAdbInput"]);
        }

        var adb = LinkedAdbDevice(entry);
        if (adb is null || adb.Info.State != DeviceAdbState.Online)
        {
            return new UnavailableTouchInjector(_loc.Format("Window_AdbOffline", serial));
        }

        var inner = await GetInjectorAsync(adb, ct);
        return new ScaledTouchInjector(inner, () => entry.ScreenSize, () => adb.ScreenSize);
    }

    /// <summary>The linked adb device entry of a window device (null when none is linked or it is not listed).</summary>
    private DeviceEntry? LinkedAdbDevice(DeviceEntry entry) =>
        entry.AdbSerial is { } serial
            ? Devices.ToArray().FirstOrDefault(d => !d.IsWindow && string.Equals(d.Serial, serial, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>
    /// Live input for the mirror view of a window device: real-time over the linked device's scrcpy control channel
    /// when it is mirroring (and scrcpy input is preferred), else buffered replay through <see cref="GetWindowInjectorAsync"/>.
    /// </summary>
    private IMirrorInput CreateWindowMirrorInput(DeviceEntry entry) => new WindowMirrorInput(
        () => _settings.Current.Devices.InputMethod == InputMethodPreference.Scrcpy
            && LinkedAdbDevice(entry)?.Session is { IsControlAvailable: true } s ? s.Injector : null,
        () => entry.ScreenSize,
        () => LinkedAdbDevice(entry)?.ScreenSize ?? default,
        new InjectorMirrorInput(ct => GetWindowInjectorAsync(entry, ct), () => entry.ScreenSize));

    // ------------------------------------------------------------------ capture lifecycle

    /// <summary>Finds the profile's window and starts capturing it (no-op when already running).</summary>
    private async Task StartWindowAsync(DeviceEntry entry, CancellationToken ct = default)
    {
        entry.WindowWanted = true;
        if (entry.WindowSource is not null || entry.WindowStarting || entry.Profile is not { } profile)
        {
            return;
        }

        entry.WindowStarting = true;
        try
        {
            entry.SessionState = SessionState.Connecting;
            entry.SessionError = null;
            var window = FindWindow(entry, profile);
            if (window is null)
            {
                entry.SessionState = SessionState.Faulted;
                entry.SessionError = _loc["Window_NotFound"];
                return;
            }

            var source = WindowFrameSource.ForProfile(profile, window.Handle, _loggers.CreateLogger<WindowFrameSource>());
            source.StateChanged += (_, state) => Ui(() => OnWindowStateChanged(entry, source, state));
            await source.StartAsync(ct);
            if (source.State == WindowCaptureState.Faulted || !entry.WindowWanted || !Devices.Contains(entry))
            {
                entry.SessionState = entry.WindowWanted ? SessionState.Faulted : SessionState.Disconnected;
                entry.SessionError = entry.WindowWanted ? source.LastError : null;
                await source.DisposeAsync();
                return;
            }

            entry.LastWindowHandle = window.Handle;
            entry.WindowSource = source;
            entry.SessionState = SessionState.Streaming;
            entry.SessionError = null;
            Current ??= entry;
            if (WindowMetrics.TryQuery(window.Handle, out var m) && profile.ClientSizeChanged(m.ClientSize))
            {
                _log.LogWarning("Window of profile {Name} is {Now} now, the crop was chosen at {Then}", profile.Name, m.ClientSize, profile.ClientSize);
            }

            _log.LogInformation("Window device {Name}: capturing \"{Title}\" ({Process}, {Hwnd:X}) via {Method}, crop {Crop}, reference {Ref}, input {Adb}",
                profile.Name, window.Title, window.ProcessName, window.Handle, source.ActiveMethod, profile.CropRect?.ToString() ?? "client area",
                profile.ReferenceSize, entry.AdbSerial ?? "none");
        }
        finally
        {
            entry.WindowStarting = false;
        }
    }

    private static DesktopWindowInfo? FindWindow(DeviceEntry entry, EmulatorProfile profile) =>
        WindowMatcher.FindBestMatch(profile, WindowEnumerator.GetVisibleWindows((uint)Environment.ProcessId), entry.LastWindowHandle);

    private async Task StartWindowQuietlyAsync(DeviceEntry entry)
    {
        try
        {
            await StartWindowAsync(entry);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not start capturing the window of {Name}", entry.Info.DisplayName);
            entry.SessionState = SessionState.Faulted;
            entry.SessionError = ex.Message;
        }
    }

    private async Task StopWindowAsync(DeviceEntry entry)
    {
        entry.WindowWanted = false;
        var source = entry.WindowSource;
        entry.WindowSource = null;
        entry.SessionState = SessionState.Disconnected;
        entry.SessionError = null;
        if (source is not null)
        {
            await source.DisposeAsync();
        }
    }

    private async Task RestartWindowAsync(DeviceEntry entry)
    {
        await StopWindowAsync(entry);
        await StartWindowQuietlyAsync(entry);
    }

    private void OnWindowStateChanged(DeviceEntry entry, WindowFrameSource source, WindowCaptureState state)
    {
        if (!ReferenceEquals(entry.WindowSource, source) || state != WindowCaptureState.Faulted)
        {
            return;
        }

        _log.LogWarning("Window device {Name} lost its window: {Error}", entry.Info.DisplayName, source.LastError);
        entry.WindowSource = null;
        entry.SessionState = SessionState.Faulted;
        entry.SessionError = WindowEnumerator.IsAlive(source.WindowHandle) ? source.LastError : _loc["Window_Closed"];
        _ = source.DisposeAsync().AsTask();
    }

    /// <summary>Re-attaches wanted window devices whose window (re)appeared, every couple of seconds.</summary>
    private void StartWindowMonitor()
    {
        if (_windowMonitor is not null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _windowMonitor = cts;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(WindowMonitorInterval, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                Ui(CheckWindows);
            }
        }, CancellationToken.None);
    }

    private void CheckWindows()
    {
        if (!_settings.Current.Devices.AutoReconnect)
        {
            return;
        }

        var waiting = Devices.Where(d => d is { IsWindow: true, WindowWanted: true, WindowSource: null, WindowStarting: false }).ToList();
        if (waiting.Count == 0)
        {
            return;
        }

        var windows = WindowEnumerator.GetVisibleWindows((uint)Environment.ProcessId);
        foreach (var entry in waiting)
        {
            if (WindowMatcher.FindBestMatch(entry.Profile!, windows, entry.LastWindowHandle) is not null)
            {
                _ = StartWindowQuietlyAsync(entry);
            }
        }
    }
}
