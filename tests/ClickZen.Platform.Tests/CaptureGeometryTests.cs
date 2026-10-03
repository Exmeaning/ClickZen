using ClickZen.Core.Geometry;
using ClickZen.Platform.Capture;

namespace ClickZen.Platform.Tests;

public sealed class CaptureGeometryTests
{
    // A typical Windows 11 window at 150 % scaling, everything in physical pixels:
    // extended frame bounds (what WGC captures) at (100, 50) 1200x900,
    // title bar 45 px + 1 px border → client origin at (101, 96), client 1198x853.
    private static readonly PointI FrameOrigin = new(100, 50);
    private static readonly SizeI FrameSize = new(1200, 900);
    private static readonly PointI ClientOrigin = new(101, 96);
    private static readonly SizeI ClientSize = new(1198, 853);

    [Fact]
    public void Whole_client_area_is_offset_by_title_bar_and_border()
    {
        var r = CaptureGeometry.ClientCropInCapture(FrameOrigin, FrameSize, FrameSize, ClientOrigin, ClientSize, null);
        Assert.Equal(new RectI(1, 46, 1198, 853), r);
    }

    [Fact]
    public void Crop_is_relative_to_client_origin()
    {
        var crop = new RectI(10, 20, 540, 960 - 200);
        var r = CaptureGeometry.ClientCropInCapture(FrameOrigin, FrameSize, FrameSize, ClientOrigin, ClientSize, crop);
        Assert.Equal(new RectI(11, 66, 540, 760), r);
    }

    [Fact]
    public void Crop_is_clamped_to_client_area()
    {
        var crop = new RectI(-5, 800, 2000, 500);
        var r = CaptureGeometry.ClientCropInCapture(FrameOrigin, FrameSize, FrameSize, ClientOrigin, ClientSize, crop);
        Assert.Equal(new RectI(1, 46 + 800, 1198, 53), r);
    }

    [Fact]
    public void Crop_outside_client_area_is_empty()
    {
        var r = CaptureGeometry.ClientCropInCapture(FrameOrigin, FrameSize, FrameSize, ClientOrigin, ClientSize, new RectI(5000, 0, 10, 10));
        Assert.True(r.IsEmpty);
    }

    [Fact]
    public void PrintWindow_image_includes_invisible_borders()
    {
        // GetWindowRect is 7 px larger left/right/bottom than the extended frame bounds on Windows 10/11.
        var windowRect = new RectI(93, 50, 1214, 907);
        var r = CaptureGeometry.ClientCropInCapture(new PointI(windowRect.X, windowRect.Y), windowRect.Size, windowRect.Size,
            ClientOrigin, ClientSize, null);
        Assert.Equal(new RectI(8, 46, 1198, 853), r);
    }

    [Fact]
    public void Image_smaller_than_bounds_is_scaled_proportionally()
    {
        // e.g. a DPI-virtualised capture at half resolution.
        var r = CaptureGeometry.ClientCropInCapture(new PointI(0, 0), new SizeI(1000, 800), new SizeI(500, 400),
            new PointI(10, 40), new SizeI(980, 750), new RectI(0, 0, 980, 750));
        Assert.Equal(new RectI(5, 20, 490, 375), r);
    }

    [Fact]
    public void Image_clipped_when_window_partly_offscreen_or_mid_resize()
    {
        // Content shrank (window resized) before the pool was recreated: clamp to the image.
        var r = CaptureGeometry.ClientCropInCapture(FrameOrigin, FrameSize, new SizeI(600, 400), ClientOrigin, ClientSize, null);
        Assert.Equal(new SizeI(600, 400), new SizeI(r.Right, r.Bottom));
    }

    [Fact]
    public void Empty_inputs_yield_empty()
    {
        Assert.True(CaptureGeometry.ClientCropInCapture(FrameOrigin, FrameSize, default, ClientOrigin, ClientSize, null).IsEmpty);
        Assert.True(CaptureGeometry.ClientCropInCapture(FrameOrigin, FrameSize, FrameSize, ClientOrigin, default, null).IsEmpty);
    }

    [Fact]
    public void Effective_crop_defaults_to_whole_client()
    {
        Assert.Equal(new RectI(0, 0, 800, 600), CaptureGeometry.EffectiveCrop(null, new SizeI(800, 600)));
        Assert.Equal(new RectI(0, 0, 800, 600), CaptureGeometry.EffectiveCrop(new RectI(5, 5, 0, 0), new SizeI(800, 600)));
        Assert.Equal(new RectI(700, 0, 100, 600), CaptureGeometry.EffectiveCrop(new RectI(700, -10, 300, 900), new SizeI(800, 600)));
    }

    [Fact]
    public void Client_frame_round_trip()
    {
        var crop = new RectI(30, 70, 720, 1280);
        var f = CaptureGeometry.ClientToFrame(new PointD(130, 170), crop);
        Assert.Equal(new PointD(100, 100), f);
        Assert.Equal(new PointD(130, 170), CaptureGeometry.FrameToClient(f, crop));
    }
}

public sealed class BgraBufferTests
{
    private static byte[] MakeImage(int width, int height, int rowPitch)
    {
        var buf = new byte[rowPitch * height];
        Array.Fill(buf, (byte)0xEE); // padding marker
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * rowPitch + x * 4;
                buf[i] = (byte)x;
                buf[i + 1] = (byte)y;
                buf[i + 2] = (byte)(x + y);
                buf[i + 3] = 0;
            }
        }

        return buf;
    }

    [Fact]
    public void Copies_region_skipping_row_padding()
    {
        const int w = 10, h = 6, pitch = 64; // 40 bytes of pixels + 24 bytes padding per row
        var src = MakeImage(w, h, pitch);

        var region = new RectI(3, 2, 4, 3);
        var dst = BgraBuffer.CopyRegion(src, pitch, region);

        Assert.Equal(4 * 3 * 4, dst.Length);
        for (var y = 0; y < region.Height; y++)
        {
            for (var x = 0; x < region.Width; x++)
            {
                var i = (y * region.Width + x) * 4;
                Assert.Equal((byte)(region.X + x), dst[i]);
                Assert.Equal((byte)(region.Y + y), dst[i + 1]);
                Assert.Equal((byte)(region.X + x + region.Y + y), dst[i + 2]);
                Assert.Equal(0, dst[i + 3]);
            }
        }

        Assert.DoesNotContain((byte)0xEE, dst);
    }

    [Fact]
    public void Full_copy_with_last_row_unpadded_buffer()
    {
        // Mapped textures may end right after the last row's pixels.
        const int w = 5, h = 3, pitch = 32;
        var src = MakeImage(w, h, pitch).AsSpan(0, pitch * (h - 1) + w * 4).ToArray();
        var dst = BgraBuffer.CopyRegion(src, pitch, new RectI(0, 0, w, h));
        Assert.Equal(w * h * 4, dst.Length);
        Assert.Equal((byte)4, dst[(2 * w + 4) * 4]);
    }

    [Fact]
    public void Force_opaque_sets_alpha()
    {
        var src = MakeImage(4, 4, 16);
        var dst = BgraBuffer.CopyRegion(src, 16, new RectI(0, 0, 4, 4), forceOpaque: true);
        for (var i = 3; i < dst.Length; i += 4)
        {
            Assert.Equal(255, dst[i]);
        }
    }

    [Fact]
    public void Out_of_bounds_region_throws()
    {
        var src = MakeImage(4, 4, 16);
        Assert.Throws<ArgumentOutOfRangeException>(() => BgraBuffer.CopyRegion(src, 16, new RectI(2, 0, 4, 4)));
        Assert.Throws<ArgumentOutOfRangeException>(() => BgraBuffer.CopyRegion(src, 16, new RectI(0, 2, 4, 4)));
    }

    [Fact]
    public void Black_detection()
    {
        var black = new byte[64 * 64 * 4];
        Assert.True(BgraBuffer.IsLikelyBlack(black, 64, 64));

        var white = Enumerable.Repeat((byte)255, 64 * 64 * 4).ToArray();
        Assert.False(BgraBuffer.IsLikelyBlack(white, 64, 64));

        var mostlyBlack = new byte[64 * 64 * 4];
        for (var i = 0; i < mostlyBlack.Length; i += 4 * 7)
        {
            mostlyBlack[i] = mostlyBlack[i + 1] = mostlyBlack[i + 2] = 200;
        }

        Assert.False(BgraBuffer.IsLikelyBlack(mostlyBlack, 64, 64));
    }
}
