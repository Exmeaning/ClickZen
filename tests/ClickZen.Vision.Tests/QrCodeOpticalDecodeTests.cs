using ClickZen.Core.Automation;
using ClickZen.Core.Wireless;
using ClickZen.Vision;

namespace ClickZen.Vision.Tests;

/// <summary>
/// Verifies that <see cref="QrCodeEncoder"/> BGRA rendering round-trips cleanly through <see cref="ImageCodec"/>
/// PNG compression with crisp white quiet zones and exact module contrast.
/// </summary>
public sealed class QrCodeOpticalDecodeTests
{
    [Fact]
    public void Rendered_adb_wireless_qr_bitmap_round_trips_through_png_with_exact_module_contrast()
    {
        var creds = AdbWirelessQrCredentials.CreateRandom();
        var qr = QrCodeEncoder.Encode(creds.Payload);
        const int modulePx = 6;
        const int quietModules = 4;
        var (w, h, bgra) = qr.ToBgraPixels(modulePx, quietModules);

        var png = ImageCodec.ToPng(new Frame(w, h, bgra, 1));
        var decodedFrame = ImageCodec.Decode(png);

        Assert.Equal(w, decodedFrame.Width);
        Assert.Equal(h, decodedFrame.Height);

        // Quiet zone corner pixel (2, 2) must be pure opaque white (255, 255, 255, 255).
        var qIdx = (2 * w + 2) * 4;
        Assert.Equal((byte)255, decodedFrame.Bgra[qIdx]);
        Assert.Equal((byte)255, decodedFrame.Bgra[qIdx + 1]);
        Assert.Equal((byte)255, decodedFrame.Bgra[qIdx + 2]);
        Assert.Equal((byte)255, decodedFrame.Bgra[qIdx + 3]);

        // Every QR module center pixel after PNG encode/decode must match qr[r, c] with 100% contrast.
        for (var r = 0; r < qr.Size; r++)
        {
            for (var c = 0; c < qr.Size; c++)
            {
                var py = (r + quietModules) * modulePx + modulePx / 2;
                var px = (c + quietModules) * modulePx + modulePx / 2;
                var idx = (py * w + px) * 4;
                var expected = qr[r, c] ? (byte)0 : (byte)255;
                Assert.Equal(expected, decodedFrame.Bgra[idx]);
                Assert.Equal(expected, decodedFrame.Bgra[idx + 1]);
                Assert.Equal(expected, decodedFrame.Bgra[idx + 2]);
            }
        }
    }
}
