using ClickZen.App.Services;
using ClickZen.App.ViewModels;
using ClickZen.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace ClickZen.App.Views;

/// <summary>AI access: the MCP server switch, token, tool groups, client configuration snippets and recent calls.</summary>
public sealed partial class AiPage : Page
{
    private readonly ILocalizer _loc;

    public AiPage()
    {
        var sp = App.Current.Services;
        _loc = sp.GetRequiredService<ILocalizer>();
        ViewModel = new AiViewModel(sp.GetRequiredService<McpService>(), sp.GetRequiredService<SettingsService>(), _loc);
        InitializeComponent();
    }

    public AiViewModel ViewModel { get; }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Dispose();
    }

    public static InfoBarSeverity StatusSeverity(bool running, string? error) =>
        running ? InfoBarSeverity.Success : error is null ? InfoBarSeverity.Informational : InfoBarSeverity.Error;

    public static Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    private void OnShowTokenClick(object sender, RoutedEventArgs e) =>
        TokenBox.PasswordRevealMode = sender is ToggleButton { IsChecked: true } ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;

    private void OnCopyTokenClick(object sender, RoutedEventArgs e)
    {
        CopyText(ViewModel.Token);
        Shell(_loc["Ai_Copied"], InfoBarSeverity.Success);
    }

    private async void OnRegenerateClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RegenerateTokenCommand.ExecuteAsync(null);
        TokenBox.Password = ViewModel.Token;
        Shell(_loc["Ai_TokenRegenerated"], InfoBarSeverity.Warning);
    }

    private void OnCopyConfigClick(object sender, RoutedEventArgs e)
    {
        CopyText(ViewModel.ConfigText);
        Shell(_loc["Ai_Copied"], InfoBarSeverity.Success);
    }

    private async void OnDocsClick(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri("https://github.com/Exmeaning/ClickZen/blob/main/docs/mcp.md"));

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text ?? "");
        Clipboard.SetContent(package);
    }

    private static void Shell(string message, InfoBarSeverity severity) => App.Current.MainWindow?.ShowInfo(message, severity);
}
