using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace ClickZen.Device.Adb.Parsing;

/// <summary>Extracts the device's Wi-Fi IPv4 address from <c>ip</c> command output.</summary>
public static partial class IpParser
{
    /// <summary>
    /// Parses <c>ip addr show wlan0</c> (or legacy <c>ifconfig wlan0</c>) output and returns the first
    /// usable IPv4 address, or <see langword="null"/> when the interface is down / missing.
    /// </summary>
    public static string? ParseWlanIp(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        foreach (Match m in InetRegex().Matches(output))
        {
            if (IsUsable(m.Groups["ip"].Value))
            {
                return m.Groups["ip"].Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses <c>ip route</c> output and returns the <c>src</c> address of the route on
    /// <paramref name="interfaceName"/>, falling back to the first route with a usable source.
    /// </summary>
    public static string? ParseRouteSource(string? output, string interfaceName = "wlan0")
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        string? fallback = null;
        foreach (var rawLine in output.Split('\n'))
        {
            var m = RouteSrcRegex().Match(rawLine);
            if (!m.Success || !IsUsable(m.Groups["ip"].Value))
            {
                continue;
            }

            if (rawLine.Contains($"dev {interfaceName} ", StringComparison.Ordinal)
                || rawLine.TrimEnd().EndsWith($"dev {interfaceName}", StringComparison.Ordinal))
            {
                return m.Groups["ip"].Value;
            }

            fallback ??= m.Groups["ip"].Value;
        }

        return fallback;
    }

    private static bool IsUsable(string candidate) =>
        IPAddress.TryParse(candidate, out var ip)
        && ip.AddressFamily == AddressFamily.InterNetwork
        && !IPAddress.IsLoopback(ip)
        && !candidate.StartsWith("169.254.", StringComparison.Ordinal)
        && !candidate.Equals("0.0.0.0", StringComparison.Ordinal);

    [GeneratedRegex(@"\binet\s+(?:addr:)?(?<ip>\d{1,3}(?:\.\d{1,3}){3})", RegexOptions.CultureInvariant)]
    private static partial Regex InetRegex();

    [GeneratedRegex(@"\bsrc\s+(?<ip>\d{1,3}(?:\.\d{1,3}){3})", RegexOptions.CultureInvariant)]
    private static partial Regex RouteSrcRegex();
}
