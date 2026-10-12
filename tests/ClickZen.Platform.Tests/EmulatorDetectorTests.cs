using ClickZen.Core.Emulators;
using ClickZen.Platform.Emulators;
using ClickZen.Platform.Windows;

namespace ClickZen.Platform.Tests;

/// <summary>In-memory processes, files, registry and windows for detector tests.</summary>
internal sealed class FakeEmulatorEnvironment : IEmulatorEnvironment
{
    public List<ProcessInfo> Processes { get; } = [];

    public List<DesktopWindowInfo> Windows { get; } = [];

    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>(machine, key, value) → data.</summary>
    public Dictionary<(bool, string, string), string> Registry { get; } = [];

    public Dictionary<string, string> Environment { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TEMP"] = @"C:\Users\me\AppData\Local\Temp",
        ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local",
        ["ProgramData"] = @"C:\ProgramData",
    };

    public int FileReads { get; private set; }

    public void AddFile(string path, string content = "")
    {
        Files[path] = content;
        for (var d = Path.GetDirectoryName(path); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
        {
            Directories.Add(d);
        }
    }

    public void AddProcess(int id, string path, string? commandLine = null) =>
        Processes.Add(new ProcessInfo(id, ProcessQuery.NameOf(path), path, commandLine ?? $"\"{path}\""));

    public void AddWindow(nint handle, int pid, string title, string cls, bool minimized = false) =>
        Windows.Add(new DesktopWindowInfo(handle, title, cls, (uint)pid, Processes.FirstOrDefault(p => p.Id == pid)?.Name ?? "", minimized));

    public IReadOnlyList<ProcessInfo> FindProcesses(Func<string, bool> nameFilter) => Processes.Where(p => nameFilter(p.Name)).ToList();

    public IReadOnlyList<DesktopWindowInfo> GetWindows() => Windows;

    public string? ReadFile(string path)
    {
        FileReads++;
        return Files.TryGetValue(path, out var s) ? s : null;
    }

    public bool FileExists(string path) => Files.ContainsKey(path);

    public bool DirectoryExists(string path) => Directories.Contains(path.TrimEnd('\\'));

    public IReadOnlyList<string> GetDirectories(string path) =>
        Directories.Where(d => string.Equals(Path.GetDirectoryName(d), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();

    public IReadOnlyList<string> GetFiles(string path, string pattern) =>
        Files.Keys.Where(f => string.Equals(Path.GetDirectoryName(f), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();

    public string? ReadRegistryString(bool localMachine, string subKey, string valueName) =>
        Registry.TryGetValue((localMachine, subKey, valueName), out var v) ? v : null;

    public IReadOnlyList<string> GetRegistrySubKeys(bool localMachine, string subKey) =>
        Registry.Keys.Where(k => k.Item1 == localMachine && k.Item2.StartsWith(subKey + "\\", StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Item2[(subKey.Length + 1)..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public string ExpandEnvironment(string text)
    {
        foreach (var (k, v) in Environment)
        {
            text = text.Replace("%" + k + "%", v, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }
}

public sealed class EmulatorDetectorTests
{
    private const string MuMuRoot = @"D:\Program Files\Netease\MuMuPlayer";
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static string MuMuVmConfig(int port) =>
        $$"""{ "vm": { "nat": { "port_forward": { "adb": { "guest_ip": "192.168.1.102", "host_port": "{{port}}" } } } } }""";

    private static FakeEmulatorEnvironment MuMuInstallation()
    {
        var env = new FakeEmulatorEnvironment();
        env.AddFile(MuMuRoot + @"\nx_main\MuMuNxMain.exe");
        env.AddFile(MuMuRoot + @"\nx_device\12.0\shell\MuMuNxDevice.exe");
        env.AddFile(MuMuRoot + @"\vms\MuMuPlayerGlobal-12.0-0\configs\vm_config.json", MuMuVmConfig(16384));
        env.AddFile(MuMuRoot + @"\vms\MuMuPlayerGlobal-12.0-0\configs\extra_config.json", """{ "playerName": "Android Device" }""");
        env.AddFile(MuMuRoot + @"\vms\MuMuPlayerGlobal-12.0-1\configs\vm_config.json", MuMuVmConfig(16416));
        env.AddFile(MuMuRoot + @"\vms\MuMuPlayerGlobal-12.0-1\configs\extra_config.json", """{ "playerName": "Game 2" }""");
        env.Registry[(true, Uninstall + @"\MuMuPlayerGlobal", "UninstallString")] = $"\"{MuMuRoot}\\uninstall.exe\"";
        return env;
    }

    [Fact]
    public void MuMu_nx_two_instances_from_vm_processes()
    {
        var env = MuMuInstallation();
        var player = MuMuRoot + @"\nx_device\12.0\shell\MuMuNxDevice.exe";
        env.AddProcess(100, player, $"\"{player}\" -v MuMuPlayerGlobal-12.0-1");
        env.AddProcess(101, player, $"\"{player}\"");
        env.AddProcess(200, @"C:\Program Files\MuMuVMMVbox\Hypervisor\MuMuVMMHeadless.exe", "\"MuMuVMMHeadless.exe\" --comment MuMuPlayerGlobal-12.0-0 --startvm abc");
        env.AddProcess(201, @"C:\Program Files\MuMuVMMVbox\Hypervisor\MuMuVMMHeadless.exe", "\"MuMuVMMHeadless.exe\" --comment MuMuPlayerGlobal-12.0-1 --startvm def");
        env.AddWindow(1000, 100, "Game 2", "Qt5156QWindowIcon");
        env.AddWindow(1001, 101, "Android Device", "Qt5156QWindowIcon");

        var found = new EmulatorDetector(env).Detect();

        Assert.Equal(2, found.Running.Count);
        var main = found.Running[0];
        Assert.Equal(("MuMuPlayerGlobal-12.0-0", 0, 16384, AdbPortSource.Config), (main.InstanceKey, main.Index, main.AdbPort, main.PortSource));
        Assert.Equal("Android Device", main.Name);
        Assert.Equal(101, main.ProcessId);
        Assert.Equal((nint)1001, main.WindowHandle);
        Assert.Equal(MuMuRoot, main.InstallDirectory);
        Assert.Equal([16385, 16386], main.FallbackPorts);
        Assert.Equal("mumu:MuMuPlayerGlobal-12.0-0", main.Key);

        var second = found.Running[1];
        Assert.Equal(("MuMuPlayerGlobal-12.0-1", 16416, 100, (nint)1000), (second.InstanceKey, second.AdbPort, second.ProcessId, second.WindowHandle));
        Assert.Equal("Game 2", second.Name);

        Assert.Contains(found.Installed, i => i.Kind == EmulatorKind.MuMu && i.InstallDirectory == MuMuRoot);
    }

    [Fact]
    public void MuMu_main_instance_without_vm_process_uses_the_first_vm()
    {
        var env = MuMuInstallation();
        var player = MuMuRoot + @"\nx_device\12.0\shell\MuMuNxDevice.exe";
        env.AddProcess(100, player, null); // command line unreadable
        env.Processes[0] = env.Processes[0] with { CommandLine = null };

        var i = Assert.Single(new EmulatorDetector(env).Detect().Running);
        Assert.Equal(("MuMuPlayerGlobal-12.0-0", 16384), (i.InstanceKey, i.AdbPort));
    }

    [Fact]
    public void MuMu_without_config_falls_back_to_the_formula()
    {
        var env = new FakeEmulatorEnvironment();
        env.AddProcess(300, @"C:\Program Files\MuMuVMMVbox\Hypervisor\MuMuVMMHeadless.exe", "x --comment MuMuPlayer-12.0-2");

        var i = Assert.Single(new EmulatorDetector(env).Detect().Running);
        Assert.Equal(("MuMuPlayer-12.0-2", 16448, AdbPortSource.Formula), (i.InstanceKey, i.AdbPort, i.PortSource));
        Assert.Equal(0, i.ProcessId);
        Assert.False(i.HasWindow);
    }

    [Fact]
    public void MuMu_4x_reads_the_nemu_forwarding()
    {
        const string root = @"C:\Program Files\Netease\MuMuPlayer-12.0";
        var env = new FakeEmulatorEnvironment();
        env.AddFile(root + @"\shell\MuMuPlayer.exe");
        env.AddFile(root + @"\vms\MuMuPlayer-12.0-0\MuMuPlayer-12.0-0.nemu",
            """<VirtualBox><Machine><NAT><Forwarding name="ADB" proto="1" hostip="127.0.0.1" hostport="16390" guestport="5555"/></NAT></Machine></VirtualBox>""");
        env.AddProcess(5, root + @"\shell\MuMuPlayer.exe", $"\"{root}\\shell\\MuMuPlayer.exe\" -v 0");
        env.AddProcess(6, @"C:\Program Files\MuMuVMMVbox\Hypervisor\MuMuVMMHeadless.exe", "x --comment MuMuPlayer-12.0-0");
        env.AddWindow(7, 5, "MuMu模拟器12", "Qt5156QWindowIcon");

        var i = Assert.Single(new EmulatorDetector(env).Detect().Running);
        Assert.Equal((16390, AdbPortSource.Config), (i.AdbPort, i.PortSource));
        Assert.EndsWith(".nemu", i.ConfigPath, StringComparison.Ordinal);
        Assert.Equal("MuMu模拟器12", i.Name);
        Assert.Equal("MuMuPlayer", i.ProcessName);
    }

    [Fact]
    public void LdPlayer_instances_from_vbox_processes()
    {
        const string root = @"C:\leidian\LDPlayer9";
        var env = new FakeEmulatorEnvironment();
        env.AddFile(root + @"\dnplayer.exe");
        env.AddFile(root + @"\vms\config\leidian0.config", """{ "basicSettings.adbDebug": 1 }""");
        env.AddFile(root + @"\vms\config\leidian1.config", """{ "basicSettings.adbDebug": 0 }""");
        env.AddProcess(10, root + @"\dnplayer.exe", $"\"{root}\\dnplayer.exe\" index=1|");
        env.AddProcess(11, root + @"\dnplayer.exe", $"\"{root}\\dnplayer.exe\"");
        env.AddProcess(20, @"C:\Program Files\ldplayer9box\Ld9BoxHeadless.exe", "Ld9BoxHeadless.exe --comment leidian0 --startvm x");
        env.AddProcess(21, @"C:\Program Files\ldplayer9box\Ld9BoxHeadless.exe", "Ld9BoxHeadless.exe --comment leidian1 --startvm y");
        env.AddWindow(30, 10, "雷电模拟器-1", "LDPlayerMainFrame");
        env.AddWindow(31, 11, "雷电模拟器", "LDPlayerMainFrame");

        var running = new EmulatorDetector(env).Detect().Running;
        Assert.Equal(2, running.Count);
        Assert.Equal(("leidian0", 5555, 11, "雷电模拟器"), (running[0].InstanceKey, running[0].AdbPort, running[0].ProcessId, running[0].Name));
        Assert.Equal(("leidian1", 5557, 10), (running[1].InstanceKey, running[1].AdbPort, running[1].ProcessId));
        Assert.All(running, r => Assert.Equal(AdbPortSource.Formula, r.PortSource));
        Assert.True(running[1].OwnsSerial("emulator-5556"));
        Assert.True(running[0].AdbEnabled);
        Assert.False(running[1].AdbEnabled);
    }

    [Fact]
    public void Nox_ports_from_vbox_files()
    {
        const string bin = @"C:\Program Files (x86)\Nox\bin";
        var env = new FakeEmulatorEnvironment();
        env.AddFile(bin + @"\Nox.exe");
        env.AddFile(bin + @"\BignoxVMS\nox\nox.vbox", """<VirtualBox><NAT><Forwarding name="port2" proto="1" hostip="127.0.0.1" hostport="62001" guestport="5555"/></NAT></VirtualBox>""");
        env.AddFile(bin + @"\BignoxVMS\Nox_1\Nox_1.vbox", """<VirtualBox><NAT><Forwarding name="port2" proto="1" hostip="127.0.0.1" hostport="62025" guestport="5555"/></NAT></VirtualBox>""");
        env.AddProcess(40, bin + @"\Nox.exe", $"\"{bin}\\Nox.exe\"");
        env.AddProcess(41, bin + @"\Nox.exe", $"\"{bin}\\Nox.exe\" -clone:Nox_1");
        env.AddProcess(42, bin + @"\Nox.exe", $"\"{bin}\\Nox.exe\" -clone:Nox_2"); // no .vbox yet → formula
        env.AddProcess(43, bin + @"\NoxVMHandle.exe", "NoxVMHandle.exe --comment Nox_1");

        var running = new EmulatorDetector(env).Detect().Running;
        Assert.Equal(3, running.Count);
        Assert.Equal(("nox", 62001, AdbPortSource.Config), (running[0].InstanceKey, running[0].AdbPort, running[0].PortSource));
        Assert.Equal(("Nox_1", 62025, AdbPortSource.Config), (running[1].InstanceKey, running[1].AdbPort, running[1].PortSource));
        Assert.Equal(("Nox_2", 62026, AdbPortSource.Formula), (running[2].InstanceKey, running[2].AdbPort, running[2].PortSource));
    }

    [Fact]
    public void MEmu_ports_from_memu_files()
    {
        const string root = @"C:\Program Files\Microvirt\MEmu";
        var env = new FakeEmulatorEnvironment();
        env.AddFile(root + @"\MEmu.exe");
        env.AddFile(root + @"\MemuHyperv VMs\MEmu_1\MEmu_1.memu", """<VirtualBox><NAT><Forwarding name="ADB" proto="1" hostip="127.0.0.1" hostport="21513" guestport="5555"/></NAT></VirtualBox>""");
        env.AddProcess(50, root + @"\MEmu.exe", $"\"{root}\\MEmu.exe\" MEmu_1");
        env.AddProcess(51, @"C:\Program Files\Microvirt\MEmuHyperv\MEmuHeadless.exe", "MEmuHeadless.exe --comment MEmu_1 --startvm x");
        env.AddWindow(52, 50, "逍遥模拟器 1", "Qt5QWindowIcon");

        var i = Assert.Single(new EmulatorDetector(env).Detect().Running);
        Assert.Equal(("MEmu_1", 1, 21513, AdbPortSource.Config), (i.InstanceKey, i.Index, i.AdbPort, i.PortSource));
        Assert.Equal((nint)52, i.WindowHandle);
    }

    [Fact]
    public void BlueStacks_port_and_adb_switch_from_conf()
    {
        const string install = @"C:\Program Files\BlueStacks_nxt";
        var env = new FakeEmulatorEnvironment();
        env.AddFile(install + @"\HD-Player.exe");
        env.Registry[(true, @"SOFTWARE\BlueStacks_nxt", "UserDefinedDir")] = @"D:\BlueStacks_nxt";
        env.AddFile(@"D:\BlueStacks_nxt\bluestacks.conf", """
            bst.enable_adb_access="0"
            bst.instance.Pie64.status.adb_port="5555"
            bst.instance.Pie64_1.display_name="Arknights"
            bst.instance.Pie64_1.status.adb_port="5575"
            """);
        env.AddProcess(60, install + @"\HD-Player.exe", $"\"{install}\\HD-Player.exe\" --instance Pie64_1");
        env.AddWindow(61, 60, "Arknights", "Qt5154QWindowIcon");

        var i = Assert.Single(new EmulatorDetector(env).Detect().Running);
        Assert.Equal(("Pie64_1", 1, 5575, AdbPortSource.Config), (i.InstanceKey, i.Index, i.AdbPort, i.PortSource));
        Assert.False(i.AdbEnabled);
        Assert.Equal("Arknights", i.Name);
    }

    [Fact]
    public void BlueStacks_default_conf_location()
    {
        const string install = @"C:\Program Files\BlueStacks_nxt_cn";
        var env = new FakeEmulatorEnvironment();
        env.AddFile(@"C:\ProgramData\BlueStacks_nxt_cn\bluestacks.conf", "bst.instance.Pie64.status.adb_port=\"5565\"\n");
        env.AddProcess(70, install + @"\HD-Player.exe", $"\"{install}\\HD-Player.exe\"");

        var i = Assert.Single(new EmulatorDetector(env).Detect().Running);
        Assert.Equal(("Pie64", 5565), (i.InstanceKey, i.AdbPort));
        Assert.Null(i.AdbEnabled);
    }

    [Fact]
    public void Avd_from_the_discovery_file()
    {
        const string qemu = @"C:\Users\me\AppData\Local\Android\Sdk\emulator\qemu\windows-x86_64\qemu-system-x86_64.exe";
        var env = new FakeEmulatorEnvironment();
        env.AddFile(@"C:\Users\me\AppData\Local\Temp\avd\running\pid_23584.ini", "avd.name=Pico_API_30\nport.serial=5554\nport.adb=5555\n");
        env.AddProcess(23584, qemu, $"\"{qemu}\" -avd Pico_API_30 -no-snapshot-save");
        env.AddProcess(23600, qemu, $"\"{qemu}\" -avd Pixel_8 -port 5556"); // no discovery file → -port
        env.AddWindow(80, 23584, "Android Emulator - Pico_API_30:5554", "Qt672QWindowIcon");
        env.AddWindow(81, 23584, "Extended controls - Pico_API_30:5554", "Qt672QWindowIcon");

        var running = new EmulatorDetector(env).Detect().Running;
        Assert.Equal(2, running.Count);
        var pico = running.Single(r => r.InstanceKey == "Pico_API_30");
        Assert.Equal((5555, AdbPortSource.Runtime, (nint)80), (pico.AdbPort, pico.PortSource, pico.WindowHandle));
        Assert.True(pico.OwnsSerial("emulator-5554"));
        Assert.Equal(@"C:\Users\me\AppData\Local\Android\Sdk", pico.InstallDirectory);
        var pixel = running.Single(r => r.InstanceKey == "Pixel_8");
        Assert.Equal((5557, AdbPortSource.Formula), (pixel.AdbPort, pixel.PortSource));
    }

    [Fact]
    public void Unrelated_qemu_is_ignored()
    {
        var env = new FakeEmulatorEnvironment();
        env.AddProcess(90, @"C:\tools\qemu-system-x86_64.exe", "qemu-system-x86_64.exe -hda disk.img");
        Assert.Empty(new EmulatorDetector(env).Detect().Running);
    }

    [Fact]
    public void Minimized_window_is_reported()
    {
        var env = MuMuInstallation();
        var player = MuMuRoot + @"\nx_device\12.0\shell\MuMuNxDevice.exe";
        env.AddProcess(100, player, $"\"{player}\" -v MuMuPlayerGlobal-12.0-0");
        env.AddWindow(1000, 100, "Android Device", "Qt5156QWindowIcon", minimized: true);

        var i = Assert.Single(new EmulatorDetector(env).Detect().Running);
        Assert.True(i.WindowMinimized);
        Assert.True(i.HasWindow);
    }

    [Fact]
    public void Nothing_running_still_reports_installed_emulators()
    {
        var env = MuMuInstallation();
        env.AddFile(@"C:\leidian\LDPlayer14\dnplayer.exe");
        env.Registry[(false, @"SOFTWARE\leidian\LDPlayer14", "InstallDir")] = @"C:\leidian\LDPlayer14\";
        env.AddFile(@"C:\Program Files\Microvirt\MEmu\MEmu.exe");
        env.Registry[(true, Uninstall + @"\MEmu", "UninstallString")] = "\"C:\\Program Files\\Microvirt\\MEmu\\uninstall\\uninstall.exe\" -u";
        env.Registry[(true, Uninstall + @"\Notepad++", "UninstallString")] = "\"C:\\Program Files\\Notepad++\\uninstall.exe\"";

        var found = new EmulatorDetector(env).Detect();
        Assert.Empty(found.Running);
        Assert.Equal(
            [(EmulatorKind.MuMu, MuMuRoot), (EmulatorKind.LDPlayer, @"C:\leidian\LDPlayer14"), (EmulatorKind.MEmu, @"C:\Program Files\Microvirt\MEmu")],
            found.Installed.Select(i => (i.Kind, i.InstallDirectory)));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Netease\\MuMuPlayer\\uninstall.exe\"", @"C:\Program Files\Netease\MuMuPlayer")]
    [InlineData("\"C:\\Program Files\\Microvirt\\MEmu\\uninstall\\uninstall.exe\" -u", @"C:\Program Files\Microvirt\MEmu\uninstall")]
    [InlineData(@"C:\Program Files\BlueStacks_nxt\BlueStacksUninstaller.exe -tmp", @"C:\Program Files\BlueStacks_nxt")]
    [InlineData("\"D:\\MuMu\\nx_main\\MuMuNxMain.ico\"", @"D:\MuMu\nx_main")]
    [InlineData(@"C:\x\app.exe,0", @"C:\x")]
    [InlineData("", null)]
    public void Executable_directory(string command, string? expected)
    {
        Assert.Equal(expected, EmulatorDetector.ExecutableDirectory(command));
    }
}
