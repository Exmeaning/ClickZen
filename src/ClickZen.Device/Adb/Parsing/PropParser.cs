using System.Globalization;

namespace ClickZen.Device.Adb.Parsing;

/// <summary>Device build properties used to describe a device.</summary>
public sealed record DeviceProps(string Brand, string Model, string AndroidVersion, int SdkLevel);

/// <summary>Parses the property dump produced by <see cref="Command"/>.</summary>
public static class PropParser
{
    internal const string BrandKey = "ro.product.brand";
    internal const string ManufacturerKey = "ro.product.manufacturer";
    internal const string ModelKey = "ro.product.model";
    internal const string ReleaseKey = "ro.build.version.release";
    internal const string SdkKey = "ro.build.version.sdk";

    /// <summary>One shell round-trip that prints <c>key=value</c> for every property we need.</summary>
    public const string Command =
        "for p in " + BrandKey + " " + ManufacturerKey + " " + ModelKey + " " + ReleaseKey + " " + SdkKey
        + "; do echo \"$p=$(getprop $p)\"; done";

    /// <summary>Parses <c>key=value</c> lines; later duplicates win, blank keys are ignored.</summary>
    public static IReadOnlyDictionary<string, string> ParseKeyValues(string? output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(output))
        {
            return result;
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            if (key.Length > 0)
            {
                result[key] = line[(eq + 1)..].Trim();
            }
        }

        return result;
    }

    /// <summary>Parses the output of <see cref="Command"/>.</summary>
    public static DeviceProps Parse(string? output)
    {
        var kv = ParseKeyValues(output);
        var brand = Get(kv, BrandKey);
        if (brand.Length == 0)
        {
            brand = Get(kv, ManufacturerKey);
        }

        _ = int.TryParse(Get(kv, SdkKey), NumberStyles.None, CultureInfo.InvariantCulture, out var sdk);
        return new DeviceProps(PrettifyBrand(brand), Get(kv, ModelKey), Get(kv, ReleaseKey), sdk);
    }

    /// <summary>"google" → "Google"; mixed-case brands ("OnePlus", "HUAWEI") are kept as-is.</summary>
    public static string PrettifyBrand(string brand)
    {
        if (brand.Length == 0 || brand.Any(char.IsUpper))
        {
            return brand;
        }

        return char.ToUpperInvariant(brand[0]) + brand[1..];
    }

    private static string Get(IReadOnlyDictionary<string, string> kv, string key) =>
        kv.TryGetValue(key, out var v) ? v : "";
}
