using ClickZen.Core.Devices;
using ClickZen.Device.Adb;
using ClickZen.Device.Adb.Parsing;

namespace ClickZen.Device.Tests.Adb;

public sealed class DeviceClassifierTests
{
    [Theory]
    [InlineData("emulator-5554", ConnectionKind.Emulator)]
    [InlineData("EMULATOR-5556", ConnectionKind.Emulator)]
    [InlineData("127.0.0.1:16384", ConnectionKind.Emulator)]
    [InlineData("127.0.0.1:7555", ConnectionKind.Emulator)]
    [InlineData("localhost:5555", ConnectionKind.Emulator)]
    [InlineData("LOCALHOST:62001", ConnectionKind.Emulator)]
    [InlineData("[::1]:5555", ConnectionKind.Emulator)]
    [InlineData("192.168.1.23:5555", ConnectionKind.Wireless)]
    [InlineData("10.0.0.5:41237", ConnectionKind.Wireless)]
    [InlineData("phone.lan:5555", ConnectionKind.Wireless)]
    [InlineData("[fe80::1]:5555", ConnectionKind.Wireless)]
    [InlineData("adb-R5CT1234ABC-XyZ12a._adb-tls-connect._tcp", ConnectionKind.Wireless)]
    [InlineData("adb-R5CT1234ABC-XyZ12a._adb-tls-connect._tcp.", ConnectionKind.Wireless)]
    [InlineData("R5CT1234ABC", ConnectionKind.Usb)]
    [InlineData("0123456789ABCDEF", ConnectionKind.Usb)]
    [InlineData("emulator-", ConnectionKind.Usb)]
    [InlineData("emulator-abc", ConnectionKind.Usb)]
    [InlineData("weird:serial", ConnectionKind.Usb)]
    [InlineData("host:99999", ConnectionKind.Usb)]
    [InlineData("", ConnectionKind.Usb)]
    [InlineData(null, ConnectionKind.Usb)]
    public void Classify(string? serial, ConnectionKind expected)
    {
        Assert.Equal(expected, DeviceClassifier.Classify(serial));
    }

    [Theory]
    [InlineData("emulator-5554", 5555)]
    [InlineData("emulator-5556", 5557)]
    [InlineData("127.0.0.1:5555", null)]
    [InlineData("R5CT", null)]
    public void Emulator_adb_port(string serial, int? expected)
    {
        Assert.Equal(expected, DeviceClassifier.EmulatorAdbPort(serial));
    }
}

public sealed class EmulatorPresetsTests
{
    [Theory]
    [InlineData(16384)]
    [InlineData(16416)]
    [InlineData(16448)]
    [InlineData(16480)]
    [InlineData(7555)]
    [InlineData(5555)]
    [InlineData(5557)]
    [InlineData(5559)]
    [InlineData(5561)]
    [InlineData(62001)]
    [InlineData(62025)]
    [InlineData(62026)]
    [InlineData(21503)]
    [InlineData(54001)]
    public void Required_ports_are_present(int port)
    {
        Assert.Contains(port, EmulatorPresets.AllPorts);
    }

    [Fact]
    public void All_ports_are_distinct_and_valid()
    {
        Assert.Equal(EmulatorPresets.AllPorts.Count, EmulatorPresets.AllPorts.Distinct().Count());
        Assert.All(EmulatorPresets.AllPorts, p => Assert.InRange(p, 1, 65535));
        Assert.Equal(EmulatorPresets.All.Count, EmulatorPresets.All.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void Port_5555_is_shared()
    {
        var ids = EmulatorPresets.ForPort(5555).Select(p => p.Id).ToArray();
        Assert.Contains("ldplayer", ids);
        Assert.Contains("bluestacks", ids);
        Assert.Contains("androidstudio", ids);
    }
}

public sealed class HostPortParserTests
{
    [Theory]
    [InlineData("192.168.1.5", "192.168.1.5", 5555)]
    [InlineData("192.168.1.5:5556", "192.168.1.5", 5556)]
    [InlineData("  phone.lan:40000 ", "phone.lan", 40000)]
    [InlineData("[fe80::1]:5555", "fe80::1", 5555)]
    [InlineData("[fe80::1]", "fe80::1", 5555)]
    [InlineData("fe80::1", "fe80::1", 5555)]
    public void Valid(string input, string host, int port)
    {
        Assert.True(HostPortParser.TryParse(input, 5555, out var h, out var p));
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(":5555")]
    [InlineData("1.2.3.4:")]
    [InlineData("1.2.3.4:0")]
    [InlineData("1.2.3.4:70000")]
    [InlineData("1.2.3.4:abc")]
    [InlineData("1.2.3.4 :5555")]
    [InlineData("[fe80::1")]
    [InlineData("[nothex]:5555")]
    [InlineData("a:b:c")]
    public void Invalid(string? input)
    {
        Assert.False(HostPortParser.TryParse(input, 5555, out _, out _));
    }

    [Fact]
    public void Port_required_when_no_default()
    {
        Assert.False(HostPortParser.TryParse("192.168.1.5", null, out _, out _));
        Assert.True(HostPortParser.TryParse("192.168.1.5:37099", null, out _, out var port));
        Assert.Equal(37099, port);
    }

    [Fact]
    public void Format_brackets_ipv6()
    {
        Assert.Equal("1.2.3.4:5555", HostPortParser.Format("1.2.3.4", 5555));
        Assert.Equal("[fe80::1]:5555", HostPortParser.Format("fe80::1", 5555));
        Assert.Equal("[fe80::1]:5555", HostPortParser.Normalize("fe80::1"));
        Assert.Equal("10.0.0.2:5555", HostPortParser.Normalize("10.0.0.2"));
        Assert.Null(HostPortParser.Normalize("bad host"));
    }
}

public sealed class DeviceListParserTests
{
    [Fact]
    public void Parses_devices_l()
    {
        var list = DeviceListParser.Parse(SampleFiles.Read("devices/devices_l.txt"));
        Assert.Equal(6, list.Count);

        Assert.Equal(new DeviceListEntry("R5CT1234ABC", DeviceAdbState.Online, "device", "SM G991B"), list[0]);
        Assert.Equal("emulator-5554", list[1].Serial);
        Assert.Equal(DeviceAdbState.Offline, list[2].State);
        Assert.Equal("adb-R5CT1234ABC-XyZ12a._adb-tls-connect._tcp", list[3].Serial);
        Assert.Equal(DeviceAdbState.Unauthorized, list[3].State);
        Assert.Equal(DeviceAdbState.Other, list[4].State);
        Assert.Equal("no permissions", list[4].RawState);
        Assert.Equal("SM S9080", list[5].Model);
    }

    [Fact]
    public void Parses_track_devices_payload()
    {
        var list = DeviceListParser.Parse(SampleFiles.Read("devices/track_devices.txt"));
        Assert.Collection(
            list,
            e => Assert.Equal(("R5CT1234ABC", DeviceAdbState.Online), (e.Serial, e.State)),
            e => Assert.Equal(("emulator-5554", DeviceAdbState.Offline), (e.Serial, e.State)),
            e => Assert.Equal(("127.0.0.1:7555", DeviceAdbState.Unauthorized), (e.Serial, e.State)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("List of devices attached\n\n")]
    [InlineData("* daemon not running; starting now at tcp:5037\n* daemon started successfully\nList of devices attached\n")]
    public void Empty(string? payload)
    {
        Assert.Empty(DeviceListParser.Parse(payload));
    }

    [Theory]
    [InlineData("device", DeviceAdbState.Online)]
    [InlineData("offline", DeviceAdbState.Offline)]
    [InlineData("connecting", DeviceAdbState.Offline)]
    [InlineData("unauthorized", DeviceAdbState.Unauthorized)]
    [InlineData("authorizing", DeviceAdbState.Unauthorized)]
    [InlineData("recovery", DeviceAdbState.Other)]
    [InlineData("bootloader", DeviceAdbState.Other)]
    [InlineData("sideload", DeviceAdbState.Other)]
    public void Map_state(string state, DeviceAdbState expected)
    {
        Assert.Equal(expected, DeviceListParser.MapState(state));
    }

    [Fact]
    public void Duplicates_are_dropped()
    {
        Assert.Single(DeviceListParser.Parse("a\tdevice\na\toffline\n"));
    }
}

public sealed class PropParserTests
{
    [Fact]
    public void Parses_prop_dump()
    {
        var text = "ro.product.brand=Xiaomi\r\nro.product.manufacturer=Xiaomi\r\nro.product.model=2201123C\r\nro.build.version.release=13\r\nro.build.version.sdk=33\r\n";
        var p = PropParser.Parse(text);
        Assert.Equal(new DeviceProps("Xiaomi", "2201123C", "13", 33), p);
    }

    [Fact]
    public void Falls_back_to_manufacturer_and_prettifies()
    {
        var text = "ro.product.brand=\nro.product.manufacturer=google\nro.product.model=Pixel 7\nro.build.version.release=14\nro.build.version.sdk=34\n";
        var p = PropParser.Parse(text);
        Assert.Equal("Google", p.Brand);
        Assert.Equal("Pixel 7", p.Model);
    }

    [Fact]
    public void Missing_values_are_blank()
    {
        var p = PropParser.Parse("garbage\n");
        Assert.Equal(new DeviceProps("", "", "", 0), p);
    }

    [Theory]
    [InlineData("google", "Google")]
    [InlineData("OnePlus", "OnePlus")]
    [InlineData("HUAWEI", "HUAWEI")]
    [InlineData("", "")]
    public void Prettify(string input, string expected)
    {
        Assert.Equal(expected, PropParser.PrettifyBrand(input));
    }

    [Fact]
    public void Value_may_contain_equals()
    {
        var kv = PropParser.ParseKeyValues("a=b=c\n=skip\n");
        Assert.Equal("b=c", kv["a"]);
        Assert.Single(kv);
    }
}
