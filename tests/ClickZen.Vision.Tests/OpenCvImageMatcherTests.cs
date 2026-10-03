using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;
using ClickZen.Vision;
using OpenCvSharp;

namespace ClickZen.Vision.Tests;

public sealed class OpenCvImageMatcherTests : IDisposable
{
    private readonly OpenCvImageMatcher _matcher = new();

    public void Dispose() => _matcher.Dispose();

    /// <summary>Deterministic noisy frame so templates are unique.</summary>
    private static Frame NoiseFrame(int w, int h, int seed = 1)
    {
        var rnd = new Random(seed);
        var px = new byte[w * h * 4];
        rnd.NextBytes(px);
        for (var i = 3; i < px.Length; i += 4)
        {
            px[i] = 255;
        }

        return new Frame(w, h, px, 1);
    }

    private static TemplateAsset Template(byte[] png, string id = "t") => new() { Id = id, Png = png, Size = ImageCodec.MeasurePng(png) };

    private static Frame Resize(Frame f, double scale)
    {
        using var src = Mat.FromPixelData(f.Height, f.Width, MatType.CV_8UC4, f.Bgra);
        using var dst = new Mat();
        Cv2.Resize(src, dst, new Size((int)(f.Width * scale), (int)(f.Height * scale)), 0, 0, InterpolationFlags.Area);
        dst.GetArray(out Vec4b[] px);
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(px.AsSpan()).ToArray();
        return new Frame(dst.Width, dst.Height, bytes, 2);
    }

    [Fact]
    public void Finds_exact_crop_at_its_location()
    {
        var frame = NoiseFrame(320, 480);
        var t = Template(ImageCodec.CropToPng(frame, new RectI(100, 200, 40, 30)));

        var r = _matcher.Match(frame, default, t, 1.0, new ImageCondition { Threshold = 0.9 });

        Assert.True(r.Found);
        Assert.Equal(new RectI(100, 200, 40, 30), r.Location);
        Assert.True(r.Score > 0.99);
        Assert.Equal(new PointI(120, 215), r.Center);
    }

    [Fact]
    public void Search_area_restricts_and_offsets_result()
    {
        var frame = NoiseFrame(320, 480);
        var t = Template(ImageCodec.CropToPng(frame, new RectI(100, 200, 40, 30)));

        var inside = _matcher.Match(frame, new RectI(80, 180, 100, 100), t, 1.0, new ImageCondition { Threshold = 0.9 });
        var outside = _matcher.Match(frame, new RectI(0, 0, 90, 90), t, 1.0, new ImageCondition { Threshold = 0.9 });

        Assert.True(inside.Found);
        Assert.Equal(100, inside.Location.X);
        Assert.Equal(200, inside.Location.Y);
        Assert.False(outside.Found);
    }

    [Fact]
    public void Threshold_rejects_other_image()
    {
        var frame = NoiseFrame(320, 480, seed: 1);
        var other = NoiseFrame(320, 480, seed: 2);
        var t = Template(ImageCodec.CropToPng(other, new RectI(10, 10, 40, 40)));

        var r = _matcher.Match(frame, default, t, 1.0, new ImageCondition { Threshold = 0.8 });

        Assert.False(r.Found);
        Assert.True(r.Score < 0.5);
    }

    [Fact]
    public void Template_is_rescaled_to_frame_resolution()
    {
        // Template authored on a 2x frame must still match the 1x frame when scale 0.5 is passed.
        var big = NoiseFrame(640, 960);
        var small = Resize(big, 0.5);
        var t = Template(ImageCodec.CropToPng(big, new RectI(200, 400, 120, 80)));

        var unscaled = _matcher.Match(small, default, t, 1.0, new ImageCondition { Threshold = 0.8 });
        var scaled = _matcher.Match(small, default, t, 0.5, new ImageCondition { Threshold = 0.8 });

        Assert.False(unscaled.Found);
        Assert.True(scaled.Found);
        Assert.InRange(scaled.Location.X, 98, 102);
        Assert.InRange(scaled.Location.Y, 198, 202);
    }

    [Fact]
    public void Multi_scale_tolerates_size_mismatch()
    {
        var big = NoiseFrame(400, 400);
        var smaller = Resize(big, 0.9);
        // Smooth template (noise does not survive resampling well), so use a blurred frame.
        var t = Template(ImageCodec.CropToPng(Blur(big), new RectI(100, 100, 120, 120)));
        var target = Blur(smaller);

        var single = _matcher.Match(target, default, t, 1.0, new ImageCondition { Threshold = 0.9 });
        var multi = _matcher.Match(target, default, t, 1.0, new ImageCondition { Threshold = 0.9, MultiScale = true });

        Assert.True(multi.Score > single.Score);
        Assert.True(multi.Found);
    }

    [Fact]
    public void Grayscale_mode_matches()
    {
        var frame = NoiseFrame(200, 200);
        var t = Template(ImageCodec.CropToPng(frame, new RectI(50, 60, 30, 30)));
        var r = _matcher.Match(frame, default, t, 1.0, new ImageCondition { Threshold = 0.9, Grayscale = true });
        Assert.True(r.Found);
        Assert.Equal(50, r.Location.X);
    }

    [Fact]
    public void Uniform_template_does_not_produce_nan()
    {
        var px = new byte[100 * 100 * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = 10;
            px[i + 1] = 200;
            px[i + 2] = 30;
            px[i + 3] = 255;
        }

        var frame = new Frame(100, 100, px);
        var t = Template(ImageCodec.CropToPng(frame, new RectI(0, 0, 20, 20)));
        var r = _matcher.Match(frame, default, t, 1.0, new ImageCondition { Threshold = 0.9 });

        Assert.True(r.Found);
        Assert.False(double.IsNaN(r.Score));
    }

    [Fact]
    public void Template_larger_than_area_is_not_found()
    {
        var frame = NoiseFrame(100, 100);
        var t = Template(ImageCodec.CropToPng(frame, new RectI(0, 0, 80, 80)));
        var r = _matcher.Match(frame, new RectI(0, 0, 40, 40), t, 1.0, new ImageCondition());
        Assert.False(r.Found);
    }

    [Fact]
    public void Codec_round_trip()
    {
        var frame = NoiseFrame(31, 17);
        var back = ImageCodec.Decode(ImageCodec.ToPng(frame));
        Assert.Equal(frame.Size, back.Size);
        Assert.Equal(frame.Bgra, back.Bgra);
    }

    private static Frame Blur(Frame f)
    {
        using var src = Mat.FromPixelData(f.Height, f.Width, MatType.CV_8UC4, f.Bgra);
        using var dst = new Mat();
        Cv2.GaussianBlur(src, dst, new Size(9, 9), 3);
        dst.GetArray(out Vec4b[] px);
        return new Frame(dst.Width, dst.Height, System.Runtime.InteropServices.MemoryMarshal.AsBytes(px.AsSpan()).ToArray(), 3);
    }
}
