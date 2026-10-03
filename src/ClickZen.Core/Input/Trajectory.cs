using ClickZen.Core.Geometry;

namespace ClickZen.Core.Input;

/// <summary>Path utilities: simplification, resampling, curves and easing.</summary>
public static class Trajectory
{
    /// <summary>
    /// Douglas–Peucker simplification on (x, y); time offsets of kept points are preserved,
    /// so playback timing stays faithful. First and last points are always kept.
    /// </summary>
    public static IReadOnlyList<TimedPoint> Simplify(IReadOnlyList<TimedPoint> path, double epsilon)
    {
        if (path.Count <= 2 || epsilon <= 0)
        {
            return path.ToArray();
        }

        var keep = new bool[path.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int Start, int End)>();
        stack.Push((0, path.Count - 1));
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            var maxDist = 0.0;
            var idx = -1;
            for (var i = s + 1; i < e; i++)
            {
                var d = PerpendicularDistance(path[i].Position, path[s].Position, path[e].Position);
                if (d > maxDist)
                {
                    maxDist = d;
                    idx = i;
                }
            }

            if (idx >= 0 && maxDist > epsilon)
            {
                keep[idx] = true;
                stack.Push((s, idx));
                stack.Push((idx, e));
            }
        }

        var result = new List<TimedPoint>();
        for (var i = 0; i < path.Count; i++)
        {
            if (keep[i])
            {
                result.Add(path[i]);
            }
        }

        return result;
    }

    internal static double PerpendicularDistance(PointD p, PointD a, PointD b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9)
        {
            return p.DistanceTo(a);
        }

        return Math.Abs(dy * p.X - dx * p.Y + b.X * a.Y - b.Y * a.X) / len;
    }

    public static double Length(IReadOnlyList<TimedPoint> path)
    {
        var total = 0.0;
        for (var i = 1; i < path.Count; i++)
        {
            total += path[i].Position.DistanceTo(path[i - 1].Position);
        }

        return total;
    }

    /// <summary>
    /// Resamples a path so consecutive points are at most <paramref name="stepMs"/> apart in time,
    /// interpolating linearly. Used by injectors that emit MOVE events at a fixed cadence.
    /// </summary>
    public static IReadOnlyList<TimedPoint> ResampleByTime(IReadOnlyList<TimedPoint> path, int stepMs)
    {
        if (path.Count < 2 || stepMs <= 0)
        {
            return path.ToArray();
        }

        var result = new List<TimedPoint> { path[0] };
        for (var i = 1; i < path.Count; i++)
        {
            var a = path[i - 1];
            var b = path[i];
            var span = b.OffsetMs - a.OffsetMs;
            if (span > stepMs)
            {
                var steps = (int)Math.Ceiling((double)span / stepMs);
                for (var k = 1; k < steps; k++)
                {
                    var t = (double)k / steps;
                    result.Add(new TimedPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.OffsetMs + (int)Math.Round(span * t)));
                }
            }

            result.Add(b);
        }

        return result;
    }

    /// <summary>Straight line from <paramref name="from"/> to <paramref name="to"/> over <paramref name="durationMs"/>.</summary>
    public static IReadOnlyList<TimedPoint> Line(PointD from, PointD to, int durationMs, int points = 2, Func<double, double>? easing = null)
    {
        points = Math.Max(2, points);
        easing ??= Easing.Linear;
        var result = new TimedPoint[points];
        for (var i = 0; i < points; i++)
        {
            var t = (double)i / (points - 1);
            var e = easing(t);
            result[i] = new TimedPoint(from.X + (to.X - from.X) * e, from.Y + (to.Y - from.Y) * e, (int)Math.Round(durationMs * t));
        }

        return result;
    }

    /// <summary>Quadratic Bézier from <paramref name="from"/> to <paramref name="to"/> through a control point.</summary>
    public static IReadOnlyList<TimedPoint> Curve(PointD from, PointD control, PointD to, int durationMs, int points, Func<double, double>? easing = null)
    {
        points = Math.Max(2, points);
        easing ??= Easing.Linear;
        var result = new TimedPoint[points];
        for (var i = 0; i < points; i++)
        {
            var t = (double)i / (points - 1);
            var u = easing(t);
            var inv = 1 - u;
            var x = inv * inv * from.X + 2 * inv * u * control.X + u * u * to.X;
            var y = inv * inv * from.Y + 2 * inv * u * control.Y + u * u * to.Y;
            result[i] = new TimedPoint(x, y, (int)Math.Round(durationMs * t));
        }

        return result;
    }

    /// <summary>Rescales all offsets so the path lasts <paramref name="durationMs"/>.</summary>
    public static IReadOnlyList<TimedPoint> WithDuration(IReadOnlyList<TimedPoint> path, int durationMs)
    {
        if (path.Count == 0)
        {
            return path;
        }

        var span = path[^1].OffsetMs - path[0].OffsetMs;
        var start = path[0].OffsetMs;
        return path.Select(p => p with
        {
            OffsetMs = span <= 0 ? 0 : (int)Math.Round((double)(p.OffsetMs - start) * durationMs / span),
        }).ToArray();
    }
}

/// <summary>Easing curves mapping t∈[0,1] → [0,1].</summary>
public static class Easing
{
    public static double Linear(double t) => t;

    /// <summary>Smooth acceleration then deceleration – resembles a human finger.</summary>
    public static double EaseInOutSine(double t) => -(Math.Cos(Math.PI * t) - 1) / 2;

    public static double EaseOutQuad(double t) => 1 - (1 - t) * (1 - t);
}
