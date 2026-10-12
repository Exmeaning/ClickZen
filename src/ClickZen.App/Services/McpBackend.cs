using System.Diagnostics;
using System.Globalization;
using System.Text;
using ClickZen.Core;
using ClickZen.Core.Automation;
using ClickZen.Core.Devices;
using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Core.Persistence;
using ClickZen.Core.Recording;
using ClickZen.Core.Settings;
using ClickZen.Core.Variables;
using ClickZen.Device.Adb;
using ClickZen.Device.Scrcpy;
using ClickZen.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace ClickZen.App.Services;

/// <summary>
/// <see cref="IClickZenBackend"/> on top of the running application. Members of <see cref="DeviceHub"/>,
/// <see cref="AutomationService"/> and <see cref="RecordingService"/> are UI-thread bound, so every access to
/// them is marshalled through the dispatcher; encoding, matching and adb work runs on worker threads.
/// Input to one device is serialized so concurrent AI calls cannot interleave gestures.
/// </summary>
public sealed class McpBackend : IClickZenBackend
{
    private static readonly TimeSpan PictureTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FreshFrameTimeout = TimeSpan.FromMilliseconds(600);

    private readonly DeviceHub _hub;
    private readonly EmulatorService _emulators;
    private readonly AutomationService _automation;
    private readonly RecordingService _recording;
    private readonly VariableStore _globals;
    private readonly IImageMatcher _matcher;
    private readonly SettingsService _settings;
    private readonly AppPaths _paths;
    private readonly ILogger<McpBackend> _log;
    private readonly Dictionary<string, SemaphoreSlim> _inputLocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastInputFrame = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private DispatcherQueue? _ui;
    private CancellationTokenSource? _playCts;
    private McpPlaybackInfo? _playback;

    public McpBackend(DeviceHub hub, EmulatorService emulators, AutomationService automation, RecordingService recording, VariableStore globals,
        IImageMatcher matcher, SettingsService settings, AppPaths paths, ILogger<McpBackend> log)
    {
        _hub = hub;
        _emulators = emulators;
        _automation = automation;
        _recording = recording;
        _globals = globals;
        _matcher = matcher;
        _settings = settings;
        _paths = paths;
        _log = log;
    }

    /// <summary>Call once from the UI thread before the server starts.</summary>
    public void Attach(DispatcherQueue ui) => _ui = ui;

    // =====================================================================================
    //  UI-thread marshalling
    // =====================================================================================

    private Task<T> OnUi<T>(Func<T> func) => OnUi(() => Task.FromResult(func()));

    private Task OnUi(Action action) => OnUi(() =>
    {
        action();
        return Task.FromResult(true);
    });

    private Task<T> OnUi<T>(Func<Task<T>> func)
    {
        var ui = _ui ?? throw new McpToolException("ClickZen is still starting.");
        if (ui.HasThreadAccess)
        {
            return func();
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!ui.TryEnqueue(async () =>
            {
                try
                {
                    tcs.TrySetResult(await func());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            tcs.TrySetException(new McpToolException("ClickZen is shutting down."));
        }

        return tcs.Task;
    }

    // =====================================================================================
    //  devices
    // =====================================================================================

    /// <summary>Resolves a device argument (id, display name, model; empty = current) on the UI thread.</summary>
    private DeviceEntry Resolve(string? device)
    {
        if (string.IsNullOrWhiteSpace(device))
        {
            return _hub.Current ?? _hub.Devices.FirstOrDefault()
                ?? throw new McpToolException("No device is connected. Connect an Android device (USB debugging on) or an emulator first.");
        }

        var key = device.Trim();
        var list = _hub.Devices.ToList();
        var hit = list.FirstOrDefault(d => d.Serial.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(d => d.Info.DisplayName.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(d => $"{d.Info.Brand} {d.Info.Model}".Trim().Equals(key, StringComparison.OrdinalIgnoreCase));
        if (hit is not null)
        {
            return hit;
        }

        var partial = list.Where(d => d.Info.DisplayName.Contains(key, StringComparison.OrdinalIgnoreCase) || d.Serial.Contains(key, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1
            ? partial[0]
            : throw new McpToolException(partial.Count == 0
                ? $"No device \"{key}\". Known devices: {string.Join(", ", list.Select(d => d.Serial))}."
                : $"\"{key}\" matches several devices: {string.Join(", ", partial.Select(d => d.Serial))}.");
    }

    private McpDevice Describe(DeviceEntry e)
    {
        var size = e.ScreenSize;
        string? input = e.IsWindow
            ? e.AdbSerial is null ? null : "window→" + e.AdbSerial
            : _settings.Current.Devices.InputMethod switch
            {
                InputMethodPreference.Root => "root",
                InputMethodPreference.Scrcpy when e.Session is { IsControlAvailable: true } => "scrcpy",
                _ => e.Info.State == DeviceAdbState.Online ? "adb" : null,
            };
        return new McpDevice(
            e.Serial,
            e.Info.DisplayName,
            e.Info.Kind.ToString().ToLowerInvariant(),
            e.IsWindow ? e.SessionState.ToString().ToLowerInvariant() : e.Info.State.ToString().ToLowerInvariant(),
            e.IsCurrent,
            e.IsActive,
            size.Width,
            size.Height,
            e.Info.Density,
            string.IsNullOrEmpty(e.Info.AndroidVersion) ? null : e.Info.AndroidVersion,
            input,
            e.SessionError);
    }

    public Task<McpStatus> GetStatusAsync(CancellationToken ct) => OnUi(() => new McpStatus(
        AppInfo.Version,
        _hub.IsAdbAvailable,
        _hub.Current?.Serial,
        _hub.Devices.Count,
        _automation.DisplayName,
        _automation.IsRunning,
        _recording.IsRecording,
        _recording.IsPlaying || IsAiPlaying,
        IsAiPlaying ? _playback?.Recording : _recording.IsPlaying ? _recording.DisplayName : null));

    public Task<IReadOnlyList<McpDevice>> ListDevicesAsync(CancellationToken ct) =>
        OnUi<IReadOnlyList<McpDevice>>(() => _hub.Devices.Select(Describe).ToList());

    // =====================================================================================
    //  emulators
    // =====================================================================================

    public async Task<McpEmulatorList> ListEmulatorsAsync(CancellationToken ct)
    {
        await _emulators.DetectAsync(ct);
        return await OnUi(() => new McpEmulatorList(
            _emulators.Instances.Select(DescribeEmulator).ToList(),
            _emulators.Installed.Select(i => i.Kind.ToString().ToLowerInvariant()).Distinct().ToList()));
    }

    public async Task<McpEmulatorConnect> ConnectEmulatorAsync(string? emulator, bool makeCurrent, CancellationToken ct)
    {
        var row = await ResolveEmulatorAsync(emulator, ct);
        var r = await _emulators.ConnectAsync(row, makeCurrent, ct);
        return await OnUi(() => new McpEmulatorConnect(r.Success, r.Serial, r.Message, DescribeEmulator(row)));
    }

    public async Task<McpEmulatorBind> BindEmulatorWindowAsync(string? emulator, CancellationToken ct)
    {
        var row = await ResolveEmulatorAsync(emulator, ct);
        var r = await _emulators.BindWindowAsync(row, ct);
        if (r.NeedsReview)
        {
            throw new McpToolException(
                $"Could not locate the Android screen inside the window of {row.Instance.Name} reliably (best guess: {r.Method}). " +
                "Ask the user to bind it on ClickZen's Devices page (\"Bind window\" opens the wizard prefilled), or use connect_emulator and scrcpy mirroring instead.");
        }

        if (!r.Success || r.DeviceId is null)
        {
            throw new McpToolException(r.Message);
        }

        return await OnUi(() =>
        {
            var size = _hub.Devices.FirstOrDefault(d => d.Serial == r.DeviceId)?.ScreenSize ?? r.Profile?.ReferenceSize ?? default;
            return new McpEmulatorBind(r.DeviceId, McpEnumName(r.Method.ToString()), r.Profile?.AdbSerial, size.Width, size.Height, r.Message);
        });
    }

    /// <summary>Detects again and resolves the argument; a clear error lists the running emulators when nothing matches.</summary>
    private async Task<DetectedEmulator> ResolveEmulatorAsync(string? emulator, CancellationToken ct)
    {
        await _emulators.DetectAsync(ct);
        return await OnUi(() =>
        {
            if (_emulators.Find(emulator) is { } row)
            {
                return row;
            }

            var running = _emulators.Instances.Select(i => $"{i.Key} ({i.Instance.Name})").ToList();
            throw new McpToolException(running.Count == 0
                ? "No emulator is running. Start one first (ClickZen does not launch emulators)."
                : string.IsNullOrWhiteSpace(emulator)
                    ? $"Several emulators are running; pass `emulator`: {string.Join(", ", running)}."
                    : $"No running emulator matches \"{emulator}\". Running: {string.Join(", ", running)}.");
        });
    }

    private McpEmulator DescribeEmulator(DetectedEmulator row)
    {
        var i = row.Instance;
        var notes = new List<string>();
        if (!i.HasWindow)
        {
            notes.Add("no window found (headless or not shown yet)");
        }
        else if (i.WindowMinimized)
        {
            notes.Add("window minimized: restore it before bind_emulator_window");
        }

        if (i.AdbEnabled == false)
        {
            notes.Add("ADB is turned off in the emulator's settings; the user must enable it");
        }

        if (i.PortSource == ClickZen.Core.Emulators.AdbPortSource.Formula)
        {
            notes.Add("port inferred from the instance number");
        }

        if (!ClickZen.Core.Emulators.EmulatorCatalog.Get(i.Kind).HardwareVerified)
        {
            notes.Add("detection for this emulator is not verified on real hardware yet");
        }

        return new McpEmulator(
            i.Key,
            i.Kind.ToString().ToLowerInvariant(),
            EmulatorUi.Title(i),
            i.InstanceKey,
            i.Index,
            i.AdbAddress,
            McpEnumName(i.PortSource.ToString()),
            i.AdbEnabled,
            McpEnumName(row.State.ToString()),
            row.ConnectedSerial,
            row.BoundDevice,
            i.HasWindow,
            i.ProcessId,
            notes);
    }

    /// <summary>camelCase like the rest of the MCP JSON ("NotConnected" → "notConnected").</summary>
    private static string McpEnumName(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    public async Task<McpDevice> ConnectAsync(string? device, CancellationToken ct)
    {
        var entry = await OnUi(() => Resolve(device));
        await EnsurePictureAsync(entry, ct);
        return await OnUi(() => Describe(entry));
    }

    public Task<McpDevice> SelectDeviceAsync(string device, CancellationToken ct) => OnUi(() =>
    {
        var entry = Resolve(device);
        _hub.Current = entry;
        return Describe(entry);
    });

    /// <summary>Starts mirroring / window capture when needed and returns the live frame source with a picture.</summary>
    private async Task<ILiveFrameSource> EnsurePictureAsync(DeviceEntry entry, CancellationToken ct)
    {
        var frames = await OnUi(() => entry.Frames);
        if (frames is null)
        {
            await OnUi(async () =>
            {
                if (!entry.IsWindow && entry.Info.State != DeviceAdbState.Online)
                {
                    throw new McpToolException($"{entry.Serial} is {entry.Info.State.ToString().ToLowerInvariant()} (adb). Check the USB debugging authorization on the device.");
                }

                await _hub.ConnectAsync(entry, ct);
                return true;
            });
            frames = await OnUi(() => entry.Frames);
        }

        if (frames is null)
        {
            var error = await OnUi(() => entry.SessionError);
            throw new McpToolException($"Could not start the live picture of {entry.Serial}" + (error is null ? "." : ": " + error));
        }

        if (frames.Latest is null)
        {
            var first = await frames.WaitForFrameAsync(-1, PictureTimeout, ct);
            if (first is null)
            {
                var error = await OnUi(() => entry.SessionError);
                throw new McpToolException($"No picture from {entry.Serial} within {PictureTimeout.TotalSeconds:0} s" + (error is null ? "." : ": " + error));
            }
        }

        return frames;
    }

    /// <summary>A frame captured after the last AI input on this device (waits briefly for one).</summary>
    private async Task<(Frame Frame, SizeI Screen)> FreshFrameAsync(DeviceEntry entry, CancellationToken ct)
    {
        var frames = await EnsurePictureAsync(entry, ct);
        long after;
        lock (_lock)
        {
            after = _lastInputFrame.GetValueOrDefault(entry.Serial, -1);
        }

        var frame = after >= 0
            ? await frames.WaitForFrameAsync(after, FreshFrameTimeout, ct) ?? frames.Latest
            : frames.Latest;
        if (frame is null)
        {
            throw new McpToolException($"No picture from {entry.Serial}.");
        }

        var screen = await OnUi(() => entry.ScreenSize);
        return (frame, screen.IsEmpty ? frame.Size : screen);
    }

    // =====================================================================================
    //  observe
    // =====================================================================================

    public async Task<McpScreenshot> TakeScreenshotAsync(string? device, McpScreenshotRequest request, CancellationToken ct)
    {
        var entry = await OnUi(() => Resolve(device));
        var (frame, screen) = await FreshFrameAsync(entry, ct);
        return await Task.Run(() =>
        {
            var map = new FrameToDevice(frame.Size, screen);
            var region = request.Region is { } r ? r.ClampTo(screen) : new RectI(0, 0, screen.Width, screen.Height);
            if (region.IsEmpty)
            {
                throw new McpToolException($"The region is outside the screen ({screen.Width}x{screen.Height}).");
            }

            var frameRect = request.Region is null ? new RectI(0, 0, frame.Width, frame.Height) : map.DeviceToFrameRect(region);
            if (frameRect.IsEmpty)
            {
                throw new McpToolException("The region is too small.");
            }

            // maxSize bounds the longest edge of the returned image; the encoder never upscales.
            var jpeg = request.Format != "png";
            var (data, size) = Vision.ImageCodec.Encode(frame, frameRect, Math.Max(0, request.MaxSize), jpeg ? request.Quality : 0);
            return new McpScreenshot(data, jpeg ? "image/jpeg" : "image/png", size.Width, size.Height, region, screen.Width, screen.Height);
        }, ct);
    }

    public async Task<McpPixel> GetPixelAsync(string? device, PointI point, CancellationToken ct)
    {
        var entry = await OnUi(() => Resolve(device));
        var (frame, screen) = await FreshFrameAsync(entry, ct);
        var p = new FrameToDevice(frame.Size, screen).DeviceToFramePoint(point);
        var fx = Math.Clamp((int)p.X, 0, frame.Width - 1);
        var fy = Math.Clamp((int)p.Y, 0, frame.Height - 1);
        var (r, g, b) = frame.PixelAt(fx, fy);
        return new McpPixel(point.X, point.Y, ConditionProbe.FormatColor(r, g, b), r, g, b);
    }

    public async Task<McpImageMatch> FindImageAsync(string? device, McpImageQuery query, CancellationToken ct)
    {
        var entry = await OnUi(() => Resolve(device));
        TemplateAsset template;
        SizeI refSize;
        string? name = null;
        if (query.Png is { } png)
        {
            var size = Vision.ImageCodec.MeasurePng(png);
            if (size.IsEmpty)
            {
                throw new McpToolException("imageBase64 could not be decoded.");
            }

            // The template is matched in device pixels (refSize = screen): bring a crop of a scaled screenshot back to device size.
            if (Math.Abs(query.ImageScale - 1) > 0.001)
            {
                size = new SizeI(Math.Max(1, (int)Math.Round(size.Width * query.ImageScale)), Math.Max(1, (int)Math.Round(size.Height * query.ImageScale)));
                png = await Task.Run(() => TemplateImaging.ResizePng(png, size), ct);
            }

            template = new TemplateAsset { Id = "mcp-" + Guid.NewGuid().ToString("N"), Name = "image", Png = png, Size = size };
            refSize = default;
        }
        else
        {
            var key = query.TemplateName!.Trim();
            (template, refSize) = await OnUi(() =>
            {
                var s = _automation.Scheme;
                var t = s.Templates.FirstOrDefault(t => t.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                    ?? s.Templates.FirstOrDefault(t => t.Id == key)
                    ?? throw new McpToolException($"The open scheme has no template \"{key}\". Templates: {string.Join(", ", s.Templates.Select(t => t.Name))}.");
                return (t, s.RefSize);
            });
            name = template.Name;
        }

        var (frame, screen) = await FreshFrameAsync(entry, ct);
        // Scheme templates are sized for the scheme's reference in the screen's orientation (either orientation works).
        var effectiveRef = AutomationCoords.EffectiveRef(refSize, screen);
        var condition = new ImageCondition
        {
            TemplateId = template.Id,
            Threshold = query.Threshold,
            Grayscale = query.Grayscale,
            MultiScale = query.MultiScale,
            Area = query.Area is { } a ? AutomationCoords.DeviceToRef(a.ClampTo(screen), screen, effectiveRef) : default,
        };
        var r = await Task.Run(() =>
        {
            if (query.Png is null)
            {
                return ConditionProbe.ProbeImage(frame, screen, effectiveRef, template, condition, _matcher);
            }

            // Ad-hoc images get a private matcher: clearing the shared cache would dispose templates a running engine is using.
            using var adHoc = new Vision.OpenCvImageMatcher();
            return ConditionProbe.ProbeImage(frame, screen, effectiveRef, template, condition, adHoc);
        }, ct);

        if (r.Problem == "area")
        {
            throw new McpToolException("The search area is outside the screen.");
        }

        RectI? location = r.RefLocation is { } loc ? AutomationCoords.RefToDevice(loc, effectiveRef, screen) : null;
        return new McpImageMatch(r.Found, double.IsNaN(r.Score) ? 0 : Math.Round(r.Score, 4), query.Threshold, location, r.DeviceCenter, name);
    }

    public async Task<McpUiDump> DumpUiAsync(string? device, CancellationToken ct)
    {
        var (serial, scale) = await OnUi(() =>
        {
            var entry = Resolve(device);
            var adbSerial = entry.AdbSerial ?? throw new McpToolException($"{entry.Info.DisplayName} has no linked adb device, so its UI tree cannot be read. Use take_screenshot.");
            var scaleX = 1.0;
            var scaleY = 1.0;
            if (entry.IsWindow)
            {
                var linked = _hub.Devices.FirstOrDefault(d => !d.IsWindow && d.Serial.Equals(adbSerial, StringComparison.OrdinalIgnoreCase));
                var from = linked?.ScreenSize ?? default;
                var to = entry.ScreenSize;
                if (!from.IsEmpty && !to.IsEmpty)
                {
                    scaleX = (double)to.Width / from.Width;
                    scaleY = (double)to.Height / from.Height;
                }
            }

            return (adbSerial, (scaleX, scaleY));
        });

        const string remote = "/data/local/tmp/clickzen-ui.xml";
        var output = await _hub.Adb.ShellAsync(serial, $"uiautomator dump {remote} >/dev/null 2>&1; cat {remote}; rm -f {remote}", TimeSpan.FromSeconds(20), ct);
        if (!output.Contains("<hierarchy", StringComparison.Ordinal))
        {
            // Some ROMs only support dumping to a tty.
            output = await _hub.Adb.ShellAsync(serial, "uiautomator dump /dev/tty", TimeSpan.FromSeconds(20), ct);
        }

        return new McpUiDump(output, scale.scaleX, scale.scaleY);
    }

    public Task<SizeI> GetScreenSizeAsync(string? device, CancellationToken ct) => OnUi(() =>
    {
        var entry = Resolve(device);
        var size = entry.ScreenSize;
        return size.IsEmpty && entry.Frames?.Latest is { } f ? f.Size : size;
    });

    public async Task<IReadOnlyList<McpApp>> ListAppsAsync(string? device, bool includeSystem, CancellationToken ct)
    {
        var serial = await AdbSerialAsync(device);
        var third = await _hub.Adb.ShellAsync(serial, "pm list packages -3", ct);
        var apps = ParsePackages(third).Select(p => new McpApp(p, false)).ToList();
        if (includeSystem)
        {
            var all = await _hub.Adb.ShellAsync(serial, "pm list packages", ct);
            var known = apps.Select(a => a.Package).ToHashSet(StringComparer.Ordinal);
            apps.AddRange(ParsePackages(all).Where(p => !known.Contains(p)).Select(p => new McpApp(p, true)));
        }

        return apps;
    }

    private static IEnumerable<string> ParsePackages(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("package:", StringComparison.Ordinal))
            .Select(l => l["package:".Length..]);

    // =====================================================================================
    //  input
    // =====================================================================================

    private SemaphoreSlim InputLock(string serial)
    {
        lock (_lock)
        {
            if (!_inputLocks.TryGetValue(serial, out var s))
            {
                s = new SemaphoreSlim(1, 1);
                _inputLocks[serial] = s;
            }

            return s;
        }
    }

    /// <summary>Runs one input operation with the device's injector, serialized per device.</summary>
    private async Task WithInputAsync(string? device, Func<ITouchInjector, SizeI, Task> action, CancellationToken ct)
    {
        var entry = await OnUi(() => Resolve(device));
        var gate = InputLock(entry.Serial);
        await gate.WaitAsync(ct);
        try
        {
            var injector = await OnUi(() => _hub.GetInjectorAsync(entry, ct));
            if (injector is UnavailableTouchInjector u)
            {
                throw new McpToolException(u.Message);
            }

            var screen = await OnUi(() => entry.ScreenSize);
            await action(injector, screen);
            var seq = await OnUi(() => entry.Frames?.Latest?.Sequence ?? -1);
            lock (_lock)
            {
                _lastInputFrame[entry.Serial] = seq;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public Task TapAsync(string? device, PointI point, int durationMs, CancellationToken ct) =>
        WithInputAsync(device, (i, _) => i.TapAsync(point, Math.Max(durationMs, 40), ct), ct);

    public Task SwipeAsync(string? device, IReadOnlyList<PointI> points, int durationMs, CancellationToken ct) =>
        WithInputAsync(device, (i, _) =>
        {
            var path = new List<TimedPoint>();
            var segments = Math.Max(1, points.Count - 1);
            for (var s = 0; s < segments; s++)
            {
                var from = points[s];
                var to = points[Math.Min(s + 1, points.Count - 1)];
                var steps = Math.Clamp(durationMs / segments / 16, 2, 60);
                for (var k = s == 0 ? 0 : 1; k <= steps; k++)
                {
                    var t = (double)k / steps;
                    var eased = Easing.EaseInOutSine(t);
                    path.Add(new TimedPoint(from.X + (to.X - from.X) * eased, from.Y + (to.Y - from.Y) * eased,
                        (int)Math.Round((s + t) * durationMs / segments)));
                }
            }

            return i.StrokeAsync(path, ct);
        }, ct);

    public Task KeyAsync(string? device, int keyCode, CancellationToken ct) =>
        WithInputAsync(device, (i, _) => i.KeyAsync(keyCode, ct), ct);

    public async Task TextAsync(string? device, string text, CancellationToken ct)
    {
        var ascii = text.All(c => c is >= ' ' and <= '~');
        if (ascii)
        {
            await WithInputAsync(device, (i, _) => i.TextAsync(text, ct), ct);
            return;
        }

        // Non-ASCII: paste through the clipboard of the scrcpy session (adb "input text" cannot type it).
        // Window devices paste through their linked adb device's session.
        var entry = await OnUi(() => Resolve(device));
        var target = await OnUi(() => entry.IsWindow
            ? entry.AdbSerial is { } s ? _hub.Devices.FirstOrDefault(d => !d.IsWindow && d.Serial.Equals(s, StringComparison.OrdinalIgnoreCase)) : null
            : entry);
        if (target is null)
        {
            throw new McpToolException($"{entry.Info.DisplayName} has no linked adb device, so non-ASCII text cannot be typed. Link one on the Devices page.");
        }

        var session = await OnUi(() => target.Session);
        if (session is not { IsControlAvailable: true })
        {
            await EnsurePictureAsync(target, ct);
            session = await OnUi(() => target.Session);
        }

        if (session is not { IsControlAvailable: true })
        {
            throw new McpToolException("Typing non-ASCII text needs scrcpy mirroring of an adb device. Start mirroring (connect_device) and retry.");
        }

        var gate = InputLock(entry.Serial);
        await gate.WaitAsync(ct);
        try
        {
            await session.Injector.SetClipboardAsync(text, paste: true, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string> LaunchAppAsync(string? device, string package, CancellationToken ct)
    {
        var serial = await AdbSerialAsync(device);
        var output = await _hub.Adb.ShellAsync(serial, $"monkey -p {ShellQuoting.Quote(package)} -c android.intent.category.LAUNCHER 1", TimeSpan.FromSeconds(20), ct);
        if (output.Contains("No activities found", StringComparison.OrdinalIgnoreCase) || output.Contains("monkey aborted", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpToolException($"{package} has no launcher activity or is not installed. Use list_apps to check the package name.");
        }

        return "";
    }

    public async Task StopAppAsync(string? device, string package, CancellationToken ct)
    {
        var serial = await AdbSerialAsync(device);
        await _hub.Adb.ShellAsync(serial, "am force-stop " + ShellQuoting.Quote(package), ct);
    }

    private Task<string> AdbSerialAsync(string? device) => OnUi(() =>
    {
        var entry = Resolve(device);
        return entry.AdbSerial ?? throw new McpToolException($"{entry.Info.DisplayName} has no linked adb device (bind one on the Devices page).");
    });

    public async Task<McpShellResult> ShellAsync(string? device, string command, bool asRoot, TimeSpan timeout, CancellationToken ct)
    {
        var serial = await AdbSerialAsync(device);
        var sw = Stopwatch.StartNew();
        string output;
        try
        {
            output = asRoot
                ? await _hub.Adb.RootShellAsync(serial, command, timeout, ct)
                : await _hub.Adb.ShellAsync(serial, command, timeout, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpToolException($"The command did not finish within {timeout.TotalSeconds:0} s.");
        }

        var truncated = output.Length > ClickZenTools.MaxShellOutputChars;
        _log.LogInformation("MCP adb_shell on {Serial} (root {Root}): {Command}", serial, asRoot, command.Length > 200 ? command[..200] : command);
        return new McpShellResult(truncated ? output[..ClickZenTools.MaxShellOutputChars] : output, truncated, sw.ElapsedMilliseconds);
    }

    // =====================================================================================
    //  automation
    // =====================================================================================

    private McpSchemeInfo DescribeScheme()
    {
        var s = _automation.Scheme;
        return new McpSchemeInfo(
            _automation.DisplayName,
            _automation.FilePath,
            _automation.IsDirty,
            s.RefSize.Width,
            s.RefSize.Height,
            s.Settings.CheckIntervalMs,
            _automation.IsRunning,
            _automation.LastFired,
            s.Tasks.Select(t => new McpTaskInfo(t.Id, t.Name, t.Enabled, t.Rules.Count, t.CooldownMs, t.Priority)).ToList(),
            s.Templates.Select(t => string.IsNullOrWhiteSpace(t.Name) ? t.Id : t.Name).ToList(),
            s.Variables.Select(v => new McpVariableDeclaration(v.Name, v.Type.ToString().ToLowerInvariant(), v.Scope.ToString().ToLowerInvariant(),
                v.Sync.ToString().ToLowerInvariant(), v.Initial.AsString, string.IsNullOrWhiteSpace(v.Description) ? null : v.Description)).ToList(),
            _automation.Recordings.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
            _automation.Runs.Select(r => new McpRunInfo(r.Label, r.State.ToString().ToLowerInvariant(), r.Rounds, r.Error)).ToList(),
            _automation.Validate().Select(i => i.Message).ToList());
    }

    public Task<McpSchemeInfo> GetSchemeAsync(CancellationToken ct) => OnUi(() =>
    {
        _automation.PollRounds();
        return DescribeScheme();
    });

    public Task<IReadOnlyList<McpFileInfo>> ListSchemesAsync(CancellationToken ct) =>
        Task.FromResult(ListFiles(_paths.SchemesDirectory, "*" + SchemePackage.FileExtension));

    public Task<IReadOnlyList<McpFileInfo>> ListRecordingsAsync(CancellationToken ct) =>
        Task.FromResult(ListFiles(_paths.RecordingsDirectory, "*" + RecordingDocument.FileExtension));

    private static IReadOnlyList<McpFileInfo> ListFiles(string dir, string pattern)
    {
        if (!Directory.Exists(dir))
        {
            return [];
        }

        return new DirectoryInfo(dir).EnumerateFiles(pattern, SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(500)
            .Select(f => new McpFileInfo(Path.GetFileNameWithoutExtension(f.Name), f.FullName, f.Length, f.LastWriteTime))
            .ToList();
    }

    private static string ResolveFile(string arg, string dir, string extension)
    {
        var candidates = new List<string>();
        if (Path.IsPathRooted(arg))
        {
            candidates.Add(arg);
            candidates.Add(arg + extension);
        }
        else
        {
            candidates.Add(Path.Combine(dir, arg));
            candidates.Add(Path.Combine(dir, arg + extension));
        }

        var hit = candidates.FirstOrDefault(File.Exists);
        if (hit is not null)
        {
            return Path.GetFullPath(hit);
        }

        if (Directory.Exists(dir))
        {
            var byName = Directory.EnumerateFiles(dir, "*" + extension, SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(Path.GetFileNameWithoutExtension(arg), StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName;
            }
        }

        throw new McpToolException($"File not found: {arg}");
    }

    public async Task<McpSchemeInfo> OpenSchemeAsync(string path, bool discardChanges, CancellationToken ct)
    {
        var file = ResolveFile(path, _paths.SchemesDirectory, SchemePackage.FileExtension);
        return await OnUi(async () =>
        {
            if (_automation.IsRunning)
            {
                if (!discardChanges)
                {
                    throw new McpToolException("Automation is running. Stop it first (stop_automation) or pass discardChanges=true.");
                }

                await _automation.StopAsync();
            }

            if (_automation.IsDirty && !discardChanges)
            {
                throw new McpToolException($"The open scheme \"{_automation.DisplayName}\" has unsaved changes. Ask the user to save it, or pass discardChanges=true.");
            }

            try
            {
                _automation.Open(file);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                throw new McpToolException($"Could not open {file}: {ex.Message}", ex);
            }

            return DescribeScheme();
        });
    }

    public async Task<McpStartResult> StartAutomationAsync(IReadOnlyList<string> devices, CancellationToken ct)
    {
        var targets = await OnUi(() =>
        {
            if (_automation.IsRunning)
            {
                throw new McpToolException("Automation is already running. Call stop_automation first.");
            }

            if (_automation.Scheme.Tasks.Count == 0)
            {
                throw new McpToolException("The open scheme has no tasks. Open a scheme first (list_schemes / open_scheme).");
            }

            return devices.Count == 0 ? _automation.ResolveTargets().ToList() : devices.Select(Resolve).Distinct().ToList();
        });

        if (targets.Count == 0)
        {
            throw new McpToolException("No target device.");
        }

        foreach (var t in targets)
        {
            await EnsurePictureAsync(t, ct);
        }

        return await OnUi(async () =>
        {
            var problems = await _automation.StartAsync(targets);
            var started = _automation.Runs.Where(r => r.Engine is not null).Select(r => r.Label).ToList();
            return new McpStartResult(started, problems);
        });
    }

    public Task StopAutomationAsync(CancellationToken ct) => OnUi(async () =>
    {
        await _automation.StopAsync();
        return true;
    });

    public Task<IReadOnlyList<McpLogEntry>> GetAutomationLogAsync(int max, CancellationToken ct) => OnUi<IReadOnlyList<McpLogEntry>>(() =>
        _automation.Log.Skip(Math.Max(0, _automation.Log.Count - max))
            .Select(l => new McpLogEntry(l.Time, l.Level.ToString(), l.Device, l.Message))
            .ToList());

    public Task<McpTaskInfo> SetTaskEnabledAsync(string task, bool enabled, CancellationToken ct) => OnUi(() =>
    {
        var key = task.Trim();
        var item = _automation.FindTask(key)
            ?? _automation.Tasks.FirstOrDefault(t => t.Task.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpToolException($"No task \"{key}\". Tasks: {string.Join(", ", _automation.Tasks.Select(t => t.Task.Name))}.");
        item.IsEnabled = enabled;
        var t = item.Task;
        return new McpTaskInfo(t.Id, t.Name, t.Enabled, t.Rules.Count, t.CooldownMs, t.Priority);
    });

    // =====================================================================================
    //  recordings
    // =====================================================================================

    private bool IsAiPlaying
    {
        get
        {
            lock (_lock)
            {
                return _playCts is not null;
            }
        }
    }

    public async Task<McpPlaybackInfo> PlayRecordingAsync(McpPlaybackRequest request, bool wait, CancellationToken ct)
    {
        var (doc, name) = await OnUi(() =>
        {
            if (_automation.Recordings.TryGetValue(request.Source, out var embedded))
            {
                return (embedded, request.Source);
            }

            return ((RecordingDocument?)null, (string?)null);
        });
        if (doc is null)
        {
            var file = ResolveFile(request.Source, _paths.RecordingsDirectory, RecordingDocument.FileExtension);
            try
            {
                doc = RecordingDocument.Load(file);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                throw new McpToolException($"Could not load {file}: {ex.Message}", ex);
            }

            name = Path.GetFileNameWithoutExtension(file);
        }

        if (doc.Gestures.Count == 0)
        {
            throw new McpToolException("The recording is empty.");
        }

        var entry = await OnUi(() =>
        {
            if (_recording.IsPlaying || _recording.IsRecording)
            {
                throw new McpToolException("The Recording page is recording or playing. Stop it first.");
            }

            return Resolve(request.Device);
        });

        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_playCts is not null)
            {
                throw new McpToolException($"A recording is already playing ({_playback?.Recording}). Call stop_playback first.");
            }

            cts = new CancellationTokenSource();
            _playCts = cts;
        }

        var info = new McpPlaybackInfo(name!, entry.Serial, doc.Gestures.Count, doc.DurationMs, "playing", null);
        _playback = info;
        var run = RunPlaybackAsync(entry, doc, request, info, cts);
        if (!wait)
        {
            return info;
        }

        using var reg = ct.Register(() => cts.Cancel());
        return await run;
    }

    private async Task<McpPlaybackInfo> RunPlaybackAsync(DeviceEntry entry, RecordingDocument doc, McpPlaybackRequest request, McpPlaybackInfo info, CancellationTokenSource cts)
    {
        try
        {
            var (injector, screen, dp) = await OnUi(async () =>
            {
                var i = await _hub.GetInjectorAsync(entry, cts.Token);
                return (i, entry.ScreenSize, entry.Info.DpScale);
            });
            if (injector is UnavailableTouchInjector u)
            {
                throw new McpToolException(u.Message);
            }

            var a = _settings.Current.Automation;
            var options = new PlaybackOptions
            {
                Speed = request.Speed,
                Loops = request.Loops,
                Humanize = request.Randomize
                    ? new HumanizeOptions { PositionJitterDp = a.DefaultPositionJitterDp, DelayJitterPercent = a.DefaultDelayJitterPercent, DurationJitterPercent = a.DefaultDurationJitterPercent }
                    : HumanizeOptions.None,
            };
            _log.LogInformation("MCP playback of {Recording} on {Serial} via {Injector}", info.Recording, entry.Serial, injector.Name);
            await Task.Run(() => new RecordingPlayer(injector).PlayAsync(doc, options, screen, dp, cts.Token), cts.Token);
            return _playback = info with { State = "completed" };
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return _playback = info with { State = "stopped" };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MCP playback failed");
            _playback = info with { State = "failed", Error = ex.Message };
            throw;
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_playCts, cts))
                {
                    _playCts = null;
                }
            }

            cts.Dispose();
        }
    }

    public async Task StopPlaybackAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            try
            {
                _playCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }
        }

        await OnUi(() => _recording.StopPlayback());
    }

    // =====================================================================================
    //  variables
    // =====================================================================================

    public Task<McpVariableSet> GetVariablesAsync(CancellationToken ct) => OnUi(() =>
    {
        var global = _globals.Snapshot().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var devices = new Dictionary<string, IReadOnlyDictionary<string, VariableValue>>(StringComparer.Ordinal);
        foreach (var (device, engine) in _automation.RunningEngines)
        {
            devices[device.Serial] = engine.Variables.Snapshot().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }

        return new McpVariableSet(global, devices);
    });

    public Task<VariableValue> SetVariableAsync(string name, VariableValue value, McpVariableTarget target, string? device, CancellationToken ct) => OnUi(() =>
    {
        VariableStore store;
        if (target == McpVariableTarget.Global)
        {
            store = _globals;
        }
        else
        {
            var entry = Resolve(device);
            store = _automation.RunningEngines.FirstOrDefault(r => r.Device == entry).Engine?.Variables
                ?? throw new McpToolException($"No automation is running on {entry.Serial}; omit device to set a global variable.");
        }

        store.Set(name, value, VariableChangeSource.User, "mcp");
        return store.Get(name);
    });
}
