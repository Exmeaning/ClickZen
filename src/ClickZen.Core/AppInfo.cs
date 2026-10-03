using System.Reflection;

namespace ClickZen.Core;

/// <summary>Product identity shared by every layer.</summary>
public static class AppInfo
{
    public const string Name = "ClickZen";
    public const string RepositoryUrl = "https://github.com/Exmeaning/ClickZen";
    public const string LatestReleaseApi = "https://api.github.com/repos/Exmeaning/ClickZen/releases/latest";

    /// <summary>Informational version without the source-revision suffix, e.g. "2.0.0-dev".</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var attr = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var v = attr?.InformationalVersion ?? typeof(AppInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? v[..plus] : v;
    }
}
