using System.Collections.Concurrent;
using ClickZen.Core.Geometry;
using ClickZen.Core.Variables;

namespace ClickZen.Mcp.Tests;

/// <summary>Records every backend call; returns canned data.</summary>
internal sealed class FakeBackend : IClickZenBackend
{
    public static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    public SizeI Screen { get; set; } = new(1080, 2400);

    public ConcurrentQueue<string> Calls { get; } = new();

    public string UiXml { get; set; } = """
        <?xml version='1.0' encoding='UTF-8' standalone='yes' ?>
        <hierarchy rotation="0">
          <node index="0" text="" resource-id="" class="android.widget.FrameLayout" package="com.demo" content-desc="" clickable="false" enabled="true" bounds="[0,0][1080,2400]">
            <node index="0" text="Settings" resource-id="com.demo:id/title" class="android.widget.TextView" package="com.demo" content-desc="" clickable="false" enabled="true" bounds="[40,100][400,180]" />
            <node index="1" text="" resource-id="com.demo:id/ok" class="android.widget.Button" package="com.demo" content-desc="Confirm" clickable="true" enabled="true" bounds="[100,2000][500,2200]" />
          </node>
        </hierarchy>
        UI hierchary dumped to: /dev/tty
        """;

    public string ShellOutput { get; set; } = "ok";

    public Exception? Throw { get; set; }

    public VariableStore Globals { get; } = new();

    private Task Done(string call)
    {
        Calls.Enqueue(call);
        return Throw is { } ex ? Task.FromException(ex) : Task.CompletedTask;
    }

    private async Task<T> Result<T>(string call, T value)
    {
        await Done(call);
        return value;
    }

    private McpDevice Device(string id = "emulator-5554") =>
        new(id, "Pixel 8 (Android 15)", "usb", "online", true, true, Screen.Width, Screen.Height, 420, "15", "scrcpy", null);

    public Task<McpStatus> GetStatusAsync(CancellationToken ct) =>
        Result("status", new McpStatus("2.1.0-test", true, "emulator-5554", 1, "Demo", false, false, false, null));

    public Task<IReadOnlyList<McpDevice>> ListDevicesAsync(CancellationToken ct) =>
        Result<IReadOnlyList<McpDevice>>("list", [Device()]);

    /// <summary>Running emulators returned by <see cref="ListEmulatorsAsync"/> (MuMu, not connected, by default).</summary>
    public List<McpEmulator> Emulators { get; } =
    [
        new("mumu:MuMuPlayer-12.0-0", "mumu", "MuMu 模拟器 · Android Device", "MuMuPlayer-12.0-0", 0, "127.0.0.1:16384", "config", null,
            "notConnected", null, null, true, 4242, []),
    ];

    public List<string> InstalledEmulators { get; } = ["mumu"];

    public Task<McpEmulatorList> ListEmulatorsAsync(CancellationToken ct) =>
        Result("emulators", new McpEmulatorList(Emulators.ToList(), InstalledEmulators.ToList()));

    public Task<McpEmulatorConnect> ConnectEmulatorAsync(string? emulator, bool makeCurrent, CancellationToken ct)
    {
        var e = Emulators.FirstOrDefault(x => emulator is null || x.Id == emulator || x.Instance == emulator)
            ?? throw new McpToolException($"No emulator \"{emulator}\".");
        return e.AdbAddress is null
            ? Result($"connect-emulator {emulator} {makeCurrent}", new McpEmulatorConnect(false, null, "port unknown", e))
            : Result($"connect-emulator {emulator} {makeCurrent}", new McpEmulatorConnect(true, e.AdbAddress, "connected", e with { State = "connected", Device = e.AdbAddress }));
    }

    public Task<McpEmulatorBind> BindEmulatorWindowAsync(string? emulator, CancellationToken ct) =>
        Result($"bind-emulator {emulator}", new McpEmulatorBind("window:abc123", "screenshotMatch", "127.0.0.1:16384", 1920, 1080, "bound"));

    public Task<McpDevice> ConnectAsync(string? device, CancellationToken ct) => Result($"connect {device}", Device(device ?? "emulator-5554"));

    public Task<McpDevice> SelectDeviceAsync(string device, CancellationToken ct) => Result($"select {device}", Device(device));

    public Task<McpScreenshot> TakeScreenshotAsync(string? device, McpScreenshotRequest request, CancellationToken ct)
    {
        var region = request.Region ?? new RectI(0, 0, Screen.Width, Screen.Height);
        var scale = request.MaxSize > 0 ? Math.Min(1, (double)request.MaxSize / Math.Max(region.Width, region.Height)) : 1;
        return Result($"screenshot {request.Format} {request.MaxSize}",
            new McpScreenshot(TinyPng, request.Format == "png" ? "image/png" : "image/jpeg",
                (int)Math.Round(region.Width * scale), (int)Math.Round(region.Height * scale), region, Screen.Width, Screen.Height));
    }

    public Task<McpPixel> GetPixelAsync(string? device, PointI point, CancellationToken ct) =>
        Result($"pixel {point.X},{point.Y}", new McpPixel(point.X, point.Y, "#FF8000", 255, 128, 0));

    public Task<McpImageMatch> FindImageAsync(string? device, McpImageQuery query, CancellationToken ct) =>
        Result($"find {query.TemplateName} png={query.Png?.Length ?? 0} area={query.Area} scale={query.ImageScale.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            new McpImageMatch(true, 0.97, query.Threshold, new RectI(10, 20, 30, 40), new PointI(25, 40), query.TemplateName));

    public Task<McpUiDump> DumpUiAsync(string? device, CancellationToken ct) => Result("dump", new McpUiDump(UiXml, 1, 1));

    public Task<SizeI> GetScreenSizeAsync(string? device, CancellationToken ct) => Task.FromResult(Screen);

    public Task TapAsync(string? device, PointI point, int durationMs, CancellationToken ct) => Done($"tap {point.X},{point.Y} {durationMs}");

    public Task SwipeAsync(string? device, IReadOnlyList<PointI> points, int durationMs, CancellationToken ct) =>
        Done($"swipe {string.Join(" ", points.Select(p => $"{p.X},{p.Y}"))} {durationMs}");

    public Task KeyAsync(string? device, int keyCode, CancellationToken ct) => Done($"key {keyCode}");

    public Task TextAsync(string? device, string text, CancellationToken ct) => Done($"text {text}");

    public Task<IReadOnlyList<McpApp>> ListAppsAsync(string? device, bool includeSystem, CancellationToken ct) =>
        Result<IReadOnlyList<McpApp>>("apps", [new McpApp("com.demo.game", false), new McpApp("com.android.settings", true)]);

    public Task<string> LaunchAppAsync(string? device, string package, CancellationToken ct) => Result($"launch {package}", "");

    public Task StopAppAsync(string? device, string package, CancellationToken ct) => Done($"stop {package}");

    public Task<McpShellResult> ShellAsync(string? device, string command, bool asRoot, TimeSpan timeout, CancellationToken ct) =>
        Result($"shell {command} root={asRoot}", new McpShellResult(ShellOutput, false, 12));

    public Task<McpSchemeInfo> GetSchemeAsync(CancellationToken ct) =>
        Result("scheme", new McpSchemeInfo("Demo", null, false, 1080, 2400, 300, false, null,
            [new McpTaskInfo("t1", "Collect", true, 2, 1000, 0)], ["coin"], [], [], [], []));

    public Task<IReadOnlyList<McpFileInfo>> ListSchemesAsync(CancellationToken ct) => Result<IReadOnlyList<McpFileInfo>>("schemes", []);

    public Task<McpSchemeInfo> OpenSchemeAsync(string path, bool discardChanges, CancellationToken ct) =>
        Result($"open {path} {discardChanges}", new McpSchemeInfo(path, path, false, 0, 0, 300, false, null, [], [], [], [], [], []));

    public Task<McpStartResult> StartAutomationAsync(IReadOnlyList<string> devices, CancellationToken ct) =>
        Result($"start {string.Join(",", devices)}", new McpStartResult(devices.Count == 0 ? ["Pixel 8"] : devices, []));

    public Task StopAutomationAsync(CancellationToken ct) => Done("stop-automation");

    public Task<IReadOnlyList<McpLogEntry>> GetAutomationLogAsync(int max, CancellationToken ct) =>
        Result<IReadOnlyList<McpLogEntry>>($"log {max}", [new McpLogEntry(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local), "Information", "Pixel 8", "Task Collect fired")]);

    public Task<McpTaskInfo> SetTaskEnabledAsync(string task, bool enabled, CancellationToken ct) =>
        Result($"task {task} {enabled}", new McpTaskInfo("t1", task, enabled, 2, 1000, 0));

    public Task<IReadOnlyList<McpFileInfo>> ListRecordingsAsync(CancellationToken ct) => Result<IReadOnlyList<McpFileInfo>>("recordings", []);

    public Task<McpPlaybackInfo> PlayRecordingAsync(McpPlaybackRequest request, bool wait, CancellationToken ct) =>
        Result($"play {request.Source} {request.Speed} {request.Loops} wait={wait}", new McpPlaybackInfo(request.Source, "Pixel 8", 3, 1500, wait ? "completed" : "playing", null));

    public Task StopPlaybackAsync(CancellationToken ct) => Done("stop-playback");

    public Task<McpVariableSet> GetVariablesAsync(CancellationToken ct) =>
        Result("vars", new McpVariableSet(Globals.Snapshot().ToDictionary(kv => kv.Key, kv => kv.Value), new Dictionary<string, IReadOnlyDictionary<string, VariableValue>>()));

    public Task<VariableValue> SetVariableAsync(string name, VariableValue value, McpVariableTarget target, string? device, CancellationToken ct)
    {
        Globals.Set(name, value, VariableChangeSource.User, "mcp");
        return Result($"set {name}={value.AsString} {target} {device}", Globals.Get(name));
    }
}
