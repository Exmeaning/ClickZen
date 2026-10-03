using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace ClickZen.Device.Decoding;

/// <summary>YUV → RGB matrix and range.</summary>
public enum YuvColorSpace
{
    /// <summary>ITU-R BT.601, limited range (SD; also the Android encoder default for many devices).</summary>
    Bt601Limited,
    /// <summary>ITU-R BT.709, limited range (HD).</summary>
    Bt709Limited,
    /// <summary>BT.601 full range (JPEG-style).</summary>
    Bt601Full,
}

/// <summary>
/// Converts planar YUV 4:2:0 (I420 / YUV420P) or semi-planar NV12 into tightly packed BGRA (alpha 255).
/// Integer fixed-point maths (8-bit fraction) with a <see cref="Vector128"/> fast path for the luma
/// rows; results match the scalar reference exactly, which the tests assert.
/// </summary>
public static class YuvConverter
{
    private readonly record struct Coeffs(int YOffset, int YMul, int RV, int GU, int GV, int BU);

    // Coefficients ×256 (8-bit fixed point).
    private static Coeffs For(YuvColorSpace cs) => cs switch
    {
        // Y' = 1.164(Y-16); R = Y' + 1.596 V'; G = Y' - 0.392 U' - 0.813 V'; B = Y' + 2.017 U'
        YuvColorSpace.Bt601Limited => new Coeffs(16, 298, 409, 100, 208, 516),
        // R = Y' + 1.793 V'; G = Y' - 0.213 U' - 0.533 V'; B = Y' + 2.112 U'
        YuvColorSpace.Bt709Limited => new Coeffs(16, 298, 459, 55, 136, 541),
        // Full range: Y' = Y; R = Y + 1.402 V'; G = Y - 0.344 U' - 0.714 V'; B = Y + 1.772 U'
        _ => new Coeffs(0, 256, 359, 88, 183, 454),
    };

    /// <summary>I420: separate Y, U, V planes; U/V are (w+1)/2 × (h+1)/2.</summary>
    public static void I420ToBgra(
        ReadOnlySpan<byte> y, int yStride,
        ReadOnlySpan<byte> u, int uStride,
        ReadOnlySpan<byte> v, int vStride,
        Span<byte> bgra, int width, int height, YuvColorSpace colorSpace = YuvColorSpace.Bt601Limited)
    {
        Validate(bgra, width, height);
        var c = For(colorSpace);
        for (var row = 0; row < height; row++)
        {
            var yRow = y.Slice(row * yStride, width);
            var uRow = u.Slice((row >> 1) * uStride);
            var vRow = v.Slice((row >> 1) * vStride);
            var dst = bgra.Slice(row * width * 4, width * 4);
            ConvertRow(yRow, uRow, vRow, 1, dst, width, c);
        }
    }

    /// <summary>NV12: Y plane followed by interleaved UV plane.</summary>
    public static void Nv12ToBgra(
        ReadOnlySpan<byte> y, int yStride,
        ReadOnlySpan<byte> uv, int uvStride,
        Span<byte> bgra, int width, int height, YuvColorSpace colorSpace = YuvColorSpace.Bt601Limited)
    {
        Validate(bgra, width, height);
        var c = For(colorSpace);
        for (var row = 0; row < height; row++)
        {
            var yRow = y.Slice(row * yStride, width);
            var uvRow = uv.Slice((row >> 1) * uvStride);
            var dst = bgra.Slice(row * width * 4, width * 4);
            ConvertRow(yRow, uvRow, uvRow[1..], 2, dst, width, c);
        }
    }

    private static void Validate(Span<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Size must be positive.");
        }

        if (bgra.Length < width * height * 4)
        {
            throw new ArgumentException("Destination buffer too small.", nameof(bgra));
        }
    }

    /// <param name="chromaStep">1 for planar U/V, 2 for interleaved NV12.</param>
    private static void ConvertRow(ReadOnlySpan<byte> yRow, ReadOnlySpan<byte> uRow, ReadOnlySpan<byte> vRow, int chromaStep,
        Span<byte> dst, int width, Coeffs c)
    {
        // Process pixel pairs sharing one chroma sample. The vector path handles 8 pixel pairs at once.
        var x = 0;
        if (Vector128.IsHardwareAccelerated && width >= 16)
        {
            x = ConvertRowVector(yRow, uRow, vRow, chromaStep, dst, width, c);
        }

        for (; x < width; x++)
        {
            var ci = (x >> 1) * chromaStep;
            WritePixel(dst, x, yRow[x], uRow[ci], vRow[ci], c);
        }
    }

    private static int ConvertRowVector(ReadOnlySpan<byte> yRow, ReadOnlySpan<byte> uRow, ReadOnlySpan<byte> vRow, int chromaStep,
        Span<byte> dst, int width, Coeffs c)
    {
        var yOff = Vector128.Create(c.YOffset);
        var yMul = Vector128.Create(c.YMul);
        var rv = Vector128.Create(c.RV);
        var gu = Vector128.Create(c.GU);
        var gv = Vector128.Create(c.GV);
        var bu = Vector128.Create(c.BU);
        var half = Vector128.Create(128);
        var round = Vector128.Create(128);
        var zero = Vector128<int>.Zero;
        var max = Vector128.Create(255);

        Span<int> ys = stackalloc int[4];
        Span<int> us = stackalloc int[4];
        Span<int> vs = stackalloc int[4];
        Span<int> rs = stackalloc int[4];
        Span<int> gs = stackalloc int[4];
        Span<int> bs = stackalloc int[4];

        var x = 0;
        var limit = width & ~3;
        for (; x < limit; x += 4)
        {
            for (var k = 0; k < 4; k++)
            {
                var ci = ((x + k) >> 1) * chromaStep;
                ys[k] = yRow[x + k];
                us[k] = uRow[ci];
                vs[k] = vRow[ci];
            }

            var yv = (Vector128.Create(ys[0], ys[1], ys[2], ys[3]) - yOff) * yMul;
            var uv = Vector128.Create(us[0], us[1], us[2], us[3]) - half;
            var vv = Vector128.Create(vs[0], vs[1], vs[2], vs[3]) - half;

            var r = Vector128.Min(Vector128.Max((yv + rv * vv + round) >> 8, zero), max);
            var g = Vector128.Min(Vector128.Max((yv - gu * uv - gv * vv + round) >> 8, zero), max);
            var b = Vector128.Min(Vector128.Max((yv + bu * uv + round) >> 8, zero), max);
            r.CopyTo(rs);
            g.CopyTo(gs);
            b.CopyTo(bs);

            for (var k = 0; k < 4; k++)
            {
                var o = (x + k) * 4;
                dst[o] = (byte)bs[k];
                dst[o + 1] = (byte)gs[k];
                dst[o + 2] = (byte)rs[k];
                dst[o + 3] = 255;
            }
        }

        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WritePixel(Span<byte> dst, int x, byte yb, byte ub, byte vb, Coeffs c)
    {
        var yy = (yb - c.YOffset) * c.YMul;
        var uu = ub - 128;
        var vv = vb - 128;
        var o = x * 4;
        dst[o] = Clamp((yy + c.BU * uu + 128) >> 8);
        dst[o + 1] = Clamp((yy - c.GU * uu - c.GV * vv + 128) >> 8);
        dst[o + 2] = Clamp((yy + c.RV * vv + 128) >> 8);
        dst[o + 3] = 255;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
