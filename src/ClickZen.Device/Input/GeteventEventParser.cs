using System.Globalization;
using System.Text.RegularExpressions;

namespace ClickZen.Device.Input;

/// <summary>One kernel input event as printed by <c>getevent</c>.</summary>
/// <param name="TimestampUs">Event time in microseconds (kernel clock) when printed with <c>-t</c>, otherwise null.</param>
/// <param name="DevicePath">Source node (<c>/dev/input/eventN</c>) when getevent reads several devices, otherwise null.</param>
public readonly record struct GeteventEvent(long? TimestampUs, string? DevicePath, int Type, int Code, int Value)
{
    public const int EvSyn = 0x00;
    public const int EvKey = 0x01;
    public const int EvAbs = 0x03;

    public const int SynReport = 0x00;
    public const int SynMtReport = 0x02;
    public const int SynDropped = 0x03;
}

/// <summary>
/// Parses event lines of <c>getevent</c> / <c>getevent -t</c> / <c>getevent -l</c>, e.g.
/// <code>
/// [     789.870862] 0003 0035 00001f40
/// [     789.870862] /dev/input/event2: 0003 0039 ffffffff
/// /dev/input/event2: EV_ABS       ABS_MT_POSITION_X    00001f40
/// </code>
/// Device-description lines (<c>add device</c>, <c>name:</c>…) and unknown labels are rejected.
/// </summary>
public static partial class GeteventEventParser
{
    private static readonly Dictionary<string, int> TypeLabels = new(StringComparer.Ordinal)
    {
        ["EV_SYN"] = GeteventEvent.EvSyn,
        ["EV_KEY"] = GeteventEvent.EvKey,
        ["EV_ABS"] = GeteventEvent.EvAbs,
    };

    private static readonly Dictionary<string, int> CodeLabels = new(StringComparer.Ordinal)
    {
        ["SYN_REPORT"] = GeteventEvent.SynReport,
        ["SYN_MT_REPORT"] = GeteventEvent.SynMtReport,
        ["SYN_DROPPED"] = GeteventEvent.SynDropped,
        ["BTN_TOUCH"] = InputDeviceInfo.BtnTouch,
        ["ABS_MT_SLOT"] = InputDeviceInfo.AbsMtSlot,
        ["ABS_MT_TOUCH_MAJOR"] = InputDeviceInfo.AbsMtTouchMajor,
        ["ABS_MT_POSITION_X"] = InputDeviceInfo.AbsMtPositionX,
        ["ABS_MT_POSITION_Y"] = InputDeviceInfo.AbsMtPositionY,
        ["ABS_MT_TRACKING_ID"] = InputDeviceInfo.AbsMtTrackingId,
        ["ABS_MT_PRESSURE"] = InputDeviceInfo.AbsMtPressure,
    };

    public static bool TryParse(string? line, out GeteventEvent ev)
    {
        ev = default;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var m = EventRegex().Match(line);
        if (!m.Success)
        {
            return false;
        }

        long? ts = null;
        if (m.Groups["sec"].Success)
        {
            var sec = long.Parse(m.Groups["sec"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
            ts = sec * 1_000_000 + ParseMicros(m.Groups["frac"].Value);
        }

        var dev = m.Groups["dev"].Success ? m.Groups["dev"].Value : null;
        if (!TryCode(m.Groups["type"].Value, TypeLabels, out var type)
            || !TryCode(m.Groups["code"].Value, CodeLabels, out var code)
            || !TryValue(m.Groups["value"].Value, out var value))
        {
            return false;
        }

        ev = new GeteventEvent(ts, dev, type, code, value);
        return true;
    }

    private static long ParseMicros(string frac)
    {
        // getevent prints %06ld; be lenient about other widths.
        var digits = frac.Length >= 6 ? frac[..6] : frac.PadRight(6, '0');
        return long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static bool TryCode(string token, Dictionary<string, int> labels, out int code)
    {
        if (labels.TryGetValue(token, out code))
        {
            return true;
        }

        // Numeric output is always zero-padded to 4 hex digits; -l prints unknown codes the same way.
        return token.Length == 4 && int.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code);
    }

    private static bool TryValue(string token, out int value)
    {
        switch (token)
        {
            case "DOWN":
                value = 1;
                return true;
            case "UP":
                value = 0;
                return true;
        }

        // %08x of a signed 32-bit value: ffffffff is -1.
        if (token.Length == 8 && uint.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u))
        {
            value = unchecked((int)u);
            return true;
        }

        value = 0;
        return false;
    }

    // Timestamp: "[   12.345678] " (current toolbox) or "12-345678: " (very old toolbox).
    // Type/code: 4 hex digits or an EV_/SYN_/ABS_/BTN_/KEY_ label; value: 8 hex digits or DOWN/UP (-l).
    [GeneratedRegex(@"^\s*(?:\[\s*(?<sec>\d+)\.(?<frac>\d+)\s*\]\s*|(?<sec>\d+)-(?<frac>\d+):\s+)?(?:(?<dev>/dev/\S+):\s*)?(?<type>[0-9a-fA-F]{4}|EV_[A-Z_]+)\s+(?<code>[0-9a-fA-F]{4}|[A-Z][A-Z0-9_]+)\s+(?<value>[0-9a-fA-F]{8}|DOWN|UP)\s*$")]
    private static partial Regex EventRegex();
}
