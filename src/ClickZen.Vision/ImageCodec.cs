using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;
using OpenCvSharp;

namespace ClickZen.Vision;

/// <summary>Image helpers shared by the UI (template capture, screenshots) and tests.</summary>
public static class ImageCodec
{
    /// <summary>Crops <paramref name="rect"/> (frame pixels) out of a frame and encodes it as PNG.</summary>
    public static byte[] CropToPng(Frame frame, RectI rect)
    {
        var r = rect.ClampTo(frame.Size);
        if (r.IsEmpty)
        {
            throw new ArgumentException("Crop rectangle is outside the frame.", nameof(rect));
        }

        using var full = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Bgra);
        using var roi = new Mat(full, new Rect(r.X, r.Y, r.Width, r.Height));
        using var bgr = new Mat();
        Cv2.CvtColor(roi, bgr, ColorConversionCodes.BGRA2BGR);
        return bgr.ImEncode(".png");
    }

    /// <summary>Encodes a whole frame as PNG.</summary>
    public static byte[] ToPng(Frame frame) => CropToPng(frame, new RectI(0, 0, frame.Width, frame.Height));

    /// <summary>
    /// Crops <paramref name="rect"/> (frame pixels), scales it so the longest edge is at most <paramref name="maxSize"/>
    /// (0 = no scaling) and encodes it as JPEG (<paramref name="jpegQuality"/> 1–100) or PNG (quality 0).
    /// </summary>
    /// <returns>The encoded bytes and the size of the encoded image.</returns>
    public static (byte[] Data, SizeI Size) Encode(Frame frame, RectI rect, int maxSize, int jpegQuality)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var r = rect.ClampTo(frame.Size);
        if (r.IsEmpty)
        {
            throw new ArgumentException("Crop rectangle is outside the frame.", nameof(rect));
        }

        using var full = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Bgra);
        using var roi = new Mat(full, new Rect(r.X, r.Y, r.Width, r.Height));
        using var bgr = new Mat();
        Cv2.CvtColor(roi, bgr, ColorConversionCodes.BGRA2BGR);

        var longest = Math.Max(r.Width, r.Height);
        var output = bgr;
        using var scaled = new Mat();
        if (maxSize > 0 && longest > maxSize)
        {
            var f = (double)maxSize / longest;
            var w = Math.Max(1, (int)Math.Round(r.Width * f));
            var h = Math.Max(1, (int)Math.Round(r.Height * f));
            Cv2.Resize(bgr, scaled, new OpenCvSharp.Size(w, h), 0, 0, InterpolationFlags.Area);
            output = scaled;
        }

        var bytes = jpegQuality > 0
            ? output.ImEncode(".jpg", new ImageEncodingParam(ImwriteFlags.JpegQuality, Math.Clamp(jpegQuality, 1, 100)))
            : output.ImEncode(".png");
        return (bytes, new SizeI(output.Width, output.Height));
    }

    /// <summary>Decodes PNG/JPEG bytes into a BGRA frame.</summary>
    public static Frame Decode(byte[] encoded, long sequence = 0)
    {
        using var bgr = Cv2.ImDecode(encoded, ImreadModes.Color);
        if (bgr.Empty())
        {
            throw new InvalidDataException("Image could not be decoded.");
        }

        using var bgra = new Mat();
        Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
        var bytes = new byte[bgra.Width * bgra.Height * 4];
        bgra.GetArray(out Vec4b[] px);
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(px.AsSpan()).CopyTo(bytes);
        return new Frame(bgra.Width, bgra.Height, bytes, sequence);
    }

    /// <summary>Width/height of an encoded image without keeping the pixels.</summary>
    public static SizeI MeasurePng(byte[] encoded)
    {
        using var m = Cv2.ImDecode(encoded, ImreadModes.Unchanged);
        return m.Empty() ? default : new SizeI(m.Width, m.Height);
    }
}
