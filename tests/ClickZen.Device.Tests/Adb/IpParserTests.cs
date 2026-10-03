using ClickZen.Device.Adb.Parsing;

namespace ClickZen.Device.Tests.Adb;

public sealed class IpParserTests
{
    [Fact]
    public void Parses_ip_addr_output()
    {
        Assert.Equal("192.168.31.142", IpParser.ParseWlanIp(SampleFiles.Read("ip/ip_addr_wlan0.txt")));
    }

    [Fact]
    public void Interface_down_returns_null()
    {
        Assert.Null(IpParser.ParseWlanIp(SampleFiles.Read("ip/ip_addr_wlan0_down.txt")));
    }

    [Fact]
    public void Missing_interface_returns_null()
    {
        Assert.Null(IpParser.ParseWlanIp(SampleFiles.Read("ip/ip_addr_missing.txt")));
    }

    [Fact]
    public void Parses_legacy_ifconfig()
    {
        Assert.Equal("10.0.0.57", IpParser.ParseWlanIp(SampleFiles.Read("ip/ifconfig_wlan0.txt")));
    }

    [Theory]
    [InlineData("    inet 127.0.0.1/8 scope host lo")]
    [InlineData("    inet 169.254.10.20/16 scope link wlan0")]
    [InlineData("    inet 999.1.1.1/24 scope global wlan0")]
    [InlineData(null)]
    [InlineData("")]
    public void Unusable_addresses_are_ignored(string? text)
    {
        Assert.Null(IpParser.ParseWlanIp(text));
    }

    [Fact]
    public void Skips_link_local_then_takes_next()
    {
        var text = "    inet 169.254.1.1/16 scope link wlan0\n    inet 172.20.10.4/28 brd 172.20.10.15 scope global wlan0\n";
        Assert.Equal("172.20.10.4", IpParser.ParseWlanIp(text));
    }

    [Fact]
    public void Route_source_prefers_wlan0()
    {
        Assert.Equal("192.168.1.23", IpParser.ParseRouteSource(SampleFiles.Read("ip/ip_route.txt")));
    }

    [Fact]
    public void Route_source_falls_back_to_first()
    {
        Assert.Equal("10.0.2.15", IpParser.ParseRouteSource("10.0.2.0/24 dev eth0 proto kernel scope link src 10.0.2.15\n"));
    }
}
