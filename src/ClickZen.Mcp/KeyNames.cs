using System.Globalization;

namespace ClickZen.Mcp;

/// <summary>Android key names accepted by <c>press_key</c> (case-insensitive) and their key codes.</summary>
public static class KeyNames
{
    private static readonly Dictionary<string, int> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["home"] = 3,
        ["back"] = 4,
        ["call"] = 5,
        ["endcall"] = 6,
        ["dpad_up"] = 19,
        ["up"] = 19,
        ["dpad_down"] = 20,
        ["down"] = 20,
        ["dpad_left"] = 21,
        ["left"] = 21,
        ["dpad_right"] = 22,
        ["right"] = 22,
        ["dpad_center"] = 23,
        ["center"] = 23,
        ["volume_up"] = 24,
        ["volume_down"] = 25,
        ["power"] = 26,
        ["camera"] = 27,
        ["tab"] = 61,
        ["space"] = 62,
        ["enter"] = 66,
        ["delete"] = 67,
        ["backspace"] = 67,
        ["del"] = 67,
        ["menu"] = 82,
        ["search"] = 84,
        ["media_play_pause"] = 85,
        ["media_stop"] = 86,
        ["media_next"] = 87,
        ["media_previous"] = 88,
        ["page_up"] = 92,
        ["page_down"] = 93,
        ["escape"] = 111,
        ["esc"] = 111,
        ["forward_del"] = 112,
        ["move_home"] = 122,
        ["move_end"] = 123,
        ["volume_mute"] = 164,
        ["app_switch"] = 187,
        ["recents"] = 187,
        ["brightness_down"] = 220,
        ["brightness_up"] = 221,
        ["sleep"] = 223,
        ["wakeup"] = 224,
        ["notifications"] = 83,
    };

    /// <summary>The documented names (one per code, for tool descriptions).</summary>
    public const string Documented = "home, back, recents (app_switch), enter, delete (backspace), tab, space, escape, menu, search, power, wakeup, sleep, volume_up, volume_down, volume_mute, dpad_up/down/left/right/center, page_up, page_down, move_home, move_end, media_play_pause, camera, notifications";

    /// <summary>Resolves a key name, "KEYCODE_X" constant or decimal key code.</summary>
    public static bool TryResolve(string? key, out int code)
    {
        code = 0;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var k = key.Trim();
        if (int.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
        {
            return code is > 0 and < 400;
        }

        if (k.StartsWith("KEYCODE_", StringComparison.OrdinalIgnoreCase))
        {
            k = k["KEYCODE_".Length..];
        }

        return Map.TryGetValue(k.Replace('-', '_').Replace(' ', '_'), out code);
    }
}
