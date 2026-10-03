namespace ClickZen.Device.Adb.Parsing;

/// <summary>Classification of an <c>adb connect</c> / <c>pair</c> / <c>disconnect</c> reply.</summary>
public enum ConnectResultKind
{
    /// <summary>Text not recognised.</summary>
    Unknown,
    Connected,
    AlreadyConnected,
    Paired,
    Disconnected,
    /// <summary>The address entered by the user could not be parsed.</summary>
    InvalidAddress,
    /// <summary>Host name could not be resolved.</summary>
    HostNotFound,
    /// <summary>TCP connection refused (nothing listening, wireless debugging off, wrong port).</summary>
    Refused,
    /// <summary>No answer in time.</summary>
    Timeout,
    /// <summary>Network/host unreachable.</summary>
    Unreachable,
    /// <summary>TCP connected but the device did not accept our key (confirm the prompt on the phone).</summary>
    Unauthorized,
    /// <summary>Pairing failed because the code was wrong (or the pairing dialog was closed).</summary>
    WrongPairingCode,
    /// <summary><c>disconnect</c> of an address that is not connected.</summary>
    NoSuchDevice,
    /// <summary>Any other failure.</summary>
    Failed,
}

/// <summary>Parsed adb host reply.</summary>
public readonly record struct ConnectParseResult(bool Success, ConnectResultKind Kind, string Raw);

/// <summary>
/// Parses the text replies of <c>host:connect</c>, <c>host:pair</c> and <c>host:disconnect</c>.
/// The adb server on Windows embeds localized OS error strings ("由于目标计算机积极拒绝…(10061)"), so the
/// Winsock error codes are matched as well as English phrases.
/// </summary>
public static class ConnectResultParser
{
    public static ConnectParseResult Parse(string? text)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length == 0)
        {
            return new(false, ConnectResultKind.Unknown, raw);
        }

        // Success forms first – they are unambiguous prefixes.
        if (raw.StartsWith("Successfully paired", StringComparison.OrdinalIgnoreCase))
        {
            return new(true, ConnectResultKind.Paired, raw);
        }

        if (raw.StartsWith("already connected", StringComparison.OrdinalIgnoreCase))
        {
            return new(true, ConnectResultKind.AlreadyConnected, raw);
        }

        if (raw.StartsWith("connected to", StringComparison.OrdinalIgnoreCase))
        {
            return new(true, ConnectResultKind.Connected, raw);
        }

        if (raw.StartsWith("disconnected", StringComparison.OrdinalIgnoreCase))
        {
            return new(true, ConnectResultKind.Disconnected, raw);
        }

        return new(false, ClassifyFailure(raw), raw);
    }

    private static ConnectResultKind ClassifyFailure(string raw)
    {
        if (Has(raw, "no such device"))
        {
            return ConnectResultKind.NoSuchDevice;
        }

        if (Has(raw, "failed to authenticate") || Has(raw, "unauthorized"))
        {
            return ConnectResultKind.Unauthorized;
        }

        if (Has(raw, "wrong password") || Has(raw, "wrong pairing code") || Has(raw, "incorrect pairing code"))
        {
            return ConnectResultKind.WrongPairingCode;
        }

        if (Has(raw, "cannot resolve host") || Has(raw, "no such host") || Has(raw, "(11001)") || Has(raw, "(11004)")
            || Has(raw, "name or service not known"))
        {
            return ConnectResultKind.HostNotFound;
        }

        if (Has(raw, "refused") || Has(raw, "(10061)") || Has(raw, "积极拒绝"))
        {
            return ConnectResultKind.Refused;
        }

        if (Has(raw, "timed out") || Has(raw, "timeout") || Has(raw, "(10060)") || Has(raw, "没有正确答复"))
        {
            return ConnectResultKind.Timeout;
        }

        if (Has(raw, "unreachable") || Has(raw, "no route to host") || Has(raw, "(10051)") || Has(raw, "(10065)")
            || Has(raw, "无法访问"))
        {
            return ConnectResultKind.Unreachable;
        }

        return ConnectResultKind.Failed;
    }

    private static bool Has(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
