using System.Globalization;
using System.Net;

namespace ClickZen.Core.Emulators;

/// <summary>Android emulator families ClickZen recognises on Windows.</summary>
public enum EmulatorKind
{
    /// <summary>MuMu Player 12: 4.x (<c>shell\MuMuPlayer.exe</c>) and 5.x (<c>nx_device\…\MuMuNxDevice.exe</c>), domestic and Global.</summary>
    MuMu,

    /// <summary>LDPlayer / 雷电模拟器 3, 4, 9 and 14 (<c>dnplayer.exe</c>).</summary>
    LDPlayer,

    /// <summary>NoxPlayer / 夜神模拟器 (<c>Nox.exe</c>).</summary>
    Nox,

    /// <summary>MEmu / 逍遥模拟器 (<c>MEmu.exe</c>).</summary>
    MEmu,

    /// <summary>BlueStacks 5, international and Chinese builds (<c>HD-Player.exe</c>).</summary>
    BlueStacks,

    /// <summary>Android Studio emulator (AVD, <c>qemu-system-*.exe</c>).</summary>
    AndroidStudio,
}

/// <summary>Where <see cref="EmulatorInstance.AdbPort"/> came from, i.e. how far it can be trusted.</summary>
public enum AdbPortSource
{
    /// <summary>The port is not known.</summary>
    Unknown,

    /// <summary>Read from the emulator's own configuration (vm_config.json, .vbox / .memu, bluestacks.conf).</summary>
    Config,

    /// <summary>Read from state the running emulator published (the AVD discovery file).</summary>
    Runtime,

    /// <summary>Computed from the instance number with the emulator's documented port formula.</summary>
    Formula,
}

/// <summary>
/// A running emulator instance found on this PC. Immutable snapshot produced by the detector; a new detection
/// produces new instances.
/// </summary>
public sealed record EmulatorInstance
{
    public required EmulatorKind Kind { get; init; }

    /// <summary>
    /// The instance within its emulator: MuMu VM name ("MuMuPlayer-12.0-1"), LDPlayer "leidian1", Nox "Nox_1",
    /// MEmu "MEmu_1", BlueStacks "Pie64", AVD name. Stable across restarts.
    /// </summary>
    public required string InstanceKey { get; init; }

    /// <summary>Instance number (0 = the main instance) when known.</summary>
    public int? Index { get; init; }

    /// <summary>Name the emulator shows for the instance (player name, window title, AVD name); may be empty.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Installation directory of the emulator, when known.</summary>
    public string? InstallDirectory { get; init; }

    /// <summary>The process that owns the emulator window; 0 when no player process was found (e.g. headless).</summary>
    public int ProcessId { get; init; }

    /// <summary>Executable name of <see cref="ProcessId"/> without ".exe".</summary>
    public string ProcessName { get; init; } = "";

    /// <summary>adb TCP port on 127.0.0.1, or null when unknown.</summary>
    public int? AdbPort { get; init; }

    public AdbPortSource PortSource { get; init; }

    /// <summary>Ports worth trying when <see cref="AdbPort"/> refuses (MuMu moves to the next free port when its port is taken).</summary>
    public IReadOnlyList<int> FallbackPorts { get; init; } = [];

    /// <summary>False when the emulator's own ADB switch is known to be off (BlueStacks); null when unknown.</summary>
    public bool? AdbEnabled { get; init; }

    /// <summary>The file the port was read from (for the log).</summary>
    public string? ConfigPath { get; init; }

    /// <summary>Top-level window of the instance; 0 when none was found.</summary>
    public nint WindowHandle { get; init; }

    public string WindowTitle { get; init; } = "";

    public string WindowClass { get; init; } = "";

    /// <summary>The window is minimized (it has no picture to capture until restored).</summary>
    public bool WindowMinimized { get; init; }

    /// <summary>"kind:instance", the identity used to remember bindings (<see cref="Devices.EmulatorProfile.EmulatorKey"/>).</summary>
    public string Key => EmulatorCatalog.KeyOf(Kind, InstanceKey);

    /// <summary><see cref="DisplayName"/>, or <see cref="InstanceKey"/> when the emulator shows no name.</summary>
    public string Name => DisplayName.Length > 0 ? DisplayName : InstanceKey;

    /// <summary>"127.0.0.1:port" for <c>adb connect</c>, or null when the port is unknown.</summary>
    public string? AdbAddress => AdbPort is int port ? EmulatorSerials.Address(port) : null;

    public bool HasWindow => WindowHandle != 0;

    /// <summary>True when an adb serial refers to this instance (its port or a fallback port, in any spelling adb uses).</summary>
    public bool OwnsSerial(string? serial) =>
        EmulatorSerials.LocalAdbPort(serial) is int port && (port == AdbPort || FallbackPorts.Contains(port));
}

/// <summary>An emulator found in the registry (installed, whether or not it runs).</summary>
public sealed record InstalledEmulator(EmulatorKind Kind, string InstallDirectory);

/// <summary>Result of one detection pass.</summary>
public sealed record EmulatorDetection(IReadOnlyList<EmulatorInstance> Running, IReadOnlyList<InstalledEmulator> Installed)
{
    public static EmulatorDetection Empty { get; } = new([], []);
}

/// <summary>adb serial spellings of local (127.0.0.1) emulators.</summary>
public static class EmulatorSerials
{
    /// <summary>
    /// The 127.0.0.1 adb port a serial refers to: "127.0.0.1:P", "localhost:P" and "[::1]:P" give P; "emulator-N" gives
    /// N + 1 (adb names local emulators after their console port, the adb port being the next one). Null otherwise.
    /// </summary>
    public static int? LocalAdbPort(string? serial)
    {
        var s = (serial ?? "").Trim();
        if (s.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(s.AsSpan("emulator-".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var console)
                && console is > 0 and < 65535
                ? console + 1
                : null;
        }

        var colon = s.LastIndexOf(':');
        if (colon <= 0
            || !int.TryParse(s.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            return null;
        }

        var host = s[..colon].Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip))
            ? port
            : null;
    }

    /// <summary>"127.0.0.1:<paramref name="port"/>".</summary>
    public static string Address(int port) => string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{port}");

    /// <summary>"emulator-N" for the adb port N + 1 (the serial adb gives an emulator it found by itself).</summary>
    public static string ConsoleSerial(int adbPort) => string.Create(CultureInfo.InvariantCulture, $"emulator-{adbPort - 1}");
}
