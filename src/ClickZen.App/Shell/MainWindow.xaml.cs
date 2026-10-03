using ClickZen.App.Services;
using ClickZen.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using WinUIEx;

namespace ClickZen.App.Shell;

public sealed partial class MainWindow : Window
{
    private readonly ILocalizer _loc;

    public MainWindow(ILocalizer localizer, ThemeService theme)
    {
        _loc = localizer;
        InitializeComponent();

        Title = AppInfo.Name;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ClickZen.ico"));

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var manager = WindowManager.Get(this);
        manager.MinWidth = 1100;
        manager.MinHeight = 700;
        manager.PersistenceId = "ClickZenMainWindow";

        ThemeService.ApplyBackdrop(this);
        theme.Register(this);

        BuildNavigation();
        StatusVersion.Text = $"{AppInfo.Name} {AppInfo.Version}";
        StatusEngine.Text = _loc["Status_EngineIdle"];
        StatusRecording.Text = _loc["Status_RecordingIdle"];
        StatusSync.Text = _loc["Status_SyncOff"];
    }

    public Frame Frame => ContentFrame;

    private void BuildNavigation()
    {
        foreach (var page in NavigationPages.All)
        {
            var item = new NavigationViewItem
            {
                Content = _loc[page.ResourceKey],
                Tag = page.Tag,
                Icon = new FontIcon { Glyph = page.Glyph ?? "\uE8A5" },
            };
            ToolTipService.SetToolTip(item, item.Content);
            if (page.IsFooter)
            {
                NavView.FooterMenuItems.Add(item);
            }
            else
            {
                NavView.MenuItems.Add(item);
            }
        }

        NavView.SelectedItem = NavView.MenuItems[0];
    }

    public void NavigateTo(string tag)
    {
        var item = NavView.MenuItems.Concat(NavView.FooterMenuItems).OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null)
        {
            NavView.SelectedItem = item;
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && NavigationPages.ByTag(tag) is { } page
            && ContentFrame.CurrentSourcePageType != page.PageType)
        {
            ContentFrame.Navigate(page.PageType, null, new EntranceNavigationTransitionInfo());
        }
    }
}
