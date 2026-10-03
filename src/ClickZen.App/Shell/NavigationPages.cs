using Microsoft.UI.Xaml.Controls;

namespace ClickZen.App.Shell;

/// <summary>A top-level destination in the navigation pane.</summary>
public sealed record NavigationPage(string Tag, string ResourceKey, Symbol? Symbol, string? Glyph, Type PageType, bool IsFooter = false);

/// <summary>Single source of truth for the navigation structure.</summary>
public static class NavigationPages
{
    public static IReadOnlyList<NavigationPage> All { get; } =
    [
        new("devices", "Nav_Devices", null, "\uE8EA", typeof(Views.DevicesPage)),
        new("mirror", "Nav_Mirror", null, "\uE7F4", typeof(Views.MirrorPage)),
        new("recording", "Nav_Recording", null, "\uE7C8", typeof(Views.RecordingPage)),
        new("automation", "Nav_Automation", null, "\uE945", typeof(Views.AutomationPage)),
        new("variables", "Nav_Variables", null, "\uE943", typeof(Views.VariablesPage)),
        new("logs", "Nav_Logs", null, "\uE9D9", typeof(Views.LogsPage)),
        new("settings", "Nav_Settings", null, "\uE713", typeof(Views.SettingsPage), IsFooter: true),
    ];

    public static NavigationPage? ByTag(string tag) => All.FirstOrDefault(p => p.Tag == tag);
}
