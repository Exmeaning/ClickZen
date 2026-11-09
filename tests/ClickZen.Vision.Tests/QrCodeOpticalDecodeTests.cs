using ClickZen.Core.Wireless;
using OpenCvSharp;

namespace ClickZen.Vision.Tests;

/// <summary>
/// Verifies that <see cref="QrCodeEncoder"/> produces optically standard-compliant ISO/IEC 18004 QR bitmaps
/// by decoding the rendered BGRA pixels through OpenCV's independent <see cref="QRCodeDetector"/>.
/// </summary>
public sealed class QrCodeOpticalDecodeTests
{
    [Theory]
    [InlineData("HELLO")]
    [InlineData("WIFI:T:ADB;S:studio-a1b2c3d4;P:98765432;;")]
    [InlineData("WIFI:T:ADB;S:studio-longservice0123456789;P:AbCdEfGh12345678;;")]
    [InlineData("https://github.com/Exmeaning/ClickZen/releases/tag/v2.1.2?mode=lan-wireless-qr-verification-payload-v6")]
    public void OpenCv_qr_detector_decodes_rendered_qr_code_exact_match(string payload)
    {
        var qr = QrCodeEncoder.Encode(payload);
        var (w, h, bgra) = qr.ToBgraPixels(modulePixels: 10, quietZoneModules: 8);

        using var bgraMat = Mat.FromPixelData(h, w, MatType.CV_8UC4, bgra);
        using var gray = new Mat();
        Cv2.CvtColor(bgraMat, gray, ColorConversionCodes.BGRA2GRAY);

        using var straight = new Mat();
        using var detector = new QRCodeDetector();
        var decoded = detector.DetectAndDecode(gray, out _, straight);

        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void Random_adb_wireless_qr_credentials_decode_optically()
    {
        var creds = AdbWirelessQrCredentials.CreateRandom();
        var (w, h, bgra) = QrCodeEncoder.Encode(creds.Payload).ToBgraPixels(modulePixels: 10, quietZoneModules: 8);

        using var bgraMat = Mat.FromPixelData(h, w, MatType.CV_8UC4, bgra);
        using var bgr = new Mat();
        Cv2.CvtColor(bgraMat, bgr, ColorConversionCodes.BGRA2BGR);

        using var straight = new Mat();
        using var detector = new QRCodeDetector();
        var decoded = detector.DetectAndDecode(bgr, out _, straight);

        Assert.Equal(creds.Payload, decoded);
        Assert.True(AdbWirelessQrCredentials.TryParse(decoded, out var roundTripped));
        Assert.Equal(creds, roundTripped);
    }
}
