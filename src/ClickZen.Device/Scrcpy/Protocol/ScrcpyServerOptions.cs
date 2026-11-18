using System.Globalization;
using System.Text;

namespace ClickZen.Device.Scrcpy.Protocol;

/// <summary>Options for one scrcpy-server session.</summary>
public sealed record ScrcpyServerOptions
{
    /// <summary>Server default video bit rate (scrcpy-server <c>Options.java</c>); omitted from the command line when unchanged.</summary>
    private const int DefaultVideoBitRate = 8_000_000;

    /// <summary>31-bit random id; identifies the abstract socket <c>scrcpy_%08x</c>.</summary>
    public required int Scid { get; init; }

    public bool Video { get; init; } = true;
    public bool Control { get; init; } = true;

    /// <summary>true: PC connects to the device (adb forward); false: device connects to PC (adb reverse).</summary>
    public bool TunnelForward { get; init; }

    /// <summary>Android display id to mirror and inject into (0 = primary display; &gt; 0 = secondary/virtual display, e.g. MuMu game tab).</summary>
    public int DisplayId { get; init; }

    /// <summary>Longest edge of the video, 0 = native.</summary>
    public int MaxSize { get; init; } = 1280;

    public int VideoBitRate { get; init; } = DefaultVideoBitRate;
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
    /// Only values that differ from the server defaults are sent (mirroring upstream
    /// scrcpy's <c>server.c</c>): Samsung's <c>ACodec::reconfigEncoder4OtherApps</c> copies
    /// <c>/proc/&lt;pid&gt;/cmdline</c> into a ~255-byte stack buffer
    /// (Genymobile/scrcpy#7169), so the <c>app_process</c> command line must stay well
    /// under 255 bytes or the server aborts with a stack-protector error on Samsung devices.
    /// Server defaults are from scrcpy-server <c>Options.java</c> (v4.1).
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
        if (!string.Equals(LogLevel, "debug", StringComparison.OrdinalIgnoreCase))
        {
            P("log_level", LogLevel);
        }

        if (!Video)
        {
            P("video", false);
        }

        // Audio is always disabled; the server default is enabled.
        P("audio", false);
        if (TunnelForward)
        {
            P("tunnel_forward", true);
        }

        if (!Control)
        {
            P("control", false);
        }

        if (DisplayId > 0)
        {
            P("display_id", DisplayId);
        }

        if (Video)
        {
            // The codec is always H.264, which is the server default.
            if (MaxSize != 0)
            {
                P("max_size", MaxSize);
            }

            if (VideoBitRate != DefaultVideoBitRate)
            {
                P("video_bit_rate", VideoBitRate);
            }

            if (MaxFps != 0)
            {
                P("max_fps", MaxFps);
            }
        }

        if (StayAwake)
        {
            P("stay_awake", true);
        }

        if (ShowTouches)
        {
            P("show_touches", true);
        }

        if (PowerOffOnClose)
        {
            P("power_off_on_close", true);
        }

        // Clipboard autosync is always disabled; the server default is enabled.
        P("clipboard_autosync", false);
        // Cleanup is always enabled, which is the server default.
        return sb.ToString();
    }
}
