namespace ClickZen.Device;

/// <summary>Locates binaries shipped next to the app in the ThirdParty folder.</summary>
public sealed class BundledTools
{
    public BundledTools(string? baseDirectory = null)
    {
        Directory = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "ThirdParty");
    }

    public string Directory { get; }

    public string AdbPath => Path.Combine(Directory, "adb.exe");

    public string ScrcpyServerPath => Path.Combine(Directory, Scrcpy.ScrcpyServerInfo.BundledFileName);

    /// <summary>Folder containing avcodec/avutil/swresample DLLs.</summary>
    public string FfmpegDirectory => Directory;

    public IReadOnlyList<string> MissingFiles()
    {
        var required = new[] { AdbPath, ScrcpyServerPath, Path.Combine(Directory, "avcodec-62.dll"), Path.Combine(Directory, "avutil-60.dll") };
        return required.Where(p => !File.Exists(p)).ToArray();
    }
}
