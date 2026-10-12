using ClickZen.Core.Emulators;

namespace ClickZen.Core.Tests.Emulators;

public sealed class EmulatorConfigParserTests
{
    // Shape of D:\Program Files\Netease\MuMuPlayer\vms\MuMuPlayerGlobal-12.0-0\configs\vm_config.json (MuMu 5.28 Global).
    private const string MuMuVmConfig = """
        {
          "display": { "systemmouse": "true" },
          "vm": {
            "cpu": "4",
            "memory": "5",
            "nat": {
              "port_forward": {
                "adb": { "guest_ip": "192.168.1.102", "host_port": "16384" },
                "api": { "host_port": "17417" },
                "event": { "host_port": "19462" },
                "input": { "host_port": "18461" }
              }
            },
            "root": "true"
          }
        }
        """;

    [Fact]
    public void MuMu_vm_config_port()
    {
        Assert.Equal(16384, EmulatorConfigParsers.MuMuVmConfigAdbPort(MuMuVmConfig));
        Assert.Equal(16416, EmulatorConfigParsers.MuMuVmConfigAdbPort("""{"vm":{"nat":{"port_forward":{"adb":{"host_port":16416}}}}}"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"vm":{}}""")]
    [InlineData("""{"vm":{"nat":{"port_forward":{"adb":{"host_port":"abc"}}}}}""")]
    [InlineData("""{"vm":{"nat":{"port_forward":{"adb":{"host_port":"70000"}}}}}""")]
    [InlineData("""{"vm":{"nat":{"port_forward":{"adb":{"host_port":0}}}}}""")]
    [InlineData("""[1, 2]""")]
    public void MuMu_vm_config_without_port(string? json)
    {
        Assert.Null(EmulatorConfigParsers.MuMuVmConfigAdbPort(json));
    }

    [Fact]
    public void MuMu_player_name_and_bridge()
    {
        const string extra = """{ "relateId": "", "playerName": "Android Device", "status": 0 }""";
        Assert.Equal("Android Device", EmulatorConfigParsers.MuMuPlayerName(extra));
        Assert.Null(EmulatorConfigParsers.MuMuPlayerName("""{ "playerName": "  " }"""));
        Assert.Null(EmulatorConfigParsers.MuMuPlayerName("{"));

        Assert.True(EmulatorConfigParsers.MuMuBridgeEnabled("""{"customer":{"network_bridge_opened":"true"}}"""));
        Assert.False(EmulatorConfigParsers.MuMuBridgeEnabled("""{"customer":{"network_bridge_opened":"false"}}"""));
        Assert.False(EmulatorConfigParsers.MuMuBridgeEnabled(null));
    }

    [Theory]
    [InlineData("MuMuPlayerGlobal-12.0-0", 0)]
    [InlineData("MuMuPlayer-12.0-3", 3)]
    [InlineData("MuMuPlayer-15.0-12", 12)]
    [InlineData("YXArkNights-12.0-1", 1)]
    [InlineData("mumuplayer-12.0-2", 2)]
    [InlineData("MuMuPlayerGlobal-12.0-base", null)]
    [InlineData("MuMuPlayer-12.0", null)]
    [InlineData("leidian0", null)]
    [InlineData(null, null)]
    public void MuMu_vm_index(string? name, int? expected)
    {
        Assert.Equal(expected, EmulatorConfigParsers.MuMuVmIndex(name));
        Assert.Equal(expected is not null, EmulatorConfigParsers.IsMuMuVmName(name));
    }

    [Fact]
    public void VBox_forwarding_rule_for_adb()
    {
        const string nox = """
            <?xml version="1.0"?>
            <VirtualBox xmlns="http://www.virtualbox.org/" version="1.12-windows">
              <Machine uuid="{x}" name="Nox_2">
                <Hardware>
                  <Network>
                    <Adapter slot="0" enabled="true">
                      <NAT>
                        <Forwarding name="port1" proto="1" hostip="127.0.0.1" hostport="62125" guestport="5554"/>
                        <Forwarding name="port2" proto="1" hostip="127.0.0.1" hostport="62026" guestport="5555"/>
                      </NAT>
                    </Adapter>
                  </Network>
                </Hardware>
              </Machine>
            </VirtualBox>
            """;
        Assert.Equal(62026, EmulatorConfigParsers.VBoxAdbHostPort(nox));
    }

    [Fact]
    public void VBox_forwarding_rule_from_a_broken_file()
    {
        const string truncated = """<Machine name="MEmu_1"><NAT><Forwarding name="ADB" proto="1" hostip="127.0.0.1" hostport="21513" guestport="5555"/><Forw""";
        Assert.Equal(21513, EmulatorConfigParsers.VBoxAdbHostPort(truncated));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<VirtualBox/>")]
    [InlineData("""<NAT><Forwarding hostport="62125" guestport="5554"/></NAT>""")]
    [InlineData("""<NAT><Forwarding hostport="x" guestport="5555"/></NAT>""")]
    public void VBox_without_adb_rule(string? xml)
    {
        Assert.Null(EmulatorConfigParsers.VBoxAdbHostPort(xml));
    }

    [Fact]
    public void BlueStacks_conf()
    {
        const string conf = """
            bst.enable_adb_access="1"
            bst.installed_images="Pie64,Rvc64"
            bst.instance.Pie64.adb_port="5555"
            bst.instance.Pie64.display_name="BlueStacks App Player"
            bst.instance.Pie64.status.adb_port="5565"
            bst.instance.Pie64_1.display_name="Arknights"
            bst.instance.Pie64_1.status.adb_port="5575"
            bst.instance.Rvc64.adb_port="5585"
            # comment
            garbage line
            """;
        var (enabled, instances) = EmulatorConfigParsers.BlueStacksConf(conf);
        Assert.True(enabled);
        Assert.Equal(3, instances.Count);

        var pie = instances.Single(i => i.Instance == "Pie64");
        Assert.Equal(5565, pie.AdbPort); // status.adb_port wins over adb_port
        Assert.Equal("BlueStacks App Player", pie.DisplayName);
        Assert.Equal(5575, instances.Single(i => i.Instance == "Pie64_1").AdbPort);
        var rvc = instances.Single(i => i.Instance == "Rvc64");
        Assert.Equal(5585, rvc.AdbPort);
        Assert.Null(rvc.DisplayName);
    }

    [Fact]
    public void BlueStacks_conf_with_adb_off_or_missing()
    {
        Assert.False(EmulatorConfigParsers.BlueStacksConf("bst.enable_adb_access=\"0\"\r\nbst.instance.Pie64.status.adb_port=\"5555\"\r\n").AdbEnabled);
        var (enabled, instances) = EmulatorConfigParsers.BlueStacksConf(null);
        Assert.Null(enabled);
        Assert.Empty(instances);
    }

    // Shape of %TEMP%\avd\running\pid_23584.ini written by emulator 36.5.10 (token removed).
    private const string AvdIni = """
        launcher.dir=C:\Users\admin\AppData\Local\Android\Sdk\emulator
        emulator.build=15081367
        port.serial=5554
        emulator.version=36.5.10.0
        avd.id=Pico_API_30_ARM_Translation
        port.adb=5555
        avd.name=Pico_API_30_ARM_Translation
        avd.dir=C:\Users\admin\.android\avd\Pico_API_30_ARM_Translation.avd
        cmdline="C:\\Users\\admin\\AppData\\Local\\Android\\Sdk\\emulator\\qemu\\windows-x86_64\\qemu-system-x86_64.exe" "-avd" "Pico_API_30_ARM_Translation"
        grpc.port=8554
        """;

    [Fact]
    public void Avd_running_ini()
    {
        var info = EmulatorConfigParsers.AvdRunningIni(AvdIni.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.NotNull(info);
        Assert.Equal("Pico_API_30_ARM_Translation", info.AvdName);
        Assert.Equal(5555, info.AdbPort);
        Assert.Equal(5554, info.ConsolePort);
    }

    [Fact]
    public void Avd_running_ini_derives_missing_ports()
    {
        var info = EmulatorConfigParsers.AvdRunningIni("avd.id=Pixel\nport.serial=5556\n");
        Assert.Equal(("Pixel", 5557, 5556), (info!.AvdName, info.AdbPort, info.ConsolePort));
        Assert.Null(EmulatorConfigParsers.AvdRunningIni("emulator.build=1\n"));
        Assert.Null(EmulatorConfigParsers.AvdRunningIni(""));
    }

    [Theory]
    [InlineData("Android Emulator - Pico_API_30_ARM_Translation:5554", "Pico_API_30_ARM_Translation", 5554)]
    [InlineData("Android Emulator - Pixel 8 API 35:5556", "Pixel 8 API 35", 5556)]
    [InlineData("Pixel8_API_34:5554", "Pixel8_API_34", 5554)]
    [InlineData("Android Emulator", null, null)]
    [InlineData("MuMu模拟器12", null, null)]
    [InlineData(null, null, null)]
    public void Avd_window_title(string? title, string? avd, int? port)
    {
        Assert.Equal((avd, port), EmulatorConfigParsers.AvdWindowTitle(title));
    }

    [Theory]
    [InlineData("leidian0", 0)]
    [InlineData("leidian12", 12)]
    [InlineData("LEIDIAN3", 3)]
    [InlineData("leidian", null)]
    [InlineData("ldplayer1", null)]
    public void LdPlayer_index(string name, int? expected)
    {
        Assert.Equal(expected, EmulatorConfigParsers.LdPlayerIndex(name));
    }

    [Theory]
    [InlineData("nox", 0)]
    [InlineData("Nox_1", 1)]
    [InlineData("Nox64_3", 3)]
    [InlineData("Nox64", 0)]
    [InlineData("MEmu", 0)]
    [InlineData("MEmu_2", 2)]
    [InlineData("Nox_x", null)]
    [InlineData("Other", null)]
    public void Suffix_index(string name, int? expected)
    {
        Assert.Equal(expected, EmulatorConfigParsers.SuffixIndex(name, "Nox", "Nox64", "MEmu"));
    }
}

public sealed class CommandLineTests
{
    [Fact]
    public void Splits_like_the_c_runtime()
    {
        Assert.Equal(
            [@"C:\Program Files\Netease\MuMuPlayer\nx_device\12.0\shell\MuMuNxDevice.exe", "-v", "MuMuPlayerGlobal-12.0-0"],
            CommandLine.Split(@"""C:\Program Files\Netease\MuMuPlayer\nx_device\12.0\shell\MuMuNxDevice.exe"" -v MuMuPlayerGlobal-12.0-0"));
        Assert.Equal(["a", "b c", "d"], CommandLine.Split("  a  \"b c\"   d "));
        Assert.Equal([@"a\\b", @"c\"""], CommandLine.Split(@"a\\b ""c\\\"""""));
        Assert.Equal(["x\"y"], CommandLine.Split("\"x\"\"y\""));
        Assert.Equal([""], CommandLine.Split("\"\""));
        Assert.Empty(CommandLine.Split(null));
        Assert.Empty(CommandLine.Split("   "));
    }

    [Fact]
    public void Reads_options_in_every_spelling()
    {
        var vbox = CommandLine.Split(@"""C:\Program Files\ldplayer9box\Ld9BoxHeadless.exe"" --comment leidian1 --startvm 20b3e0e5 --vrde config");
        Assert.Equal("leidian1", CommandLine.Option(vbox, "--comment"));
        Assert.Null(CommandLine.Option(vbox, "--missing"));

        var nox = CommandLine.Split(@"""C:\Program Files (x86)\Nox\bin\Nox.exe"" -clone:Nox_2 -resolution:1280x720");
        Assert.Equal("Nox_2", CommandLine.Option(nox, "-clone"));

        var bs = CommandLine.Split(@"""C:\Program Files\BlueStacks_nxt\HD-Player.exe"" --instance=Pie64_1 --cmd launchApp");
        Assert.Equal("Pie64_1", CommandLine.FirstOption(bs, "-instance", "--instance"));

        var avd = CommandLine.Split(@"qemu-system-x86_64.exe -avd Pixel_8 -port 5556");
        Assert.Equal("Pixel_8", CommandLine.Option(avd, "-avd"));
        Assert.Equal(5556, CommandLine.IntOption(avd, "-port"));
        Assert.Null(CommandLine.IntOption(avd, "-avd"));

        Assert.Null(CommandLine.Option(["--comment"], "--comment"));
    }
}
