using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// Bit-for-bit ISO/IEC 18004 EC Level M golden module matrices verified against Nayuki QR-Code-generator
    /// across all supported versions (1 through 6).
    /// </summary>
    [Theory]
    [InlineData(1, "HELLO", "32fbc389880456d584e32bdf2c31372391c92f9f853e119a75bec5e14c380a23")]
    [InlineData(2, "WIFI:T:ADB;S:c;P:123456;;", "46dd1079dcbab459dd9463d5cb8f671b1b110fe40f3e69df042ac3c21d7659e5")]
    [InlineData(3, "WIFI:T:ADB;S:clickzen-a1b2c3d4;P:654321;;", "f9040d3d47c1b2135f3fe1eecaade9f2f946917827d822544c0bd6081b256e4c")]
    [InlineData(4, "WIFI:T:ADB;S:clickzen-midlen0123;P:AbCdEfGh1234;;", "4a8b570736732d4801037c2edd5a9b82901110cb4703b4cec318db497598af8f")]
    [InlineData(5, "https://github.com/Exmeaning/ClickZen/pull/6?feature=wireless-adb-qr-mdns-v5", "cbbe84520c0654d23b53f46ef54cb9438acd993c9efa17a189e4895e5f313fe2")]
    [InlineData(6, "https://github.com/Exmeaning/ClickZen/releases/tag/v2.1.2?mode=lan-wireless-qr-verification-payload-v6", "c7dc9f21acd491376e2a06d211284e32fabab2c2537bbed2fabd728007723a50")]
    public void Matches_iso18004_reference_module_matrix_across_versions_1_to_6(int expectedVersion, string payload, string expectedSha256)
    {
        var qr = QrCodeEncoder.Encode(payload);
        Assert.Equal(expectedVersion, qr.Version);
        Assert.Equal(17 + 4 * expectedVersion, qr.Size);

        var sb = new StringBuilder(qr.Size * qr.Size);
        for (var r = 0; r < qr.Size; r++)
        {
            for (var c = 0; c < qr.Size; c++)
            {
                sb.Append(qr[r, c] ? '1' : '0');
            }
        }

        var actualHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(sb.ToString())));
        Assert.Equal(expectedSha256, actualHash);
    }

    [Fact]
    public void Qr_encoder_rejects_payloads_exceeding_version6_capacity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => QrCodeEncoder.Encode(new string('A', 200)));
}
