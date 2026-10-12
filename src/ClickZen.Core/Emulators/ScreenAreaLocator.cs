using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;

namespace ClickZen.Core.Emulators;

/// <summary>How <see cref="ScreenAreaLocator"/> found the Android screen inside an emulator window.</summary>
public enum ScreenAreaMethod
{
    /// <summary>Nothing better was found: the whole client area (the user should check).</summary>
    ClientArea,

    /// <summary>The client area already has the device's aspect ratio.</summary>
    AspectMatch,

    /// <summary>The emulator's render child window.</summary>
    RenderWindow,

    /// <summary>The device's own screenshot was found in the window picture.</summary>
    ScreenshotMatch,

    /// <summary>The window picture was cut where the uniform title bar / tool bar / borders end.</summary>
    EdgeDetection,
}

/// <summary>Where the Android screen is inside an emulator window's client area.</summary>
/// <param name="Crop">Client-area rectangle (physical px), or null for the whole client area.</param>
/// <param name="Confidence">0–1; one-click binding asks the user to check below <see cref="ScreenAreaLocator.ConfidentEnough"/>.</param>
public sealed record ScreenArea(RectI? Crop, ScreenAreaMethod Method, double Confidence)
{
    public static ScreenArea WholeClient { get; } = new(null, ScreenAreaMethod.ClientArea, 0);
}

/// <summary>
/// Pure geometry for locating an emulator's Android screen in its window: aspect-ratio checks and a scan for the
/// uniform bars (title bar, tool bar, letterbox borders) around the picture. Image matching against an adb screenshot
/// lives in the app (it needs OpenCV) and feeds its result back through <see cref="FromScreenshotMatch"/>.
/// </summary>
public static class ScreenAreaLocator
{
    /// <summary>Areas at or above this confidence are used without asking.</summary>
    public const double ConfidentEnough = 0.75;

    /// <summary>Aspect ratios closer than this (relative) count as equal.</summary>
    public const double AspectTolerance = 0.02;

    /// <summary>The whole client area when it already has the device's aspect ratio, else null.</summary>
    public static ScreenArea? FromAspect(SizeI client, SizeI device)
    {
        if (client.IsEmpty || device.IsEmpty)
        {
            return null;
        }

        var oriented = ScreenOrientation.OrientLike(device, client);
        return ScreenOrientation.AspectDifference(client, oriented) <= AspectTolerance
            ? new ScreenArea(null, ScreenAreaMethod.AspectMatch, 0.9)
            : null;
    }

    /// <summary>
    /// The rectangle of a render child window (client coordinates of the main window), accepted when it lies inside the
    /// client area, is large enough and – when the device size is known – has the device's aspect ratio.
    /// </summary>
    public static ScreenArea? FromRenderWindow(RectI child, SizeI client, SizeI device)
    {
        var clamped = child.ClampTo(client);
        if (clamped.IsEmpty || clamped.Width < client.Width / 3 || clamped.Height < client.Height / 3)
        {
            return null;
        }

        if (!device.IsEmpty)
        {
            var oriented = ScreenOrientation.OrientLike(device, clamped.Size);
            var diff = ScreenOrientation.AspectDifference(clamped.Size, oriented);
            if (diff > 0.05)
            {
                return null;
            }

            return new ScreenArea(Normalize(clamped, client), ScreenAreaMethod.RenderWindow, diff <= AspectTolerance ? 0.95 : 0.8);
        }

        return new ScreenArea(Normalize(clamped, client), ScreenAreaMethod.RenderWindow, 0.7);
    }

    /// <summary>A screenshot match (location in client pixels and its score) turned into a screen area.</summary>
    public static ScreenArea? FromScreenshotMatch(RectI location, double score, SizeI client, double minScore = 0.8)
    {
        var clamped = location.ClampTo(client);
        if (clamped.IsEmpty || score < minScore || clamped.Width < client.Width / 4 || clamped.Height < client.Height / 4)
        {
            return null;
        }

        return new ScreenArea(Normalize(clamped, client), ScreenAreaMethod.ScreenshotMatch, Math.Clamp(score, 0, 1));
    }

    /// <summary>
    /// Trims uniform bars off the window picture: walks in from each edge while the row/column is (almost) one colour,
    /// then checks that the remaining rectangle has the device's aspect ratio. Works for title bars and tool bars drawn
    /// in a flat colour and for black letterbox borders, which covers the common emulator layouts. Null when the result
    /// does not look like the device screen.
    /// </summary>
    public static ScreenArea? FromEdges(Frame frame, SizeI device)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width < 32 || frame.Height < 32 || device.IsEmpty)
        {
            return null;
        }

        var top = 0;
        while (top < frame.Height / 3 && IsUniformRow(frame, top))
        {
            top++;
        }

        var bottom = frame.Height;
        while (bottom > frame.Height * 2 / 3 && IsUniformRow(frame, bottom - 1))
        {
            bottom--;
        }

        var left = 0;
        while (left < frame.Width / 3 && IsUniformColumn(frame, left, top, bottom))
        {
            left++;
        }

        var right = frame.Width;
        while (right > frame.Width * 2 / 3 && IsUniformColumn(frame, right - 1, top, bottom))
        {
            right--;
        }

        var rect = new RectI(left, top, right - left, bottom - top);
        if (rect.IsEmpty)
        {
            return null;
        }

        var oriented = ScreenOrientation.OrientLike(device, rect.Size);
        if (ScreenOrientation.AspectDifference(rect.Size, oriented) > AspectTolerance)
        {
            return null;
        }

        // A picture that itself starts with a flat row (a dark status bar) would be cut too short; aspect matching above
        // rejects most of those. Nothing trimmed = the client area is the screen.
        return rect.Size == frame.Size
            ? new ScreenArea(null, ScreenAreaMethod.AspectMatch, 0.9)
            : new ScreenArea(rect, ScreenAreaMethod.EdgeDetection, 0.8);
    }

    /// <summary>Null when <paramref name="rect"/> covers the whole client area (the profile then stores no crop).</summary>
    private static RectI? Normalize(RectI rect, SizeI client) =>
        rect.X == 0 && rect.Y == 0 && rect.Width == client.Width && rect.Height == client.Height ? null : rect;

    private const int ColorTolerance = 6;

    private static bool IsUniformRow(Frame f, int y)
    {
        var (r0, g0, b0) = f.PixelAt(0, y);
        var step = Math.Max(1, f.Width / 256);
        for (var x = step; x < f.Width; x += step)
        {
            if (!Close(f.PixelAt(x, y), r0, g0, b0))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUniformColumn(Frame f, int x, int top, int bottom)
    {
        if (bottom - top <= 1)
        {
            return true;
        }

        var (r0, g0, b0) = f.PixelAt(x, top);
        var step = Math.Max(1, (bottom - top) / 256);
        for (var y = top + step; y < bottom; y += step)
        {
            if (!Close(f.PixelAt(x, y), r0, g0, b0))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Close((byte R, byte G, byte B) p, byte r, byte g, byte b) =>
        Math.Abs(p.R - r) <= ColorTolerance && Math.Abs(p.G - g) <= ColorTolerance && Math.Abs(p.B - b) <= ColorTolerance;
}
