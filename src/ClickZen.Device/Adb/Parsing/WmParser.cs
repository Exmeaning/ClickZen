using System.Globalization;
using System.Text.RegularExpressions;
using ClickZen.Core.Geometry;

namespace ClickZen.Device.Adb.Parsing;

/// <summary>Result of <c>wm size</c>. <see cref="Effective"/> is what the device currently renders at.</summary>
public readonly record struct WmSize(SizeI Physical, SizeI Override)
{
    public bool HasOverride => !Override.IsEmpty;

    /// <summary>Override size if one is set (that is the resolution actually in effect), otherwise the physical size.</summary>
    public SizeI Effective => HasOverride ? Override : Physical;

    public bool IsEmpty => Effective.IsEmpty;
}

/// <summary>Result of <c>wm density</c>. <see cref="Effective"/> is the density currently in effect.</summary>
public readonly record struct WmDensity(int Physical, int Override)
{
    public bool HasOverride => Override > 0;

    public int Effective => HasOverride ? Override : Physical;
}

/// <summary>Parses the output of <c>wm size</c> / <c>wm density</c>.</summary>
public static partial class WmParser
{
    /// <summary>
    /// Parses <c>wm size</c> output, e.g.
    /// <code>
    /// Physical size: 1080x2400
    /// Override size: 720x1600
    /// </code>
    /// Unknown or error output yields an empty result.
    /// </summary>
    public static WmSize ParseSize(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return default;
        }

        SizeI physical = default, overridden = default;
        foreach (Match m in SizeRegex().Matches(output))
        {
            if (!int.TryParse(m.Groups["w"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var w)
                || !int.TryParse(m.Groups["h"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var h)
                || w <= 0 || h <= 0)
            {
                continue;
            }

            var size = new SizeI(w, h);
            if (m.Groups["kind"].Value.Equals("Override", StringComparison.OrdinalIgnoreCase))
            {
                overridden = size;
            }
            else if (physical.IsEmpty)
            {
                // Some multi-display builds print several "Physical size" lines; the first is the default display.
                physical = size;
            }
        }

        return new WmSize(physical, overridden);
    }

    /// <summary>
    /// Parses <c>wm density</c> output, e.g.
    /// <code>
    /// Physical density: 440
    /// Override density: 380
    /// </code>
    /// </summary>
    public static WmDensity ParseDensity(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return default;
        }

        int physical = 0, overridden = 0;
        foreach (Match m in DensityRegex().Matches(output))
        {
            if (!int.TryParse(m.Groups["d"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d <= 0)
            {
                continue;
            }

            if (m.Groups["kind"].Value.Equals("Override", StringComparison.OrdinalIgnoreCase))
            {
                overridden = d;
            }
            else if (physical == 0)
            {
                physical = d;
            }
        }

        return new WmDensity(physical, overridden);
    }

    [GeneratedRegex(@"(?<kind>Physical|Override)\s+size:\s*(?<w>\d+)\s*x\s*(?<h>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"(?<kind>Physical|Override)\s+density:\s*(?<d>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DensityRegex();
}
