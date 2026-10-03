using ClickZen.Core.Geometry;

namespace ClickZen.Core.Devices;

/// <summary>How the device is reached.</summary>
public enum ConnectionKind
{
    Usb,
    Wireless,
    /// <summary>Emulator reached over adb TCP on localhost (MuMu, LDPlayer, Nox…).</summary>
    Emulator,
}

/// <summary>adb-reported state of a device.</summary>
public enum DeviceAdbState
{
    Online,
    Offline,
    Unauthorized,
    /// <summary>Anything else adb reports (bootloader, recovery, sideload…).</summary>
    Other,
}

/// <summary>
/// A device as seen by adb, enriched with properties. Immutable snapshot – a new instance is
/// produced whenever something changes.
/// </summary>
public sealed record DeviceInfo
{
    public required string Serial { get; init; }
    public DeviceAdbState State { get; init; }
    public ConnectionKind Kind { get; init; }
    public string Brand { get; init; } = "";
    public string Model { get; init; } = "";
    public string AndroidVersion { get; init; } = "";
    public int SdkLevel { get; init; }
    /// <summary>Physical (natural orientation) display size from <c>wm size</c>; empty if unknown.</summary>
    public SizeI PhysicalSize { get; init; }
    /// <summary>Display density in dpi from <c>wm density</c>; 0 if unknown.</summary>
    public int Density { get; init; }

    /// <summary>Human readable name, e.g. "Xiaomi 2201123C (Android 13)".</summary>
    public string DisplayName
    {
        get
        {
            var name = $"{Brand} {Model}".Trim();
            if (name.Length == 0)
            {
                name = Serial;
            }

            return AndroidVersion.Length > 0 ? $"{name} (Android {AndroidVersion})" : name;
        }
    }

    /// <summary>Pixels per dp (density / 160); 1 if unknown.</summary>
    public double DpScale => Density > 0 ? Density / 160.0 : 1.0;
}
