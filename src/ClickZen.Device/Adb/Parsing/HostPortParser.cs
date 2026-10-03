using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace ClickZen.Device.Adb.Parsing;

/// <summary>Parses user-entered addresses such as <c>192.168.1.5</c>, <c>192.168.1.5:5555</c> or <c>[fe80::1]:5555</c>.</summary>
public static class HostPortParser
{
    public const int DefaultAdbPort = 5555;

    /// <summary>
    /// Parses <paramref name="input"/>. When no port is given, <paramref name="defaultPort"/> is used; pass
    /// <see langword="null"/> to require an explicit port (pairing ports are random).
    /// </summary>
    public static bool TryParse(string? input, int? defaultPort, out string host, out int port)
    {
        host = "";
        port = 0;
        var s = (input ?? "").Trim();
        if (s.Length == 0 || s.Any(char.IsWhiteSpace))
        {
            return false;
        }

        string hostPart;
        string? portPart;
        if (s.StartsWith('['))
        {
            // [ipv6]:port or [ipv6]
            var close = s.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }

            hostPart = s[1..close];
            var rest = s[(close + 1)..];
            if (rest.Length == 0)
            {
                portPart = null;
            }
            else if (rest.StartsWith(':'))
            {
                portPart = rest[1..];
            }
            else
            {
                return false;
            }

            if (!IPAddress.TryParse(hostPart, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }
        }
        else
        {
            var colons = s.Count(c => c == ':');
            if (colons == 0)
            {
                hostPart = s;
                portPart = null;
            }
            else if (colons == 1)
            {
                var idx = s.IndexOf(':', StringComparison.Ordinal);
                hostPart = s[..idx];
                portPart = s[(idx + 1)..];
            }
            else
            {
                // Bare IPv6 without brackets: no port possible.
                if (!IPAddress.TryParse(s, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6)
                {
                    return false;
                }

                hostPart = s;
                portPart = null;
            }
        }

        if (hostPart.Length == 0)
        {
            return false;
        }

        int p;
        if (portPart is null)
        {
            if (defaultPort is not int d)
            {
                return false;
            }

            p = d;
        }
        else if (!int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out p))
        {
            return false;
        }

        if (p is < 1 or > 65535)
        {
            return false;
        }

        host = hostPart;
        port = p;
        return true;
    }

    /// <summary>Formats a host/port pair the way adb expects (IPv6 hosts are bracketed).</summary>
    public static string Format(string host, int port) =>
        host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
            ? $"[{host}]:{port.ToString(CultureInfo.InvariantCulture)}"
            : $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Parses and re-formats to the canonical <c>host:port</c> form, or <see langword="null"/> if invalid.</summary>
    public static string? Normalize(string? input, int? defaultPort = DefaultAdbPort) =>
        TryParse(input, defaultPort, out var host, out var port) ? Format(host, port) : null;
}
