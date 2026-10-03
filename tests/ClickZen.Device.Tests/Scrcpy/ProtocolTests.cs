using ClickZen.Device.Scrcpy.Protocol;

namespace ClickZen.Device.Tests.Scrcpy;

/// <summary>Golden bytes copied from scrcpy v4.1 app/tests/test_control_msg_serialize.c.</summary>
public sealed class ControlMessageWriterTests
{
    private readonly byte[] _buf = new byte[ControlMessageWriter.MaxMessageSize];

    [Fact]
    public void Inject_keycode_matches_upstream()
    {
        // AKEY_EVENT_ACTION_UP, AKEYCODE_ENTER (66), repeat 5, AMETA_SHIFT_ON|AMETA_SHIFT_LEFT_ON (0x41)
        var n = ControlMessageWriter.WriteKeycode(_buf, KeyAction.Up, 66, 5, 0x41);
        Assert.Equal(new byte[] { 0x00, 0x01, 0, 0, 0, 0x42, 0, 0, 0, 0x05, 0, 0, 0, 0x41 }, _buf[..n]);
    }

    [Fact]
    public void Inject_text_matches_upstream()
    {
        var n = ControlMessageWriter.WriteText(_buf, "hello, world!");
        Assert.Equal(18, n);
        Assert.Equal(new byte[] { 0x01, 0, 0, 0, 0x0d, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', (byte)',', (byte)' ', (byte)'w', (byte)'o', (byte)'r', (byte)'l', (byte)'d', (byte)'!' }, _buf[..n]);
    }

    [Fact]
    public void Inject_text_long_is_truncated_to_300()
    {
        var n = ControlMessageWriter.WriteText(_buf, new string('a', 400));
        Assert.Equal(5 + 300, n);
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x01, 0x2c }, _buf[..5]);
    }

    [Fact]
    public void Inject_text_truncation_respects_utf8_boundaries()
    {
        // 299 ASCII + "中" (3 bytes) would cross 300 → the CJK char must be dropped entirely.
        var n = ControlMessageWriter.WriteText(_buf, new string('a', 299) + "中");
        Assert.Equal(5 + 299, n);
    }

    [Fact]
    public void Inject_touch_event_matches_upstream()
    {
        var n = ControlMessageWriter.WriteTouch(_buf, MotionAction.Down, 0x1234567887654321, 100, 200, 1080, 1920, 1.0f, 1, 1);
        Assert.Equal(32, n);
        Assert.Equal(new byte[]
        {
            0x02,
            0x00,
            0x12, 0x34, 0x56, 0x78, 0x87, 0x65, 0x43, 0x21,
            0x00, 0x00, 0x00, 0x64, 0x00, 0x00, 0x00, 0xc8,
            0x04, 0x38, 0x07, 0x80,
            0xff, 0xff,
            0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x01,
        }, _buf[..n]);
    }

    [Fact]
    public void Inject_scroll_event_matches_upstream()
    {
        var n = ControlMessageWriter.WriteScroll(_buf, 260, 1026, 1080, 1920, 16, -16, 1);
        Assert.Equal(21, n);
        Assert.Equal(new byte[]
        {
            0x03,
            0x00, 0x00, 0x01, 0x04, 0x00, 0x00, 0x04, 0x02,
            0x04, 0x38, 0x07, 0x80,
            0x7F, 0xFF,
            0x80, 0x00,
            0x00, 0x00, 0x00, 0x01,
        }, _buf[..n]);
    }

    [Fact]
    public void Back_or_screen_on_matches_upstream()
    {
        var n = ControlMessageWriter.WriteBackOrScreenOn(_buf, KeyAction.Up);
        Assert.Equal(new byte[] { 0x04, 0x01 }, _buf[..n]);
    }

    [Fact]
    public void Set_clipboard_matches_upstream()
    {
        var n = ControlMessageWriter.WriteSetClipboard(_buf, 0x0102030405060708, true, "hello, world!");
        Assert.Equal(27, n);
        Assert.Equal(new byte[]
        {
            0x09,
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            1,
            0x00, 0x00, 0x00, 0x0d,
            (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', (byte)',', (byte)' ', (byte)'w', (byte)'o', (byte)'r', (byte)'l', (byte)'d', (byte)'!',
        }, _buf[..n]);
    }

    [Fact]
    public void Set_clipboard_long_fills_max_message_size()
    {
        var n = ControlMessageWriter.WriteSetClipboard(_buf, 1, true, new string('a', ControlMessageWriter.ClipboardTextMaxLength + 10));
        Assert.Equal(ControlMessageWriter.MaxMessageSize, n);
    }

    [Fact]
    public void Set_display_power_and_rotate_match_upstream()
    {
        Assert.Equal(new byte[] { 0x0a, 0x01 }, _buf[..ControlMessageWriter.WriteSetDisplayPower(_buf, true)]);
        Assert.Equal(new byte[] { 0x0b }, _buf[..ControlMessageWriter.WriteSimple(_buf, ControlMessageType.RotateDevice)]);
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.5f, 0x8000)]
    [InlineData(1f, 0xFFFF)]
    [InlineData(2f, 0xFFFF)]
    [InlineData(-1f, 0)]
    public void Pressure_fixed_point(float f, int expected) => Assert.Equal((ushort)expected, ControlMessageWriter.FloatToU16FixedPoint(f));

    [Fact]
    public void Simple_rejects_payload_messages() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ControlMessageWriter.WriteSimple(_buf, ControlMessageType.InjectTouchEvent));
}

public sealed class StreamProtocolTests
{
    [Fact]
    public void Session_packet_round_trip()
    {
        var buf = new byte[12];
        StreamProtocol.WriteSessionHeader(buf, 1080, 2400, clientResized: true);
        Assert.Equal(new byte[] { 0x80, 0, 0, 1, 0, 0, 0x04, 0x38, 0, 0, 0x09, 0x60 }, buf);

        var h = StreamProtocol.ParseHeader(buf);
        Assert.True(h.IsSession);
        Assert.True(h.ClientResized);
        Assert.Equal(1080, h.Width);
        Assert.Equal(2400, h.Height);
    }

    [Theory]
    [InlineData(123456789L, false, true, 5000)]
    [InlineData(0L, true, false, 30)]
    [InlineData((1L << 61) - 1, false, false, 1)]
    public void Media_header_round_trip(long pts, bool config, bool key, int size)
    {
        var buf = new byte[12];
        StreamProtocol.WriteMediaHeader(buf, pts, config, key, size);
        Assert.Equal(0, buf[0] & 0x80);

        var h = StreamProtocol.ParseHeader(buf);
        Assert.False(h.IsSession);
        Assert.Equal(config, h.IsConfig);
        Assert.Equal(key, h.IsKeyFrame);
        Assert.Equal(pts, h.Pts);
        Assert.Equal(size, h.PacketSize);
    }

    [Fact]
    public void Zero_size_media_packet_is_rejected()
    {
        var buf = new byte[12];
        StreamProtocol.WriteMediaHeader(buf, 1, false, false, 0);
        Assert.Throws<InvalidDataException>(() => StreamProtocol.ParseHeader(buf));
    }

    [Fact]
    public void Device_name_is_nul_terminated_utf8()
    {
        var field = new byte[64];
        "Pixel 7 Pro"u8.CopyTo(field);
        Assert.Equal("Pixel 7 Pro", StreamProtocol.ParseDeviceName(field));

        var full = Enumerable.Repeat((byte)'x', 64).ToArray();
        Assert.Equal(63, StreamProtocol.ParseDeviceName(full).Length);
    }

    [Fact]
    public void Codec_id_parses_h264() =>
        Assert.Equal(VideoCodecId.H264, StreamProtocol.ParseCodecId("h264"u8));
}

public sealed class ScrcpyServerOptionsTests
{
    [Fact]
    public void Command_line_has_version_scid_and_options()
    {
        var o = new ScrcpyServerOptions { Scid = 0x1234ABCD, MaxSize = 1280, VideoBitRate = 8_000_000, MaxFps = 60, TunnelForward = true };
        var cmd = o.BuildCommand();

        Assert.StartsWith("CLASSPATH=/data/local/tmp/clickzen-scrcpy-server.jar app_process / com.genymobile.scrcpy.Server 4.1 ", cmd, StringComparison.Ordinal);
        Assert.Contains(" scid=1234abcd", cmd, StringComparison.Ordinal);
        Assert.Contains(" audio=false", cmd, StringComparison.Ordinal);
        Assert.Contains(" tunnel_forward=true", cmd, StringComparison.Ordinal);
        Assert.Contains(" video_codec=h264", cmd, StringComparison.Ordinal);
        Assert.Contains(" max_size=1280", cmd, StringComparison.Ordinal);
        Assert.Contains(" cleanup=true", cmd, StringComparison.Ordinal);
        Assert.Equal("scrcpy_1234abcd", o.SocketName);
    }

    [Fact]
    public void Control_only_omits_video_options()
    {
        var cmd = new ScrcpyServerOptions { Scid = 1, Video = false }.BuildCommand();
        Assert.Contains(" video=false", cmd, StringComparison.Ordinal);
        Assert.DoesNotContain("max_size", cmd, StringComparison.Ordinal);
        Assert.Contains(" scid=00000001", cmd, StringComparison.Ordinal);
    }
}
