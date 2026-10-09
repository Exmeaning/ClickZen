namespace ClickZen.Core.Settings;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public enum LanguagePreference
{
    System,
    ZhHans,
    English,
}

public enum InputMethodPreference
{
    /// <summary>scrcpy control channel (fast, multi-touch). Falls back to adb automatically.</summary>
    Scrcpy,
    /// <summary>adb shell input (compatible, slower).</summary>
    Adb,
    /// <summary>su + sendevent on rooted devices.</summary>
    Root,
}

public enum LogLevelPreference
{
    Debug,
    Information,
    Warning,
}

/// <summary>
/// Strongly typed application settings, persisted to %LocalAppData%\ClickZen\settings.json.
/// Every property here must have an observable effect – no dead keys.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public AppearanceSettings Appearance { get; set; } = new();
    public DeviceDefaults Devices { get; set; } = new();
    public MirrorSettings Mirror { get; set; } = new();
    public AutomationSettings Automation { get; set; } = new();
    public RecordingSettings Recording { get; set; } = new();
    public SyncSettings Sync { get; set; } = new();
    public McpSettings Mcp { get; set; } = new();
    public ToolSettings Tools { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
}

public sealed class AppearanceSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public LanguagePreference Language { get; set; } = LanguagePreference.System;
}

public sealed class DeviceDefaults
{
    public bool AutoConnectSaved { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
    /// <summary>Longest video edge sent by scrcpy-server, 0 = native resolution.</summary>
    public int MaxSize { get; set; } = 1280;
    public int VideoBitRateMbps { get; set; } = 8;
    public int MaxFps { get; set; } = 60;
    public InputMethodPreference InputMethod { get; set; } = InputMethodPreference.Scrcpy;
}

public sealed class MirrorSettings
{
    public bool HighQualityScaling { get; set; } = true;
    public bool ShowTouches { get; set; }
    public bool StayAwake { get; set; } = true;
    public bool OpenMirrorOnConnect { get; set; } = true;
}

public sealed class AutomationSettings
{
    public int DefaultCheckIntervalMs { get; set; } = 300;
    public double DefaultPositionJitterDp { get; set; } = 3.0;
    public double DefaultDelayJitterPercent { get; set; } = 10.0;
    public double DefaultDurationJitterPercent { get; set; } = 10.0;
    public bool ClearVariablesOnStop { get; set; } = true;
}

public sealed class RecordingSettings
{
    public double SwipeThresholdDp { get; set; } = 8.0;
    public int LongPressThresholdMs { get; set; } = 450;
    public bool KeepFullTrajectory { get; set; } = true;
    public double DefaultPlaybackSpeed { get; set; } = 1.0;
}

public sealed class SyncSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 9527;
    /// <summary>DPAPI-protected token (base64). Empty = no authentication.</summary>
    public string ProtectedToken { get; set; } = "";
    public int MaxClients { get; set; } = 16;
}

/// <summary>
/// The MCP server (AI access): exposes devices, input, automation, recordings, variables and adb shell
/// to AI clients over Streamable HTTP. Off by default; loopback only unless <see cref="AllowRemote"/>.
/// </summary>
public sealed class McpSettings
{
    public const int DefaultPort = 9528;

    public bool Enabled { get; set; }
    public int Port { get; set; } = DefaultPort;
    /// <summary>DPAPI-protected bearer token (base64). Generated when the server is first enabled.</summary>
    public string ProtectedToken { get; set; } = "";
    /// <summary>Listen on every interface instead of 127.0.0.1 (no TLS – trusted networks only).</summary>
    public bool AllowRemote { get; set; }

    /// <summary>Device list, screenshots, UI tree, image search (read only).</summary>
    public bool AllowObserve { get; set; } = true;
    /// <summary>Taps, swipes, keys, text, launching and stopping apps.</summary>
    public bool AllowInput { get; set; } = true;
    /// <summary>Opening schemes, starting and stopping automation, enabling tasks.</summary>
    public bool AllowAutomation { get; set; } = true;
    /// <summary>Listing and playing back recordings.</summary>
    public bool AllowRecording { get; set; } = true;
    /// <summary>Reading and writing automation variables.</summary>
    public bool AllowVariables { get; set; } = true;
    /// <summary>Arbitrary adb shell commands.</summary>
    public bool AllowShell { get; set; } = true;
}

public sealed class ToolSettings
{
    /// <summary>Optional custom adb.exe. Empty = use the bundled adb.</summary>
    public string CustomAdbPath { get; set; } = "";
}

public sealed class GeneralSettings
{
    public bool ConfirmOnExit { get; set; } = true;
    public bool MinimizeToTray { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    public LogLevelPreference LogLevel { get; set; } = LogLevelPreference.Information;
}
