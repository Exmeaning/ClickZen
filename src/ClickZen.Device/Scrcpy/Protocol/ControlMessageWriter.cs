using System.Buffers.Binary;
using System.Text;

namespace ClickZen.Device.Scrcpy.Protocol;

/// <summary>Control message type ids (scrcpy 4.1, <c>app/src/control_msg.h</c>).</summary>
public enum ControlMessageType : byte
{
    InjectKeycode = 0,
    InjectText = 1,
    InjectTouchEvent = 2,
    InjectScrollEvent = 3,
    BackOrScreenOn = 4,
    ExpandNotificationPanel = 5,
    ExpandSettingsPanel = 6,
    CollapsePanels = 7,
    GetClipboard = 8,
    SetClipboard = 9,
    SetDisplayPower = 10,
    RotateDevice = 11,
}

/// <summary>Android <c>KeyEvent</c> actions.</summary>
public enum KeyAction : byte
{
    Down = 0,
    Up = 1,
}

/// <summary>Android <c>MotionEvent</c> actions used for touch injection.</summary>
public enum MotionAction : byte
{
    Down = 0,
    Up = 1,
    Move = 2,
    Cancel = 3,
}

/// <summary>
/// Serializes scrcpy control messages (client → device). All integers are big-endian.
/// Byte layouts are verified against scrcpy's own unit tests (test_control_msg_serialize.c).
/// </summary>
public static class ControlMessageWriter
{
    public const int MaxMessageSize = 1 << 18;
    public const int InjectTextMaxLength = 300;
    public const int ClipboardTextMaxLength = MaxMessageSize - 14;

    /// <summary>Pointer id scrcpy uses for a generic finger (we use 0..9 for explicit fingers).</summary>
    public const long PointerIdGenericFinger = -2;

    public const int ButtonPrimary = 1;

    /// <summary>INJECT_KEYCODE – 14 bytes.</summary>
    public static int WriteKeycode(Span<byte> buf, KeyAction action, int keycode, int repeat = 0, int metaState = 0)
    {
        buf[0] = (byte)ControlMessageType.InjectKeycode;
        buf[1] = (byte)action;
        BinaryPrimitives.WriteInt32BigEndian(buf[2..], keycode);
        BinaryPrimitives.WriteInt32BigEndian(buf[6..], repeat);
        BinaryPrimitives.WriteInt32BigEndian(buf[10..], metaState);
        return 14;
    }

    /// <summary>INJECT_TEXT – 5 + n bytes, text truncated to 300 UTF-8 bytes on a code point boundary.</summary>
    public static int WriteText(Span<byte> buf, string text)
    {
        buf[0] = (byte)ControlMessageType.InjectText;
        return 1 + WriteString(buf[1..], text, InjectTextMaxLength);
    }

    /// <summary>INJECT_TOUCH_EVENT – 32 bytes.</summary>
    /// <param name="screenWidth">Must equal the current video width, otherwise the server drops the event.</param>
    public static int WriteTouch(Span<byte> buf, MotionAction action, long pointerId, int x, int y,
        int screenWidth, int screenHeight, float pressure, int actionButton = 0, int buttons = 0)
    {
        buf[0] = (byte)ControlMessageType.InjectTouchEvent;
        buf[1] = (byte)action;
        BinaryPrimitives.WriteInt64BigEndian(buf[2..], pointerId);
        WritePosition(buf[10..], x, y, screenWidth, screenHeight);
        BinaryPrimitives.WriteUInt16BigEndian(buf[22..], FloatToU16FixedPoint(pressure));
        BinaryPrimitives.WriteInt32BigEndian(buf[24..], actionButton);
        BinaryPrimitives.WriteInt32BigEndian(buf[28..], buttons);
        return 32;
    }

    /// <summary>INJECT_SCROLL_EVENT – 21 bytes. Scroll amounts are clamped to [-16, 16].</summary>
    public static int WriteScroll(Span<byte> buf, int x, int y, int screenWidth, int screenHeight, float hscroll, float vscroll, int buttons = 0)
    {
        buf[0] = (byte)ControlMessageType.InjectScrollEvent;
        WritePosition(buf[1..], x, y, screenWidth, screenHeight);
        BinaryPrimitives.WriteInt16BigEndian(buf[13..], FloatToI16FixedPoint(Math.Clamp(hscroll / 16f, -1f, 1f)));
        BinaryPrimitives.WriteInt16BigEndian(buf[15..], FloatToI16FixedPoint(Math.Clamp(vscroll / 16f, -1f, 1f)));
        BinaryPrimitives.WriteInt32BigEndian(buf[17..], buttons);
        return 21;
    }

    /// <summary>BACK_OR_SCREEN_ON – 2 bytes.</summary>
    public static int WriteBackOrScreenOn(Span<byte> buf, KeyAction action)
    {
        buf[0] = (byte)ControlMessageType.BackOrScreenOn;
        buf[1] = (byte)action;
        return 2;
    }

    /// <summary>SET_CLIPBOARD – 14 + n bytes.</summary>
    public static int WriteSetClipboard(Span<byte> buf, long sequence, bool paste, string text)
    {
        buf[0] = (byte)ControlMessageType.SetClipboard;
        BinaryPrimitives.WriteInt64BigEndian(buf[1..], sequence);
        buf[9] = paste ? (byte)1 : (byte)0;
        return 10 + WriteString(buf[10..], text, ClipboardTextMaxLength);
    }

    /// <summary>SET_DISPLAY_POWER – 2 bytes.</summary>
    public static int WriteSetDisplayPower(Span<byte> buf, bool on)
    {
        buf[0] = (byte)ControlMessageType.SetDisplayPower;
        buf[1] = on ? (byte)1 : (byte)0;
        return 2;
    }

    /// <summary>Single-byte messages: panels, rotate.</summary>
    public static int WriteSimple(Span<byte> buf, ControlMessageType type)
    {
        if (type is not (ControlMessageType.ExpandNotificationPanel or ControlMessageType.ExpandSettingsPanel
            or ControlMessageType.CollapsePanels or ControlMessageType.RotateDevice))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Not a payload-less message.");
        }

        buf[0] = (byte)type;
        return 1;
    }

    private static void WritePosition(Span<byte> buf, int x, int y, int w, int h)
    {
        BinaryPrimitives.WriteInt32BigEndian(buf, x);
        BinaryPrimitives.WriteInt32BigEndian(buf[4..], y);
        BinaryPrimitives.WriteUInt16BigEndian(buf[8..], (ushort)Math.Clamp(w, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(buf[10..], (ushort)Math.Clamp(h, 0, ushort.MaxValue));
    }

    /// <summary>Writes u32 length + UTF-8 bytes; returns total bytes written.</summary>
    private static int WriteString(Span<byte> buf, string text, int maxLength)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? "");
        var len = Utf8TruncationIndex(bytes, maxLength);
        BinaryPrimitives.WriteInt32BigEndian(buf, len);
        bytes.AsSpan(0, len).CopyTo(buf[4..]);
        return 4 + len;
    }

    /// <summary>Same rule as scrcpy's sc_str_utf8_truncation_index: never cut inside a code point.</summary>
    internal static int Utf8TruncationIndex(ReadOnlySpan<byte> utf8, int maxLength)
    {
        if (utf8.Length <= maxLength)
        {
            return utf8.Length;
        }

        var len = maxLength;
        while (len > 0 && (utf8[len] & 0x80) != 0 && (utf8[len] & 0xC0) != 0xC0)
        {
            len--;
        }

        return len;
    }

    internal static ushort FloatToU16FixedPoint(float f)
    {
        f = Math.Clamp(f, 0f, 1f);
        var u = (uint)(f * 65536f);
        return u >= 0xFFFF ? (ushort)0xFFFF : (ushort)u;
    }

    internal static short FloatToI16FixedPoint(float f)
    {
        f = Math.Clamp(f, -1f, 1f);
        var i = (int)(f * 32768f);
        return i >= 0x7FFF ? (short)0x7FFF : (short)i;
    }
}
