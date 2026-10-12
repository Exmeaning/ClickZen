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

    [GeneratedRegex(@"^(?:MuMuPlayer(?:Global)?|YXArkNights)-\d+(?:\.\d+)?-(\d+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MuMuVmRegex();

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
