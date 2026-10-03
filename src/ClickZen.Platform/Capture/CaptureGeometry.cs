using ClickZen.Core.Geometry;

namespace ClickZen.Platform.Capture;

/// <summary>
/// Pure coordinate maths for window capture (unit-tested).
/// <para>
/// Spaces, all in <b>physical pixels</b> (the process is Per-Monitor V2 aware and the capture threads
/// additionally switch to PMv2 while querying window metrics):
/// <list type="bullet">
/// <item><b>screen</b>: virtual-desktop coordinates (GetWindowRect, ClientToScreen, DWMWA_EXTENDED_FRAME_BOUNDS).</item>
/// <item><b>capture image</b>: the bitmap a backend produced. For WGC its origin is the DWM extended frame
/// bounds (visible window without the invisible resize border / shadow); for PrintWindow it is GetWindowRect.</item>
/// <item><b>client</b>: origin = top-left of the client area. Crop rectangles are stored in this space.</item>
/// <item><b>frame</b>: the emitted <c>Frame</c> = the crop region; frame (0,0) = client <c>crop.X, crop.Y</c>.</item>
/// </list>
/// </para>
/// </summary>
public static class CaptureGeometry
{
    /// <summary>
    /// Region of the capture image holding <paramref name="crop"/> (client coordinates; null = whole client area).
    /// </summary>
    /// <param name="captureOriginScreen">Screen position of the capture image's top-left corner.</param>
    /// <param name="captureBoundsSize">Screen-space size the capture image represents (normally equal to <paramref name="imageSize"/>).</param>
    /// <param name="imageSize">Actual size of the capture image in pixels. When it differs from
    /// <paramref name="captureBoundsSize"/> (DPI virtualisation, mid-resize) the offsets are scaled proportionally.</param>
    /// <param name="clientOriginScreen">ClientToScreen(0, 0).</param>
    /// <param name="clientSize">Client area size.</param>
    /// <param name="crop">Requested crop in client coordinates; clamped to the client area.</param>
    /// <returns>The region, clamped to the image; empty when nothing of the crop is visible.</returns>
    public static RectI ClientCropInCapture(PointI captureOriginScreen, SizeI captureBoundsSize, SizeI imageSize,
        PointI clientOriginScreen, SizeI clientSize, RectI? crop)
    {
        if (imageSize.IsEmpty || clientSize.IsEmpty)
        {
            return default;
        }

        var c = EffectiveCrop(crop, clientSize);
        if (c.IsEmpty)
        {
            return default;
        }

        var sx = captureBoundsSize.IsEmpty ? 1.0 : (double)imageSize.Width / captureBoundsSize.Width;
        var sy = captureBoundsSize.IsEmpty ? 1.0 : (double)imageSize.Height / captureBoundsSize.Height;
        var offX = clientOriginScreen.X - captureOriginScreen.X;
        var offY = clientOriginScreen.Y - captureOriginScreen.Y;

        var x0 = (int)Math.Round((offX + c.X) * sx);
        var y0 = (int)Math.Round((offY + c.Y) * sy);
        var x1 = (int)Math.Round((offX + c.Right) * sx);
        var y1 = (int)Math.Round((offY + c.Bottom) * sy);
        return new RectI(x0, y0, x1 - x0, y1 - y0).ClampTo(imageSize);
    }

    /// <summary>The crop actually used: <paramref name="crop"/> clamped to the client area, or the whole client area.</summary>
    public static RectI EffectiveCrop(RectI? crop, SizeI clientSize)
    {
        var whole = new RectI(0, 0, clientSize.Width, clientSize.Height);
        if (crop is not { } r || r.IsEmpty)
        {
            return whole;
        }

        return r.ClampTo(whole);
    }

    /// <summary>Client point → frame point for a frame cut at <paramref name="effectiveCrop"/>.</summary>
    public static PointD ClientToFrame(PointD client, RectI effectiveCrop) =>
        new(client.X - effectiveCrop.X, client.Y - effectiveCrop.Y);

    /// <summary>Frame point → client point for a frame cut at <paramref name="effectiveCrop"/>.</summary>
    public static PointD FrameToClient(PointD frame, RectI effectiveCrop) =>
        new(frame.X + effectiveCrop.X, frame.Y + effectiveCrop.Y);
}

/// <summary>BGRA buffer helpers shared by the capture backends.</summary>
public static class BgraBuffer
{
    /// <summary>
    /// Copies <paramref name="region"/> out of a BGRA image whose rows are <paramref name="sourceRowPitch"/> bytes apart
    /// (row pitch ≥ width × 4, e.g. a mapped D3D11 staging texture) into a tightly packed buffer.
    /// </summary>
    /// <param name="forceOpaque">Set alpha to 255 (GDI leaves it 0).</param>
    public static byte[] CopyRegion(ReadOnlySpan<byte> source, int sourceRowPitch, RectI region, bool forceOpaque = false)
    {
        var dst = new byte[region.Width * region.Height * 4];
        CopyRegion(source, sourceRowPitch, region, dst, forceOpaque);
        return dst;
    }

    public static void CopyRegion(ReadOnlySpan<byte> source, int sourceRowPitch, RectI region, Span<byte> destination, bool forceOpaque = false)
    {
        if (region.IsEmpty)
        {
            return;
        }

        var rowBytes = region.Width * 4;
        if (region.X < 0 || region.Y < 0 || sourceRowPitch < (region.X + region.Width) * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Region lies outside the source rows.");
        }

        if ((long)(region.Y + region.Height - 1) * sourceRowPitch + region.X * 4 + rowBytes > source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Region lies outside the source buffer.");
        }

        if (destination.Length < rowBytes * region.Height)
        {
            throw new ArgumentException("Destination too small.", nameof(destination));
        }

        for (var y = 0; y < region.Height; y++)
        {
            var src = source.Slice((region.Y + y) * sourceRowPitch + region.X * 4, rowBytes);
            var dst = destination.Slice(y * rowBytes, rowBytes);
            src.CopyTo(dst);
            if (forceOpaque)
            {
                for (var i = 3; i < rowBytes; i += 4)
                {
                    dst[i] = 255;
                }
            }
        }
    }

    /// <summary>
    /// Samples a grid over the whole image and returns the luminance mean and variance.
    /// Used to detect black/blank captures (PrintWindow failing, minimised GPU windows).
    /// </summary>
    public static (double Mean, double Variance) SampleLuminance(ReadOnlySpan<byte> bgra, int width, int height, int grid = 32)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
        {
            return (0, 0);
        }

        var nx = Math.Min(grid, width);
        var ny = Math.Min(grid, height);
        double sum = 0, sumSq = 0;
        var n = 0;
        for (var j = 0; j < ny; j++)
        {
            var y = (int)((j + 0.5) * height / ny);
            for (var i = 0; i < nx; i++)
            {
                var x = (int)((i + 0.5) * width / nx);
                var p = (y * width + x) * 4;
                var l = 0.114 * bgra[p] + 0.587 * bgra[p + 1] + 0.299 * bgra[p + 2];
                sum += l;
                sumSq += l * l;
                n++;
            }
        }

        var mean = sum / n;
        return (mean, Math.Max(0, sumSq / n - mean * mean));
    }

    /// <summary>True for an (almost) uniformly black image.</summary>
    public static bool IsLikelyBlack(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var (mean, variance) = SampleLuminance(bgra, width, height);
        return mean < 4 && variance < 4;
    }
}
