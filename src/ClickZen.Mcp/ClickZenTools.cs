using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClickZen.Core;
using ClickZen.Core.Geometry;
using ClickZen.Core.Variables;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ClickZen.Mcp;

/// <summary>
/// The MCP tools ClickZen exposes. Each public tool method carries <see cref="McpServerToolAttribute"/> (name and
/// behaviour hints) and <see cref="McpCapabilityAttribute"/> (the user-switchable group it belongs to).
/// Descriptions are written for the model: what the tool does, when to use it, units of every argument.
/// </summary>
[McpServerToolType]
public sealed partial class ClickZenTools
{
    public const int MaxShellOutputChars = 64 * 1024;
    public const int MaxTextChars = 4000;

    /// <summary>Sent to clients on initialize: how to use this server well.</summary>
    public const string Instructions = """
        ClickZen controls Android devices (phones over USB / Wi-Fi and emulators) connected to this PC.

        Typical loop: list_devices -> take_screenshot (or dump_ui for exact element text and bounds) -> act with tap / swipe / scroll / press_key / input_text / tap_element -> take_screenshot again to verify the result.

        Coordinates are device pixels in the device's current orientation (screenWidth x screenHeight from list_devices). Screenshots are scaled down by default: when you measure a point on a screenshot, pass refWidth/refHeight = that image's width/height and ClickZen rescales it to the device.

        Omit `device` to use the current device (the one selected in ClickZen's title bar).
        Emulators on this PC: list_emulators shows running ones with their exact adb address; connect_emulator connects one (then use it like any device); bind_emulator_window captures its window instead of scrcpy.
        Native apps: prefer dump_ui and tap_element (exact, no pixel guessing). Games, canvases, video: use screenshots, find_image and get_pixel_color.
        Automation schemes (.czscheme) are image-recognition scripts built in ClickZen: list_schemes, open_scheme, get_scheme, start_automation, stop_automation, get_automation_log. Variables (get_variables / set_variable) are shared with running schemes.
        The user can disable tool groups in ClickZen's AI page; a disabled tool reports that it is disabled.
        """;

    private static readonly Dictionary<string, McpCapability> CapabilityByTool = BuildCapabilityMap();

    private readonly IClickZenBackend? _backend;
    private readonly Func<McpCapability, bool> _allowed;

    /// <param name="backend">Null builds the tool list only (stdio bridge fallback); every call then fails.</param>
    /// <param name="allowed">Which capability groups are enabled (for get_status).</param>
    public ClickZenTools(IClickZenBackend? backend, Func<McpCapability, bool>? allowed = null)
    {
        _backend = backend;
        _allowed = allowed ?? (_ => true);
    }

    /// <summary>Tool name → capability group, for every tool.</summary>
    public static IReadOnlyDictionary<string, McpCapability> Capabilities => CapabilityByTool;

    /// <summary>Capability of a tool name; unknown names count as <see cref="McpCapability.Observe"/>.</summary>
    public static McpCapability CapabilityOf(string toolName) =>
        CapabilityByTool.TryGetValue(toolName, out var c) ? c : McpCapability.Observe;

    /// <summary>Creates the SDK tool objects bound to this instance.</summary>
    public IReadOnlyList<McpServerTool> CreateTools()
    {
        var list = new List<McpServerTool>();
        foreach (var m in ToolMethods())
        {
            list.Add(McpServerTool.Create(m, this, new McpServerToolCreateOptions()));
        }

        return list;
    }

    private IClickZenBackend B => _backend ?? throw new McpToolException(
        "ClickZen is not reachable. Start ClickZen and enable the MCP server on its AI page, then retry.");

    // =====================================================================================
    //  Observe
    // =====================================================================================

    [McpServerTool(Name = "get_status", Title = "ClickZen status", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Observe)]
    [Description("Overall ClickZen state: version, adb availability, current device, open automation scheme, whether automation / recording / playback is running, and which tool groups the user enabled.")]
    public Task<string> GetStatus(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var s = await B.GetStatusAsync(ct);
        var enabled = McpCapabilities.All.Where(_allowed).Select(c => c.ToString().ToLowerInvariant()).ToArray();
        return McpJson.Serialize(new { status = s, enabledToolGroups = enabled });
    }, cancellationToken);

    [McpServerTool(Name = "list_devices", Title = "List devices", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Observe)]
    [Description("Lists Android devices and bound emulator windows known to ClickZen with id, name, connection kind, state, screen size (the coordinate space of input tools), density, whether a live picture is running and which input method will be used. The device marked isCurrent is used when a tool's `device` argument is omitted.")]
    public Task<string> ListDevices(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var devices = await B.ListDevicesAsync(ct);
        return devices.Count == 0
            ? "No devices. Connect an Android device with USB debugging enabled, or add a wireless device / emulator in ClickZen's Devices page."
            : McpJson.Serialize(devices);
    }, cancellationToken);

    [McpServerTool(Name = "connect_device", Title = "Connect / select device", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Observe)]
    [Description("Starts the live picture (scrcpy mirroring or window capture) of a device and waits until it is ready; optionally makes it the current device. Screenshots start mirroring automatically, so this is only needed to switch the current device or to warm up.")]
    public Task<string> ConnectDevice(
        [Description("Device id or name from list_devices; omit for the current device.")] string? device = null,
        [Description("Also make this the current device (used when `device` is omitted later).")] bool makeCurrent = false,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var d = await B.ConnectAsync(device, ct);
        if (makeCurrent)
        {
            d = await B.SelectDeviceAsync(d.Id, ct);
        }

        return McpJson.Serialize(d);
    }, cancellationToken);

    [McpServerTool(Name = "list_emulators", Title = "List running emulators", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Observe)]
    [Description("Detects Android emulators running on this PC (MuMu, LDPlayer, NoxPlayer, MEmu, BlueStacks, Android Studio) from their processes and their own settings – no adb needed – with each instance's exact adb address, whether it is connected to ClickZen and whether its window is bound as a window device. Also lists installed emulators that are not running. Use connect_emulator to connect one, then list_devices / take_screenshot as usual.")]
    public Task<string> ListEmulators(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var list = await B.ListEmulatorsAsync(ct);
        if (list.Running.Count == 0)
        {
            return list.Installed.Count == 0
                ? "No emulator is running and none is installed (MuMu, LDPlayer, NoxPlayer, MEmu, BlueStacks or Android Studio). Start an emulator, or connect a phone."
                : $"No emulator is running. Installed: {string.Join(", ", list.Installed)}. Ask the user to start one (ClickZen does not launch emulators).";
        }

        return McpJson.Serialize(list);
    }, cancellationToken);

    [McpServerTool(Name = "connect_emulator", Title = "Connect emulator", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Observe)]
    [Description("Connects a running emulator to ClickZen over adb at the exact port read from its settings (adb connect 127.0.0.1:port; instances adb already lists as emulator-NNNN are just selected). Afterwards it appears in list_devices and is the current device unless makeCurrent=false.")]
    public Task<string> ConnectEmulator(
        [Description("Emulator id, instance name or display name from list_emulators; omit when only one emulator is running.")] string? emulator = null,
        [Description("Make the connected emulator the current device (default true).")] bool makeCurrent = true,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var r = await B.ConnectEmulatorAsync(emulator, makeCurrent, ct);
        return r.Connected ? McpJson.Serialize(r) : throw new McpToolException(r.Message);
    }, cancellationToken);

    [McpServerTool(Name = "bind_emulator_window", Title = "Bind emulator window", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Observe)]
    [Description("Binds a running emulator's window as a window device: ClickZen captures the emulator window itself (works when the window is in the background or covered, not when minimized), locates the Android screen in it automatically, links the emulator's adb device for input and saves the binding (binding again updates it). Returns the window device id to pass as `device`. Use it when screenshots of the window are wanted instead of scrcpy mirroring, e.g. for emulators where scrcpy video does not work.")]
    public Task<string> BindEmulatorWindow(
        [Description("Emulator id, instance name or display name from list_emulators; omit when only one emulator is running.")] string? emulator = null,
        CancellationToken cancellationToken = default) => Run(async ct =>
        McpJson.Serialize(await B.BindEmulatorWindowAsync(emulator, ct)), cancellationToken);

    [McpServerTool(Name = "take_screenshot", Title = "Take screenshot", ReadOnly = true, Idempotent = false, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Observe)]
    [Description("Captures the device screen and returns it as an image (JPEG by default, scaled so the longest edge is maxSize). The text part tells how image pixels map to device pixels. To tap something seen on the image, pass its image coordinates to tap with refWidth/refHeight = the image size (full-screen screenshots only).")]
    public Task<CallToolResult> TakeScreenshot(
        [Description("Device id or name from list_devices; omit for the current device.")] string? device = null,
        [Description("\"jpeg\" (default, smaller) or \"png\" (lossless, for reading small text or exact colours).")] string format = "jpeg",
        [Description("Longest edge of the returned image in pixels (default 1280, 0 = full resolution, otherwise 256–4096).")] int maxSize = 1280,
        [Description("JPEG quality 30–100 (default 80).")] int quality = 80,
        [Description("Optional crop region in device pixels: left. Give x, y, width and height together.")] int? x = null,
        [Description("Crop region: top (device pixels).")] int? y = null,
        [Description("Crop region: width (device pixels).")] int? width = null,
        [Description("Crop region: height (device pixels).")] int? height = null,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var fmt = format?.Trim().ToLowerInvariant() switch
        {
            null or "" or "jpeg" or "jpg" => "jpeg",
            "png" => "png",
            _ => throw new McpToolException("format must be \"jpeg\" or \"png\"."),
        };
        var size = maxSize <= 0 ? 0 : Math.Clamp(maxSize, 256, 4096);
        var region = Region(x, y, width, height);
        var shot = await B.TakeScreenshotAsync(device, new McpScreenshotRequest(fmt, size, Math.Clamp(quality, 30, 100), region), ct);
        return new CallToolResult
        {
            Content =
            [
                ImageContentBlock.FromBytes(shot.Data, shot.MimeType),
                new TextContentBlock { Text = DescribeScreenshot(shot) },
            ],
        };
    }, cancellationToken);

    [McpServerTool(Name = "dump_ui", Title = "Dump UI hierarchy", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Observe)]
    [Description("Returns the on-screen Android UI elements (uiautomator): class, text, content description, resource id, flags (clickable, scrollable, checked…), centre point and bounds in device pixels. Exact and cheap compared to screenshots for native apps; returns little for games, WebView canvases and video. Needs adb (window devices use their linked adb device). Takes 1–3 s.")]
    public Task<string> DumpUi(
        [Description("Device id or name from list_devices; omit for the current device.")] string? device = null,
        [Description("Only elements whose text, description, id or class contains this (case-insensitive).")] string? filter = null,
        [Description("Maximum elements to return (default 250).")] int maxNodes = 250,
        [Description("Include layout-only containers too (default false: only elements with text, a description or an interaction).")] bool includeAll = false,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var nodes = await ReadUiAsync(device, includeAll, ct);
        var shown = UiHierarchy.Filter(nodes, filter);
        if (shown.Count == 0)
        {
            return filter is null
                ? "No UI elements found (the screen may be a game, video or canvas – use take_screenshot instead)."
                : $"No element contains \"{filter}\". {nodes.Count} element(s) on screen; call dump_ui without filter to see them.";
        }

        return UiHierarchy.Format(shown, Math.Clamp(maxNodes, 1, 2000));
    }, cancellationToken);

    [McpServerTool(Name = "find_image", Title = "Find image on screen", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Observe)]
    [Description("Searches the live screen for an image (OpenCV template matching): a template of the open automation scheme (by name), or any PNG/JPEG you provide as base64 – for example a crop of an earlier screenshot (pass that screenshot's imageScale so it is resized to device pixels). Returns whether it was found, the score and the centre in device pixels – ready for tap.")]
    public Task<string> FindImage(
        [Description("Name (or id) of a template in the open scheme (see get_scheme).")] string? template = null,
        [Description("Alternatively: the image to find, base64-encoded PNG or JPEG (data: URL prefix allowed).")] string? imageBase64 = null,
        [Description("For imageBase64: device pixels per image pixel. 1 (default) when the image was cut at device resolution; when cut from a scaled screenshot use device width / screenshot width (e.g. 1080/576 = 1.875).")] double imageScale = 1,
        [Description("Device id or name from list_devices; omit for the current device.")] string? device = null,
        [Description("Similarity threshold 0.5–1.0 (default 0.85).")] double threshold = 0.85,
        [Description("Match in grayscale (ignores tint, faster).")] bool grayscale = false,
        [Description("Also try scales 0.8–1.2 (slower; for UIs that resize).")] bool multiScale = false,
        [Description("Optional search area in device pixels: left. Give x, y, width and height together.")] int? x = null,
        [Description("Search area: top.")] int? y = null,
        [Description("Search area: width.")] int? width = null,
        [Description("Search area: height.")] int? height = null,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        byte[]? png = null;
        if (!string.IsNullOrWhiteSpace(imageBase64))
        {
            png = DecodeBase64Image(imageBase64);
        }

        if (png is null && string.IsNullOrWhiteSpace(template))
        {
            throw new McpToolException("Pass either template (a template name of the open scheme) or imageBase64.");
        }

        if (!double.IsFinite(imageScale) || imageScale < 0.1 || imageScale > 10)
        {
            throw new McpToolException("imageScale must be between 0.1 and 10.");
        }

        var r = await B.FindImageAsync(device, new McpImageQuery(template, png, Region(x, y, width, height), Math.Clamp(threshold, 0.5, 1.0), grayscale, multiScale, imageScale), ct);
        return McpJson.Serialize(r);
    }, cancellationToken);

    [McpServerTool(Name = "get_pixel_color", Title = "Get pixel colour", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Observe)]
    [Description("Returns the colour (#RRGGBB and r/g/b) of one screen pixel. Coordinates are device pixels, or image pixels of a screenshot with refWidth/refHeight.")]
    public Task<string> GetPixelColor(
        [Description("X in device pixels (or in the reference image when refWidth/refHeight are given).")] int x,
        [Description("Y in device pixels (or in the reference image).")] int y,
        [Description("Device id or name from list_devices; omit for the current device.")] string? device = null,
        [Description("Width of the image x/y were measured on (e.g. a scaled screenshot).")] int? refWidth = null,
        [Description("Height of the image x/y were measured on.")] int? refHeight = null,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var screen = await B.GetScreenSizeAsync(device, ct);
        var p = CoordinateScaling.ToDevice(x, y, refWidth, refHeight, screen);
        CoordinateScaling.EnsureOnScreen(p, screen);
        return McpJson.Serialize(await B.GetPixelAsync(device, p, ct));
    }, cancellationToken);

    [McpServerTool(Name = "wait_for_text", Title = "Wait for text", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Observe)]
    [Description("Waits until a UI element whose text or content description contains the given text appears (or disappears with gone=true), polling dump_ui. Returns the element (with its centre) or an error on timeout. Native UI only.")]
    public Task<string> WaitForText(
        [Description("Text to look for (case-insensitive substring of text or content description).")] string text,
        [Description("Device id or name from list_devices; omit for the current device.")] string? device = null,
        [Description("Give up after this many milliseconds (default 10000, max 60000).")] int timeoutMs = 10_000,
        [Description("Wait for the text to disappear instead.")] bool gone = false,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new McpToolException("text is required.");
        }

        var sw = Stopwatch.StartNew();
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 0, 60_000));
        while (true)
        {
            var nodes = await ReadUiAsync(device, includeAll: false, ct);
            var match = MatchText(nodes, text, exact: false).FirstOrDefault();
            if (!gone && match is not null)
            {
                return $"Found after {sw.ElapsedMilliseconds} ms:\n" + UiHierarchy.Format([match], 1);
            }

            if (gone && match is null)
            {
                return $"\"{text}\" is gone after {sw.ElapsedMilliseconds} ms.";
            }

            if (sw.Elapsed >= timeout)
            {
                throw new McpToolException(gone
                    ? $"\"{text}\" is still on screen after {sw.ElapsedMilliseconds} ms."
                    : $"\"{text}\" did not appear within {sw.ElapsedMilliseconds} ms. Visible texts: {VisibleTexts(nodes)}");
            }

            await Task.Delay(500, ct);
        }
    }, cancellationToken);

    [McpServerTool(Name = "list_apps", Title = "List installed apps", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Observe)]
    [Description("Lists installed package names (third-party apps by default). Use with launch_app.")]
    public Task<string> ListApps(
        [Description("Device id or name from list_devices; omit for the current device.")] string? device = null,
        [Description("Only packages containing this text (case-insensitive).")] string? filter = null,
        [Description("Include system packages.")] bool includeSystem = false,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var apps = await B.ListAppsAsync(device, includeSystem, ct);
        var shown = apps.Where(a => string.IsNullOrWhiteSpace(filter) || a.Package.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Package, StringComparer.Ordinal)
            .ToList();
        return shown.Count == 0 ? "No matching packages." : $"{shown.Count} package(s):\n" + string.Join('\n', shown.Select(a => a.System ? a.Package + " (system)" : a.Package));
    }, cancellationToken);

    // =====================================================================================
    //  Input
    // =====================================================================================

    [McpServerTool(Name = "tap", Title = "Tap", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Input)]
    [Description("Taps a point on the screen; with durationMs ≥ 500 it is a long press. Coordinates are device pixels, or image pixels of a scaled screenshot when refWidth/refHeight give that image's size.")]
    public Task<string> Tap(
        [Description("X in device pixels (or in the reference image when refWidth/refHeight are given).")] int x,
        [Description("Y in device pixels (or in the reference image).")] int y,
        [Description("Device id or name from list_devices; omit for the current device.")] string? device = null,
        [Description("Press duration in milliseconds (default 0 = normal tap; 500+ = long press; max 10000).")] int durationMs = 0,
        [Description("Width of the image x/y were measured on (e.g. a scaled screenshot).")] int? refWidth = null,
        [Description("Height of the image x/y were measured on.")] int? refHeight = null,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var screen = await B.GetScreenSizeAsync(device, ct);
        var p = CoordinateScaling.ToDevice(x, y, refWidth, refHeight, screen);
        CoordinateScaling.EnsureOnScreen(p, screen);
        var d = Math.Clamp(durationMs, 0, 10_000);
        await B.TapAsync(device, p, d, ct);
        return d >= 500 ? $"Long-pressed ({p.X}, {p.Y}) for {d} ms." : $"Tapped ({p.X}, {p.Y}).";
    }, cancellationToken);

    [McpServerTool(Name = "tap_element", Title = "Tap UI element", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Input)]
    [Description("Finds a UI element by its text, content description or resource id (via dump_ui) and taps its centre. More reliable than pixel coordinates for native apps. Fails with the list of visible texts when nothing matches.")]
    public Task<string> TapElement(
        [Description("Text or content description to match (case-insensitive substring unless exact=true).")] string? text = null,
        [Description("Alternatively: resource id, full (com.app:id/name) or just the name.")] string? resourceId = null,
        [Description("Device id or name from list_devices; omit for the current device.")] string? device = null,
        [Description("Require the whole text to match (case-insensitive).")] bool exact = false,
        [Description("Which match to tap when several match (0 = first, in screen order).")] int index = 0,
        [Description("Long-press instead of tap.")] bool longPress = false,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(resourceId))
        {
            throw new McpToolException("Pass text or resourceId.");
        }

        var nodes = await ReadUiAsync(device, includeAll: false, ct);
        IEnumerable<UiNode> matches = nodes;
        if (!string.IsNullOrWhiteSpace(text))
        {
            matches = MatchText(nodes, text, exact);
        }

        if (!string.IsNullOrWhiteSpace(resourceId))
        {
            var id = resourceId.Trim();
            matches = matches.Where(n => n.ResourceId is { } rid && (rid.Equals(id, StringComparison.OrdinalIgnoreCase) || rid.EndsWith(":id/" + id, StringComparison.OrdinalIgnoreCase)));
        }

        var list = matches.ToList();
        if (list.Count == 0)
        {
            throw new McpToolException($"No element matches. Visible texts: {VisibleTexts(nodes)}");
        }

        if (index < 0 || index >= list.Count)
        {
            throw new McpToolException($"{list.Count} element(s) match; index must be 0–{list.Count - 1}.");
        }

        var node = list[index];
        var p = node.Center;
        await B.TapAsync(device, p, longPress ? 800 : 0, ct);
        return (longPress ? "Long-pressed " : "Tapped ") + $"({p.X}, {p.Y}):\n" + UiHierarchy.Format([node], 1)
            + (list.Count > 1 ? $"({list.Count} elements matched; used index {index}.)" : "");
    }, cancellationToken);

    [McpServerTool(Name = "swipe", Title = "Swipe", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Input)]
    [Description("Swipes in a straight line from (x1, y1) to (x2, y2). Use a short duration (100–300 ms) for a fling, a long one (800+ ms) for a precise drag. Coordinates are device pixels, or image pixels with refWidth/refHeight.")]
    public Task<string> Swipe(
        [Description("Start X.")] int x1,
        [Description("Start Y.")] int y1,
        [Description("End X.")] int x2,
        [Description("End Y.")] int y2,
        [Description("Device id or name from list_devices; omit for the current device.")] string? device = null,
        [Description("Duration in milliseconds (default 300, 50–10000).")] int durationMs = 300,
        [Description("Width of the image the points were measured on.")] int? refWidth = null,
        [Description("Height of the image the points were measured on.")] int? refHeight = null,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var screen = await B.GetScreenSizeAsync(device, ct);
        var a = CoordinateScaling.ToDevice(x1, y1, refWidth, refHeight, screen);
        var b = CoordinateScaling.ToDevice(x2, y2, refWidth, refHeight, screen);
        CoordinateScaling.EnsureOnScreen(a, screen);
        CoordinateScaling.EnsureOnScreen(b, screen);
        var d = Math.Clamp(durationMs, 50, 10_000);
        await B.SwipeAsync(device, [a, b], d, ct);
        return $"Swiped ({a.X}, {a.Y}) → ({b.X}, {b.Y}) in {d} ms.";
    }, cancellationToken);

    [McpServerTool(Name = "scroll", Title = "Scroll", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Input)]
    [Description("Scrolls the content in a direction with a swipe through the middle of the screen (or of the given area). direction=down reveals content further down (the finger moves up).")]
    public Task<string> Scroll(
        [Description("\"down\", \"up\", \"left\" or \"right\" – the direction the content scrolls towards.")] string direction,
        [Description("Device id or name from list_devices; omit for the current device.")] string? device = null,
        [Description("Swipe length as a fraction of the screen (or area) size, 0.1–0.9 (default 0.5).")] double distance = 0.5,
        [Description("Swipe duration in ms (default 400).")] int durationMs = 400,
        [Description("Optional area to scroll inside, device pixels: left. Give x, y, width and height together.")] int? x = null,
        [Description("Area: top.")] int? y = null,
        [Description("Area: width.")] int? width = null,
        [Description("Area: height.")] int? height = null,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var screen = await B.GetScreenSizeAsync(device, ct);
        if (screen.IsEmpty)
        {
            throw new McpToolException("The screen size of this device is unknown yet; take a screenshot first.");
        }

        var area = Region(x, y, width, height)?.ClampTo(screen) ?? new RectI(0, 0, screen.Width, screen.Height);
        if (area.IsEmpty)
        {
            throw new McpToolException("The scroll area is outside the screen.");
        }

        var f = Math.Clamp(distance, 0.1, 0.9);
        var c = area.Center;
        var dx = (int)(area.Width * f / 2);
        var dy = (int)(area.Height * f / 2);
        var (a, b) = direction?.Trim().ToLowerInvariant() switch
        {
            "down" => (new PointI(c.X, c.Y + dy), new PointI(c.X, c.Y - dy)),
            "up" => (new PointI(c.X, c.Y - dy), new PointI(c.X, c.Y + dy)),
            "right" => (new PointI(c.X + dx, c.Y), new PointI(c.X - dx, c.Y)),
            "left" => (new PointI(c.X - dx, c.Y), new PointI(c.X + dx, c.Y)),
            _ => throw new McpToolException("direction must be up, down, left or right."),
        };
        var d = Math.Clamp(durationMs, 100, 5000);
        await B.SwipeAsync(device, [a, b], d, ct);
        return $"Scrolled {direction!.Trim().ToLowerInvariant()} (swipe ({a.X}, {a.Y}) → ({b.X}, {b.Y}), {d} ms).";
    }, cancellationToken);

    [McpServerTool(Name = "press_key", Title = "Press key", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Input)]
    [Description("Presses an Android key. Names: " + KeyNames.Documented + ". Also accepts KEYCODE_* constants and numeric key codes.")]
    public Task<string> PressKey(
        [Description("Key name (e.g. \"back\", \"home\", \"enter\"), KEYCODE_* constant or numeric key code.")] string key,
        [Description("Device id or name from list_devices; omit for the current device.")] string? device = null,
        [Description("Press it this many times (1–20).")] int repeat = 1,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        if (!KeyNames.TryResolve(key, out var code))
        {
            throw new McpToolException($"Unknown key \"{key}\". Use one of: {KeyNames.Documented}, a KEYCODE_* name or a number.");
        }

        var n = Math.Clamp(repeat, 1, 20);
        for (var i = 0; i < n; i++)
        {
            await B.KeyAsync(device, code, ct);
        }

        return n == 1 ? $"Pressed {key} (key code {code})." : $"Pressed {key} (key code {code}) {n} times.";
    }, cancellationToken);

    [McpServerTool(Name = "input_text", Title = "Type text", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Input)]
    [Description("Types text into the focused input field (tap the field first). Non-ASCII text (e.g. Chinese) is pasted through the clipboard and needs scrcpy mirroring of an adb device.")]
    public Task<string> InputText(
        [Description("The text to type (max 4000 characters).")] string text,
        [Description("Device id or name from list_devices; omit for the current device.")] string? device = null,
        [Description("Press Enter afterwards (submit / search).")] bool pressEnter = false,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new McpToolException("text is empty.");
        }

        if (text.Length > MaxTextChars)
        {
            throw new McpToolException($"text is longer than {MaxTextChars} characters.");
        }

        await B.TextAsync(device, text, ct);
        if (pressEnter)
        {
            await B.KeyAsync(device, 66, ct);
        }

        return $"Typed {text.Length} character(s)" + (pressEnter ? " and pressed Enter." : ".");
    }, cancellationToken);

    [McpServerTool(Name = "launch_app", Title = "Launch app", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Input)]
    [Description("Starts an app by package name (its launcher activity), like tapping its icon. Use list_apps to find package names.")]
    public Task<string> LaunchApp(
        [Description("Package name, e.g. com.android.settings.")] string package,
        [Description("Device id or name from list_devices; omit for the current device.")] string? device = null,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var pkg = ValidatePackage(package);
        var info = await B.LaunchAppAsync(device, pkg, ct);
        return $"Launched {pkg}." + (string.IsNullOrWhiteSpace(info) ? "" : " " + info.Trim());
    }, cancellationToken);

    [McpServerTool(Name = "stop_app", Title = "Force-stop app", ReadOnly = false, Idempotent = true, Destructive = true, OpenWorld = false)]
    [McpCapability(McpCapability.Input)]
    [Description("Force-stops an app (am force-stop). Unsaved state in that app is lost.")]
    public Task<string> StopApp(
        [Description("Package name, e.g. com.example.game.")] string package,
        [Description("Device id or name from list_devices; omit for the current device.")] string? device = null,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var pkg = ValidatePackage(package);
        await B.StopAppAsync(device, pkg, ct);
        return $"Stopped {pkg}.";
    }, cancellationToken);

    // =====================================================================================
    //  Automation
    // =====================================================================================

    [McpServerTool(Name = "get_scheme", Title = "Open automation scheme", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Automation)]
    [Description("Describes the automation scheme open in ClickZen: name, file, reference resolution, tasks (with enabled state), template names (usable with find_image), declared variables, embedded recordings, validation issues and – while running – per-device state and rounds.")]
    public Task<string> GetScheme(CancellationToken cancellationToken = default) => Run(async ct =>
        McpJson.Serialize(await B.GetSchemeAsync(ct)), cancellationToken);

    [McpServerTool(Name = "list_schemes", Title = "List scheme files", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Automation)]
    [Description("Lists .czscheme files in ClickZen's schemes folder (Documents\\ClickZen\\Schemes).")]
    public Task<string> ListSchemes(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var files = await B.ListSchemesAsync(ct);
        return files.Count == 0 ? "No scheme files in the schemes folder." : McpJson.Serialize(files);
    }, cancellationToken);

    [McpServerTool(Name = "open_scheme", Title = "Open scheme", ReadOnly = false, Idempotent = true, Destructive = true, OpenWorld = false)]
    [McpCapability(McpCapability.Automation)]
    [Description("Opens a .czscheme file in ClickZen (replaces the open scheme). Refuses when the open scheme has unsaved changes or automation is running, unless discardChanges=true (stops the run and drops the changes).")]
    public Task<string> OpenScheme(
        [Description("Full path of a .czscheme file, or a file name from list_schemes.")] string path,
        [Description("Discard unsaved changes / stop running automation first.")] bool discardChanges = false,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new McpToolException("path is required.");
        }

        return McpJson.Serialize(await B.OpenSchemeAsync(path.Trim(), discardChanges, ct));
    }, cancellationToken);

    [McpServerTool(Name = "start_automation", Title = "Start automation", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Automation)]
    [Description("Runs the open automation scheme on one or more devices (each gets its own engine). Devices must have a live picture (started automatically). Without devices: the targets chosen on the Automation page, else the current device. Runs until stop_automation or until the scheme stops itself.")]
    public Task<string> StartAutomation(
        [Description("Device ids or names; omit for the default targets.")] string[]? devices = null,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var r = await B.StartAutomationAsync(devices ?? [], ct);
        if (r.StartedOn.Count == 0)
        {
            throw new McpToolException("Automation did not start. " + string.Join(" ", r.Problems));
        }

        return McpJson.Serialize(new { started = r.StartedOn, problems = r.Problems });
    }, cancellationToken);

    [McpServerTool(Name = "stop_automation", Title = "Stop automation", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Automation)]
    [Description("Stops every running automation engine and waits until they ended.")]
    public Task<string> StopAutomation(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await B.StopAutomationAsync(ct);
        return "Automation stopped.";
    }, cancellationToken);

    [McpServerTool(Name = "get_automation_log", Title = "Automation log", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Automation)]
    [Description("Returns the newest lines of the automation run log (task firings, warnings, errors), oldest first.")]
    public Task<string> GetAutomationLog(
        [Description("Number of lines (default 50, max 500).")] int max = 50,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var entries = await B.GetAutomationLogAsync(Math.Clamp(max, 1, 500), ct);
        if (entries.Count == 0)
        {
            return "The automation log is empty.";
        }

        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.Append(e.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ').Append(e.Level);
            if (!string.IsNullOrEmpty(e.Device))
            {
                sb.Append(" [").Append(e.Device).Append(']');
            }

            sb.Append(' ').Append(e.Message).Append('\n');
        }

        return sb.ToString();
    }, cancellationToken);

    [McpServerTool(Name = "set_task_enabled", Title = "Enable / disable task", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Automation)]
    [Description("Enables or disables a task of the open scheme (by name or id). Marks the scheme unsaved; takes effect on the next start.")]
    public Task<string> SetTaskEnabled(
        [Description("Task name or id (see get_scheme).")] string task,
        [Description("true to enable, false to disable.")] bool enabled,
        CancellationToken cancellationToken = default) => Run(async ct =>
        McpJson.Serialize(await B.SetTaskEnabledAsync(task, enabled, ct)), cancellationToken);

    // =====================================================================================
    //  Recording
    // =====================================================================================

    [McpServerTool(Name = "list_recordings", Title = "List recordings", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Recording)]
    [Description("Lists .czrec touch recordings in ClickZen's recordings folder (Documents\\ClickZen\\Recordings). Recordings embedded in the open scheme are listed by get_scheme.")]
    public Task<string> ListRecordings(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var files = await B.ListRecordingsAsync(ct);
        return files.Count == 0 ? "No recordings in the recordings folder." : McpJson.Serialize(files);
    }, cancellationToken);

    [McpServerTool(Name = "play_recording", Title = "Play recording", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Recording)]
    [Description("Replays a touch recording on a device with its original timing (rescaled to the device's screen). By default returns immediately; wait=true returns when playback ended (up to 10 minutes).")]
    public Task<string> PlayRecording(
        [Description("A .czrec path, a file name from list_recordings, or the id of a recording embedded in the open scheme.")] string recording,
        [Description("Device id or name from list_devices; omit for the current device.")] string? device = null,
        [Description("Speed factor 0.1–10 (default 1).")] double speed = 1,
        [Description("Repetitions, 1–1000 (default 1).")] int loops = 1,
        [Description("Add small human-like random jitter to positions and timing.")] bool randomize = false,
        [Description("Wait until playback ended before returning.")] bool wait = false,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        if (string.IsNullOrWhiteSpace(recording))
        {
            throw new McpToolException("recording is required.");
        }

        var request = new McpPlaybackRequest(recording.Trim(), device, Math.Clamp(double.IsFinite(speed) ? speed : 1, 0.1, 10), Math.Clamp(loops, 1, 1000), randomize);
        if (!wait)
        {
            return McpJson.Serialize(await B.PlayRecordingAsync(request, false, ct));
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(10));
        return McpJson.Serialize(await B.PlayRecordingAsync(request, true, limit.Token));
    }, cancellationToken);

    [McpServerTool(Name = "stop_playback", Title = "Stop playback", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Recording)]
    [Description("Stops any recording playback (started by AI or on the Recording page).")]
    public Task<string> StopPlayback(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await B.StopPlaybackAsync(ct);
        return "Playback stopped.";
    }, cancellationToken);

    // =====================================================================================
    //  Variables
    // =====================================================================================

    [McpServerTool(Name = "get_variables", Title = "Get variables", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Variables)]
    [Description("Returns the automation variables: the global store (shared by all devices) and, for each running device, its scheme-scope variables.")]
    public Task<string> GetVariables(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var v = await B.GetVariablesAsync(ct);
        return McpJson.Serialize(new { global = v.Global, devices = v.Devices });
    }, cancellationToken);

    [McpServerTool(Name = "set_variable", Title = "Set variable", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [McpCapability(McpCapability.Variables)]
    [Description("Sets an automation variable. Without device: in the global store (visible to every running scheme). With device: in that device's running scheme scope. Declared variables keep their type (the value is converted).")]
    public Task<string> SetVariable(
        [Description("Variable name.")] string name,
        [Description("New value: number, boolean or string.")] JsonElement value,
        [Description("Write into this running device's scheme scope instead of the global store.")] string? device = null,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        if (string.IsNullOrWhiteSpace(name) || !VariableNameRegex().IsMatch(name.Trim()))
        {
            throw new McpToolException("name must start with a letter or underscore and contain letters, digits, underscores and dots.");
        }

        if (value.ValueKind is not (JsonValueKind.Number or JsonValueKind.String or JsonValueKind.True or JsonValueKind.False))
        {
            throw new McpToolException("value must be a number, a boolean or a string.");
        }

        var target = string.IsNullOrWhiteSpace(device) ? McpVariableTarget.Global : McpVariableTarget.Device;
        var stored = await B.SetVariableAsync(name.Trim(), VariableValue.FromJson(value), target, device, ct);
        return McpJson.Serialize(new { name = name.Trim(), value = stored, scope = target == McpVariableTarget.Global ? "global" : "device" });
    }, cancellationToken);

    // =====================================================================================
    //  Shell
    // =====================================================================================

    [McpServerTool(Name = "adb_shell", Title = "adb shell", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
    [McpCapability(McpCapability.Shell)]
    [Description("Runs a shell command on the device (adb shell) and returns its output (stdout and stderr, truncated at 64 KB). Powerful: can change settings, delete data or install apps. Prefer the dedicated tools when one exists.")]
    public Task<string> AdbShell(
        [Description("The command line, e.g. \"dumpsys battery\" or \"getprop ro.product.model\".")] string command,
        [Description("Device id or name from list_devices; omit for the current device.")] string? device = null,
        [Description("Run as root via su (rooted devices only).")] bool asRoot = false,
        [Description("Timeout in seconds (default 30, max 300).")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default) => Run(async ct =>
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new McpToolException("command is required.");
        }

        var r = await B.ShellAsync(device, command, asRoot, TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300)), ct);
        var output = r.Output ?? "";
        var truncated = r.Truncated;
        if (output.Length > MaxShellOutputChars)
        {
            output = output[..MaxShellOutputChars];
            truncated = true;
        }

        var head = string.Create(CultureInfo.InvariantCulture, $"[{r.ElapsedMs} ms{(truncated ? ", output truncated" : "")}]\n");
        return head + (output.Length == 0 ? "(no output)" : output);
    }, cancellationToken);

    // =====================================================================================
    //  helpers
    // =====================================================================================

    /// <summary>
    /// Runs a tool body, turning failures into <see cref="McpException"/> so the message reaches the client
    /// (the SDK replaces other exceptions with a generic text).
    /// </summary>
    internal static async Task<T> Run<T>(Func<CancellationToken, Task<T>> body, CancellationToken ct)
    {
        try
        {
            return await body(ct);
        }
        catch (McpException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new McpException("The operation timed out.", ex);
        }
        catch (McpToolException ex)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (Exception ex)
        {
            throw new McpException($"{ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    internal static RectI? Region(int? x, int? y, int? width, int? height)
    {
        if (x is null && y is null && width is null && height is null)
        {
            return null;
        }

        if (x is not { } rx || y is not { } ry || width is not { } rw || height is not { } rh)
        {
            throw new McpToolException("Give x, y, width and height together (or none of them).");
        }

        if (rw <= 0 || rh <= 0)
        {
            throw new McpToolException("width and height must be positive.");
        }

        return new RectI(rx, ry, rw, rh);
    }

    internal static string ValidatePackage(string? package)
    {
        var p = package?.Trim() ?? "";
        if (!PackageRegex().IsMatch(p))
        {
            throw new McpToolException($"\"{package}\" is not a valid package name (e.g. com.example.app).");
        }

        return p;
    }

    internal static byte[] DecodeBase64Image(string text)
    {
        var s = text.Trim();
        var comma = s.IndexOf(',', StringComparison.Ordinal);
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
        {
            s = s[(comma + 1)..];
        }

        try
        {
            var bytes = Convert.FromBase64String(s);
            var png = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
            var jpeg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8;
            return png || jpeg ? bytes : throw new McpToolException("imageBase64 must be a PNG or JPEG image.");
        }
        catch (FormatException ex)
        {
            throw new McpToolException("imageBase64 is not valid base64.", ex);
        }
    }

    internal static string DescribeScreenshot(McpScreenshot s)
    {
        var full = s.Region.X == 0 && s.Region.Y == 0 && s.Region.Width == s.ScreenWidth && s.Region.Height == s.ScreenHeight;
        var sx = s.ImageWidth > 0 ? (double)s.Region.Width / s.ImageWidth : 1;
        var sy = s.ImageHeight > 0 ? (double)s.Region.Height / s.ImageHeight : 1;
        var inv = CultureInfo.InvariantCulture;
        var oneToOne = Math.Abs(sx - 1) < 0.001 && Math.Abs(sy - 1) < 0.001;
        var findHint = oneToOne ? "" : string.Create(inv, $" When passing this image (or a crop of it) to find_image, set imageScale={sx:0.####}.");
        if (full)
        {
            return oneToOne
                ? string.Create(inv, $"Screenshot {s.ImageWidth}x{s.ImageHeight} = device screen (1:1). Image pixels are device pixels.")
                : string.Create(inv, $"Screenshot {s.ImageWidth}x{s.ImageHeight} of a {s.ScreenWidth}x{s.ScreenHeight} screen. To tap a point measured on this image, call tap with x, y and refWidth={s.ImageWidth}, refHeight={s.ImageHeight} (or multiply by {sx:0.####}).") + findHint;
        }

        return string.Create(inv, $"Crop {s.ImageWidth}x{s.ImageHeight} of device region x={s.Region.X}, y={s.Region.Y}, {s.Region.Width}x{s.Region.Height} (screen {s.ScreenWidth}x{s.ScreenHeight}). Device coordinates = ({s.Region.X} + imageX × {sx:0.####}, {s.Region.Y} + imageY × {sy:0.####}).") + findHint;
    }

    private async Task<IReadOnlyList<UiNode>> ReadUiAsync(string? device, bool includeAll, CancellationToken ct)
    {
        var dump = await B.DumpUiAsync(device, ct);
        return UiHierarchy.Parse(dump.Xml, dump.ScaleX, dump.ScaleY, includeAll);
    }

    private static IEnumerable<UiNode> MatchText(IEnumerable<UiNode> nodes, string text, bool exact)
    {
        var t = text.Trim();
        bool Hit(string? s) => s is not null && (exact ? s.Trim().Equals(t, StringComparison.OrdinalIgnoreCase) : s.Contains(t, StringComparison.OrdinalIgnoreCase));
        return nodes.Where(n => Hit(n.Text) || Hit(n.ContentDesc));
    }

    private static string VisibleTexts(IReadOnlyList<UiNode> nodes)
    {
        var texts = nodes.Select(n => n.Text ?? n.ContentDesc).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(40).Select(s => "\"" + UiHierarchy.Truncate(s!, 40) + "\"").ToList();
        return texts.Count == 0 ? "(none – this screen may not expose native UI; use take_screenshot)" : string.Join(", ", texts);
    }

    private static IEnumerable<MethodInfo> ToolMethods() =>
        typeof(ClickZenTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    private static Dictionary<string, McpCapability> BuildCapabilityMap()
    {
        var map = new Dictionary<string, McpCapability>(StringComparer.Ordinal);
        foreach (var m in ToolMethods())
        {
            var name = m.GetCustomAttribute<McpServerToolAttribute>()!.Name ?? m.Name;
            map[name] = m.GetCustomAttribute<McpCapabilityAttribute>()?.Capability
                ?? throw new InvalidOperationException($"Tool method {m.Name} has no McpCapability attribute.");
        }

        return map;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex VariableNameRegex();

    /// <summary>Product identity used in server info.</summary>
    public static string ServerVersion => AppInfo.Version;
}
