using ClickZen.Core.Wireless;

namespace ClickZen.Core.Tests.Wireless;

public sealed class QrAndCredentialsTests
{
    [Fact]
    public void Random_credentials_generate_valid_android_adb_wifi_qr_payload_and_round_trip()
    {
        var creds = AdbWirelessQrCredentials.CreateRandom();

        Assert.StartsWith("clickzen-", creds.ServiceName, StringComparison.Ordinal);
        Assert.Equal(6, creds.Password.Length);
        Assert.StartsWith("WIFI:T:ADB;S:", creds.Payload, StringComparison.Ordinal);
        Assert.EndsWith(";;", creds.Payload, StringComparison.Ordinal);

        Assert.True(AdbWirelessQrCredentials.TryParse(creds.Payload, out var parsed));
        Assert.Equal(creds, parsed);
    }

    [Fact]
    public void Credentials_escape_and_unescape_reserved_characters()
    {
        var creds = AdbWirelessQrCredentials.Create("cz;host:01", @"p\w,1""2");

        Assert.Equal(@"WIFI:T:ADB;S:cz\;host\:01;P:p\\w\,1\""2;;", creds.Payload);
        Assert.True(AdbWirelessQrCredentials.TryParse(creds.Payload, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(creds.ServiceName, parsed.ServiceName);
        Assert.Equal(creds.Password, parsed.Password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("WIFI:T:WPA;S:home;P:123456;;")]
    [InlineData("WIFI:T:ADB;S:;P:123456;;")]
    public void Rejects_non_adb_or_incomplete_qr_payloads(string payload) =>
        Assert.False(AdbWirelessQrCredentials.TryParse(payload, out _));

    [Fact]
    public void Qr_encoder_draws_finder_patterns_and_scales_versions_cleanly()
    {
        var v1 = QrCodeEncoder.Encode("HELLO");
        Assert.Equal(1, v1.Version);
        Assert.Equal(21, v1.Size);

        // Top-left, top-right, bottom-left finder corners are dark modules.
        Assert.True(v1[0, 0]);
        Assert.True(v1[3, 3]);
        Assert.False(v1[1, 1]);
        Assert.True(v1[0, v1.Size - 1]);
        Assert.True(v1[v1.Size - 1, 0]);

        var creds = AdbWirelessQrCredentials.Create("clickzen-01020304", "123456");
        var qr = QrCodeEncoder.Encode(creds.Payload);
        Assert.InRange(qr.Version, 2, 4);

        var (w, h, bgra) = qr.ToBgraPixels(modulePixels: 4, quietZoneModules: 3);
        Assert.Equal((qr.Size + 6) * 4, w);
        Assert.Equal(w, h);
        Assert.Equal(w * h * 4, bgra.Length);
    }

    [Fact]
    public void Qr_encoder_rejects_payloads_exceeding_version6_capacity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => QrCodeEncoder.Encode(new string('A', 200)));
}
