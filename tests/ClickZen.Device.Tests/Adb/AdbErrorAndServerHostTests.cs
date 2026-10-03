using System.Net.Sockets;
using ClickZen.Device.Adb;
using AsacAdbException = AdvancedSharpAdbClient.Exceptions.AdbException;
using AsacDeviceNotFound = AdvancedSharpAdbClient.Exceptions.DeviceNotFoundException;

namespace ClickZen.Device.Tests.Adb;

public sealed class AdbErrorMapperTests
{
    [Fact]
    public void Device_not_found_exception()
    {
        var ex = AdbErrorMapper.Map(new AsacDeviceNotFound("R5CT"), "R5CT", "shell");
        Assert.Equal(AdbErrorKind.DeviceNotFound, ex.Kind);
        Assert.Equal("R5CT", ex.Serial);
        Assert.IsType<AsacDeviceNotFound>(ex.InnerException);
    }

    [Theory]
    [InlineData("device unauthorized.\nThis adb server's $ADB_VENDOR_KEYS is not set", AdbErrorKind.Unauthorized)]
    [InlineData("device still authorizing", AdbErrorKind.Unauthorized)]
    [InlineData("device offline", AdbErrorKind.Offline)]
    [InlineData("device 'R5CT' not found", AdbErrorKind.DeviceNotFound)]
    [InlineData("no devices/emulators found", AdbErrorKind.DeviceNotFound)]
    [InlineData("something odd", AdbErrorKind.CommandFailed)]
    public void Server_fail_messages(string adbError, AdbErrorKind expected)
    {
        var asac = new AsacAdbException("An error occurred while reading a response from ADB: " + adbError, adbError);
        Assert.Equal(expected, AdbErrorMapper.Classify(asac));
        var mapped = AdbErrorMapper.Map(asac);
        Assert.Contains(adbError.Trim(), mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Socket_errors()
    {
        Assert.Equal(AdbErrorKind.ConnectionRefused, AdbErrorMapper.Classify(new SocketException((int)SocketError.ConnectionRefused)));
        Assert.Equal(AdbErrorKind.Timeout, AdbErrorMapper.Classify(new SocketException((int)SocketError.TimedOut)));
        Assert.Equal(AdbErrorKind.CommandFailed, AdbErrorMapper.Classify(new SocketException((int)SocketError.ConnectionReset)));
        var wrapped = new AsacAdbException("receive failed", new SocketException((int)SocketError.ConnectionRefused));
        Assert.Equal(AdbErrorKind.ConnectionRefused, AdbErrorMapper.Classify(wrapped));
    }

    [Fact]
    public void Misc()
    {
        Assert.Equal(AdbErrorKind.Timeout, AdbErrorMapper.Classify(new TimeoutException()));
        Assert.Equal(AdbErrorKind.CommandFailed, AdbErrorMapper.Classify(new IOException("broken pipe")));
        Assert.Equal(AdbErrorKind.CommandFailed, AdbErrorMapper.Classify(new AdvancedSharpAdbClient.Exceptions.ShellCommandUnresponsiveException(new IOException())));
        Assert.Equal(AdbErrorKind.Unknown, AdbErrorMapper.Classify(new InvalidOperationException("x")));
        Assert.Equal(AdbErrorKind.Offline, AdbErrorMapper.Classify(new AggregateException(new AsacAdbException("x", "device offline"))));
    }

    [Fact]
    public void Own_exception_passes_through()
    {
        var own = new AdbException(AdbErrorKind.Timeout, "t");
        Assert.Same(own, AdbErrorMapper.Map(own));
        Assert.Equal(AdbErrorKind.Timeout, AdbErrorMapper.Classify(own));
    }
}

public sealed class AdbServerHostTests
{
    private const int R = 20; // AdbServer.RequiredAdbVersion.Build in ASAC 3.6.16

    [Theory]
    [InlineData(null, 41, "Start")]
    [InlineData(null, null, "Fail")]
    [InlineData(41, 41, "Reuse")]
    [InlineData(41, 40, "Reuse")]  // running newer than ours
    [InlineData(40, 41, "Reuse")]  // one behind: tolerated
    [InlineData(39, 41, "Restart")]
    [InlineData(41, null, "Reuse")]
    [InlineData(R - 1, null, "Fail")]
    [InlineData(R - 1, R - 2, "Fail")] // too old and ours is not newer
    public void Decide(int? running, int? exe, string expected)
    {
        Assert.Equal(expected, AdbServerHost.Decide(running, exe).ToString());
    }

    [Fact]
    public void Required_version_matches_assumption()
    {
        Assert.Equal(R, AdvancedSharpAdbClient.AdbServer.RequiredAdbVersion.Build);
    }

    [Fact]
    public void Resolve_adb_path_prefers_existing_custom()
    {
        var temp = Path.GetTempFileName();
        try
        {
            Assert.Equal(temp, AdbServerHost.ResolveAdbPath(temp, @"C:\app\ThirdParty\adb.exe"));
            Assert.Equal(temp, AdbServerHost.ResolveAdbPath($"  \"{temp}\" ", @"C:\app\ThirdParty\adb.exe"));
            Assert.Equal(@"C:\app\ThirdParty\adb.exe", AdbServerHost.ResolveAdbPath(temp + ".missing", @"C:\app\ThirdParty\adb.exe"));
            Assert.Equal(@"C:\app\ThirdParty\adb.exe", AdbServerHost.ResolveAdbPath("", @"C:\app\ThirdParty\adb.exe"));
            Assert.Equal(@"C:\app\ThirdParty\adb.exe", AdbServerHost.ResolveAdbPath(null, @"C:\app\ThirdParty\adb.exe"));
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
