using ClickZen.Core.Automation;
using ClickZen.Core.Emulators;
using ClickZen.Core.Geometry;
using OpenCvSharp;

namespace ClickZen.Vision.Tests;

/// <summary>
/// One-click window binding finds the Android screen inside an emulator window by matching the device's own
/// screenshot (adb screencap) against the window picture. Synthetic windows: a scaled "screen" with a title bar on top
/// and a tool bar on the right, as MuMu / LDPlayer / Nox draw them.
/// </summary>
public sealed class ScreenAreaMatchingTests : IDisposable
{
    private readonly OpenCvImageMatcher _matcher = new();

    public void Dispose() => _matcher.Dispose();

    /// <summary>A phone-like screen: gradients and blocks (smooth enough to survive scaling, unique enough to match).</summary>
    private static Mat Screen(int w, int h)
    {
        var m = new Mat(h, w, MatType.CV_8UC3);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                m.Set(y, x, new Vec3b((byte)(x * 255 / w), (byte)(y * 255 / h), (byte)((x + y) % 256)));
            }
        }

        Cv2.Rectangle(m, new Rect(w / 10, h / 8, w / 3, h / 5), new Scalar(20, 200, 30), -1);
        Cv2.Circle(m, new Point(w * 2 / 3, h * 2 / 3), Math.Min(w, h) / 6, new Scalar(240, 240, 240), -1);
        Cv2.PutText(m, "ClickZen", new Point(w / 8, h / 2), HersheyFonts.HersheySimplex, Math.Max(1, w / 300.0), new Scalar(0, 0, 0), 3);
        return m;
    }

    /// <summary>The window: the screen scaled to <paramref name="screenRect"/>, flat bars everywhere else.</summary>
    private static Frame Window(Mat screen, SizeI client, RectI screenRect)
    {
        using var canvas = new Mat(client.Height, client.Width, MatType.CV_8UC3, new Scalar(48, 48, 48));
        Cv2.Rectangle(canvas, new Rect(0, 0, client.Width, screenRect.Y), new Scalar(230, 230, 230), -1); // title bar
        using var scaled = new Mat();
        Cv2.Resize(screen, scaled, new Size(screenRect.Width, screenRect.Height), 0, 0, InterpolationFlags.Area);
        scaled.CopyTo(new Mat(canvas, new Rect(screenRect.X, screenRect.Y, screenRect.Width, screenRect.Height)));
        using var bgra = new Mat();
        Cv2.CvtColor(canvas, bgra, ColorConversionCodes.BGR2BGRA);
        bgra.GetArray(out Vec4b[] px);
        return new Frame(client.Width, client.Height, System.Runtime.InteropServices.MemoryMarshal.AsBytes(px.AsSpan()).ToArray(), 1);
    }

    private static byte[] Png(Mat m) => m.ImEncode(".png");

    [Fact]
    public void Finds_a_landscape_screen_below_a_title_bar_and_left_of_a_tool_bar()
    {
        using var screen = Screen(1920, 1080);
        // 1330×760 client: 40 px title bar, 1280×720 screen, 50 px tool bar on the right.
        var window = Window(screen, new SizeI(1330, 760), new RectI(0, 40, 1280, 720));

        var area = ScreenAreaLocator.FromScreenshot(window, Png(screen), new SizeI(1920, 1080), _matcher);

        Assert.NotNull(area);
        Assert.Equal(ScreenAreaMethod.ScreenshotMatch, area.Method);
        var crop = Assert.IsType<RectI>(area.Crop);
        Assert.InRange(crop.X, 0, 2);
        Assert.InRange(crop.Y, 38, 42);
        Assert.InRange(crop.Width, 1276, 1284);
        Assert.InRange(crop.Height, 716, 724);
        Assert.True(area.Confidence >= ScreenAreaLocator.ConfidentEnough, $"confidence {area.Confidence}");
    }

    [Fact]
    public void Finds_a_portrait_screen_with_side_bars()
    {
        using var screen = Screen(720, 1280);
        // Portrait screen 405×720 centred in a 520×752 client below a 32 px title bar.
        var window = Window(screen, new SizeI(520, 752), new RectI(57, 32, 405, 720));

        var area = ScreenAreaLocator.FromScreenshot(window, Png(screen), new SizeI(720, 1280), _matcher);

        var crop = Assert.IsType<RectI>(area?.Crop);
        Assert.InRange(crop.X, 55, 59);
        Assert.InRange(crop.Y, 30, 34);
        Assert.InRange(crop.Height, 716, 724);
    }

    [Fact]
    public void Unrelated_picture_does_not_match()
    {
        using var screen = Screen(1920, 1080);
        using var other = new Mat(1080, 1920, MatType.CV_8UC3);
        Cv2.Randu(other, new Scalar(0, 0, 0), new Scalar(255, 255, 255));
        var window = Window(other, new SizeI(1330, 760), new RectI(0, 40, 1280, 720));

        Assert.Null(ScreenAreaLocator.FromScreenshot(window, Png(screen), new SizeI(1920, 1080), _matcher));
    }

    [Fact]
    public void Candidate_scales_fit_the_window()
    {
        var scales = ScreenAreaLocator.CandidateScales(new SizeI(1330, 760), new SizeI(1920, 1080));
        Assert.NotEmpty(scales);
        Assert.Contains(scales, s => Math.Abs(s - 720.0 / 1080) < 0.001); // height minus the 40 px bar
        Assert.All(scales, s => Assert.True(1920 * s <= 1331 && 1080 * s <= 761));
        Assert.Equal(scales.OrderByDescending(s => s), scales);
        Assert.Empty(ScreenAreaLocator.CandidateScales(default, new SizeI(1, 1)));
    }
}
