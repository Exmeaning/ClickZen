using System.Globalization;
using System.Text.RegularExpressions;

namespace ClickZen.Device.Input;

/// <summary>Range of one absolute axis as reported by <c>getevent -p</c>.</summary>
public readonly record struct AbsAxis(int Code, int Min, int Max)
{
    public int Span => Max - Min + 1;
}

/// <summary>One input device block from <c>getevent -p</c>.</summary>
public sealed record InputDeviceInfo(string Path, string Name, IReadOnlyDictionary<int, AbsAxis> Axes, IReadOnlySet<int> Keys, bool IsDirect)
{
    public const int AbsMtSlot = 0x2f;
    public const int AbsMtPositionX = 0x35;
    public const int AbsMtPositionY = 0x36;
    public const int AbsMtTrackingId = 0x39;
    public const int AbsMtPressure = 0x3a;
    public const int AbsMtTouchMajor = 0x30;
    public const int BtnTouch = 0x14a;

    public bool IsMultiTouch => Axes.ContainsKey(AbsMtPositionX) && Axes.ContainsKey(AbsMtPositionY);

    public bool HasSlots => Axes.ContainsKey(AbsMtSlot);

    public AbsAxis X => Axes[AbsMtPositionX];

    public AbsAxis Y => Axes[AbsMtPositionY];

    /// <summary>Highest slot index (number of fingers − 1), 0 when slots are not supported.</summary>
    public int MaxSlot => Axes.TryGetValue(AbsMtSlot, out var a) ? a.Max : 0;

    /// <summary>Event number from the path (/dev/input/eventN), int.MaxValue if unknown.</summary>
    public int EventNumber =>
        int.TryParse(Path.AsSpan(Path.LastIndexOf("event", StringComparison.Ordinal) + 5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;
}

/// <summary>
/// Parses <c>getevent -p</c> (numeric codes) into device blocks and picks the touchscreen.
/// Works line-by-line per device block – the 1.x regex looked for the path and the name on one
/// line, which never matches real output, and silently fell back to a hard-coded event5.
/// </summary>
public static partial class GeteventDeviceParser
{
    private static readonly string[] TouchNameHints = ["touch", "_ts", "-ts", "ts_", "fts", "goodix", "synaptics", "atmel", "himax", "novatek", "focal", "sec_touch", "ilitek", "elan"];

    public static IReadOnlyList<InputDeviceInfo> Parse(string output)
    {
        var devices = new List<InputDeviceInfo>();
        string? path = null;
        var name = "";
        var axes = new Dictionary<int, AbsAxis>();
        var keys = new HashSet<int>();
        var direct = false;
        string? section = null;
        var inProps = false;

        void Flush()
        {
            if (path is not null)
            {
                devices.Add(new InputDeviceInfo(path, name, new Dictionary<int, AbsAxis>(axes), new HashSet<int>(keys), direct));
            }

            path = null;
            name = "";
            axes.Clear();
            keys.Clear();
            direct = false;
            section = null;
            inProps = false;
        }

        foreach (var raw in output.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            var add = AddDeviceRegex().Match(line);
            if (add.Success)
            {
                Flush();
                path = add.Groups[1].Value;
                continue;
            }

            if (path is null)
            {
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.StartsWith("name:", StringComparison.Ordinal))
            {
                name = trimmed[5..].Trim().Trim('"');
                continue;
            }

            if (trimmed.StartsWith("input props:", StringComparison.Ordinal))
            {
                inProps = true;
                section = null;
                continue;
            }

            if (inProps)
            {
                if (trimmed.Contains("INPUT_PROP_DIRECT", StringComparison.Ordinal) || trimmed == "0001")
                {
                    direct = true;
                }

                continue;
            }

            var sec = SectionRegex().Match(trimmed);
            if (sec.Success)
            {
                section = sec.Groups[1].Value;
                trimmed = sec.Groups[2].Value.Trim();
            }

            switch (section)
            {
                case "ABS":
                    var abs = AbsRegex().Match(trimmed);
                    if (abs.Success)
                    {
                        var code = int.Parse(abs.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        axes[code] = new AbsAxis(code,
                            int.Parse(abs.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                            int.Parse(abs.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture));
                    }

                    break;
                case "KEY":
                    foreach (Match k in HexCodeRegex().Matches(trimmed))
                    {
                        keys.Add(int.Parse(k.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    }

                    break;
            }
        }

        Flush();
        return devices;
    }

    /// <summary>
    /// The most likely touchscreen: multi-touch devices only, then INPUT_PROP_DIRECT, then a
    /// touch-like name, then the lowest event number. Null when there is none.
    /// </summary>
    public static InputDeviceInfo? PickTouchscreen(IReadOnlyList<InputDeviceInfo> devices) =>
        devices.Where(d => d.IsMultiTouch)
            .OrderByDescending(d => d.IsDirect)
            .ThenByDescending(d => TouchNameHints.Any(h => d.Name.Contains(h, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(d => d.EventNumber)
            .FirstOrDefault();

    [GeneratedRegex(@"^add device \d+:\s*(\S+)")]
    private static partial Regex AddDeviceRegex();

    [GeneratedRegex(@"^([A-Z]{2,3})\s*\([0-9a-fA-F]{4}\):(.*)$")]
    private static partial Regex SectionRegex();

    [GeneratedRegex(@"^([0-9a-fA-F]{4})\s*:\s*value\s*-?\d+,\s*min\s*(-?\d+),\s*max\s*(-?\d+)")]
    private static partial Regex AbsRegex();

    [GeneratedRegex(@"\b[0-9a-fA-F]{4}\b")]
    private static partial Regex HexCodeRegex();
}
