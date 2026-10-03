using ClickZen.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace ClickZen.Core.Settings;

/// <summary>
/// Owns the single in-memory <see cref="AppSettings"/> instance. Changes are applied by calling
/// <see cref="Update"/>, which persists immediately and raises <see cref="Changed"/>.
/// </summary>
public sealed class SettingsService
{
    private readonly JsonFileStore<AppSettings> _store;
    private readonly ILogger<SettingsService> _logger;

    public SettingsService(AppPaths paths, ILogger<SettingsService> logger)
    {
        _logger = logger;
        _store = new JsonFileStore<AppSettings>(paths.SettingsFile, logger);
        Current = _store.Load();
        Normalize(Current);
    }

    public AppSettings Current { get; }

    public event EventHandler<AppSettings>? Changed;

    public void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        mutate(Current);
        Normalize(Current);
        Save();
        Changed?.Invoke(this, Current);
    }

    public void Save()
    {
        try
        {
            _store.Save(Current);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Failed to save settings to {Path}", _store.FilePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Failed to save settings to {Path}", _store.FilePath);
        }
    }

    /// <summary>Clamp values into their valid ranges (protects against hand-edited files).</summary>
    internal static void Normalize(AppSettings s)
    {
        s.FormatVersion = AppSettings.CurrentFormatVersion;
        s.Appearance ??= new();
        s.Devices ??= new();
        s.Mirror ??= new();
        s.Automation ??= new();
        s.Recording ??= new();
        s.Sync ??= new();
        s.Tools ??= new();
        s.General ??= new();

        s.Devices.MaxSize = s.Devices.MaxSize == 0 ? 0 : Math.Clamp(s.Devices.MaxSize, 480, 4096);
        s.Devices.VideoBitRateMbps = Math.Clamp(s.Devices.VideoBitRateMbps, 1, 64);
        s.Devices.MaxFps = Math.Clamp(s.Devices.MaxFps, 5, 120);
        s.Automation.DefaultCheckIntervalMs = Math.Clamp(s.Automation.DefaultCheckIntervalMs, 30, 60_000);
        s.Automation.DefaultPositionJitterDp = Math.Clamp(s.Automation.DefaultPositionJitterDp, 0, 50);
        s.Automation.DefaultDelayJitterPercent = Math.Clamp(s.Automation.DefaultDelayJitterPercent, 0, 100);
        s.Automation.DefaultDurationJitterPercent = Math.Clamp(s.Automation.DefaultDurationJitterPercent, 0, 100);
        s.Recording.SwipeThresholdDp = Math.Clamp(s.Recording.SwipeThresholdDp, 1, 100);
        s.Recording.LongPressThresholdMs = Math.Clamp(s.Recording.LongPressThresholdMs, 100, 5000);
        s.Recording.DefaultPlaybackSpeed = Math.Clamp(s.Recording.DefaultPlaybackSpeed, 0.1, 5.0);
        s.Sync.Port = Math.Clamp(s.Sync.Port, 1024, 65535);
        s.Sync.MaxClients = Math.Clamp(s.Sync.MaxClients, 1, 256);
        s.Sync.ProtectedToken ??= "";
        s.Tools.CustomAdbPath ??= "";
    }
}
