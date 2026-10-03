using ClickZen.App.Services;
using ClickZen.App.ViewModels;
using ClickZen.Core.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace ClickZen.App.Views;

public sealed partial class VariablesPage : Page
{
    private readonly ILocalizer _loc;

    public VariablesPage()
    {
        var sp = App.Current.Services;
        _loc = sp.GetRequiredService<ILocalizer>();
        ViewModel = new VariablesViewModel(sp.GetRequiredService<AutomationService>(), sp.GetRequiredService<VariableSyncService>(),
            _loc, sp.GetRequiredService<Core.Settings.SettingsService>());
        InitializeComponent();
        ViewModel.Rows.CollectionChanged += (_, _) => UpdateEmpty();
        ViewModel.Clients.CollectionChanged += (_, _) => UpdateEmpty();
        UpdateEmpty();
    }

    public VariablesViewModel ViewModel { get; }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Dispose();
    }

    public static Visibility VisibleIfText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static InfoBarSeverity StatusSeverity(bool running, string? error) =>
        running ? InfoBarSeverity.Success : error is null ? InfoBarSeverity.Informational : InfoBarSeverity.Error;

    private void UpdateEmpty()
    {
        EmptyText.Visibility = ViewModel.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoClientsText.Visibility = ViewModel.Clients.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ live value editing

    private void OnValueKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && sender is TextBox box)
        {
            Commit(box);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape && sender is TextBox esc && esc.Tag is VariableRow row)
        {
            esc.Text = row.CurrentValue;
            e.Handled = true;
        }
    }

    private void OnValueLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            Commit(box);
        }
    }

    private void Commit(TextBox box)
    {
        if (box.Tag is not VariableRow row || box.Text == row.CurrentValue)
        {
            return;
        }

        // Strings are displayed quoted; accept the quoted form back.
        var text = box.Text;
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1];
        }

        if (ViewModel.SetValue(row, text) is { } error)
        {
            Shell(error, InfoBarSeverity.Error);
            box.Text = row.CurrentValue;
        }
    }

    // ------------------------------------------------------------------ row menu

    private void OnRowMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VariableRow row } button)
        {
            return;
        }

        var menu = new MenuFlyout();
        if (row.IsDeclared)
        {
            var remove = new MenuFlyoutItem { Text = _loc["Vars_Remove"], Icon = new FontIcon { Glyph = "\uE74D" } };
            remove.Click += (_, _) => ViewModel.Remove(row);
            menu.Items.Add(remove);
        }
        else
        {
            var declare = new MenuFlyoutItem { Text = _loc["Vars_Declare"], Icon = new FontIcon { Glyph = "\uE710" } };
            declare.Click += (_, _) => ViewModel.Declare(row);
            menu.Items.Add(declare);
        }

        var copy = new MenuFlyoutItem { Text = _loc["Vars_CopyName"], Icon = new FontIcon { Glyph = "\uE8C8" } };
        copy.Click += (_, _) => CopyText(row.Name);
        menu.Items.Add(copy);
        menu.ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    // ------------------------------------------------------------------ sync card

    private void OnShowTokenClick(object sender, RoutedEventArgs e) =>
        TokenBox.PasswordRevealMode = sender is ToggleButton { IsChecked: true } ? PasswordRevealMode.Visible : PasswordRevealMode.Peek;

    private void OnCopyTokenClick(object sender, RoutedEventArgs e)
    {
        CopyText(ViewModel.Token);
        Shell(_loc["Vars_TokenCopied"], InfoBarSeverity.Success);
    }

    private async void OnProtocolClick(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri("https://github.com/Exmeaning/ClickZen/blob/main/docs/variable-sync-protocol.md"));

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text ?? "");
        Clipboard.SetContent(package);
    }

    private static void Shell(string message, InfoBarSeverity severity) => App.Current.MainWindow?.ShowInfo(message, severity);
}
