using System.Globalization;
using System.Text;

namespace ClickZen.Device.Scrcpy.Protocol;

/// <summary>Options for one scrcpy-server session.</summary>
public sealed record ScrcpyServerOptions
{
    /// <summary>31-bit random id; identifies the abstract socket <c>scrcpy_%08x</c>.</summary>
    public required int Scid { get; init; }

    public bool Video { get; init; } = true;
    public bool Control { get; init; } = true;

    /// <summary>true: PC connects to the device (adb forward); false: device connects to PC (adb reverse).</summary>
    public bool TunnelForward { get; init; }

    /// <summary>Longest edge of the video, 0 = native.</summary>
    public int MaxSize { get; init; } = 1280;

    public int VideoBitRate { get; init; } = 8_000_000;
    public int MaxFps { get; init; } = 60;
    public bool StayAwake { get; init; } = true;
    public bool ShowTouches { get; init; }
    public bool PowerOffOnClose { get; init; }
    public string LogLevel { get; init; } = "info";

    public string SocketName => "scrcpy_" + Scid.ToString("x8", CultureInfo.InvariantCulture);

    public static int NewScid() => Random.Shared.Next(1, int.MaxValue);

    /// <summary>
    /// Command line run on the device via <c>adb shell</c>:
    /// <c>CLASSPATH=... app_process / com.genymobile.scrcpy.Server 4.1 key=value...</c>.
    /// Audio is always disabled; the codec is H.264 for maximum decoder compatibility.
    /// </summary>
    public string BuildCommand()
    {
        var sb = new StringBuilder();
        sb.Append("CLASSPATH=").Append(ScrcpyServerInfo.DevicePath)
          .Append(" app_process / ").Append(ScrcpyServerInfo.MainClass)
          .Append(' ').Append(ScrcpyServerInfo.Version);

        void P(string key, object value) =>
            sb.Append(' ').Append(key).Append('=').Append(Convert.ToString(value, CultureInfo.InvariantCulture)!.ToLowerInvariant());

        P("scid", Scid.ToString("x8", CultureInfo.InvariantCulture));
        P("log_level", LogLevel);
        P("video", Video);
        P("audio", false);
        P("control", Control);
        P("tunnel_forward", TunnelForward);
        if (Video)
        {
            P("video_codec", "h264");
            P("max_size", MaxSize);
            P("video_bit_rate", VideoBitRate);
            P("max_fps", MaxFps);
        }

        P("stay_awake", StayAwake);
        P("show_touches", ShowTouches);
        P("power_off_on_close", PowerOffOnClose);
        P("clipboard_autosync", false);
        P("cleanup", true);
        return sb.ToString();
    }
}
