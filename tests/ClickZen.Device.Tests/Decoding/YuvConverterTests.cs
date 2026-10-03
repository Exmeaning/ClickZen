using ClickZen.Device.Decoding;

namespace ClickZen.Device.Tests.Decoding;

public sealed class YuvConverterTests
{
    /// <summary>Floating-point reference implementation of BT.601 limited range.</summary>
    private static (byte R, byte G, byte B) Reference601(byte y, byte u, byte v)
    {
        var yy = 1.164 * (y - 16);
        var uu = u - 128.0;
        var vv = v - 128.0;
        static byte C(double d) => (byte)Math.Clamp((int)Math.Round(d), 0, 255);
        return (C(yy + 1.596 * vv), C(yy - 0.392 * uu - 0.813 * vv), C(yy + 2.017 * uu));
    }

    private static (byte[] Y, byte[] U, byte[] V) RandomI420(int w, int h, int seed)
    {
        var rnd = new Random(seed);
        var y = new byte[w * h];
        var u = new byte[((w + 1) / 2) * ((h + 1) / 2)];
        var v = new byte[u.Length];
        rnd.NextBytes(y);
        rnd.NextBytes(u);
        rnd.NextBytes(v);
        return (y, u, v);
    }

    [Theory]
    [InlineData(16, 128, 128, 0, 0, 0)]       // black
    [InlineData(235, 128, 128, 255, 255, 255)] // white
    [InlineData(81, 90, 240, 255, 0, 0)]       // red (BT.601)
    [InlineData(145, 54, 34, 0, 255, 0)]       // green
    [InlineData(41, 240, 110, 0, 0, 255)]      // blue
    public void Primary_colours_are_converted(byte y, byte u, byte v, int r, int g, int b)
    {
        var bgra = new byte[4];
        YuvConverter.I420ToBgra([y], 1, [u], 1, [v], 1, bgra, 1, 1);
        Assert.InRange(bgra[2], r - 2, r + 2);
        Assert.InRange(bgra[1], g - 2, g + 2);
        Assert.InRange(bgra[0], b - 2, b + 2);
        Assert.Equal(255, bgra[3]);
    }

    [Theory]
    [InlineData(64, 36)]
    [InlineData(17, 9)]   // odd sizes exercise chroma rounding and the scalar tail
    [InlineData(1280, 4)]
    public void I420_matches_float_reference_within_one_level(int w, int h)
    {
        var (y, u, v) = RandomI420(w, h, 42);
        var cw = (w + 1) / 2;
        var bgra = new byte[w * h * 4];
        YuvConverter.I420ToBgra(y, w, u, cw, v, cw, bgra, w, h);

        for (var row = 0; row < h; row++)
        {
            for (var col = 0; col < w; col++)
            {
                var ci = (row / 2) * cw + col / 2;
                var (r, g, b) = Reference601(y[row * w + col], u[ci], v[ci]);
                var o = (row * w + col) * 4;
                Assert.InRange(bgra[o + 2], r - 1, r + 1);
                Assert.InRange(bgra[o + 1], g - 1, g + 1);
                Assert.InRange(bgra[o], b - 1, b + 1);
            }
        }
    }

    [Fact]
    public void Nv12_equals_i420_for_same_samples()
    {
        const int w = 48, h = 20;
        var (y, u, v) = RandomI420(w, h, 7);
        var cw = w / 2;
        var uv = new byte[u.Length * 2];
        for (var i = 0; i < u.Length; i++)
        {
            uv[2 * i] = u[i];
            uv[2 * i + 1] = v[i];
        }

        var a = new byte[w * h * 4];
        var b = new byte[w * h * 4];
        YuvConverter.I420ToBgra(y, w, u, cw, v, cw, a, w, h);
        YuvConverter.Nv12ToBgra(y, w, uv, w, b, w, h);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Strides_larger_than_width_are_honoured()
    {
        const int w = 20, h = 4, stride = 32;
        var y = new byte[stride * h];
        var u = new byte[16 * 2];
        var v = new byte[16 * 2];
        Array.Fill(y, (byte)235);
        Array.Fill(u, (byte)128);
        Array.Fill(v, (byte)128);
        // Garbage in padding must not leak into the output.
        for (var r = 0; r < h; r++)
        {
            for (var c = w; c < stride; c++)
            {
                y[r * stride + c] = 0;
            }
        }

        var bgra = new byte[w * h * 4];
        YuvConverter.I420ToBgra(y, stride, u, 16, v, 16, bgra, w, h);
        Assert.All(bgra.Where((_, i) => i % 4 != 3), px => Assert.Equal(255, px));
    }

    [Fact]
    public void Bt709_differs_from_bt601_for_saturated_colour()
    {
        var a = new byte[4];
        var b = new byte[4];
        YuvConverter.I420ToBgra([81], 1, [90], 1, [240], 1, a, 1, 1, YuvColorSpace.Bt601Limited);
        YuvConverter.I420ToBgra([81], 1, [90], 1, [240], 1, b, 1, 1, YuvColorSpace.Bt709Limited);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Too_small_destination_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            YuvConverter.I420ToBgra(new byte[4], 2, new byte[1], 1, new byte[1], 1, new byte[15], 2, 2));
    }
}
