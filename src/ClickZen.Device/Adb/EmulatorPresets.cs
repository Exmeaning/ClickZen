using ClickZen.Core.Emulators;

namespace ClickZen.Device.Adb;

/// <summary>A well-known Android emulator and the localhost adb ports it listens on.</summary>
/// <param name="Id">Stable identifier (usable as a localization key).</param>
/// <param name="Name">Default display name.</param>
/// <param name="Ports">adb TCP ports on 127.0.0.1, first instance first.</param>
public sealed record EmulatorPreset(string Id, string Name, IReadOnlyList<int> Ports);

/// <summary>
/// Port table used by <see cref="AdbService.ScanEmulatorsAsync(CancellationToken)"/> as a fallback when process
/// detection cannot tell the exact port. Derived from <see cref="EmulatorCatalog"/> (the port formulas of the first
/// few instances) plus the legacy ports of older emulator versions.
/// </summary>
public static class EmulatorPresets
{
    public static IReadOnlyList<EmulatorPreset> All { get; } = Build();

    /// <summary>Every distinct port across <see cref="All"/>, in table order.</summary>
    public static IReadOnlyList<int> AllPorts { get; } = All.SelectMany(p => p.Ports).Distinct().ToArray();

    /// <summary>Presets that use <paramref name="port"/> (several emulators share 5555).</summary>
    public static IReadOnlyList<EmulatorPreset> ForPort(int port) =>
        All.Where(p => p.Ports.Contains(port)).ToArray();

    private static EmulatorPreset[] Build()
    {
        var list = new List<EmulatorPreset>();
        foreach (var d in EmulatorCatalog.All)
        {
            list.Add(new EmulatorPreset(d.Id, d.ChineseName, d.ScanPorts));
            if (d.Kind == EmulatorKind.MuMu)
            {
                // MuMu 6 / MuMu X and some MuMu 12 main instances also answer on 7555.
                list.Add(new EmulatorPreset("mumu", "MuMu 模拟器（旧版）", [7555]));
            }
        }

        // 安卓模拟器大师 (no process detection; port scan only).
        list.Add(new EmulatorPreset("androidemulatormaster", "安卓模拟器大师", [54001]));
        return [.. list];
    }
}
