using System.Runtime.InteropServices.WindowsRuntime;
using ClickZen.App.Services;
using ClickZen.App.ViewModels;
using ClickZen.Core.Emulators;
using ClickZen.Core.Wireless;
using ClickZen.Device.Adb;
using ClickZen.Device.Adb.Wireless;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClickZen.App.Views;

public sealed record DiscoveredLanDeviceItem(string Address, string Subtitle);

public sealed partial class AddWirelessDialog : ContentDialog
{
    private readonly DevicesViewModel _vm;
    private readonly ILocalizer _loc;
    private CancellationTokenSource? _qrCts;

    public AddWirelessDialog(DevicesViewModel vm)
    {
        _vm = vm;
        _loc = App.Current.Services.GetRequiredService<ILocalizer>();
        InitializeComponent();
        CloseButtonText = _loc["Common_Close"];
        PresetList.Text = string.Join(Environment.NewLine,
            EmulatorPresets.All.Select(p => $"{p.Name}: {string.Join(", ", p.Ports.Select(x => "127.0.0.1:" + x))}"));
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        StartQrSession();
        _ = RefreshLanDevicesAsync();
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args) => StopQrSession();

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = sender.SelectedItem?.Tag as string;
        if (QrPanel is null)
        {
            return;
        }

        QrPanel.Visibility = tag == "qr" ? Visibility.Visible : Visibility.Collapsed;
        DirectPanel.Visibility = tag == "direct" ? Visibility.Visible : Visibility.Collapsed;
        PairPanel.Visibility = tag == "pair" ? Visibility.Visible : Visibility.Collapsed;
        ScanPanel.Visibility = tag == "scan" ? Visibility.Visible : Visibility.Collapsed;
        Result.Text = "";

        if (tag == "qr")
        {
            if (_qrCts is null)
            {
                StartQrSession();
            }

            _ = RefreshLanDevicesAsync();
        }
        else
        {
            StopQrSession();
        }
    }

    private void OnRefreshQrClick(object sender, RoutedEventArgs e) => StartQrSession();

    private async void OnRefreshLanClick(object sender, RoutedEventArgs e) => await RefreshLanDevicesAsync();

    private async void OnConnectLanItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DiscoveredLanDeviceItem item })
        {
            return;
        }

        await RunAsync(RefreshLanButton, async () =>
        {
            var r = await _vm.ConnectAddressAsync(item.Address, item.Address);
            if (r.Success)
            {
                await _vm.RememberConnectedDeviceAsync(r.Address);
                Show(_loc.Format("Devices_Connected", r.Address), true);
            }
            else
            {
                Show(r.Message, false);
            }
        });
    }

    private void StartQrSession()
    {
        StopQrSession();
        var creds = AdbWirelessQrCredentials.CreateRandom();
        RenderQrBitmap(creds.Payload);
        QrCodeMetaText.Text = $"{creds.ServiceName}  ·  PIN {creds.Password}";
        QrStatusText.Text = _loc["AddWireless_QrWaiting"];

        var cts = new CancellationTokenSource();
        _qrCts = cts;
        _ = RunQrLoopAsync(creds, cts.Token);
    }

    private void StopQrSession()
    {
        var cts = _qrCts;
        _qrCts = null;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void RenderQrBitmap(string payload)
    {
        var matrix = QrCodeEncoder.Encode(payload);
        var (width, height, bgra) = matrix.ToBgraPixels(modulePixels: 5, quietZoneModules: 3);
        var bmp = new WriteableBitmap(width, height);
        using (var stream = bmp.PixelBuffer.AsStream())
        {
            stream.Write(bgra, 0, bgra.Length);
        }

        bmp.Invalidate();
        QrImage.Source = bmp;
    }

    private async Task RunQrLoopAsync(AdbWirelessQrCredentials creds, CancellationToken ct)
    {
        var coordinator = _vm.CreateQrPairCoordinator();
        var progress = new Progress<WirelessQrPairProgress>(p => DispatcherQueue.TryEnqueue(() =>
        {
            QrStatusText.Text = p.Stage switch
            {
                WirelessQrPairStage.WaitingForScan => _loc["AddWireless_QrWaiting"],
                WirelessQrPairStage.Pairing => _loc.Format("AddWireless_QrPairing", p.PairAddress ?? ""),
                WirelessQrPairStage.Connecting => _loc["AddWireless_QrConnecting"],
                WirelessQrPairStage.Connected => _loc.Format("Devices_Connected", p.ConnectAddress ?? ""),
                WirelessQrPairStage.PairedNeedsConnectPort => _loc["AddWireless_QrPairedManualPort"],
                WirelessQrPairStage.Failed => p.ErrorMessage ?? _loc["AddWireless_QrFailed"],
                _ => _loc["AddWireless_QrWaiting"],
            };
        }));

        try
        {
            var outcome = await coordinator.PairAndConnectAsync(creds, progress, ct);
            if (outcome.Stage == WirelessQrPairStage.Connected && !string.IsNullOrEmpty(outcome.ConnectAddress))
            {
                await _vm.RememberConnectedDeviceAsync(outcome.ConnectAddress);
                Show(_loc.Format("Devices_Connected", outcome.ConnectAddress), true);
                await RefreshLanDevicesAsync();
            }
            else if (outcome.Stage == WirelessQrPairStage.PairedNeedsConnectPort)
            {
                Show(_loc["AddWireless_QrPairedManualPort"], true);
                await RefreshLanDevicesAsync();
            }
            else if (outcome.Stage == WirelessQrPairStage.Failed)
            {
                Show(outcome.ErrorMessage ?? _loc["AddWireless_QrFailed"], false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Show(ex.Message, false);
        }
    }

    private async Task RefreshLanDevicesAsync()
    {
        try
        {
            RefreshLanButton.IsEnabled = false;
            var list = await _vm.DiscoverLanDevicesAsync();
            var items = list.Select(s => new DiscoveredLanDeviceItem(
                s.Address,
                s.Kind == MdnsAdbServiceKind.TlsConnect
                    ? $"{s.InstanceName}  ·  TLS"
                    : $"{s.InstanceName}  ·  TCP")).ToList();
            LanDevicesList.ItemsSource = items;
            LanDevicesList.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            LanEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception)
        {
        }
        finally
        {
            RefreshLanButton.IsEnabled = true;
        }
    }

    private async void OnDirectConnect(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DirectAddress.Text))
        {
            Show(_loc["AddWireless_AddressRequired"], false);
            return;
        }

        await RunAsync(DirectConnect, async () =>
        {
            var r = await _vm.ConnectAddressAsync(DirectAddress.Text, DirectName.Text);
            Show(r.Success ? _loc.Format("Devices_Connected", r.Address) : r.Message, r.Success);
        });
    }

    private async void OnPair(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PairAddress.Text) || string.IsNullOrWhiteSpace(PairCode.Text))
        {
            Show(_loc["AddWireless_PairFieldsRequired"], false);
            return;
        }

        await RunAsync(PairButton, async () =>
        {
            var r = await _vm.PairAsync(PairAddress.Text, PairCode.Text);
            if (!r.Success)
            {
                Show(r.Message, false);
                return;
            }

            var host = PairAddress.Text.Trim();
            var colon = host.LastIndexOf(':');
            var cleanHost = (colon > 0 ? host[..colon] : host).Trim('[', ']');

            // Auto-discover the random connect port on the same LAN host via mDNS so the user doesn't have to type it.
            var discovered = await _vm.DiscoverLanDevicesAsync();
            var autoTarget = discovered.FirstOrDefault(s => string.Equals(s.Host, cleanHost, StringComparison.OrdinalIgnoreCase));
            if (autoTarget is not null)
            {
                PairConnectAddress.Text = autoTarget.Address;
                var cr = await _vm.ConnectAddressAsync(autoTarget.Address, null);
                if (cr.Success)
                {
                    await _vm.RememberConnectedDeviceAsync(cr.Address);
                    Show(_loc.Format("Devices_Connected", cr.Address), true);
                    return;
                }
            }

            Show(_loc["Devices_Paired"], true);
            if (string.IsNullOrWhiteSpace(PairConnectAddress.Text))
            {
                PairConnectAddress.Text = (colon > 0 ? host[..colon] : host) + ":";
                PairConnectAddress.Focus(FocusState.Programmatic);
                PairConnectAddress.SelectionStart = PairConnectAddress.Text.Length;
            }
        });
    }

    private async void OnPairConnect(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PairConnectAddress.Text) || PairConnectAddress.Text.TrimEnd().EndsWith(':'))
        {
            Show(_loc["AddWireless_ConnectPortRequired"], false);
            return;
        }

        await RunAsync(PairConnectButton, async () =>
        {
            var r = await _vm.ConnectAddressAsync(PairConnectAddress.Text, null);
            if (r.Success)
            {
                await _vm.RememberConnectedDeviceAsync(r.Address);
            }

            Show(r.Success ? _loc.Format("Devices_Connected", r.Address) : r.Message, r.Success);
        });
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        await RunAsync(ScanButton, async () =>
        {
            ScanResults.Items.Clear();
            var found = await _vm.ScanEmulatorsAsync();
            foreach (var serial in found)
            {
                ScanResults.Items.Add(DescribeScanResult(serial));
            }

            Show(found.Count == 0 ? _loc["Devices_ScanNone"] : _loc.Format("Devices_ScanFound", found.Count), found.Count > 0);
        });
    }

    /// <summary>
    /// "127.0.0.1:16384  (MuMu 模拟器 · Android Device)" for a detected instance, else the port preset names
    /// ("127.0.0.1:5555  (雷电模拟器 / BlueStacks 蓝叠 / …)"). Results may be adb serials such as "emulator-5554".
    /// </summary>
    private string DescribeScanResult(string serial)
    {
        if (_vm.Emulators.Instances.FirstOrDefault(i => i.Instance.OwnsSerial(serial)) is { } detected)
        {
            return $"{serial}  ({EmulatorUi.Title(detected.Instance)})";
        }

        var presets = EmulatorSerials.LocalAdbPort(serial) is int port ? EmulatorPresets.ForPort(port) : [];
        return presets.Count > 0 ? $"{serial}  ({string.Join(" / ", presets.Select(p => p.Name))})" : serial;
    }

    private async Task RunAsync(Control trigger, Func<Task> action)
    {
        trigger.IsEnabled = false;
        Busy.IsActive = true;
        Result.Text = "";
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Show(ex.Message, false);
        }
        finally
        {
            Busy.IsActive = false;
            trigger.IsEnabled = true;
        }
    }

    private void Show(string text, bool success)
    {
        Result.Text = text;
        Result.Foreground = (Brush)Application.Current.Resources[success ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"];
    }
}
