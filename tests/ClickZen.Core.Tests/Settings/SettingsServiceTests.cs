using ClickZen.Core.Persistence;
using ClickZen.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClickZen.Core.Tests.Settings;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cz-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private SettingsService Create() => new(new AppPaths(_dir), NullLogger<SettingsService>.Instance);

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var s = Create().Current;
        Assert.Equal(ThemePreference.System, s.Appearance.Theme);
        Assert.Equal(1280, s.Devices.MaxSize);
        Assert.Equal(AppSettings.CurrentFormatVersion, s.FormatVersion);
    }

    [Fact]
    public void Update_persists_and_raises_changed()
    {
        var svc = Create();
        var raised = 0;
        svc.Changed += (_, _) => raised++;

        svc.Update(s => s.Appearance.Theme = ThemePreference.Dark);

        Assert.Equal(1, raised);
        Assert.Equal(ThemePreference.Dark, Create().Current.Appearance.Theme);
        Assert.Contains("\"dark\"", File.ReadAllText(Path.Combine(_dir, "settings.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void Corrupt_file_is_moved_aside_and_defaults_used()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ this is not json");

        var s = Create().Current;

        Assert.Equal(ThemePreference.System, s.Appearance.Theme);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Partial_file_keeps_defaults_for_missing_sections()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{ "appearance": { "language": "english" } }""");

        var s = Create().Current;

        Assert.Equal(LanguagePreference.English, s.Appearance.Language);
        Assert.Equal(60, s.Devices.MaxFps);
        Assert.NotNull(s.Sync);
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{ "devices": { "maxFps": 9999, "maxSize": 10 }, "sync": { "port": 80 }, "recording": { "defaultPlaybackSpeed": 50 } }""");

        var s = Create().Current;

        Assert.Equal(120, s.Devices.MaxFps);
        Assert.Equal(480, s.Devices.MaxSize);
        Assert.Equal(1024, s.Sync.Port);
        Assert.Equal(5.0, s.Recording.DefaultPlaybackSpeed);
    }

    [Fact]
    public void MaxSize_zero_means_native_and_is_kept()
    {
        var svc = Create();
        svc.Update(s => s.Devices.MaxSize = 0);
        Assert.Equal(0, Create().Current.Devices.MaxSize);
    }
}
