using ClickZen.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace ClickZen.App.Views;

public sealed partial class LogsPage : Page
{
    public LogsPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<LogsViewModel>();
        InitializeComponent();
        foreach (var cmd in ViewModel.QuickCommands)
        {
            var item = new MenuFlyoutItem { Text = cmd };
            item.Click += (_, _) => ViewModel.UseQuickCommand.Execute(cmd);
            QuickMenu.Items.Add(item);
        }

        ViewModel.EntriesAppended += (_, _) => ScrollToEnd(LogList, ViewModel.Entries.Count);
        ViewModel.TerminalAppended += (_, _) => ScrollToEnd(TerminalList, ViewModel.Terminal.Count);
        Loaded += (_, _) => ScrollToEnd(LogList, ViewModel.Entries.Count);
    }

    public LogsViewModel ViewModel { get; }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Dispose();
    }

    private static void ScrollToEnd(ListView list, int count)
    {
        if (count > 0)
        {
            list.ScrollIntoView(list.Items[count - 1]);
        }
    }

    private void OnCommandKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                ViewModel.RunCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                ViewModel.HistoryStep(-1);
                MoveCaretToEnd((TextBox)sender);
                e.Handled = true;
                break;
            case VirtualKey.Down:
                ViewModel.HistoryStep(1);
                MoveCaretToEnd((TextBox)sender);
                e.Handled = true;
                break;
        }
    }

    private static void MoveCaretToEnd(TextBox box) => box.DispatcherQueue.TryEnqueue(() => box.SelectionStart = box.Text.Length);
}
