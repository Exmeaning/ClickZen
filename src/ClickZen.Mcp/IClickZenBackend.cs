using ClickZen.Core.Geometry;
using ClickZen.Core.Variables;

namespace ClickZen.Mcp;

// The contract between the MCP tools and the running application. ClickZen.App implements it on top of
// DeviceHub / AutomationService / RecordingService; tests use a fake. Everything here is thread-safe from
// the caller's point of view (the implementation marshals to the UI thread where needed).
//
// Coordinates are device pixels in the device's current orientation unless stated otherwise.

/// <summary>A device as reported to AI clients.</summary>
/// <param name="Id">Stable id to pass back as the <c>device</c> argument (adb serial, or "window:…" for emulator windows).</param>
/// <param name="ScreenWidth">Screen width in the current orientation (the coordinate space of all input tools), 0 if unknown.</param>
/// <param name="IsMirroring">Whether a live picture (scrcpy video or window capture) is running.</param>
/// <param name="InputMethod">Input backend that will be used ("scrcpy", "adb", "root", "window"…), null if unavailable.</param>
public sealed record McpDevice(
    string Id,
    string Name,
    string Kind,
    string State,
    bool IsCurrent,
    bool IsMirroring,
    int ScreenWidth,
    int ScreenHeight,
    int Density,
    string? AndroidVersion,
    string? InputMethod,
    string? Error);

/// <summary>An encoded screenshot.</summary>
/// <param name="Data">Encoded image bytes.</param>
/// <param name="MimeType">"image/jpeg" or "image/png".</param>
/// <param name="ImageWidth">Width of <see cref="Data"/> (after scaling).</param>
/// <param name="Region">Device-pixel rectangle the image shows (whole screen when no region was requested).</param>
/// <param name="ScreenWidth">Device screen width in the current orientation.</param>
public sealed record McpScreenshot(byte[] Data, string MimeType, int ImageWidth, int ImageHeight, RectI Region, int ScreenWidth, int ScreenHeight);

/// <summary>How a screenshot should be produced.</summary>
/// <param name="Format">"jpeg" or "png".</param>
/// <param name="MaxSize">Longest edge of the returned image in pixels (0 = no scaling).</param>
/// <param name="Region">Optional device-pixel rectangle to crop.</param>
public sealed record McpScreenshotRequest(string Format, int MaxSize, int Quality, RectI? Region);

/// <summary>Result of an image search on the live screen.</summary>
/// <param name="Location">Best match in device pixels (also when below the threshold), null when nothing could be matched.</param>
public sealed record McpImageMatch(bool Found, double Score, double Threshold, RectI? Location, PointI? Center, string? Template);

/// <summary>What to look for in <see cref="IClickZenBackend.FindImageAsync"/>.</summary>
/// <param name="TemplateName">Name (or id) of a template in the open scheme.</param>
/// <param name="Png">Or: an encoded image (PNG/JPEG).</param>
/// <param name="ImageScale">For <paramref name="Png"/>: device pixels per image pixel (1 when it was cut at device resolution,
/// e.g. 1.875 when cut from a 576-wide screenshot of a 1080-wide screen).</param>
/// <param name="Area">Optional device-pixel search area.</param>
public sealed record McpImageQuery(string? TemplateName, byte[]? Png, RectI? Area, double Threshold, bool Grayscale, bool MultiScale, double ImageScale = 1);

/// <summary>Colour of a pixel.</summary>
public sealed record McpPixel(int X, int Y, string Hex, int R, int G, int B);

/// <summary>Raw UI hierarchy text (uiautomator XML) in device pixels.</summary>
/// <param name="Xml">The dump.</param>
/// <param name="ScaleX">Factor to convert the dump's bounds into this device's coordinates (window devices, else 1).</param>
public sealed record McpUiDump(string Xml, double ScaleX, double ScaleY);

/// <summary>A task of the open scheme.</summary>
public sealed record McpTaskInfo(string Id, string Name, bool Enabled, int Rules, int CooldownMs, int Priority);

/// <summary>A variable declared in the open scheme.</summary>
public sealed record McpVariableDeclaration(string Name, string Type, string Scope, string Sync, string Initial, string? Description);

/// <summary>A device run of the current automation.</summary>
public sealed record McpRunInfo(string Device, string State, long Rounds, string? Error);

/// <summary>The open scheme document and the automation state.</summary>
public sealed record McpSchemeInfo(
    string Name,
    string? FilePath,
    bool IsDirty,
    int RefWidth,
    int RefHeight,
    int CheckIntervalMs,
    bool IsRunning,
    string? LastFired,
    IReadOnlyList<McpTaskInfo> Tasks,
    IReadOnlyList<string> Templates,
    IReadOnlyList<McpVariableDeclaration> Variables,
    IReadOnlyList<string> EmbeddedRecordings,
    IReadOnlyList<McpRunInfo> Runs,
    IReadOnlyList<string> Issues);

/// <summary>A file on disk (scheme or recording).</summary>
public sealed record McpFileInfo(string Name, string Path, long SizeBytes, DateTimeOffset Modified);

/// <summary>A line of the automation run log.</summary>
public sealed record McpLogEntry(DateTime Time, string Level, string Device, string Message);

/// <summary>Variables visible to AI clients: the global store and the scheme store of each running device.</summary>
public sealed record McpVariableSet(IReadOnlyDictionary<string, VariableValue> Global, IReadOnlyDictionary<string, IReadOnlyDictionary<string, VariableValue>> Devices);

/// <summary>Where <see cref="IClickZenBackend.SetVariableAsync"/> writes.</summary>
public enum McpVariableTarget
{
    /// <summary>Global store (shared by all devices).</summary>
    Global,
    /// <summary>Scheme store of a running device.</summary>
    Device,
}

/// <summary>Outcome of starting an automation run.</summary>
public sealed record McpStartResult(IReadOnlyList<string> StartedOn, IReadOnlyList<string> Problems);

/// <summary>A recording playback request.</summary>
/// <param name="Source">Path of a .czrec file, a file name in the recordings folder, or the id of a recording embedded in the open scheme.</param>
public sealed record McpPlaybackRequest(string Source, string? Device, double Speed, int Loops, bool Randomize);

/// <summary>Outcome (or state) of a playback.</summary>
public sealed record McpPlaybackInfo(string Recording, string Device, int Gestures, long DurationMs, string State, string? Error);

/// <summary>Application status.</summary>
public sealed record McpStatus(
    string Version,
    bool AdbAvailable,
    string? CurrentDevice,
    int DeviceCount,
    string SchemeName,
    bool AutomationRunning,
    bool RecordingActive,
    bool PlaybackActive,
    string? PlaybackRecording);

/// <summary>Output of a shell command.</summary>
public sealed record McpShellResult(string Output, bool Truncated, long ElapsedMs);

/// <summary>An installed package.</summary>
public sealed record McpApp(string Package, bool System);

/// <summary>
/// Everything the MCP tools need from ClickZen. <c>device</c> arguments are a device id from
/// <see cref="ListDevicesAsync"/>, a display name, or null/empty for the current device.
/// Failures are reported by throwing <see cref="McpToolException"/> (message shown to the AI) or any other exception.
/// </summary>
public interface IClickZenBackend
{
    Task<McpStatus> GetStatusAsync(CancellationToken ct);

    Task<IReadOnlyList<McpDevice>> ListDevicesAsync(CancellationToken ct);

    /// <summary>Starts mirroring (scrcpy session / window capture) if needed and waits for a picture.</summary>
    Task<McpDevice> ConnectAsync(string? device, CancellationToken ct);

    /// <summary>Makes a device the current one (title bar switcher).</summary>
    Task<McpDevice> SelectDeviceAsync(string device, CancellationToken ct);

    /// <summary>Starts mirroring if needed, waits for a frame newer than any pending input and encodes it.</summary>
    Task<McpScreenshot> TakeScreenshotAsync(string? device, McpScreenshotRequest request, CancellationToken ct);

    Task<McpPixel> GetPixelAsync(string? device, PointI point, CancellationToken ct);

    Task<McpImageMatch> FindImageAsync(string? device, McpImageQuery query, CancellationToken ct);

    /// <summary>uiautomator dump of the device (window devices: of the linked adb device).</summary>
    Task<McpUiDump> DumpUiAsync(string? device, CancellationToken ct);

    /// <summary>Screen size in the current orientation (input coordinate space).</summary>
    Task<SizeI> GetScreenSizeAsync(string? device, CancellationToken ct);

    Task TapAsync(string? device, PointI point, int durationMs, CancellationToken ct);

    Task SwipeAsync(string? device, IReadOnlyList<PointI> points, int durationMs, CancellationToken ct);

    Task KeyAsync(string? device, int keyCode, CancellationToken ct);

    Task TextAsync(string? device, string text, CancellationToken ct);

    Task<IReadOnlyList<McpApp>> ListAppsAsync(string? device, bool includeSystem, CancellationToken ct);

    /// <summary>Launches an app by package name (launcher activity).</summary>
    Task<string> LaunchAppAsync(string? device, string package, CancellationToken ct);

    Task StopAppAsync(string? device, string package, CancellationToken ct);

    Task<McpShellResult> ShellAsync(string? device, string command, bool asRoot, TimeSpan timeout, CancellationToken ct);

    Task<McpSchemeInfo> GetSchemeAsync(CancellationToken ct);

    Task<IReadOnlyList<McpFileInfo>> ListSchemesAsync(CancellationToken ct);

    /// <summary>Opens a .czscheme (path or name in the schemes folder). Refuses unsaved changes / running automation unless <paramref name="discardChanges"/>.</summary>
    Task<McpSchemeInfo> OpenSchemeAsync(string path, bool discardChanges, CancellationToken ct);

    /// <summary>Starts the open scheme on the given devices (empty = the automation page's targets, else the current device).</summary>
    Task<McpStartResult> StartAutomationAsync(IReadOnlyList<string> devices, CancellationToken ct);

    Task StopAutomationAsync(CancellationToken ct);

    Task<IReadOnlyList<McpLogEntry>> GetAutomationLogAsync(int max, CancellationToken ct);

    Task<McpTaskInfo> SetTaskEnabledAsync(string task, bool enabled, CancellationToken ct);

    Task<IReadOnlyList<McpFileInfo>> ListRecordingsAsync(CancellationToken ct);

    /// <summary>Starts a playback; when <paramref name="wait"/> returns after it ended (or <paramref name="ct"/> fired).</summary>
    Task<McpPlaybackInfo> PlayRecordingAsync(McpPlaybackRequest request, bool wait, CancellationToken ct);

    /// <summary>Stops AI-started and page playbacks.</summary>
    Task StopPlaybackAsync(CancellationToken ct);

    Task<McpVariableSet> GetVariablesAsync(CancellationToken ct);

    /// <summary>Sets a variable; returns the stored value (converted to the declared type).</summary>
    Task<VariableValue> SetVariableAsync(string name, VariableValue value, McpVariableTarget target, string? device, CancellationToken ct);
}

/// <summary>An expected tool failure whose message is shown to the AI client as is.</summary>
public sealed class McpToolException : Exception
{
    public McpToolException()
    {
    }

    public McpToolException(string message) : base(message)
    {
    }

    public McpToolException(string message, Exception inner) : base(message, inner)
    {
    }
}
