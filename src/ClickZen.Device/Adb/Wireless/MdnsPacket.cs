using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ClickZen.Device.Adb.Wireless;

public enum MdnsAdbServiceKind
{
    /// <summary><c>_adb-tls-connect._tcp.local.</c> — Android 11+ Wireless Debugging TLS endpoint.</summary>
    TlsConnect,
    /// <summary><c>_adb-tls-pairing._tcp.local.</c> — Android 11+ Wireless Debugging QR / code pairing endpoint.</summary>
    TlsPairing,
    /// <summary><c>_adb._tcp.local.</c> — Legacy TCP/IP wireless ADB endpoint (e.g. port 5555).</summary>
    LegacyTcp,
}

/// <summary>An Android wireless ADB endpoint discovered over LAN mDNS.</summary>
public sealed record MdnsAdbService(
    MdnsAdbServiceKind Kind,
    string InstanceName,
    string Host,
    int Port,
    IReadOnlyDictionary<string, string>? Properties = null)
{
    public string Address => Host.Contains(':', StringComparison.Ordinal) && !Host.StartsWith('[')
        ? $"[{Host}]:{Port}"
        : $"{Host}:{Port}";
}

/// <summary>
/// Minimal, bounds-checked RFC 6762 / RFC 6763 mDNS query builder and response parser for Android ADB DNS-SD services,
/// plus a parser for <c>adb mdns services</c> CLI output.
/// </summary>
public static partial class MdnsPacket
{
    public const string TlsConnectService = "_adb-tls-connect._tcp.local.";
    public const string TlsPairingService = "_adb-tls-pairing._tcp.local.";
    public const string LegacyTcpService = "_adb._tcp.local.";

    private static readonly string[] DefaultServices = [TlsConnectService, TlsPairingService, LegacyTcpService];

    private const ushort TypeA = 1;
    private const ushort TypePtr = 12;
    private const ushort TypeTxt = 16;
    private const ushort TypeAaaa = 28;
    private const ushort TypeSrv = 33;
    private const ushort ClassIn = 1;

    /// <summary>Builds a standard DNS-SD PTR query packet asking for one or more service types.</summary>
    public static byte[] BuildPtrQuery(bool unicastResponse = false, params ReadOnlySpan<string> serviceTypes)
    {
        if (serviceTypes.Length == 0)
        {
            serviceTypes = DefaultServices;
        }

        using var ms = new MemoryStream(128);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header[0..], 0); // Transaction ID = 0 for mDNS
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0); // Standard query flags
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)serviceTypes.Length); // QDCOUNT
        ms.Write(header);

        ushort qclass = unicastResponse ? (ushort)(ClassIn | 0x8000) : ClassIn;
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail[0..], TypePtr);
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..], qclass);

        foreach (var svc in serviceTypes)
        {
            WriteDomainName(ms, svc);
            ms.Write(tail);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Extracts Android ADB DNS-SD endpoints from an incoming mDNS UDP packet.
    /// <paramref name="senderAddress"/> is used as a last-resort host fallback if the responder sent SRV without A/AAAA in the same datagram.
    /// </summary>
    public static IReadOnlyList<MdnsAdbService> ParseMessage(ReadOnlySpan<byte> packet, IPAddress? senderAddress = null)
    {
        if (packet.Length < 12)
        {
            return [];
        }

        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        var anCount = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
        var nsCount = BinaryPrimitives.ReadUInt16BigEndian(packet[8..]);
        var arCount = BinaryPrimitives.ReadUInt16BigEndian(packet[10..]);
        var totalRecords = anCount + nsCount + arCount;
        if (totalRecords == 0)
        {
            return [];
        }

        var offset = 12;
        for (var q = 0; q < qdCount; q++)
        {
            if (!TryReadName(packet, ref offset, out _) || offset + 4 > packet.Length)
            {
                return [];
            }

            offset += 4;
        }

        var srvRecords = new List<(string FullName, string Target, int Port, uint Ttl)>();
        var ipv4ByHost = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ipv6ByHost = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var txtByFullName = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        for (var r = 0; r < totalRecords && offset < packet.Length; r++)
        {
            if (!TryReadName(packet, ref offset, out var rrName) || offset + 10 > packet.Length)
            {
                break;
            }

            var rrType = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(packet[(offset + 4)..]);
            var rdLen = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 8)..]);
            offset += 10;
            if (offset + rdLen > packet.Length)
            {
                break;
            }

            var rdataOffset = offset;
            offset += rdLen;
            if (ttl == 0)
            {
                // Goodbye record (TTL=0 means service went offline).
                continue;
            }

            switch (rrType)
            {
                case TypeA when rdLen == 4:
                {
                    var ip = new IPAddress(packet.Slice(rdataOffset, 4));
                    if (!IPAddress.IsLoopback(ip))
                    {
                        ipv4ByHost[NormalizeDomain(rrName)] = ip.ToString();
                    }

                    break;
                }
                case TypeAaaa when rdLen == 16:
                {
                    var ip = new IPAddress(packet.Slice(rdataOffset, 16));
                    if (!IPAddress.IsLoopback(ip) && !ip.IsIPv6LinkLocal)
                    {
                        ipv6ByHost[NormalizeDomain(rrName)] = ip.ToString();
                    }

                    break;
                }
                case TypeSrv when rdLen >= 6:
                {
                    var port = BinaryPrimitives.ReadUInt16BigEndian(packet[(rdataOffset + 4)..]);
                    var targetCursor = rdataOffset + 6;
                    if (port > 0 && TryReadName(packet, ref targetCursor, out var target))
                    {
                        srvRecords.Add((NormalizeDomain(rrName), NormalizeDomain(target), port, ttl));
                    }

                    break;
                }
                case TypeTxt when rdLen > 0:
                {
                    txtByFullName[NormalizeDomain(rrName)] = ParseTxtRecord(packet.Slice(rdataOffset, rdLen));
                    break;
                }
                case TypePtr:
                {
                    // PTR maps service type -> instance name; SRV carries actual port + target.
                    var ptrCursor = rdataOffset;
                    _ = TryReadName(packet, ref ptrCursor, out _);
                    break;
                }
            }
        }

        var services = new List<MdnsAdbService>();
        foreach (var srv in srvRecords)
        {
            if (!TryClassifyServiceName(srv.FullName, out var kind, out var instanceName))
            {
                continue;
            }

            string? host = null;
            if (ipv4ByHost.TryGetValue(srv.Target, out var v4))
            {
                host = v4;
            }
            else if (ipv6ByHost.TryGetValue(srv.Target, out var v6))
            {
                host = v6;
            }
            else if (senderAddress is not null && !IPAddress.IsLoopback(senderAddress))
            {
                host = senderAddress.IsIPv4MappedToIPv6 ? senderAddress.MapToIPv4().ToString() : senderAddress.ToString();
            }

            if (string.IsNullOrEmpty(host))
            {
                continue;
            }

            txtByFullName.TryGetValue(srv.FullName, out var props);
            services.Add(new MdnsAdbService(kind, instanceName, host, srv.Port, props));
        }

        return services;
    }

    /// <summary>
    /// Parses the output of <c>adb mdns services</c>, e.g.:
    /// <c>adb-R5CT123-abc	_adb-tls-connect._tcp.	192.168.1.42:39123</c>.
    /// </summary>
    public static IReadOnlyList<MdnsAdbService> ParseAdbMdnsServicesOutput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var list = new List<MdnsAdbService>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("List of discovered", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var m = AdbMdnsLineRegex().Match(line);
            if (!m.Success)
            {
                continue;
            }

            var instance = m.Groups["inst"].Value.Trim();
            var svcType = m.Groups["type"].Value.Trim().TrimEnd('.');
            var host = m.Groups["host"].Value.Trim().Trim('[', ']');
            if (!int.TryParse(m.Groups["port"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port)
                || port is <= 0 or > 65535)
            {
                continue;
            }

            MdnsAdbServiceKind? kind = svcType.ToLowerInvariant() switch
            {
                "_adb-tls-connect._tcp" => MdnsAdbServiceKind.TlsConnect,
                "_adb-tls-pairing._tcp" => MdnsAdbServiceKind.TlsPairing,
                "_adb._tcp" => MdnsAdbServiceKind.LegacyTcp,
                _ => null,
            };
            if (kind is { } k)
            {
                list.Add(new MdnsAdbService(k, instance, host, port));
            }
        }

        return list;
    }

    private static bool TryClassifyServiceName(string fullName, out MdnsAdbServiceKind kind, out string instanceName)
    {
        foreach (var (suffix, k) in new[]
                 {
                     ("._adb-tls-connect._tcp.local", MdnsAdbServiceKind.TlsConnect),
                     ("._adb-tls-pairing._tcp.local", MdnsAdbServiceKind.TlsPairing),
                     ("._adb._tcp.local", MdnsAdbServiceKind.LegacyTcp),
                 })
        {
            if (fullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && fullName.Length > suffix.Length)
            {
                kind = k;
                instanceName = fullName[..^suffix.Length];
                return true;
            }
        }

        kind = default;
        instanceName = "";
        return false;
    }

    private static Dictionary<string, string> ParseTxtRecord(ReadOnlySpan<byte> rdata)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < rdata.Length)
        {
            var len = rdata[i++];
            if (len == 0 || i + len > rdata.Length)
            {
                break;
            }

            var entry = Encoding.UTF8.GetString(rdata.Slice(i, len));
            i += len;
            var eq = entry.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                dict[entry[..eq]] = entry[(eq + 1)..];
            }
        }

        return dict;
    }

    private static void WriteDomainName(Stream stream, string domain)
    {
        foreach (var label in domain.Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > 63)
            {
                throw new ArgumentException($"Invalid DNS label '{label}'.", nameof(domain));
            }

            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
    }

    private static bool TryReadName(ReadOnlySpan<byte> packet, ref int offset, out string name)
    {
        name = "";
        if (offset < 0 || offset >= packet.Length)
        {
            return false;
        }

        var sb = new StringBuilder();
        var cursor = offset;
        var jumped = false;
        var nextOffset = offset;
        var hops = 0;

        while (true)
        {
            if (cursor < 0 || cursor >= packet.Length || ++hops > 64)
            {
                return false;
            }

            var len = packet[cursor];
            if (len == 0)
            {
                if (!jumped)
                {
                    nextOffset = cursor + 1;
                }

                offset = nextOffset;
                name = sb.ToString();
                return true;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= packet.Length)
                {
                    return false;
                }

                var pointer = ((len & 0x3F) << 8) | packet[cursor + 1];
                if (!jumped)
                {
                    nextOffset = cursor + 2;
                    jumped = true;
                }

                cursor = pointer;
                continue;
            }

            if ((len & 0xC0) != 0 || cursor + 1 + len > packet.Length)
            {
                return false;
            }

            if (sb.Length > 0)
            {
                sb.Append('.');
            }

            sb.Append(Encoding.UTF8.GetString(packet.Slice(cursor + 1, len)));
            cursor += 1 + len;
        }
    }

    private static string NormalizeDomain(string name) => name.Trim().TrimEnd('.');

    [GeneratedRegex(@"^(?<inst>\S+)\s+(?<type>_adb(?:-tls-(?:connect|pairing))?\._tcp\.?)\s+(?<host>\S+):(?<port>\d+)\s*$")]
    private static partial Regex AdbMdnsLineRegex();
}
