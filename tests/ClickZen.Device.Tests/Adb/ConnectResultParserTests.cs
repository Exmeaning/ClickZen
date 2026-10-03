using ClickZen.Device.Adb.Parsing;

namespace ClickZen.Device.Tests.Adb;

public sealed class ConnectResultParserTests
{
    [Theory]
    [InlineData("connected to 192.168.1.23:5555", ConnectResultKind.Connected)]
    [InlineData("already connected to 127.0.0.1:16384", ConnectResultKind.AlreadyConnected)]
    [InlineData("Successfully paired to 192.168.1.23:37099 [guid=adb-R5CT1234ABC-XyZ12a]", ConnectResultKind.Paired)]
    [InlineData("disconnected 192.168.1.23:5555", ConnectResultKind.Disconnected)]
    [InlineData("disconnected everything", ConnectResultKind.Disconnected)]
    public void Success_forms(string text, ConnectResultKind kind)
    {
        var r = ConnectResultParser.Parse(text);
        Assert.True(r.Success);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(text, r.Raw);
    }

    [Theory]
    [InlineData("failed to connect to '192.168.1.23:5555': Connection refused", ConnectResultKind.Refused)]
    [InlineData("cannot connect to 127.0.0.1:62001: 由于目标计算机积极拒绝，无法连接。 (10061)", ConnectResultKind.Refused)]
    [InlineData("cannot connect to 192.168.1.99:5555: A connection attempt failed because the connected party did not properly respond after a period of time (10060)", ConnectResultKind.Timeout)]
    [InlineData("failed to connect to '10.0.0.5:5555': Operation timed out", ConnectResultKind.Timeout)]
    [InlineData("failed to resolve host: 'phone.lan': No such host is known. (11001)", ConnectResultKind.HostNotFound)]
    [InlineData("failed to connect to '10.1.1.1:5555': Network is unreachable", ConnectResultKind.Unreachable)]
    [InlineData("failed to authenticate to 192.168.1.23:5555", ConnectResultKind.Unauthorized)]
    [InlineData("Failed: Wrong password or connection was dropped.", ConnectResultKind.WrongPairingCode)]
    [InlineData("no such device '192.168.1.23:5555'", ConnectResultKind.NoSuchDevice)]
    [InlineData("error: protocol fault (couldn't read status): connection reset", ConnectResultKind.Failed)]
    public void Failure_forms(string text, ConnectResultKind kind)
    {
        var r = ConnectResultParser.Parse(text);
        Assert.False(r.Success);
        Assert.Equal(kind, r.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_is_unknown_failure(string? text)
    {
        var r = ConnectResultParser.Parse(text);
        Assert.False(r.Success);
        Assert.Equal(ConnectResultKind.Unknown, r.Kind);
    }

    [Fact]
    public void Trims_newlines()
    {
        var r = ConnectResultParser.Parse("connected to 1.2.3.4:5555\n");
        Assert.True(r.Success);
        Assert.Equal("connected to 1.2.3.4:5555", r.Raw);
    }
}
