using System.Globalization;
using System.Text.RegularExpressions;
using ClickZen.Core.Geometry;
using ClickZen.Device.Adb;

namespace ClickZen.Device.Input;

/// <summary>Supplies the current display rotation of a device (Surface.ROTATION_*).</summary>
public interface IDisplayRotationResolver
{
    Task<DisplayRotation> GetRotationAsync(string serial, CancellationToken ct);
}

/// <summary>A resolver that always returns the same rotation (tests, or when the caller already knows it).</summary>
public sealed class FixedRotationResolver(DisplayRotation rotation) : IDisplayRotationResolver
{
    public static FixedRotationResolver Natural { get; } = new(DisplayRotation.Rotation0);

    public Task<DisplayRotation> GetRotationAsync(string serial, CancellationToken ct) => Task.FromResult(rotation);
}

/// <summary>
/// Reads the rotation from <c>dumpsys input</c> (viewport / SurfaceOrientation of the default display), falling
/// back to <c>dumpsys window displays</c>. Unknown output means <see cref="DisplayRotation.Rotation0"/>.
/// </summary>
public sealed class DumpsysRotationResolver(AdbService adb) : IDisplayRotationResolver
{
    public const string InputCommand = "dumpsys input | grep -E 'Viewport|SurfaceOrientation'";
    public const string WindowCommand = "dumpsys window displays | grep -E 'mCurrentRotation|mRotation='";

    public async Task<DisplayRotation> GetRotationAsync(string serial, CancellationToken ct)
    {
        if (DisplayRotationParser.TryParse(await adb.ShellAsync(serial, InputCommand, ct), out var rotation))
        {
            return rotation;
        }

        return DisplayRotationParser.TryParse(await adb.ShellAsync(serial, WindowCommand, ct), out rotation)
            ? rotation
            : DisplayRotation.Rotation0;
    }
}

/// <summary>Extracts the default display's rotation from various <c>dumpsys</c> formats.</summary>
public static partial class DisplayRotationParser
{
    /// <summary>
    /// Recognises, in order of preference:
    /// <list type="bullet">
    /// <item><c>Viewport INTERNAL: displayId=0, … orientation=1, …</c> (dumpsys input, Android 10+)</item>
    /// <item><c>SurfaceOrientation: 1</c> (dumpsys input, touch mapper)</item>
    /// <item><c>mCurrentRotation=ROTATION_90</c> / <c>mRotation=1</c> (dumpsys window)</item>
    /// </list>
    /// </summary>
    public static bool TryParse(string? output, out DisplayRotation rotation)
    {
        rotation = DisplayRotation.Rotation0;
        if (string.IsNullOrEmpty(output))
        {
            return false;
        }

        // Prefer the viewport of display 0; inactive placeholder viewports have displayId=-1.
        foreach (Match m in ViewportRegex().Matches(output))
        {
            if (TryRotation(m.Groups["r"].Value, out rotation))
            {
                return true;
            }
        }

        foreach (var regex in new[] { SurfaceOrientationRegex(), CurrentRotationRegex(), RotationRegex() })
        {
            var m = regex.Match(output);
            if (m.Success && TryRotation(m.Groups["r"].Value, out rotation))
            {
                return true;
            }
        }

        rotation = DisplayRotation.Rotation0;
        return false;
    }

    private static bool TryRotation(string token, out DisplayRotation rotation)
    {
        rotation = DisplayRotation.Rotation0;
        if (token.StartsWith("ROTATION_", StringComparison.Ordinal))
        {
            token = token["ROTATION_".Length..];
            if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var degrees) || degrees % 90 != 0 || degrees > 270)
            {
                return false;
            }

            rotation = (DisplayRotation)(degrees / 90);
            return true;
        }

        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 3)
        {
            rotation = (DisplayRotation)n;
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"Viewport\s+\w+:\s*displayId=0\b[^\n]*?\borientation=(?<r>\d+)")]
    private static partial Regex ViewportRegex();

    [GeneratedRegex(@"SurfaceOrientation:\s*(?<r>\d+)")]
    private static partial Regex SurfaceOrientationRegex();

    [GeneratedRegex(@"mCurrentRotation=(?<r>ROTATION_\d+|\d+)")]
    private static partial Regex CurrentRotationRegex();

    [GeneratedRegex(@"\bmRotation=(?<r>ROTATION_\d+|\d+)")]
    private static partial Regex RotationRegex();
}
