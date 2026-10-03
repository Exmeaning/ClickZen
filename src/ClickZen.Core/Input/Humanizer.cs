using ClickZen.Core.Geometry;

namespace ClickZen.Core.Input;

/// <summary>
/// Randomisation parameters. Position jitter is in dp (converted with the device density) so it
/// means the same thing on every screen – unlike the 1.x "percent of the coordinate" approach,
/// which jittered x=1000 a lot and x=0 not at all.
/// </summary>
public sealed record HumanizeOptions
{
    public static HumanizeOptions None { get; } = new() { Enabled = false };

    public bool Enabled { get; init; } = true;

    /// <summary>Standard deviation of the gaussian position offset, in dp.</summary>
    public double PositionJitterDp { get; init; } = 3.0;

    /// <summary>± percent applied to waits and inter-action delays.</summary>
    public double DelayJitterPercent { get; init; } = 10.0;

    /// <summary>± percent applied to press/stroke durations.</summary>
    public double DurationJitterPercent { get; init; } = 10.0;

    /// <summary>Bend straight swipes into a gentle curve and ease their speed.</summary>
    public bool CurvedSwipes { get; init; } = true;
}

/// <summary>
/// Applies <see cref="HumanizeOptions"/>. Deterministic for a given <see cref="Random"/> seed,
/// which the tests rely on.
/// </summary>
public sealed class Humanizer
{
    private readonly Random _random;

    public Humanizer(Random? random = null) => _random = random ?? Random.Shared;

    /// <summary>Gaussian offset (Box–Muller), truncated at 3σ, then clamped to the screen.</summary>
    public PointI Jitter(PointI p, HumanizeOptions o, double dpScale, SizeI screen)
    {
        if (!o.Enabled || o.PositionJitterDp <= 0)
        {
            return p;
        }

        var sigma = o.PositionJitterDp * Math.Max(dpScale, 0.1);
        var dx = Math.Clamp(NextGaussian() * sigma, -3 * sigma, 3 * sigma);
        var dy = Math.Clamp(NextGaussian() * sigma, -3 * sigma, 3 * sigma);
        var x = (int)Math.Round(p.X + dx);
        var y = (int)Math.Round(p.Y + dy);
        if (!screen.IsEmpty)
        {
            x = Math.Clamp(x, 0, screen.Width - 1);
            y = Math.Clamp(y, 0, screen.Height - 1);
        }

        return new PointI(x, y);
    }

    /// <summary>Random point inside <paramref name="area"/>, biased toward the centre (sum of two uniforms).</summary>
    public PointI PointIn(RectI area)
    {
        if (area.IsEmpty)
        {
            return area.Center;
        }

        var u = (_random.NextDouble() + _random.NextDouble()) / 2;
        var v = (_random.NextDouble() + _random.NextDouble()) / 2;
        return new PointI(area.X + (int)(u * (area.Width - 1)), area.Y + (int)(v * (area.Height - 1)));
    }

    public int Delay(int ms, HumanizeOptions o) => Scale(ms, o.Enabled ? o.DelayJitterPercent : 0);

    public int Duration(int ms, HumanizeOptions o) => Scale(ms, o.Enabled ? o.DurationJitterPercent : 0);

    /// <summary>Uniform integer in [min, max] (inclusive).</summary>
    public int Between(int min, int max) => min >= max ? min : _random.Next(min, max + 1);

    /// <summary>Weighted random index; non-positive weights are never picked unless all are.</summary>
    public int PickWeighted(IReadOnlyList<double> weights)
    {
        if (weights.Count == 0)
        {
            return -1;
        }

        var total = weights.Where(w => w > 0).Sum();
        if (total <= 0)
        {
            return _random.Next(weights.Count);
        }

        var r = _random.NextDouble() * total;
        for (var i = 0; i < weights.Count; i++)
        {
            if (weights[i] <= 0)
            {
                continue;
            }

            r -= weights[i];
            if (r <= 0)
            {
                return i;
            }
        }

        return weights.Count - 1;
    }

    /// <summary>
    /// Builds the stroke for a swipe. With humanising enabled the path bows slightly to one side
    /// (control point offset ≤ 8% of the length) and follows an ease-in-out speed profile.
    /// </summary>
    public IReadOnlyList<TimedPoint> SwipePath(PointD from, PointD to, int durationMs, HumanizeOptions o)
    {
        var len = from.DistanceTo(to);
        var points = Math.Clamp((int)(durationMs / 8.0), 2, 120);
        if (!o.Enabled || !o.CurvedSwipes || len < 1)
        {
            return Trajectory.Line(from, to, durationMs, points);
        }

        var mid = new PointD((from.X + to.X) / 2, (from.Y + to.Y) / 2);
        var nx = -(to.Y - from.Y) / len;
        var ny = (to.X - from.X) / len;
        var bow = (_random.NextDouble() * 2 - 1) * 0.08 * len;
        var control = new PointD(mid.X + nx * bow, mid.Y + ny * bow);
        return Trajectory.Curve(from, control, to, durationMs, points, Easing.EaseInOutSine);
    }

    private int Scale(int value, double percent)
    {
        if (percent <= 0 || value <= 0)
        {
            return Math.Max(0, value);
        }

        var f = 1 + (_random.NextDouble() * 2 - 1) * percent / 100.0;
        return Math.Max(0, (int)Math.Round(value * f));
    }

    private double NextGaussian()
    {
        var u1 = 1.0 - _random.NextDouble();
        var u2 = _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
