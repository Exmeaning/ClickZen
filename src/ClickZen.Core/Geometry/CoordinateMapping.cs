namespace ClickZen.Core.Geometry;

/// <summary>
/// How a source image of size <see cref="Source"/> is drawn inside a viewport of size <see cref="Viewport"/>
/// with uniform scaling and centering (letterbox / pillarbox). All values are in the viewport's units
/// (e.g. DIPs or physical pixels – the caller decides, consistently).
/// </summary>
public readonly record struct LetterboxFit(SizeI Source, double ViewportWidth, double ViewportHeight)
{
    public double Scale => Source.IsEmpty || ViewportWidth <= 0 || ViewportHeight <= 0
        ? 0
        : Math.Min(ViewportWidth / Source.Width, ViewportHeight / Source.Height);

    public double ContentWidth => Source.Width * Scale;
    public double ContentHeight => Source.Height * Scale;
    public double OffsetX => (ViewportWidth - ContentWidth) / 2;
    public double OffsetY => (ViewportHeight - ContentHeight) / 2;

    /// <summary>Viewport point → source pixel. Returns null when outside the drawn content.</summary>
    public PointD? ViewportToSource(PointD viewport)
    {
        var s = Scale;
        if (s <= 0)
        {
            return null;
        }

        var x = (viewport.X - OffsetX) / s;
        var y = (viewport.Y - OffsetY) / s;
        if (x < 0 || y < 0 || x >= Source.Width || y >= Source.Height)
        {
            return null;
        }

        return new PointD(x, y);
    }

    /// <summary>Viewport point → source pixel, clamped into the source even when outside (for drags).</summary>
    public PointD ViewportToSourceClamped(PointD viewport)
    {
        var s = Scale;
        if (s <= 0)
        {
            return default;
        }

        var x = Math.Clamp((viewport.X - OffsetX) / s, 0, Source.Width - 1);
        var y = Math.Clamp((viewport.Y - OffsetY) / s, 0, Source.Height - 1);
        return new PointD(x, y);
    }

    /// <summary>Source pixel → viewport point.</summary>
    public PointD SourceToViewport(PointD source) => new(OffsetX + source.X * Scale, OffsetY + source.Y * Scale);
}

/// <summary>
/// Converts between frame pixels (the image we see: video frame, or cropped window capture)
/// and device pixels (what input injection and persisted coordinates use).
/// Both spaces share the same orientation – the frame is a uniformly or non-uniformly scaled copy
/// of the device's current screen.
/// </summary>
public readonly record struct FrameToDevice(SizeI Frame, SizeI Device)
{
    public PointD FrameToDevicePoint(PointD frame) =>
        Frame.IsEmpty ? frame : new PointD(frame.X * Device.Width / Frame.Width, frame.Y * Device.Height / Frame.Height);

    public PointD DeviceToFramePoint(PointD device) =>
        Device.IsEmpty ? device : new PointD(device.X * Frame.Width / Device.Width, device.Y * Frame.Height / Device.Height);

    public RectI DeviceToFrameRect(RectI device)
    {
        var a = DeviceToFramePoint(new PointD(device.X, device.Y));
        var b = DeviceToFramePoint(new PointD(device.Right, device.Bottom));
        return RectI.FromCorners(a.Round(), b.Round()).ClampTo(Frame);
    }

    public RectI FrameToDeviceRect(RectI frame)
    {
        var a = FrameToDevicePoint(new PointD(frame.X, frame.Y));
        var b = FrameToDevicePoint(new PointD(frame.Right, frame.Bottom));
        return RectI.FromCorners(a.Round(), b.Round()).ClampTo(Device);
    }

    public PointI ClampToDevice(PointD p) =>
        new(Math.Clamp((int)Math.Round(p.X), 0, Math.Max(0, Device.Width - 1)),
            Math.Clamp((int)Math.Round(p.Y), 0, Math.Max(0, Device.Height - 1)));
}

/// <summary>
/// Persisted coordinates carry the screen size they were authored on (<see cref="RefSize"/>).
/// At run time they are rescaled to the current screen. When orientation differs
/// (portrait-authored script on a landscape screen) we do not rotate – the script almost
/// certainly expects the authored orientation – callers are expected to warn instead.
/// </summary>
public static class RefScaling
{
    public static PointI Scale(PointI p, SizeI refSize, SizeI current)
    {
        if (refSize.IsEmpty || current.IsEmpty || refSize == current)
        {
            return p;
        }

        return new PointI(
            (int)Math.Round((double)p.X * current.Width / refSize.Width),
            (int)Math.Round((double)p.Y * current.Height / refSize.Height));
    }

    public static RectI Scale(RectI r, SizeI refSize, SizeI current)
    {
        if (refSize.IsEmpty || current.IsEmpty || refSize == current)
        {
            return r;
        }

        var a = Scale(new PointI(r.X, r.Y), refSize, current);
        var b = Scale(new PointI(r.Right, r.Bottom), refSize, current);
        return RectI.FromCorners(a, b);
    }

    /// <summary>True when authored and current screens have different orientation.</summary>
    public static bool OrientationDiffers(SizeI refSize, SizeI current) =>
        !refSize.IsEmpty && !current.IsEmpty && refSize.IsLandscape != current.IsLandscape;
}

/// <summary>Display rotation as reported by Android (Surface.ROTATION_*).</summary>
public enum DisplayRotation
{
    Rotation0 = 0,
    Rotation90 = 1,
    Rotation180 = 2,
    Rotation270 = 3,
}

/// <summary>
/// Maps raw touchscreen coordinates (always in the panel's natural orientation) to the
/// current screen orientation. Used for device-side (getevent / sendevent) recording and injection.
/// </summary>
public static class RotationMapper
{
    /// <param name="natural">Point in the natural (rotation 0) screen space.</param>
    /// <param name="naturalSize">Screen size in natural orientation.</param>
    public static PointD NaturalToRotated(PointD natural, SizeI naturalSize, DisplayRotation rotation)
    {
        double w = naturalSize.Width, h = naturalSize.Height;
        return rotation switch
        {
            DisplayRotation.Rotation90 => new PointD(natural.Y, w - 1 - natural.X),
            DisplayRotation.Rotation180 => new PointD(w - 1 - natural.X, h - 1 - natural.Y),
            DisplayRotation.Rotation270 => new PointD(h - 1 - natural.Y, natural.X),
            _ => natural,
        };
    }

    public static PointD RotatedToNatural(PointD rotated, SizeI naturalSize, DisplayRotation rotation)
    {
        double w = naturalSize.Width, h = naturalSize.Height;
        return rotation switch
        {
            DisplayRotation.Rotation90 => new PointD(w - 1 - rotated.Y, rotated.X),
            DisplayRotation.Rotation180 => new PointD(w - 1 - rotated.X, h - 1 - rotated.Y),
            DisplayRotation.Rotation270 => new PointD(rotated.Y, h - 1 - rotated.X),
            _ => rotated,
        };
    }

    public static SizeI RotatedSize(SizeI naturalSize, DisplayRotation rotation) =>
        rotation is DisplayRotation.Rotation90 or DisplayRotation.Rotation270 ? naturalSize.Transposed : naturalSize;

    /// <summary>Linear map of a raw axis value in [min, max] onto [0, pixels).</summary>
    public static double AxisToPixels(int raw, int min, int max, int pixels)
    {
        if (max <= min || pixels <= 0)
        {
            return raw;
        }

        return Math.Clamp((double)(raw - min) * pixels / (max - min + 1), 0, pixels - 1);
    }

    public static int PixelsToAxis(double px, int min, int max, int pixels)
    {
        if (max <= min || pixels <= 0)
        {
            return (int)Math.Round(px);
        }

        return Math.Clamp((int)Math.Round(min + px * (max - min + 1) / pixels), min, max);
    }
}
