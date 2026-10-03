namespace ClickZen.Device.Scrcpy;

/// <summary>
/// The scrcpy-server bundled with ClickZen. The client version string passed to the server
/// must match exactly, and the wire protocol is version-specific – bump all three together
/// with build/ThirdParty.targets.
/// </summary>
public static class ScrcpyServerInfo
{
    public const string Version = "4.1";

    /// <summary>File name inside the app's ThirdParty folder.</summary>
    public const string BundledFileName = "scrcpy-server";

    /// <summary>Where the server jar is pushed on the device.</summary>
    public const string DevicePath = "/data/local/tmp/clickzen-scrcpy-server.jar";

    public const string MainClass = "com.genymobile.scrcpy.Server";
}
