using ClickZen.Core.Geometry;

namespace ClickZen.Mcp;

/// <summary>
/// Coordinates sent by AI clients. Input tools take device pixels; when the model measured a point on a
/// scaled screenshot it can pass that screenshot's size as <c>refWidth</c>/<c>refHeight</c> and the point is
/// scaled to the device screen.
/// </summary>
public static class CoordinateScaling
{
    /// <summary>Maps a point measured at <paramref name="refWidth"/>×<paramref name="refHeight"/> to <paramref name="screen"/> (unchanged without a reference size).</summary>
    public static PointI ToDevice(int x, int y, int? refWidth, int? refHeight, SizeI screen)
    {
        if (refWidth is not { } rw || refHeight is not { } rh || rw <= 0 || rh <= 0 || screen.IsEmpty || (rw == screen.Width && rh == screen.Height))
        {
            return new PointI(x, y);
        }

        return new PointI(
            (int)Math.Round(x * (double)screen.Width / rw),
            (int)Math.Round(y * (double)screen.Height / rh));
    }

    /// <summary>Maps a rectangle measured at a reference size to device pixels, clipped to the screen.</summary>
    public static RectI ToDevice(RectI r, int? refWidth, int? refHeight, SizeI screen)
    {
        var a = ToDevice(r.X, r.Y, refWidth, refHeight, screen);
        var b = ToDevice(r.Right, r.Bottom, refWidth, refHeight, screen);
        var rect = new RectI(a.X, a.Y, Math.Max(0, b.X - a.X), Math.Max(0, b.Y - a.Y));
        return screen.IsEmpty ? rect : rect.ClampTo(screen);
    }

    /// <summary>Throws a tool error when a point lies outside the screen (when the size is known).</summary>
    public static void EnsureOnScreen(PointI p, SizeI screen)
    {
        if (!screen.IsEmpty && (p.X < 0 || p.Y < 0 || p.X >= screen.Width || p.Y >= screen.Height))
        {
            throw new McpToolException($"Point ({p.X}, {p.Y}) is outside the screen ({screen.Width}x{screen.Height}). Coordinates are device pixels; pass refWidth/refHeight when they were measured on a scaled screenshot.");
        }
    }
}
