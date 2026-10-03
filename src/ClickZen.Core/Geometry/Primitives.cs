namespace ClickZen.Core.Geometry;

/// <summary>Integer point in device pixels (or any pixel space stated by the caller).</summary>
public readonly record struct PointI(int X, int Y)
{
    public override string ToString() => $"{X}, {Y}";
}

/// <summary>Floating point used for intermediate maths and sub-pixel input positions.</summary>
public readonly record struct PointD(double X, double Y)
{
    public PointI Round() => new((int)Math.Round(X), (int)Math.Round(Y));

    public double DistanceTo(PointD other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static implicit operator PointD(PointI p) => new(p.X, p.Y);
}

/// <summary>Width/height in pixels.</summary>
public readonly record struct SizeI(int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool IsLandscape => Width > Height;

    /// <summary>The same size with width and height swapped (rotation by 90°).</summary>
    public SizeI Transposed => new(Height, Width);

    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>Axis-aligned rectangle; <see cref="X"/>/<see cref="Y"/> is the top-left corner.</summary>
public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public SizeI Size => new(Width, Height);
    public PointI Center => new(X + Width / 2, Y + Height / 2);

    public bool Contains(PointI p) => p.X >= X && p.Y >= Y && p.X < Right && p.Y < Bottom;

    public bool Contains(PointD p) => p.X >= X && p.Y >= Y && p.X < Right && p.Y < Bottom;

    public static RectI FromCorners(PointI a, PointI b)
    {
        var x0 = Math.Min(a.X, b.X);
        var y0 = Math.Min(a.Y, b.Y);
        return new RectI(x0, y0, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    /// <summary>Intersection with <paramref name="bounds"/>; empty if they do not overlap.</summary>
    public RectI ClampTo(RectI bounds)
    {
        var x0 = Math.Max(X, bounds.X);
        var y0 = Math.Max(Y, bounds.Y);
        var x1 = Math.Min(Right, bounds.Right);
        var y1 = Math.Min(Bottom, bounds.Bottom);
        return x1 > x0 && y1 > y0 ? new RectI(x0, y0, x1 - x0, y1 - y0) : default;
    }

    public RectI ClampTo(SizeI size) => ClampTo(new RectI(0, 0, size.Width, size.Height));

    public override string ToString() => $"({X}, {Y}, {Width}x{Height})";
}
