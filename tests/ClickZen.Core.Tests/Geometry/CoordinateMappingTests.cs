using ClickZen.Core.Geometry;

namespace ClickZen.Core.Tests.Geometry;

public sealed class CoordinateMappingTests
{
    [Fact]
    public void Letterbox_portrait_frame_in_wide_viewport_is_pillarboxed()
    {
        var fit = new LetterboxFit(new SizeI(1080, 2400), 1000, 600);

        Assert.Equal(0.25, fit.Scale, 6);
        Assert.Equal(270, fit.ContentWidth, 6);
        Assert.Equal(365, fit.OffsetX, 6);
        Assert.Equal(0, fit.OffsetY, 6);
    }

    [Fact]
    public void Letterbox_round_trips_and_rejects_bars()
    {
        var fit = new LetterboxFit(new SizeI(1080, 2400), 1000, 600);

        var src = fit.ViewportToSource(new PointD(365 + 135, 300));
        Assert.NotNull(src);
        Assert.Equal(540, src.Value.X, 6);
        Assert.Equal(1200, src.Value.Y, 6);

        var back = fit.SourceToViewport(src.Value);
        Assert.Equal(500, back.X, 6);
        Assert.Equal(300, back.Y, 6);

        Assert.Null(fit.ViewportToSource(new PointD(10, 300)));     // left bar
        Assert.Null(fit.ViewportToSource(new PointD(990, 300)));    // right bar
    }

    [Fact]
    public void Letterbox_clamped_keeps_drags_inside()
    {
        var fit = new LetterboxFit(new SizeI(100, 100), 200, 100);
        var p = fit.ViewportToSourceClamped(new PointD(-50, 500));
        Assert.Equal(0, p.X);
        Assert.Equal(99, p.Y);
    }

    [Fact]
    public void Letterbox_empty_source_is_safe()
    {
        var fit = new LetterboxFit(default, 100, 100);
        Assert.Equal(0, fit.Scale);
        Assert.Null(fit.ViewportToSource(new PointD(1, 1)));
    }

    [Fact]
    public void Frame_to_device_scales_both_axes_independently()
    {
        var map = new FrameToDevice(new SizeI(576, 1280), new SizeI(1080, 2400));
        var d = map.FrameToDevicePoint(new PointD(288, 640));
        Assert.Equal(540, d.X, 6);
        Assert.Equal(1200, d.Y, 6);

        var f = map.DeviceToFramePoint(new PointD(1080, 2400));
        Assert.Equal(576, f.X, 6);
        Assert.Equal(1280, f.Y, 6);
    }

    [Fact]
    public void Device_rect_maps_to_clamped_frame_rect()
    {
        var map = new FrameToDevice(new SizeI(540, 1200), new SizeI(1080, 2400));
        var r = map.DeviceToFrameRect(new RectI(1000, 2300, 200, 200));
        Assert.Equal(new RectI(500, 1150, 40, 50), r);
    }

    [Fact]
    public void Clamp_to_device_rounds_and_bounds()
    {
        var map = new FrameToDevice(new SizeI(10, 10), new SizeI(1080, 2400));
        Assert.Equal(new PointI(1079, 0), map.ClampToDevice(new PointD(5000.4, -3)));
    }

    [Theory]
    [InlineData(1080, 2400, 1080, 2400, 540, 1200, 540, 1200)]
    [InlineData(1080, 2400, 720, 1600, 540, 1200, 360, 800)]
    [InlineData(1440, 3200, 1080, 2400, 1440, 3200, 1080, 2400)]
    public void Ref_scaling_rescales_points(int rw, int rh, int cw, int ch, int x, int y, int ex, int ey)
    {
        Assert.Equal(new PointI(ex, ey), RefScaling.Scale(new PointI(x, y), new SizeI(rw, rh), new SizeI(cw, ch)));
    }

    [Fact]
    public void Ref_scaling_with_unknown_ref_is_identity()
    {
        Assert.Equal(new PointI(5, 6), RefScaling.Scale(new PointI(5, 6), default, new SizeI(100, 100)));
    }

    [Fact]
    public void Ref_scaling_detects_orientation_change()
    {
        Assert.True(RefScaling.OrientationDiffers(new SizeI(1080, 2400), new SizeI(2400, 1080)));
        Assert.False(RefScaling.OrientationDiffers(new SizeI(1080, 2400), new SizeI(720, 1600)));
    }

    [Theory]
    [InlineData(DisplayRotation.Rotation0)]
    [InlineData(DisplayRotation.Rotation90)]
    [InlineData(DisplayRotation.Rotation180)]
    [InlineData(DisplayRotation.Rotation270)]
    public void Rotation_round_trips(DisplayRotation rot)
    {
        var size = new SizeI(1080, 2400);
        var p = new PointD(100, 2000);
        var rotated = RotationMapper.NaturalToRotated(p, size, rot);
        var back = RotationMapper.RotatedToNatural(rotated, size, rot);
        Assert.Equal(p.X, back.X, 6);
        Assert.Equal(p.Y, back.Y, 6);

        var rs = RotationMapper.RotatedSize(size, rot);
        Assert.InRange(rotated.X, 0, rs.Width - 1);
        Assert.InRange(rotated.Y, 0, rs.Height - 1);
    }

    [Fact]
    public void Rotation90_maps_natural_top_left_to_bottom_left()
    {
        // Phone rotated counter-clockwise (ROTATION_90): natural top-left becomes bottom-left of the landscape screen.
        var r = RotationMapper.NaturalToRotated(new PointD(0, 0), new SizeI(1080, 2400), DisplayRotation.Rotation90);
        Assert.Equal(0, r.X);
        Assert.Equal(1079, r.Y);
    }

    [Fact]
    public void Axis_mapping_uses_min_and_max()
    {
        Assert.Equal(0, RotationMapper.AxisToPixels(0, 0, 4095, 1080));
        Assert.Equal(1079, RotationMapper.AxisToPixels(4095, 0, 4095, 1080), 0);
        Assert.Equal(540, RotationMapper.AxisToPixels(2048, 0, 4095, 1080), 0);
        // Non-zero minimum
        Assert.Equal(0, RotationMapper.AxisToPixels(100, 100, 1099, 1000));

        var axis = RotationMapper.PixelsToAxis(540, 0, 4095, 1080);
        Assert.Equal(540, RotationMapper.AxisToPixels(axis, 0, 4095, 1080), 0);
    }
}
