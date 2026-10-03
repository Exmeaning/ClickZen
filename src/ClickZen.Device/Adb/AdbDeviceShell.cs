using ClickZen.Core.Automation;

namespace ClickZen.Device.Adb;

/// <summary>Binds <see cref="AdbService"/> to one device as the engine's <see cref="IDeviceShell"/>.</summary>
public sealed class AdbDeviceShell : IDeviceShell
{
    private readonly AdbService _adb;

    public AdbDeviceShell(AdbService adb, string serial)
    {
        _adb = adb;
        Serial = serial;
    }

    public string Serial { get; }

    public Task<string> ShellAsync(string command, bool asRoot, CancellationToken ct) =>
        asRoot ? _adb.RootShellAsync(Serial, command, ct) : _adb.ShellAsync(Serial, command, ct);
}
