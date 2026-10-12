using ClickZen.Core.Devices;
using ClickZen.Core.Emulators;
using ClickZen.Device.Scrcpy;
using ClickZen.Platform.Capture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClickZen.App.Services;

/// <summary>
/// Developer self-check for the emulator assistant (<c>--selftest-emulator</c>): with an Android Studio emulator (AVD)
/// running, the Devices page must detect it with the port from its runtime file, connect it with one click (it is
/// already online as emulator-NNNN), then bind its window with one click — the window device must appear, capture a
/// picture with the device's aspect ratio and use the device's screen size — and binding again must update the same
/// profile. The profile is deleted at the end.
/// Run with CLICKZEN_DATA_DIR pointing to a temporary directory. Exit code 0 = pass, 2 = fail, 4 = no AVD running.
/// </summary>
internal sealed class EmulatorSelfTest
{
    private readonly Shell.MainWindow _window;
    private readonly IServiceProvider _services;
    private readonly ILogger _log;

    public EmulatorSelfTest(Shell.MainWindow window, IServiceProvider services, ILogger log)
    {
        _window = window;
        _services = services;
        _log = log;
    }

    public async Task RunAsync()
    {
        var exit = 2;
        DeviceHub? hub = null;
        EmulatorProfile? profile = null;
        try
        {
            hub = _services.GetRequiredService<DeviceHub>();
            var emulators = _services.GetRequiredService<EmulatorService>();
            _window.NavigateTo("devices");

            // 1. Detection: the AVD with its exact adb port and its window.
            var found = await emulators.DetectAsync();
            var row = found.FirstOrDefault(r => r.Instance.Kind == EmulatorKind.AndroidStudio && r.Instance.HasWindow);
            if (row is null)
            {
                _log.LogError("Emulator self-test: no Android Studio emulator with a window detected ({Count} instance(s): {List})",
                    found.Count, string.Join(", ", found.Select(f => f.Key)));
                exit = 4;
                return;
            }

            var i = row.Instance;
            _log.LogInformation("Emulator self-test: detected {Key} adb {Port} ({Source}), window {Title}", i.Key, i.AdbPort, i.PortSource, i.WindowTitle);
            if (i.PortSource != AdbPortSource.Runtime || i.AdbPort is not int port || port % 2 == 0)
            {
                _log.LogError("Emulator self-test failed: expected an odd adb port from the runtime file, got {Port} ({Source})", i.AdbPort, i.PortSource);
                return;
            }

            // 2. One-click connect: adb lists the AVD itself, so this must resolve to emulator-(port-1) and select it.
            var connected = await emulators.ConnectAsync(row);
            var expectedSerial = EmulatorSerials.ConsoleSerial(port);
            _log.LogInformation("Emulator self-test: connect -> {Ok} {Serial} \"{Message}\"", connected.Success, connected.Serial, connected.Message);
            if (!connected.Success || connected.Serial != expectedSerial || row.State != EmulatorConnectionState.Connected
                || hub.Current?.Serial != expectedSerial)
            {
                _log.LogError("Emulator self-test failed: connect gave {Serial} / state {State} / current {Current}, expected {Expected}",
                    connected.Serial, row.State, hub.Current?.Serial, expectedSerial);
                return;
            }

            var adbDevice = await SelfTestUtil.WaitForAsync(
                () => hub.Devices.FirstOrDefault(d => d.Serial == expectedSerial && !d.Info.PhysicalSize.IsEmpty), TimeSpan.FromSeconds(20));
            if (adbDevice is null)
            {
                _log.LogError("Emulator self-test failed: {Serial} never reported its screen size", expectedSerial);
                return;
            }

            // 3. One-click binding.
            var bind = await emulators.BindWindowAsync(row);
            profile = bind.Profile;
            _log.LogInformation("Emulator self-test: bind -> ok {Ok}, review {Review}, method {Method}, device {Device}, \"{Message}\"",
                bind.Success, bind.NeedsReview, bind.Method, bind.DeviceId, bind.Message);
            if (!bind.Success || bind.Profile is not { } p || bind.DeviceId is null)
            {
                _log.LogError("Emulator self-test failed: one-click binding did not save a profile");
                return;
            }

            var physical = adbDevice.Info.PhysicalSize;
            if (p.AdbSerial != expectedSerial || p.EmulatorKey != i.Key || p.ReferenceSize.IsEmpty
                || Math.Abs((double)p.ReferenceSize.Width * p.ReferenceSize.Height - (double)physical.Width * physical.Height) > 0.5)
            {
                _log.LogError("Emulator self-test failed: profile adb {Adb}, key {Key}, reference {Ref} (device {Physical})",
                    p.AdbSerial, p.EmulatorKey, p.ReferenceSize, physical);
                return;
            }

            // 4. The window device captures a picture with the device's aspect ratio and the device's screen size.
            var entry = await SelfTestUtil.WaitForAsync(() => hub.FindWindowDevice(p.Id), TimeSpan.FromSeconds(5));
            var source = entry is null ? null : await SelfTestUtil.WaitForAsync(() => entry.WindowSource, TimeSpan.FromSeconds(10));
            var frame = source is null ? null : await source.WaitForFrameAsync(-1, TimeSpan.FromSeconds(5), CancellationToken.None);
            if (entry is null || source is null || frame is null || entry.SessionState != SessionState.Streaming)
            {
                _log.LogError("Emulator self-test failed: window device {Device} state {State} ({Error}), frame {Frame}",
                    bind.DeviceId, entry?.SessionState, entry?.SessionError, frame?.Size);
                return;
            }

            var aspect = ScreenOrientation.AspectDifference(frame.Size, ScreenOrientation.OrientLike(physical, frame.Size));
            _log.LogInformation("Emulator self-test: window device {Serial} streaming via {Method}, frame {Frame}, screen {Screen}, aspect diff {Aspect:P1}",
                entry.Serial, source.ActiveMethod, frame.Size, entry.ScreenSize, aspect);
            if (aspect > 0.03 || entry.ScreenSize != ScreenOrientation.OrientLike(physical, frame.Size) || BgraBuffer.IsLikelyBlack(frame.Bgra, frame.Width, frame.Height)
                || hub.Current != entry || !row.IsBound || row.BoundDevice != bind.DeviceId)
            {
                _log.LogError("Emulator self-test failed: picture aspect {Aspect:P1}, screen {Screen}, current {Current}, bound {Bound}",
                    aspect, entry.ScreenSize, hub.Current?.Serial, row.BoundDevice);
                return;
            }

            // 5. Binding again updates the same profile instead of adding one.
            var again = await emulators.BindWindowAsync(row);
            var all = hub.Profiles.GetAll();
            if (!again.Success || again.Profile?.Id != p.Id || all.Count(x => x.EmulatorKey == i.Key) != 1)
            {
                _log.LogError("Emulator self-test failed: rebinding created {Count} profile(s) for the instance (ids {First} / {Second})",
                    all.Count(x => x.EmulatorKey == i.Key), p.Id, again.Profile?.Id);
                return;
            }

            _log.LogInformation("Emulator self-test passed");
            exit = 0;
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Emulator self-test crashed");
            exit = 2;
        }
        finally
        {
            if (hub is not null && profile is not null)
            {
                try
                {
                    hub.Profiles.Remove(profile.Id);
                    await Task.Delay(300);
                    _log.LogInformation("Emulator self-test: profile removed, window device present: {Present}", hub.FindWindowDevice(profile.Id) is not null);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Emulator self-test: could not remove the profile");
                }
            }

            Environment.Exit(exit);
        }
    }
}
