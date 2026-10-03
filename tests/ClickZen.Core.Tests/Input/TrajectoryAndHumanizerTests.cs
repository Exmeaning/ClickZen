using ClickZen.Core.Geometry;
using ClickZen.Core.Input;

namespace ClickZen.Core.Tests.Input;

public sealed class TrajectoryTests
{
    private static TimedPoint P(double x, double y, int t) => new(x, y, t);

    [Fact]
    public void Simplify_keeps_endpoints_and_drops_collinear_points()
    {
        var path = Enumerable.Range(0, 50).Select(i => P(i * 10, i * 5, i * 10)).ToArray();
        var s = Trajectory.Simplify(path, 2);
        Assert.Equal(2, s.Count);
        Assert.Equal(path[0], s[0]);
        Assert.Equal(path[^1], s[^1]);
    }

    [Fact]
    public void Simplify_keeps_corner_and_its_timing()
    {
        var path = new[] { P(0, 0, 0), P(50, 0, 50), P(100, 0, 100), P(100, 50, 150), P(100, 100, 200) };
        var s = Trajectory.Simplify(path, 1);
        Assert.Equal(3, s.Count);
        Assert.Equal(P(100, 0, 100), s[1]);
    }

    [Fact]
    public void Simplify_does_not_cap_point_count()
    {
        // A zig-zag where every vertex matters must survive intact (1.x truncated to 7 points).
        var path = Enumerable.Range(0, 30).Select(i => P(i * 20, i % 2 == 0 ? 0 : 100, i * 16)).ToArray();
        Assert.Equal(30, Trajectory.Simplify(path, 5).Count);
    }

    [Fact]
    public void Resample_inserts_points_at_most_step_apart()
    {
        var path = new[] { P(0, 0, 0), P(100, 0, 100) };
        var r = Trajectory.ResampleByTime(path, 16);
        Assert.Equal(0, r[0].OffsetMs);
        Assert.Equal(100, r[^1].OffsetMs);
        for (var i = 1; i < r.Count; i++)
        {
            Assert.InRange(r[i].OffsetMs - r[i - 1].OffsetMs, 1, 16);
            Assert.True(r[i].X >= r[i - 1].X);
        }
    }

    [Fact]
    public void Line_hits_endpoints_and_duration()
    {
        var l = Trajectory.Line(new PointD(0, 0), new PointD(100, 200), 300, 4);
        Assert.Equal(4, l.Count);
        Assert.Equal(P(0, 0, 0), l[0]);
        Assert.Equal(P(100, 200, 300), l[^1]);
    }

    [Fact]
    public void Curve_starts_and_ends_on_endpoints()
    {
        var c = Trajectory.Curve(new PointD(0, 0), new PointD(50, 100), new PointD(100, 0), 200, 11, Easing.EaseInOutSine);
        Assert.Equal(0, c[0].X, 6);
        Assert.Equal(100, c[^1].X, 6);
        Assert.Equal(0, c[^1].Y, 6);
        Assert.True(c[5].Y > 10);
    }

    [Fact]
    public void WithDuration_rescales_offsets()
    {
        var path = new[] { P(0, 0, 100), P(1, 1, 200), P(2, 2, 300) };
        var r = Trajectory.WithDuration(path, 1000);
        Assert.Equal(new[] { 0, 500, 1000 }, r.Select(p => p.OffsetMs));
    }

    [Fact]
    public void Easing_curves_are_bounded()
    {
        foreach (var f in new Func<double, double>[] { Easing.Linear, Easing.EaseInOutSine, Easing.EaseOutQuad })
        {
            Assert.Equal(0, f(0), 9);
            Assert.Equal(1, f(1), 9);
        }
    }
}

public sealed class HumanizerTests
{
    private static readonly SizeI Screen = new(1080, 2400);

    [Fact]
    public void Disabled_options_change_nothing()
    {
        var h = new Humanizer(new Random(1));
        Assert.Equal(new PointI(10, 20), h.Jitter(new PointI(10, 20), HumanizeOptions.None, 2.75, Screen));
        Assert.Equal(500, h.Delay(500, HumanizeOptions.None));
        Assert.Equal(500, h.Duration(500, HumanizeOptions.None));
    }

    [Fact]
    public void Jitter_is_independent_of_coordinate_value_and_bounded()
    {
        var h = new Humanizer(new Random(42));
        var o = new HumanizeOptions { PositionJitterDp = 3 };
        const double dpScale = 2.75;
        var max = 3 * 3 * dpScale + 1;

        foreach (var origin in new[] { new PointI(0, 0), new PointI(540, 1200), new PointI(1079, 2399) })
        {
            var sum = 0.0;
            for (var i = 0; i < 2000; i++)
            {
                var p = h.Jitter(origin, o, dpScale, Screen);
                Assert.InRange(p.X, Math.Max(0, origin.X - max), origin.X + max);
                Assert.InRange(p.Y, Math.Max(0, origin.Y - max), origin.Y + max);
                Assert.InRange(p.X, 0, 1079);
                Assert.InRange(p.Y, 0, 2399);
                sum += Math.Abs(p.X - origin.X);
            }

            // Points that are not at the screen edge actually move (1.x never moved x=0).
            if (origin.X == 540)
            {
                Assert.True(sum / 2000 > 2);
            }
        }
    }

    [Fact]
    public void Delay_jitter_stays_within_percent_and_varies()
    {
        var h = new Humanizer(new Random(7));
        var o = new HumanizeOptions { DelayJitterPercent = 20 };
        var values = Enumerable.Range(0, 500).Select(_ => h.Delay(1000, o)).ToArray();
        Assert.All(values, v => Assert.InRange(v, 800, 1200));
        Assert.True(values.Distinct().Count() > 50);
    }

    [Fact]
    public void Point_in_area_stays_inside()
    {
        var h = new Humanizer(new Random(3));
        var area = new RectI(100, 200, 50, 20);
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(area.Contains(h.PointIn(area)));
        }
    }

    [Fact]
    public void Weighted_pick_respects_weights()
    {
        var h = new Humanizer(new Random(5));
        var counts = new int[3];
        for (var i = 0; i < 6000; i++)
        {
            counts[h.PickWeighted([1, 0, 2])]++;
        }

        Assert.Equal(0, counts[1]);
        Assert.InRange(counts[2] / (double)counts[0], 1.6, 2.4);
    }

    [Fact]
    public void Swipe_path_ends_exactly_on_target_with_requested_duration()
    {
        var h = new Humanizer(new Random(9));
        var path = h.SwipePath(new PointD(100, 2000), new PointD(100, 500), 300, new HumanizeOptions());
        Assert.Equal(100, path[0].X, 6);
        Assert.Equal(2000, path[0].Y, 6);
        Assert.Equal(500, path[^1].Y, 6);
        Assert.Equal(300, path[^1].OffsetMs);
        Assert.True(path.Count > 10);
    }
}
