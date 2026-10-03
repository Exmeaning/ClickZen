using ClickZen.App.Shell;
using ClickZen.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace ClickZen.App.Services;

/// <summary>
/// Startup self-check used by CI and the per-milestone verification:
/// visits every page, flips the theme, then exits with code 0 (or non-zero on failure).
/// The data directory should be redirected with CLICKZEN_DATA_DIR so user settings are untouched.
/// </summary>
internal sealed class SmokeTest
{
    private readonly MainWindow _window;
    private readonly IServiceProvider _services;
    private readonly ILogger _log;

    public SmokeTest(MainWindow window, IServiceProvider services, ILogger log)
    {
        _window = window;
        _services = services;
        _log = log;
    }

    public async Task RunAsync()
    {
        var exitCode = 0;
        try
        {
            await Task.Delay(800);

            // Localization must resolve: a missing resource makes the localizer echo the key.
            var loc = _services.GetRequiredService<ILocalizer>();
            foreach (var page in NavigationPages.All)
            {
                var text = loc[page.ResourceKey];
                if (string.IsNullOrWhiteSpace(text) || text == page.ResourceKey)
                {
                    throw new InvalidOperationException($"Resource '{page.ResourceKey}' did not resolve (got '{text}').");
                }

                _log.LogInformation("Smoke: {Key} = {Text}", page.ResourceKey, text);
            }

            foreach (var page in NavigationPages.All)
            {
                _window.NavigateTo(page.Tag);
                await Task.Delay(250);
                if (_window.Frame.CurrentSourcePageType != page.PageType)
                {
                    throw new InvalidOperationException($"Navigation to '{page.Tag}' did not land on {page.PageType.Name}.");
                }
            }

            var settings = _services.GetRequiredService<SettingsService>();
            foreach (var theme in new[] { ThemePreference.Dark, ThemePreference.Light, ThemePreference.System })
            {
                settings.Update(s => s.Appearance.Theme = theme);
                await Task.Delay(150);
                var expected = _services.GetRequiredService<ThemeService>().ElementTheme;
                if (_window.Content is FrameworkElement root && root.RequestedTheme != expected)
                {
                    throw new InvalidOperationException($"Theme {theme} was not applied.");
                }
            }

            _log.LogInformation("Smoke test passed");
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Smoke test failed");
            exitCode = 2;
        }

        var marker = Environment.GetEnvironmentVariable("CLICKZEN_SMOKE_MARKER");
        if (!string.IsNullOrEmpty(marker))
        {
            await File.WriteAllTextAsync(marker, exitCode == 0 ? "ok" : "fail");
        }

        Environment.Exit(exitCode);
    }
}
