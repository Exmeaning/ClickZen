using ClickZen.App.Services;
using ClickZen.App.ViewModels;
using ClickZen.Device.Adb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClickZen.App.Views;

public sealed partial class AddWirelessDialog : ContentDialog
{
    private readonly DevicesViewModel _vm;
    private readonly ILocalizer _loc;

    public AddWirelessDialog(DevicesViewModel vm)
    {
        _vm = vm;
        _loc = App.Current.Services.GetRequiredService<ILocalizer>();
        InitializeComponent();
        CloseButtonText = _loc["Common_Close"];
        PresetList.Text = string.Join(Environment.NewLine,
            EmulatorPresets.All.Select(p => $"{p.Name}: {string.Join(", ", p.Ports.Select(x => "127.0.0.1:" + x))}"));
    }

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = sender.SelectedItem?.Tag as string;
        DirectPanel.Visibility = tag == "direct" ? Visibility.Visible : Visibility.Collapsed;
        PairPanel.Visibility = tag == "pair" ? Visibility.Visible : Visibility.Collapsed;
        ScanPanel.Visibility = tag == "scan" ? Visibility.Visible : Visibility.Collapsed;
        Result.Text = "";
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
            Show(r.Success ? _loc["Devices_Paired"] : r.Message, r.Success);
            if (r.Success && string.IsNullOrWhiteSpace(PairConnectAddress.Text))
            {
                // Same host, the user still has to type the connect port shown on the phone.
                var host = PairAddress.Text.Trim();
                var colon = host.LastIndexOf(':');
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
            Show(r.Success ? _loc.Format("Devices_Connected", r.Address) : r.Message, r.Success);
        });
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        await RunAsync(ScanButton, async () =>
        {
            ScanResults.Items.Clear();
            var found = await _vm.ScanEmulatorsAsync();
            foreach (var addr in found)
            {
                var preset = EmulatorPresets.ForPort(int.Parse(addr[(addr.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture));
                ScanResults.Items.Add(preset.Count > 0 ? $"{addr}  ({string.Join(" / ", preset.Select(p => p.Name))})" : addr);
            }

            Show(found.Count == 0 ? _loc["Devices_ScanNone"] : _loc.Format("Devices_ScanFound", found.Count), found.Count > 0);
        });
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
