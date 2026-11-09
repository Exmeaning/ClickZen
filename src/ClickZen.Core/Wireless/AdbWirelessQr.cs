using System.Security.Cryptography;
using System.Text;

namespace ClickZen.Core.Wireless;

/// <summary>
/// Android 11+ Wireless Debugging QR code pairing payload (<c>WIFI:T:ADB;S:&lt;serviceName&gt;;P:&lt;password&gt;;;</c>,
/// matching AOSP's <c>WirelessDebuggingFragment</c> and Android Studio's QR pairing flow).
/// </summary>
public sealed record AdbWirelessQrCredentials(string ServiceName, string Password, string Payload)
{
    private const string Prefix = "WIFI:T:ADB;";

    /// <summary>Generates a fresh random pairing credential set (<c>clickzen-&lt;8 hex&gt;</c> + 6-digit numeric code).</summary>
    public static AdbWirelessQrCredentials CreateRandom()
    {
        Span<byte> rand = stackalloc byte[4];
        RandomNumberGenerator.Fill(rand);
        var suffix = Convert.ToHexStringLower(rand);
        var code = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Create($"clickzen-{suffix}", code);
    }

    public static AdbWirelessQrCredentials Create(string serviceName, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var s = serviceName.Trim();
        var p = password.Trim();
        var payload = $"{Prefix}S:{Escape(s)};P:{Escape(p)};;";
        return new AdbWirelessQrCredentials(s, p, payload);
    }

    public static bool TryParse(string? payload, out AdbWirelessQrCredentials? credentials)
    {
        credentials = null;
        if (string.IsNullOrWhiteSpace(payload) || !payload.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string? service = null;
        string? password = null;
        var body = payload[Prefix.Length..];
        var i = 0;
        while (i < body.Length)
        {
            if (body[i] == ';')
            {
                i++;
                continue;
            }

            var colon = body.IndexOf(':', i);
            if (colon <= i)
            {
                break;
            }

            var key = body[i..colon];
            var sb = new StringBuilder();
            var j = colon + 1;
            while (j < body.Length && body[j] != ';')
            {
                if (body[j] == '\\' && j + 1 < body.Length)
                {
                    j++;
                }

                sb.Append(body[j]);
                j++;
            }

            if (key == "S")
            {
                service = sb.ToString();
            }
            else if (key == "P")
            {
                password = sb.ToString();
            }

            i = j + 1;
        }

        if (string.IsNullOrEmpty(service) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        credentials = new AdbWirelessQrCredentials(service, password, payload);
        return true;
    }

    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '\\' or ';' or ',' or ':' or '"')
            {
                sb.Append('\\');
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }
}
