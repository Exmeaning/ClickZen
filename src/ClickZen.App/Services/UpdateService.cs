using System.Net.Http;
using ClickZen.App.Shell;
using ClickZen.Core;
using ClickZen.Core.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;

namespace ClickZen.App.Services;

/// <summary>Bounded, best-effort GitHub checks. Network failures never prevent startup.</summary>
public sealed class UpdateService(ILocalizer loc, ILogger<UpdateService> log) : IDisposable
{
    private readonly HttpClient _http = CreateClient();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClickZen/" + AppInfo.Version);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public async Task CheckAsync(MainWindow window, bool manual)
    {
        if (App.IsSelfCheck || !await _gate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var json = await _http.GetStringAsync(AppInfo.LatestReleaseApi);
            var release = UpdateCheck.ParseLatestRelease(json);
            if (release is null)
            {
                throw new InvalidDataException("Latest release response has no usable release version.");
            }

            if (UpdateCheck.IsNewer(release.Version, AppInfo.Version))
            {
                window.ShowInfo(loc.Format("Settings_UpdateAvailable", release.Tag), InfoBarSeverity.Success);
                window.SetInfoAction(new HyperlinkButton
                {
                    Content = loc["Settings_ViewRelease"],
                    NavigateUri = new Uri(AppInfo.RepositoryUrl + "/releases/latest"),
                });
            }
            else if (manual)
            {
                window.ShowInfo(loc["Settings_UpToDate"], InfoBarSeverity.Success);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            log.LogInformation(ex, "Release check failed");
            if (manual)
            {
                window.ShowInfo(loc["Settings_UpdateFailed"], InfoBarSeverity.Warning);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
