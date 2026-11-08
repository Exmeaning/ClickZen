using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ClickZen.Core.Emulators;

/// <summary>
/// Parsers for the files emulators keep their per-instance settings in. Pure functions over file contents:
/// they never throw for malformed input and return null / defaults instead.
/// </summary>
public static partial class EmulatorConfigParsers
{
    private const int AdbGuestPort = 5555;

    // ------------------------------------------------------------------ MuMu 12

    /// <summary>
    /// MuMu 12 <c>vms\&lt;vm&gt;\configs\vm_config.json</c>: <c>vm.nat.port_forward.adb.host_port</c> (a string or number).
    /// </summary>
    public static int? MuMuVmConfigAdbPort(string? json)
    {
        var root = ParseJson(json);
        if (root is not { } r)
        {
            return null;
        }

        using (r)
        {
            return TryGetPath(r.RootElement, out var port, "vm", "nat", "port_forward", "adb", "host_port") ? AsPort(port) : null;
        }
    }

    /// <summary>MuMu 12 <c>vms\&lt;vm&gt;\configs\extra_config.json</c>: <c>playerName</c> (the instance's display name).</summary>
    public static string? MuMuPlayerName(string? json)
    {
        var root = ParseJson(json);
        if (root is not { } r)
        {
            return null;
        }

        using (r)
        {
            return TryGetPath(r.RootElement, out var name, "playerName") && name.ValueKind == JsonValueKind.String
                ? NullIfBlank(name.GetString())
                : null;
        }
    }

    /// <summary>
    /// MuMu 12 <c>vms\&lt;vm&gt;\configs\customer_config.json</c>: <c>customer.network_bridge_opened</c>. With bridged
    /// networking the instance is reached at its LAN address rather than the forwarded localhost port.
    /// </summary>
    public static bool MuMuBridgeEnabled(string? json)
    {
        var root = ParseJson(json);
        if (root is not { } r)
        {
            return false;
        }

        using (r)
        {
            return TryGetPath(r.RootElement, out var v, "customer", "network_bridge_opened")
                && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() is "true" or "1"));
        }
    }

    /// <summary>Instance number from a MuMu VM directory name such as "MuMuPlayer-12.0-3", "MuMuPlayerGlobal-12.0-0", "YXArkNights-12.0-1".</summary>
    public static int? MuMuVmIndex(string? vmName)
    {
        var m = MuMuVmRegex().Match(vmName ?? "");
        return m.Success && int.TryParse(m.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : null;
    }

    /// <summary>True for MuMu 12 VM directory names (see <see cref="MuMuVmIndex"/>).</summary>
    public static bool IsMuMuVmName(string? vmName) => MuMuVmIndex(vmName) is not null;

    /// <summary>The first MuMu 12 VM name anywhere in <paramref name="text"/> (e.g. a player's command line), or null.</summary>
    public static string? FindMuMuVmName(string? text)
    {
        var m = MuMuVmSearchRegex().Match(text ?? "");
        return m.Success ? m.Value : null;
    }

    /// <summary>
    /// Instance number in a MuMu player command line: <c>-v 1</c>, <c>--vmindex 1</c>, <c>--index 1</c> (as passed to
    /// <c>MuMuNxDevice.exe</c> / <c>MuMuPlayer.exe</c> in MuMu 4.x–6.x), or the suffix of a VM directory name.
    /// </summary>
    public static int? MuMuCommandLineIndex(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        if (MuMuVmIndex(FindMuMuVmName(commandLine)) is int vmIdx)
        {
            return vmIdx;
        }

        var args = CommandLine.Split(commandLine);
        foreach (var opt in new[] { "-v", "--vmindex", "--index", "-m" })
        {
            var raw = CommandLine.Option(args, opt);
            if (raw is not null)
            {
                if (int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var idx))
                {
                    return idx;
                }

                if (MuMuVmIndex(raw) is int namedIdx)
                {
                    return namedIdx;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Inverse of MuMu 12/5/6's port formula (<c>16384 + (index % 32) × 32 + (index / 32) × 4</c>, plus up to +3 conflict
    /// offset; legacy MuMu 6 single-instance port 7555 → 0). Mirrors MAA's <c>MuMuPortToIndex</c>.
    /// </summary>
    public static int? MuMuPortToIndex(int port)
    {
        if (port == 7555)
        {
            return 0;
        }

        if (port is < 16384 or >= 17408)
        {
            return null;
        }

        var k = (port - 16384) / 4;
        var index = ((k & 7) << 5) | (k >> 3);
        return index is >= 0 and < 256 ? index : null;
    }

    /// <summary>
    /// Parses <c>MuMuManager.exe info --vmindex all</c> (and optional <c>MuMuManager.exe adb --vmindex all</c> on MuMu v6
    /// where <c>info</c> omits <c>adb_port</c>, matching MaaFramework's <c>AdbDeviceWin32Finder</c>).
    /// </summary>
    public static IReadOnlyList<MuMuManagerInstanceInfo> ParseMuMuManagerInfo(string? infoJson, string? adbJson = null)
    {
        using var infoDoc = ParseJsonAfterBanner(infoJson);
        if (infoDoc is null || infoDoc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var adbPorts = ParseMuMuAdbPorts(adbJson);
        var root = infoDoc.RootElement;
        var list = new List<MuMuManagerInstanceInfo>();

        if (IsSingleMuMuVmObject(root))
        {
            if (ParseSingleMuMuVm(null, root, adbPorts) is { } single)
            {
                list.Add(single);
            }

            return list;
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object && ParseSingleMuMuVm(prop.Name, prop.Value, adbPorts) is { } item)
            {
                list.Add(item);
            }
        }

        return list.OrderBy(i => i.Index).ToList();
    }

    private static bool IsSingleMuMuVmObject(JsonElement obj) =>
        obj.TryGetProperty("index", out _)
        || obj.TryGetProperty("is_process_started", out _)
        || obj.TryGetProperty("is_android_started", out _)
        || obj.TryGetProperty("player_state", out _);

    private static MuMuManagerInstanceInfo? ParseSingleMuMuVm(string? key, JsonElement obj, Dictionary<int, int> adbPorts)
    {
        int? index = null;
        if (obj.TryGetProperty("index", out var idxEl))
        {
            index = AsNonNegativeInt(idxEl);
        }

        if (index is null && key is not null && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var keyIdx))
        {
            index = keyIdx;
        }

        if (index is not int vmIndex)
        {
            return null;
        }

        var name = obj.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
            ? NullIfBlank(nEl.GetString())
            : null;
        var processStarted = TryGetBool(obj, "is_process_started");
        var androidStarted = TryGetBool(obj, "is_android_started");
        var state = obj.TryGetProperty("player_state", out var stEl) && stEl.ValueKind == JsonValueKind.String
            ? stEl.GetString()
            : null;
        var pid = obj.TryGetProperty("pid", out var pidEl) ? AsNonNegativeInt(pidEl) ?? 0 : 0;
        var headlessPid = obj.TryGetProperty("headless_pid", out var hPidEl) ? AsNonNegativeInt(hPidEl) ?? 0 : 0;
        var running = processStarted == true
            || androidStarted == true
            || string.Equals(state, "start_finished", StringComparison.OrdinalIgnoreCase)
            || pid > 0;

        int? port = obj.TryGetProperty("adb_port", out var pEl) ? AsPort(pEl) : null;
        if (port is null && adbPorts.TryGetValue(vmIndex, out var fallbackPort))
        {
            port = fallbackPort;
        }

        if (port is null && adbPorts.TryGetValue(-1, out var singleAdbPort) && vmIndex == 0)
        {
            port = singleAdbPort;
        }

        var mainWnd = obj.TryGetProperty("main_wnd", out var mwEl) ? AsHwnd(mwEl) : 0;
        var renderWnd = obj.TryGetProperty("render_wnd", out var rwEl) ? AsHwnd(rwEl) : 0;

        return new MuMuManagerInstanceInfo(vmIndex, name, running, port, mainWnd, renderWnd, pid, headlessPid);
    }

    private static Dictionary<int, int> ParseMuMuAdbPorts(string? adbJson)
    {
        var map = new Dictionary<int, int>();
        using var doc = ParseJsonAfterBanner(adbJson);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return map;
        }

        var root = doc.RootElement;
        if (root.TryGetProperty("adb_port", out var direct) && AsPort(direct) is int singlePort)
        {
            map[-1] = singlePort;
            if (MuMuPortToIndex(singlePort) is int derivedIdx)
            {
                map[derivedIdx] = singlePort;
            }

            return map;
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (int.TryParse(prop.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var idx)
                && prop.Value.ValueKind == JsonValueKind.Object
                && prop.Value.TryGetProperty("adb_port", out var pEl)
                && AsPort(pEl) is int p)
            {
                map[idx] = p;
            }
        }

        return map;
    }

    private static JsonDocument? ParseJsonAfterBanner(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var brace = text.IndexOf('{', StringComparison.Ordinal);
        return brace < 0 ? null : ParseJson(text[brace..]);
    }

    private static bool? TryGetBool(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when v.TryGetInt32(out var n) => n != 0,
            JsonValueKind.String => v.GetString()?.Trim() switch
            {
                "true" or "1" => true,
                "false" or "0" => false,
                _ => null,
            },
            _ => null,
        };
    }

    private static int? AsNonNegativeInt(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) && n >= 0)
        {
            return n;
        }

        return e.ValueKind == JsonValueKind.String
            && int.TryParse(e.GetString()?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var s)
            && s >= 0
                ? s
                : null;
    }

    private static nint AsHwnd(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var num) && num > 0)
        {
            return (nint)num;
        }

        if (e.ValueKind != JsonValueKind.String)
        {
            return 0;
        }

        var s = e.GetString()?.Trim();
        if (string.IsNullOrEmpty(s) || s == "0")
        {
            return 0;
        }

        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }

        return long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) && hex > 0 ? (nint)hex : 0;
    }

    [GeneratedRegex(@"^(?:MuMuPlayer(?:Global)?|YXArkNights)-\d+(?:\.\d+)?-(\d+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MuMuVmRegex();

    [GeneratedRegex(@"(?<![\w-])(?:MuMuPlayer(?:Global)?|YXArkNights)-\d+(?:\.\d+)?-\d+(?![\w.])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MuMuVmSearchRegex();

    // ------------------------------------------------------------------ VirtualBox XML (Nox .vbox, MEmu .memu, old MuMu .nemu)

    /// <summary>
    /// The host port forwarded to the guest's adb port (5555) in a VirtualBox machine file:
    /// <c>&lt;Forwarding … hostport="62026" guestport="5555"/&gt;</c>. NoxPlayer's .vbox, MEmu's .memu and
    /// old MuMu's .nemu files use this format.
    /// </summary>
    public static int? VBoxAdbHostPort(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var doc = XDocument.Parse(xml, LoadOptions.None);
            foreach (var e in doc.Descendants().Where(e => e.Name.LocalName == "Forwarding"))
            {
                var guest = Attr(e, "guestport");
                var host = Attr(e, "hostport");
                if (int.TryParse(guest, NumberStyles.None, CultureInfo.InvariantCulture, out var g) && g == AdbGuestPort
                    && int.TryParse(host, NumberStyles.None, CultureInfo.InvariantCulture, out var h) && h is > 0 and < 65536)
                {
                    return h;
                }
            }
        }
        catch (XmlException)
        {
            // Truncated or otherwise broken files: fall back to the regex below.
        }

        // Last resort for files the XML parser rejects: look at the attributes directly.
        foreach (Match m in ForwardingRegex().Matches(xml))
        {
            var attrs = m.Value;
            if (GuestPortRegex().Match(attrs) is { Success: true } g && g.Groups[1].Value == "5555"
                && HostPortRegex().Match(attrs) is { Success: true } h
                && int.TryParse(h.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536)
            {
                return port;
            }
        }

        return null;
    }

    private static string? Attr(XElement e, string name) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    [GeneratedRegex(@"<Forwarding\b[^>]*>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ForwardingRegex();

    [GeneratedRegex(@"\bguestport\s*=\s*""(\d+)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GuestPortRegex();

    [GeneratedRegex(@"\bhostport\s*=\s*""(\d+)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HostPortRegex();

    // ------------------------------------------------------------------ BlueStacks 5

    /// <summary>Settings of one instance in <c>bluestacks.conf</c>.</summary>
    public sealed record BlueStacksInstanceConfig(string Instance, int? AdbPort, string? DisplayName);

    /// <summary>
    /// Parses <c>bluestacks.conf</c> (lines of <c>key="value"</c>). Returns the global ADB switch
    /// (<c>bst.enable_adb_access</c>, null when absent) and every instance found under <c>bst.instance.&lt;name&gt;.</c>.
    /// The port is <c>bst.instance.&lt;name&gt;.status.adb_port</c>, falling back to <c>….adb_port</c>.
    /// </summary>
    public static (bool? AdbEnabled, IReadOnlyList<BlueStacksInstanceConfig> Instances) BlueStacksConf(string? text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (line.Length == 0 || line[0] == '#' || eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            values[key] = value;
        }

        bool? enabled = values.TryGetValue("bst.enable_adb_access", out var e) ? e.Trim() == "1" : null;

        const string prefix = "bst.instance.";
        var names = values.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..])
            .Select(rest => rest.IndexOf('.', StringComparison.Ordinal) is var dot and > 0 ? rest[..dot] : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var instances = names.Select(n =>
        {
            int? port = null;
            foreach (var key in new[] { $"{prefix}{n}.status.adb_port", $"{prefix}{n}.adb_port" })
            {
                if (values.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536)
                {
                    port = p;
                    break;
                }
            }

            return new BlueStacksInstanceConfig(n, port, values.TryGetValue($"{prefix}{n}.display_name", out var d) ? NullIfBlank(d) : null);
        }).ToList();

        return (enabled, instances);
    }

    // ------------------------------------------------------------------ Android Studio emulator

    /// <summary>Contents of the AVD discovery file <c>%TEMP%\avd\running\pid_&lt;pid&gt;.ini</c>.</summary>
    public sealed record AvdRunningInfo(string? AvdName, int? AdbPort, int? ConsolePort);

    /// <summary>
    /// Parses <c>pid_&lt;pid&gt;.ini</c>, written by every running Android Studio emulator:
    /// <c>avd.name</c> (or <c>avd.id</c>), <c>port.adb</c> and <c>port.serial</c> (the console port).
    /// </summary>
    public static AvdRunningInfo? AvdRunningIni(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                values.TryAdd(line[..eq].Trim(), line[(eq + 1)..].Trim());
            }
        }

        int? Port(string key) =>
            values.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536 ? p : null;

        var name = values.TryGetValue("avd.name", out var n) ? NullIfBlank(n) : null;
        name ??= values.TryGetValue("avd.id", out var id) ? NullIfBlank(id) : null;
        var adb = Port("port.adb");
        var console = Port("port.serial");
        if (adb is null && console is int c)
        {
            adb = c + 1;
        }

        return name is null && adb is null ? null : new AvdRunningInfo(name, adb, console ?? (adb - 1));
    }

    /// <summary>
    /// The AVD name in an Android Studio emulator window title: "Android Emulator - Pixel_8_API_35:5554" gives
    /// ("Pixel_8_API_35", 5554); older titles without the prefix ("Pixel_8_API_35:5554") work too.
    /// </summary>
    public static (string? AvdName, int? ConsolePort) AvdWindowTitle(string? title)
    {
        var m = AvdTitleRegex().Match(title ?? "");
        if (!m.Success)
        {
            return (null, null);
        }

        int? port = int.TryParse(m.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null;
        return (m.Groups[1].Value, port);
    }

    [GeneratedRegex(@"^(?:Android Emulator - )?([^:\s][^:]*):(\d{4,5})$", RegexOptions.CultureInvariant)]
    private static partial Regex AvdTitleRegex();

    // ------------------------------------------------------------------ LDPlayer

    /// <summary>Instance number from "leidian3" (the VM name LDPlayer passes to its VirtualBox process).</summary>
    public static int? LdPlayerIndex(string? vmName)
    {
        var s = (vmName ?? "").Trim();
        return s.StartsWith("leidian", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(s.AsSpan("leidian".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var i)
            ? i
            : null;
    }

    /// <summary>
    /// Instance number in an LDPlayer player command line: "index=2" (as written by ldconsole) or a "leidian2" argument.
    /// </summary>
    public static int? LdPlayerCommandLineIndex(string? commandLine)
    {
        var m = LdIndexRegex().Match(commandLine ?? "");
        return m.Success && int.TryParse(m.Groups[1].Success ? m.Groups[1].ValueSpan : m.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var i)
            ? i
            : null;
    }

    [GeneratedRegex(@"(?<![\w])index[=:](\d+)|(?<![\w])leidian(\d+)(?![\w])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LdIndexRegex();

    /// <summary>
    /// LDPlayer <c>vms\config\leidian&lt;N&gt;.config</c> (JSON with dotted keys): whether the instance's ADB debugging
    /// switch (<c>basicSettings.adbDebug</c>, 1 = local connection on) is on; null when absent or unreadable.
    /// </summary>
    public static bool? LdPlayerAdbEnabled(string? json)
    {
        var root = ParseJson(json);
        if (root is not { } r)
        {
            return null;
        }

        using (r)
        {
            if (r.RootElement.ValueKind != JsonValueKind.Object || !r.RootElement.TryGetProperty("basicSettings.adbDebug", out var v))
            {
                return null;
            }

            return v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt32(out var n) => n != 0,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => v.GetString()?.Trim() is { Length: > 0 } s ? s != "0" && !s.Equals("false", StringComparison.OrdinalIgnoreCase) : null,
                _ => null,
            };
        }
    }

    // ------------------------------------------------------------------ Nox / MEmu instance names

    /// <summary>Instance number from "Nox_2", "Nox64_2", "MEmu_2" (2) or "nox", "Nox64", "MEmu" (0).</summary>
    public static int? SuffixIndex(string? instance, params string[] baseNames)
    {
        var s = (instance ?? "").Trim();
        foreach (var b in baseNames)
        {
            if (s.Equals(b, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (s.Length > b.Length + 1 && s.StartsWith(b + "_", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(s.AsSpan(b.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var i))
            {
                return i;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ helpers

    private static JsonDocument? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetPath(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var p in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(p, out value))
            {
                return false;
            }
        }

        return true;
    }

    private static int? AsPort(JsonElement e)
    {
        int port;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out port))
        {
            return port is > 0 and < 65536 ? port : null;
        }

        return e.ValueKind == JsonValueKind.String
            && int.TryParse(e.GetString()?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is > 0 and < 65536
                ? port
                : null;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>An emulator instance entry reported by <c>MuMuManager.exe info --vmindex all</c>.</summary>
public sealed record MuMuManagerInstanceInfo(
    int Index,
    string? Name,
    bool IsRunning,
    int? AdbPort,
    nint MainWindowHandle,
    nint RenderWindowHandle,
    int ProcessId = 0,
    int HeadlessProcessId = 0);
