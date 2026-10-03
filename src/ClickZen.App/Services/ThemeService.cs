using ClickZen.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.UI.ViewManagement;

namespace ClickZen.App.Services;

/// <summary>
/// Applies the theme preference to every registered window and keeps the custom title bar
/// buttons readable. "System" follows Windows live via <see cref="UISettings.ColorValuesChanged"/>.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly List<Window> _windows = [];
    private readonly UISettings _uiSettings = new();
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    public ThemeService(SettingsService settings)
    {
        _settings = settings;
        _settings.Changed += (_, _) => ApplyAll();
        _uiSettings.ColorValuesChanged += OnSystemColorsChanged;
    }

    public ThemePreference Preference => _settings.Current.Appearance.Theme;

    public void Register(Window window)
    {
        _dispatcher ??= window.DispatcherQueue;
        _windows.Add(window);
        window.Closed += (_, _) => _windows.Remove(window);
        Apply(window);
    }

    public ElementTheme ElementTheme => Preference switch
    {
        ThemePreference.Light => ElementTheme.Light,
        ThemePreference.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>The theme actually in effect (resolves "System").</summary>
    public bool IsDarkEffective => Preference switch
    {
        ThemePreference.Light => false,
        ThemePreference.Dark => true,
        _ => IsSystemDark(),
    };

    public static bool IsSystemDark()
    {
        var bg = new UISettings().GetColorValue(UIColorType.Background);
        return bg.R + bg.G + bg.B < 384;
    }

    private void OnSystemColorsChanged(UISettings sender, object args)
    {
        if (Preference == ThemePreference.System)
        {
            _dispatcher?.TryEnqueue(ApplyAll);
        }
    }

    private void ApplyAll()
    {
        foreach (var w in _windows.ToArray())
        {
            Apply(w);
        }
    }

    private void Apply(Window window)
    {
        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = ElementTheme;
        }

        UpdateTitleBarButtons(window);
    }

    private void UpdateTitleBarButtons(Window window)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var tb = window.AppWindow.TitleBar;
        var dark = IsDarkEffective;
        var fg = dark ? Colors.White : Colors.Black;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = fg;
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedForegroundColor = fg;
        tb.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x10, 0, 0, 0);
        tb.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x20, 0, 0, 0);
        tb.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x80, 0, 0, 0);
    }

    public void Dispose() => _uiSettings.ColorValuesChanged -= OnSystemColorsChanged;

    /// <summary>Mica when available, otherwise the default solid window background.</summary>
    public static void ApplyBackdrop(Window window)
    {
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
        {
            window.SystemBackdrop = new MicaBackdrop();
        }
        else if (Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported())
        {
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
        }
    }
}
