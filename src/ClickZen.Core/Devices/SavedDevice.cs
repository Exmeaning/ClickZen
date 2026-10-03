namespace ClickZen.Core.Devices;

/// <summary>A wireless device the user chose to remember (persisted in devices.json).</summary>
public sealed class SavedDevice
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 5555;
    public bool AutoConnect { get; set; } = true;

    /// <summary>"host:port" as used by <c>adb connect</c> and as the device serial once connected.</summary>
    public string Address => $"{Host}:{Port}";
}

/// <summary>Root document of %LocalAppData%\ClickZen\devices.json.</summary>
public sealed class DevicesDocument
{
    public int FormatVersion { get; set; } = 1;
    public List<SavedDevice> Saved { get; set; } = [];
}
