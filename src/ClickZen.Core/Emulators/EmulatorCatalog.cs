using System.Globalization;

namespace ClickZen.Core.Emulators;

/// <summary>A child window class/title that shows an emulator's Android screen (null = any).</summary>
public sealed record RenderWindowPattern(string? ClassName, string? Title);

/// <summary>
/// What ClickZen knows about one emulator family: process names, port rules, registry locations and window hints.
/// Sources: the emulators' own documentation and the detection code of MaaFramework / MAA / ALAS / StarRailCopilot
/// (see docs/emulators.md). <see cref="HardwareVerified"/> says whether detection was tried on a real installation.
/// </summary>
public sealed record EmulatorDefinition
{
    public required EmulatorKind Kind { get; init; }

    /// <summary>Stable id (also the id of the fallback scan preset).</summary>
    public required string Id { get; init; }

    public required string EnglishName { get; init; }

    /// <summary>Default Chinese name (the UI uses localized resources; this is for the port preset table).</summary>
    public required string ChineseName { get; init; }

    /// <summary>Executable names (without ".exe") of the per-instance player process that owns the window.</summary>
    public required IReadOnlyList<string> PlayerProcesses { get; init; }

    /// <summary>Prefix that also identifies a player process (AVD: "qemu-system-"), or null.</summary>
    public string? PlayerProcessPrefix { get; init; }

    /// <summary>Executable names of the per-instance virtual machine process (its command line names the instance).</summary>
    public IReadOnlyList<string> VmProcesses { get; init; } = [];

    /// <summary>Prefixes of HKLM/HKCU …\Uninstall sub keys whose UninstallString points into the installation.</summary>
    public IReadOnlyList<string> UninstallKeyPrefixes { get; init; } = [];

    /// <summary>adb port of instance N by the emulator's documented formula, or null when there is no reliable formula.</summary>
    public Func<int, int?> PortForIndex { get; init; } = _ => null;

    /// <summary>How many instances the fallback port scan covers.</summary>
    public int ScanInstances { get; init; } = 4;

    /// <summary>Child windows that show the Android screen (used to find the screen area when binding the window).</summary>
    public IReadOnlyList<RenderWindowPattern> RenderWindows { get; init; } = [];

    /// <summary>Window class of the main window, when known (preferred when a player owns several windows).</summary>
    public IReadOnlyList<string> MainWindowClasses { get; init; } = [];

    /// <summary>True once detection was verified against a real installation of this emulator.</summary>
    public bool HardwareVerified { get; init; }

    /// <summary>Ports the fallback scan tries (formula for instances 0..<see cref="ScanInstances"/>-1).</summary>
    public IReadOnlyList<int> ScanPorts =>
        Enumerable.Range(0, ScanInstances).Select(PortForIndex).OfType<int>().Distinct().ToArray();

    /// <summary>True when <paramref name="processName"/> (without ".exe", any case) is a player process of this emulator.</summary>
    public bool IsPlayerProcess(string processName) =>
        PlayerProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase)
        || (PlayerProcessPrefix is { } prefix && processName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public bool IsVmProcess(string processName) => VmProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase);
}

/// <summary>The emulator families ClickZen detects, and helpers shared by detection, scanning and the UI.</summary>
public static class EmulatorCatalog
{
    public static EmulatorDefinition MuMu { get; } = new()
    {
        Kind = EmulatorKind.MuMu,
        Id = "mumu12",
        EnglishName = "MuMu Player",
        ChineseName = "MuMu 模拟器 12",
        // 5.x ("nx"): nx_device\<engine>\shell\MuMuNxDevice.exe per instance; 4.x: shell\MuMuPlayer.exe per instance.
        PlayerProcesses = ["MuMuNxDevice", "MuMuPlayer"],
        VmProcesses = ["MuMuVMMHeadless"],
        UninstallKeyPrefixes = ["MuMuPlayer", "MuMu Player", "YXArkNights"],
        // Official: 16384 + 32 × index (the port moves up by one when it is taken).
        PortForIndex = i => i >= 0 ? 16384 + 32 * i : null,
        ScanInstances = 6,
        RenderWindows = [new(null, "nemudisplay"), new("nemuwin", null)],
        MainWindowClasses = ["Qt5156QWindowIcon", "Qt6QWindowIcon"],
    };

    public static EmulatorDefinition LDPlayer { get; } = new()
    {
        Kind = EmulatorKind.LDPlayer,
        Id = "ldplayer",
        EnglishName = "LDPlayer",
        ChineseName = "雷电模拟器",
        PlayerProcesses = ["dnplayer"],
        VmProcesses = ["Ld9BoxHeadless", "LdVBoxHeadless", "LdBoxHeadless"],
        UninstallKeyPrefixes = ["LDPlayer", "leidian"],
        // Official (LDPlayer 9): instance N is emulator-(5554 + 2N), i.e. adb port 5555 + 2N; adb finds it by itself.
        PortForIndex = i => i >= 0 ? 5555 + 2 * i : null,
        ScanInstances = 6,
        RenderWindows = [new("RenderWindow", null), new(null, "TheRender")],
        MainWindowClasses = ["LDPlayerMainFrame"],
    };

    public static EmulatorDefinition Nox { get; } = new()
    {
        Kind = EmulatorKind.Nox,
        Id = "nox",
        EnglishName = "NoxPlayer",
        ChineseName = "夜神模拟器",
        PlayerProcesses = ["Nox"],
        VmProcesses = ["NoxVMHandle"],
        UninstallKeyPrefixes = ["Nox"],
        // Official: 62001 for the first instance, then 62025, 62026, …
        PortForIndex = i => i switch { 0 => 62001, > 0 => 62024 + i, _ => null },
        ScanInstances = 6,
    };

    public static EmulatorDefinition MEmu { get; } = new()
    {
        Kind = EmulatorKind.MEmu,
        Id = "memu",
        EnglishName = "MEmu",
        ChineseName = "逍遥模拟器",
        PlayerProcesses = ["MEmu"],
        VmProcesses = ["MEmuHeadless"],
        UninstallKeyPrefixes = ["MEmu"],
        // Official forum: 21503 + 10 × index (NAT mode).
        PortForIndex = i => i >= 0 ? 21503 + 10 * i : null,
        ScanInstances = 6,
    };

    public static EmulatorDefinition BlueStacks { get; } = new()
    {
        Kind = EmulatorKind.BlueStacks,
        Id = "bluestacks",
        EnglishName = "BlueStacks",
        ChineseName = "BlueStacks 蓝叠",
        PlayerProcesses = ["HD-Player"],
        UninstallKeyPrefixes = ["BlueStacks"],
        // No reliable formula (Hyper-V builds pick a new port on every start): the port comes from bluestacks.conf.
        // The scan still tries the common ones.
        PortForIndex = i => i >= 0 ? 5555 + 10 * i : null,
        ScanInstances = 4,
    };

    public static EmulatorDefinition AndroidStudio { get; } = new()
    {
        Kind = EmulatorKind.AndroidStudio,
        Id = "androidstudio",
        EnglishName = "Android Studio Emulator",
        ChineseName = "Android Studio 模拟器",
        PlayerProcesses = [],
        PlayerProcessPrefix = "qemu-system-",
        // Console port 5554 + 2N, adb port one higher; adb lists it as emulator-<console port> by itself.
        PortForIndex = i => i >= 0 ? 5555 + 2 * i : null,
        ScanInstances = 1,
        HardwareVerified = true,
    };

    public static IReadOnlyList<EmulatorDefinition> All { get; } = [MuMu, LDPlayer, Nox, MEmu, BlueStacks, AndroidStudio];

    public static EmulatorDefinition Get(EmulatorKind kind) => All.First(d => d.Kind == kind);

    /// <summary>The definition whose player or VM process has this executable name (without ".exe"), or null.</summary>
    public static EmulatorDefinition? ForProcess(string processName) =>
        All.FirstOrDefault(d => d.IsPlayerProcess(processName) || d.IsVmProcess(processName));

    /// <summary>True for any player or VM process name of a known emulator.</summary>
    public static bool IsEmulatorProcess(string processName) => ForProcess(processName) is not null;

    /// <summary>"kind:instance" identity of an instance (lower-case kind, instance as is).</summary>
    public static string KeyOf(EmulatorKind kind, string instanceKey) =>
        string.Create(CultureInfo.InvariantCulture, $"{kind.ToString().ToLowerInvariant()}:{instanceKey}");

    /// <summary>Parses a key produced by <see cref="KeyOf"/>.</summary>
    public static bool TryParseKey(string? key, out EmulatorKind kind, out string instanceKey)
    {
        kind = default;
        instanceKey = "";
        var colon = key?.IndexOf(':', StringComparison.Ordinal) ?? -1;
        if (colon <= 0 || colon == key!.Length - 1 || !Enum.TryParse(key.AsSpan(0, colon), ignoreCase: true, out kind) || !Enum.IsDefined(kind))
        {
            return false;
        }

        instanceKey = key[(colon + 1)..];
        return true;
    }
}
