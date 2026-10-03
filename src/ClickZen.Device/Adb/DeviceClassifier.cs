using System.Net;
using ClickZen.Core.Devices;

namespace ClickZen.Device.Adb;

/// <summary>Infers how a device is reached from its adb serial.</summary>
public static class DeviceClassifier
{
    /// <summary>
    /// <list type="bullet">
    /// <item><c>emulator-5554</c> → <see cref="ConnectionKind.Emulator"/></item>
    /// <item><c>127.0.0.1:port</c>, <c>localhost:port</c>, <c>[::1]:port</c> → <see cref="ConnectionKind.Emulator"/></item>
    /// <item>any other <c>host:port</c>, or an mDNS serial such as <c>adb-XXXX._adb-tls-connect._tcp</c> → <see cref="ConnectionKind.Wireless"/></item>
    /// <item>everything else → <see cref="ConnectionKind.Usb"/></item>
    /// </list>
    /// </summary>
    public static ConnectionKind Classify(string? serial)
    {
        var s = (serial ?? "").Trim();
        if (s.Length == 0)
        {
            return ConnectionKind.Usb;
        }

        if (IsEmulatorConsoleSerial(s))
        {
            return ConnectionKind.Emulator;
        }

        if (IsMdnsSerial(s))
        {
            return ConnectionKind.Wireless;
        }

        if (TrySplitHostPort(s, out var host))
        {
            return IsLoopback(host) ? ConnectionKind.Emulator : ConnectionKind.Wireless;
        }

        return ConnectionKind.Usb;
    }

    /// <summary>True for <c>emulator-NNNN</c> serials (the console port is NNNN, adb port NNNN+1).</summary>
    public static bool IsEmulatorConsoleSerial(string serial) =>
        serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase)
        && serial.Length > "emulator-".Length
        && serial.AsSpan("emulator-".Length).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>For <c>emulator-5554</c> returns 5555 (its adb port); otherwise <see langword="null"/>.</summary>
    public static int? EmulatorAdbPort(string serial) =>
        IsEmulatorConsoleSerial(serial) && int.TryParse(serial.AsSpan("emulator-".Length), out var console)
            ? console + 1
            : null;

    private static bool IsMdnsSerial(string s) =>
        s.Contains("._adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase)
        || s.Contains("._adb._tcp", StringComparison.OrdinalIgnoreCase);

    private static bool TrySplitHostPort(string s, out string host)
    {
        host = "";
        var idx = s.LastIndexOf(':');
        if (idx <= 0 || idx == s.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(s.AsSpan(idx + 1), out var port) || port is < 1 or > 65535)
        {
            return false;
        }

        host = s[..idx].Trim('[', ']');
        return host.Length > 0;
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
}
