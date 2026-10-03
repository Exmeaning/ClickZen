using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ClickZen.App.Services;

/// <summary>Pure helpers used by x:Bind function bindings on the recording page.</summary>
public static class RecordingUi
{
    public static bool Not(bool value) => !value;

    public static Brush? PlayingBrush(bool playing) =>
        Application.Current.Resources.TryGetValue(playing ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush", out var b)
            ? b as Brush
            : null;
}
