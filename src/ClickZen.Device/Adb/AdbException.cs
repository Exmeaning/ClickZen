using System.Net.Sockets;
using AsacAdbException = AdvancedSharpAdbClient.Exceptions.AdbException;
using AsacDeviceNotFoundException = AdvancedSharpAdbClient.Exceptions.DeviceNotFoundException;
using AsacShellUnresponsiveException = AdvancedSharpAdbClient.Exceptions.ShellCommandUnresponsiveException;

namespace ClickZen.Device.Adb;

/// <summary>Category of an adb failure. The UI maps each value to a localized hint.</summary>
public enum AdbErrorKind
{
    Unknown,
    /// <summary>The adb server could not be started (missing/broken adb.exe, port 5037 taken by something else…).</summary>
    ServerStartFailed,
    /// <summary>The serial is not (or no longer) known to the adb server.</summary>
    DeviceNotFound,
    /// <summary>The device has not accepted this computer's RSA key yet.</summary>
    Unauthorized,
    /// <summary>The device is attached but offline.</summary>
    Offline,
    /// <summary>The operation did not finish in time.</summary>
    Timeout,
    /// <summary>The device accepted the request but the command failed or the stream broke.</summary>
    CommandFailed,
    /// <summary>The adb server is not reachable (nothing listening on 127.0.0.1:5037).</summary>
    ConnectionRefused,
}

/// <summary>
/// The single exception type surfaced by <c>ClickZen.Device.Adb</c>. Exceptions thrown by AdvancedSharpAdbClient
/// and the socket layer are translated by <see cref="AdbErrorMapper"/>; the original is kept as
/// <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class AdbException : Exception
{
    public AdbException()
        : this(AdbErrorKind.Unknown, "adb error")
    {
    }

    public AdbException(string message)
        : this(AdbErrorKind.Unknown, message)
    {
    }

    public AdbException(string message, Exception innerException)
        : this(AdbErrorKind.Unknown, message, null, innerException)
    {
    }

    public AdbException(AdbErrorKind kind, string message, string? serial = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Serial = serial;
    }

    public AdbErrorKind Kind { get; }

    /// <summary>The device involved, if any.</summary>
    public string? Serial { get; }

    public override string ToString() => $"[{Kind}] {base.ToString()}";
}

/// <summary>Translates exceptions from AdvancedSharpAdbClient / sockets into <see cref="AdbException"/>.</summary>
public static class AdbErrorMapper
{
    /// <summary>
    /// Wraps <paramref name="ex"/> in an <see cref="AdbException"/> with the best matching kind.
    /// An <see cref="AdbException"/> is returned unchanged.
    /// </summary>
    public static AdbException Map(Exception ex, string? serial = null, string? operation = null)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is AdbException own)
        {
            return own;
        }

        var kind = Classify(ex);
        var detail = ex is AsacAdbException { AdbError: { Length: > 0 } adbError } ? adbError : ex.Message;
        var prefix = operation is null ? "" : operation + ": ";
        var target = serial is null ? "" : $" ({serial})";
        return new AdbException(kind, $"{prefix}{detail.Trim()}{target}", serial, ex);
    }

    /// <summary>Best-effort classification of an arbitrary exception.</summary>
    public static AdbErrorKind Classify(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        switch (ex)
        {
            case AdbException own:
                return own.Kind;
            case AsacDeviceNotFoundException:
                return AdbErrorKind.DeviceNotFound;
            case TimeoutException:
                return AdbErrorKind.Timeout;
            case SocketException se:
                return ClassifySocket(se);
            case AsacAdbException adb:
                {
                    var byText = ClassifyServerMessage(adb.AdbError ?? adb.Response.Message ?? adb.Message);
                    if (byText != AdbErrorKind.Unknown)
                    {
                        return byText;
                    }

                    if (adb.InnerException is SocketException inner)
                    {
                        return ClassifySocket(inner);
                    }

                    if (adb is AsacShellUnresponsiveException)
                    {
                        return AdbErrorKind.CommandFailed;
                    }

                    return adb.ConnectionReset ? AdbErrorKind.ConnectionRefused : AdbErrorKind.CommandFailed;
                }

            case IOException io when io.InnerException is SocketException ioInner:
                return ClassifySocket(ioInner);
            case IOException:
                return AdbErrorKind.CommandFailed;
            case AggregateException { InnerExceptions.Count: 1 } agg:
                return Classify(agg.InnerExceptions[0]);
            default:
                return ClassifyServerMessage(ex.Message);
        }
    }

    /// <summary>Classifies the FAIL text sent by the adb server (e.g. <c>device unauthorized.</c>).</summary>
    public static AdbErrorKind ClassifyServerMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return AdbErrorKind.Unknown;
        }

        if (Has(message, "unauthorized") || Has(message, "still authorizing"))
        {
            return AdbErrorKind.Unauthorized;
        }

        if (Has(message, "device offline") || Has(message, "still connecting"))
        {
            return AdbErrorKind.Offline;
        }

        if ((Has(message, "device") && Has(message, "not found"))
            || Has(message, "no devices/emulators found")
            || Has(message, "no devices found")
            || Has(message, "no emulators found")
            || Has(message, "no such device"))
        {
            return AdbErrorKind.DeviceNotFound;
        }

        if (Has(message, "timed out") || Has(message, "timeout"))
        {
            return AdbErrorKind.Timeout;
        }

        if (Has(message, "actively refused") || Has(message, "connection refused") || Has(message, "积极拒绝"))
        {
            return AdbErrorKind.ConnectionRefused;
        }

        return AdbErrorKind.Unknown;
    }

    private static AdbErrorKind ClassifySocket(SocketException se) => se.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => AdbErrorKind.ConnectionRefused,
        SocketError.TimedOut => AdbErrorKind.Timeout,
        _ => AdbErrorKind.CommandFailed,
    };

    private static bool Has(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
