using ClickZen.Device;
using ClickZen.Device.Scrcpy;

namespace ClickZen.Device.Tests;

public sealed class BundledToolsTests
{
    [Fact]
    public void Paths_are_resolved_under_ThirdParty()
    {
        var tools = new BundledTools(@"C:\app");
        Assert.Equal(@"C:\app\ThirdParty\adb.exe", tools.AdbPath);
        Assert.Equal(@"C:\app\ThirdParty\scrcpy-server", tools.ScrcpyServerPath);
    }

    [Fact]
    public void Reports_missing_files()
    {
        var tools = new BundledTools(Path.Combine(Path.GetTempPath(), "cz-none-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(4, tools.MissingFiles().Count);
    }

    [Fact]
    public void Server_version_matches_build_targets()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ClickZen.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var targets = File.ReadAllText(Path.Combine(dir.FullName, "build", "ThirdParty.targets"));
        Assert.Contains($"<CzScrcpyVersion>{ScrcpyServerInfo.Version}</CzScrcpyVersion>", targets, StringComparison.Ordinal);
    }
}
