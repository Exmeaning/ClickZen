namespace ClickZen.Core.Persistence;

/// <summary>
/// Well-known locations for ClickZen data. Everything lives under the user's local app data
/// so behaviour never depends on the current working directory.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClickZen");
        DocumentsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ClickZen");
    }

    /// <summary>%LocalAppData%\ClickZen</summary>
    public string Root { get; }

    /// <summary>Documents\ClickZen – default location for user schemes and recordings.</summary>
    public string DocumentsRoot { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string DevicesFile => Path.Combine(Root, "devices.json");
    public string EmulatorProfilesFile => Path.Combine(Root, "emulator-profiles.json");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string CrashDirectory => Path.Combine(Root, "crash");
    public string RuntimeDirectory => Path.Combine(Root, "runtime");
    public string SchemesDirectory => Path.Combine(DocumentsRoot, "Schemes");
    public string RecordingsDirectory => Path.Combine(DocumentsRoot, "Recordings");
    public string ScreenshotsDirectory => Path.Combine(DocumentsRoot, "Screenshots");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CrashDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
    }
}
