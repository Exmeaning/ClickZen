using ClickZen.Core.Devices;

namespace ClickZen.Device.Adb.Parsing;

/// <summary>One entry of <c>adb devices [-l]</c> / <c>host:track-devices</c>.</summary>
/// <param name="Model">The <c>model:</c> field of <c>devices -l</c> output (underscores → spaces), or empty.</param>
public readonly record struct DeviceListEntry(string Serial, DeviceAdbState State, string RawState, string Model = "");

/// <summary>Parses the payload of <c>host:devices[-l]</c> and <c>host:track-devices</c> (<c>serial\tstate</c> per line).</summary>
public static class DeviceListParser
{
    public static IReadOnlyList<DeviceListEntry> Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        var result = new List<DeviceListEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in payload.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith('*'))
            {
                continue;
            }

            // "serial<TAB>state" (track-devices) or "serial   state key:value…" (devices -l).
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            string serial, rest;
            if (tab > 0)
            {
                serial = line[..tab].Trim();
                rest = line[(tab + 1)..].Trim();
            }
            else
            {
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2)
                {
                    continue;
                }

                serial = parts[0];
                rest = parts[1];
            }

            var stateWord = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
            // "no permissions (…)" is two words.
            if (stateWord == "no" && rest.StartsWith("no permissions", StringComparison.Ordinal))
            {
                stateWord = "no permissions";
            }

            if (serial.Length == 0 || !seen.Add(serial))
            {
                continue;
            }

            var model = "";
            foreach (var token in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("model:", StringComparison.Ordinal))
                {
                    model = token["model:".Length..].Replace('_', ' ');
                }
            }

            result.Add(new DeviceListEntry(serial, MapState(stateWord), stateWord, model));
        }

        return result;
    }

    /// <summary>Maps adb's textual state to <see cref="DeviceAdbState"/>.</summary>
    public static DeviceAdbState MapState(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "device" => DeviceAdbState.Online,
        "offline" or "connecting" => DeviceAdbState.Offline,
        "unauthorized" or "authorizing" => DeviceAdbState.Unauthorized,
        _ => DeviceAdbState.Other,
    };
}
