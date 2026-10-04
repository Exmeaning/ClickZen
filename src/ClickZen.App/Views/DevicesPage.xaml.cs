using ClickZen.App.Services;
using ClickZen.App.ViewModels;
using ClickZen.Device.Adb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClickZen.App.Views;

public sealed partial class DevicesPage : Page
{
    public DevicesPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<DevicesViewModel>();
        InitializeComponent();
    }

    public DevicesViewModel ViewModel { get; }

    public bool Not(bool value) => !value;

    public bool HasMessage(string? message) => !string.IsNullOrEmpty(message);

    public InfoBarSeverity Severity(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    private void OnMessageClosed(InfoBar sender, object args) => ViewModel.Message = null;

    private static T Tagged<T>(object sender) => (T)((FrameworkElement)sender).Tag;

    private void OnStartMirrorClick(object sender, RoutedEventArgs e) => ViewModel.StartMirrorCommand.Execute(Tagged<DeviceEntry>(sender));

    private void OnStopMirrorClick(object sender, RoutedEventArgs e) => ViewModel.StopMirrorCommand.Execute(Tagged<DeviceEntry>(sender));

    private void OnSetCurrentClick(object sender, RoutedEventArgs e) => ViewModel.SetCurrentCommand.Execute(Tagged<DeviceEntry>(sender));

    private void OnDisconnectWirelessClick(object sender, RoutedEventArgs e) => ViewModel.DisconnectWirelessCommand.Execute(Tagged<DeviceEntry>(sender));

    private void OnConnectSavedClick(object sender, RoutedEventArgs e) => ViewModel.ConnectSavedCommand.Execute(Tagged<SavedDeviceItem>(sender));

    private void OnRemoveSavedClick(object sender, RoutedEventArgs e) => ViewModel.RemoveSavedCommand.Execute(Tagged<SavedDeviceItem>(sender));

    private void OnToggleAutoConnectClick(object sender, RoutedEventArgs e) => ViewModel.ToggleAutoConnectCommand.Execute(Tagged<SavedDeviceItem>(sender));

    private async void OnAddWirelessClick(object sender, RoutedEventArgs e)
    {
        var dialog = new AddWirelessDialog(ViewModel) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

    private async void OnBindWindowClick(object sender, RoutedEventArgs e) => await RunWizardAsync(null);

    private async void OnEditProfileClick(object sender, RoutedEventArgs e) => await RunWizardAsync(Tagged<EmulatorProfileItem>(sender).Model);

    private async void OnEditWindowDeviceClick(object sender, RoutedEventArgs e)
    {
        if (Tagged<DeviceEntry>(sender).Profile is { } profile)
        {
            await RunWizardAsync(profile);
        }
    }

    private async void OnRemoveProfileClick(object sender, RoutedEventArgs e)
    {
        var item = Tagged<EmulatorProfileItem>(sender);
        var loc = App.Current.Services.GetRequiredService<ILocalizer>();
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = loc["Window_RemoveTitle"],
            Content = loc.Format("Window_RemoveConfirm", item.Name),
            PrimaryButtonText = loc["Window_RemoveYes"],
            CloseButtonText = loc["Common_Cancel"],
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.RemoveProfileCommand.Execute(item);
        }
    }

    private async Task RunWizardAsync(ClickZen.Core.Devices.EmulatorProfile? profile)
    {
        var dialog = new BindWindowDialog(ViewModel.Hub, profile) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
        if (dialog.SavedProfile is { } saved)
        {
            ViewModel.OnProfileSaved(saved);
        }
    }
}
