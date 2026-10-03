namespace ClickZen.Device.Adb;

/// <summary>A well-known Android emulator and the localhost adb ports it listens on.</summary>
/// <param name="Id">Stable identifier (usable as a localization key).</param>
/// <param name="Name">Default display name.</param>
/// <param name="Ports">adb TCP ports on 127.0.0.1, first instance first.</param>
public sealed record EmulatorPreset(string Id, string Name, IReadOnlyList<int> Ports);

/// <summary>Port table used by <see cref="AdbService.ScanEmulatorsAsync"/>.</summary>
public static class EmulatorPresets
{
    public static IReadOnlyList<EmulatorPreset> All { get; } =
    [
        new("mumu12", "MuMu 模拟器 12", [16384, 16416, 16448, 16480]),
        new("mumu", "MuMu 模拟器（旧版）", [7555]),
        new("ldplayer", "雷电模拟器", [5555, 5557, 5559, 5561]),
        new("nox", "夜神模拟器", [62001, 62025, 62026]),
        new("memu", "逍遥模拟器", [21503]),
        new("bluestacks", "BlueStacks 蓝叠", [5555]),
        new("androidemulatormaster", "安卓模拟器大师", [54001]),
        new("androidstudio", "Android Studio 模拟器", [5555]),
    ];

    /// <summary>Every distinct port across <see cref="All"/>, in table order.</summary>
    public static IReadOnlyList<int> AllPorts { get; } = All.SelectMany(p => p.Ports).Distinct().ToArray();

    /// <summary>Presets that use <paramref name="port"/> (several emulators share 5555).</summary>
    public static IReadOnlyList<EmulatorPreset> ForPort(int port) =>
        All.Where(p => p.Ports.Contains(port)).ToArray();
}
