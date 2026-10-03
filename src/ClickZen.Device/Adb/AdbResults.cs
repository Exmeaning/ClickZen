using ClickZen.Device.Adb.Parsing;

namespace ClickZen.Device.Adb;

/// <summary>Outcome of <see cref="AdbService.ConnectAsync"/>, <see cref="AdbService.PairAsync"/> or <see cref="AdbService.DisconnectAsync"/>.</summary>
/// <param name="Success">True when the operation achieved its goal (including "already connected").</param>
/// <param name="Kind">Classification for localized UI hints.</param>
/// <param name="Message">The adb server's reply (English, as sent by adb) or our own diagnostic.</param>
/// <param name="Address">Canonical <c>host:port</c> that was used, or the raw input when it could not be parsed.</param>
public sealed record AdbConnectResult(bool Success, ConnectResultKind Kind, string Message, string Address)
{
    /// <summary>Allows <c>var (ok, message) = await adb.ConnectAsync(...)</c>.</summary>
    public void Deconstruct(out bool ok, out string message)
    {
        ok = Success;
        message = Message;
    }
}

/// <summary>Outcome of <see cref="AdbService.CheckRootAsync"/>.</summary>
/// <param name="IsRooted">True when <c>id</c> ran as uid 0.</param>
/// <param name="Status">Detailed status; <see cref="RootStatus.Timeout"/> means the user has not answered the root prompt on the phone.</param>
/// <param name="Output">Raw output of the probe.</param>
public sealed record RootCheckResult(bool IsRooted, RootStatus Status, string Output);
