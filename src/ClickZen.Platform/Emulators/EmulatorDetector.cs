using System.Globalization;
using ClickZen.Core.Emulators;
using ClickZen.Platform.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClickZen.Platform.Emulators;

/// <summary>
/// Finds running Android emulator instances on this PC with their exact adb port and window, by looking at processes,
/// the emulators' own configuration files and the registry. Never runs the emulators' command line tools and never
/// talks to adb, so it is safe to call at any time (it takes a few tens of milliseconds).
/// </summary>
public sealed class EmulatorDetector
{
    private readonly IEmulatorEnvironment _env;
    private readonly ILogger _log;

    public EmulatorDetector(IEmulatorEnvironment environment, ILogger? logger = null)
    {
        _env = environment ?? throw new ArgumentNullException(nameof(environment));
        _log = logger ?? NullLogger.Instance;
    }

    /// <summary>Running instances (sorted by emulator then instance number) and installed emulators from the registry.</summary>
    public EmulatorDetection Detect()
    {
        IReadOnlyList<ProcessInfo> processes;
        IReadOnlyList<DesktopWindowInfo> windows;
        try
        {
            processes = _env.FindProcesses(EmulatorCatalog.IsEmulatorProcess);
            windows = processes.Count == 0 ? [] : _env.GetWindows();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Emulator detection: could not list processes or windows");
            return new EmulatorDetection([], DetectInstalled());
        }

        var running = new List<EmulatorInstance>();
        foreach (var def in EmulatorCatalog.All)
        {
            var players = processes.Where(p => def.IsPlayerProcess(p.Name)).ToList();
            var vms = processes.Where(p => def.IsVmProcess(p.Name)).ToList();
            if (players.Count == 0 && vms.Count == 0)
            {
                continue;
            }

            try
            {
                var found = def.Kind switch
                {
                    EmulatorKind.MuMu => DetectMuMu(players, vms),
                    EmulatorKind.LDPlayer => DetectLdPlayer(players, vms),
                    EmulatorKind.Nox => DetectNox(players),
                    EmulatorKind.MEmu => DetectMEmu(players, vms),
                    EmulatorKind.BlueStacks => DetectBlueStacks(players),
                    EmulatorKind.AndroidStudio => DetectAvd(players),
                    _ => [],
                };
                running.AddRange(found.Select(i => AttachWindow(i, def, windows)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.LogWarning(ex, "Emulator detection failed for {Emulator}", def.EnglishName);
            }
        }

        var sorted = running
            .GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(i => i.Kind).ThenBy(i => i.Index ?? int.MaxValue).ThenBy(i => i.InstanceKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var i in sorted)
        {
            _log.LogInformation("Emulator detected: {Kind} {Instance} \"{Name}\" pid {Pid}, adb {Port} ({Source}{Config}), window {Hwnd:X} \"{Title}\"",
                i.Kind, i.InstanceKey, i.Name, i.ProcessId, i.AdbPort?.ToString(CultureInfo.InvariantCulture) ?? "?", i.PortSource,
                i.ConfigPath is null ? "" : " " + i.ConfigPath, i.WindowHandle, i.WindowTitle);
        }

        return new EmulatorDetection(sorted, DetectInstalled());
    }

    // ================================================================== MuMu 12

    /// <summary>
    /// MuMu 12: one player per instance (5.x <c>nx_device\&lt;engine&gt;\shell\MuMuNxDevice.exe</c>, 4.x
    /// <c>shell\MuMuPlayer.exe</c>) and one <c>MuMuVMMHeadless.exe --comment &lt;vm&gt;</c> per running VM. The VM name
    /// ("MuMuPlayer-12.0-1") comes from either command line; its port from <c>vms\&lt;vm&gt;\configs\vm_config.json</c>.
    /// </summary>
    private List<EmulatorInstance> DetectMuMu(List<ProcessInfo> players, List<ProcessInfo> vms)
    {
        var result = new List<EmulatorInstance>();
        var vmNames = vms
            .Select(v => EmulatorConfigParsers.FindMuMuVmName(CommandLine.Option(CommandLine.Split(v.CommandLine), "--comment") ?? v.CommandLine))
            .OfType<string>()
            .ToList();

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in players)
        {
            var install = MuMuInstallDir(p.Path);
            var vm = EmulatorConfigParsers.FindMuMuVmName(p.CommandLine);
            if (vm is null)
            {
                // No instance on the command line (the main instance started from the shortcut): take an unclaimed VM.
                var candidates = vmNames.Where(n => !claimed.Contains(n)).ToList();
                vm = candidates.Count == 1 ? candidates[0]
                    : candidates.FirstOrDefault(n => EmulatorConfigParsers.MuMuVmIndex(n) == 0)
                    ?? (install is not null ? FirstMuMuVm(install, 0) : null);
            }

            if (vm is null)
            {
                continue;
            }

            claimed.Add(vm);
            result.Add(MuMuInstance(vm, install, p));
        }

        // VMs without a player window (started headless, or the player process could not be read).
        foreach (var vm in vmNames.Where(n => !claimed.Contains(n)))
        {
            claimed.Add(vm);
            var install = players.Select(p => MuMuInstallDir(p.Path)).FirstOrDefault(d => d is not null) ?? MuMuInstallFromRegistry(vm);
            result.Add(MuMuInstance(vm, install, null));
        }

        return result;
    }

    private EmulatorInstance MuMuInstance(string vm, string? install, ProcessInfo? player)
    {
        var index = EmulatorConfigParsers.MuMuVmIndex(vm);
        int? port = null;
        string? configPath = null;
        string? name = null;
        if (install is not null)
        {
            var configs = Path.Combine(install, "vms", vm, "configs");
            configPath = Path.Combine(configs, "vm_config.json");
            port = EmulatorConfigParsers.MuMuVmConfigAdbPort(_env.ReadFile(configPath));
            name = EmulatorConfigParsers.MuMuPlayerName(_env.ReadFile(Path.Combine(configs, "extra_config.json")));
            if (port is null)
            {
                // MuMu 12 4.x kept the forwarding in the VirtualBox machine file.
                var nemu = Path.Combine(install, "vms", vm, vm + ".nemu");
                port = EmulatorConfigParsers.VBoxAdbHostPort(_env.ReadFile(nemu));
                configPath = port is null ? null : nemu;
            }
        }

        var source = AdbPortSource.Config;
        if (port is null && index is int i)
        {
            port = EmulatorCatalog.MuMu.PortForIndex(i);
            source = AdbPortSource.Formula;
            configPath = null;
        }

        return new EmulatorInstance
        {
            Kind = EmulatorKind.MuMu,
            InstanceKey = vm,
            Index = index,
            DisplayName = name ?? "",
            InstallDirectory = install,
            ProcessId = player?.Id ?? 0,
            ProcessName = player?.Name ?? "",
            AdbPort = port,
            PortSource = port is null ? AdbPortSource.Unknown : source,
            // MuMu takes the next free port when its port is in use.
            FallbackPorts = port is int p ? [p + 1, p + 2] : [],
            ConfigPath = configPath,
        };
    }

    /// <summary>Installation directory from a MuMu player path: …\nx_device\12.0\shell\MuMuNxDevice.exe (3 up) or …\shell\MuMuPlayer.exe (1 up).</summary>
    private string? MuMuInstallDir(string playerPath)
    {
        if (string.IsNullOrEmpty(playerPath))
        {
            return null;
        }

        var dir = Path.GetDirectoryName(playerPath);
        for (var up = 0; dir is not null && up < 4; up++)
        {
            if (_env.DirectoryExists(Path.Combine(dir, "vms")) && IsInstall(EmulatorCatalog.MuMu, dir))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private string? MuMuInstallFromRegistry(string vm) =>
        InstallDirsFromRegistry(EmulatorCatalog.MuMu).FirstOrDefault(d => _env.DirectoryExists(Path.Combine(d, "vms", vm)));

    private string? FirstMuMuVm(string install, int index) =>
        _env.GetDirectories(Path.Combine(install, "vms"))
            .Select(Path.GetFileName)
            .FirstOrDefault(n => EmulatorConfigParsers.MuMuVmIndex(n) == index);

    // ================================================================== LDPlayer

    /// <summary>
    /// LDPlayer: <c>dnplayer.exe</c> per instance; <c>Ld9BoxHeadless.exe --comment leidian&lt;N&gt;</c> per VM. There is
    /// no port in its configuration: instance N listens on 5555 + 2N and adb lists it as emulator-(5554 + 2N) by itself.
    /// </summary>
    private List<EmulatorInstance> DetectLdPlayer(List<ProcessInfo> players, List<ProcessInfo> vms)
    {
        var result = new List<EmulatorInstance>();
        var vmIndexes = vms
            .Select(v => EmulatorConfigParsers.LdPlayerIndex(CommandLine.Option(CommandLine.Split(v.CommandLine), "--comment")))
            .OfType<int>()
            .ToList();

        var claimed = new HashSet<int>();
        foreach (var p in players)
        {
            var index = EmulatorConfigParsers.LdPlayerCommandLineIndex(p.CommandLine);
            if (index is null)
            {
                var free = vmIndexes.Where(i => !claimed.Contains(i)).ToList();
                index = free.Count == 1 ? free[0] : free.Contains(0) || vmIndexes.Count == 0 ? 0 : null;
            }

            if (index is int i && claimed.Add(i))
            {
                result.Add(LdInstance(i, Path.GetDirectoryName(p.Path), p));
            }
        }

        foreach (var i in vmIndexes.Where(i => !claimed.Contains(i)))
        {
            claimed.Add(i);
            result.Add(LdInstance(i, InstallDirsFromRegistry(EmulatorCatalog.LDPlayer).FirstOrDefault(), null));
        }

        return result;
    }

    private EmulatorInstance LdInstance(int index, string? install, ProcessInfo? player)
    {
        var key = "leidian" + index.ToString(CultureInfo.InvariantCulture);
        bool? adbEnabled = null;
        string? config = null;
        if (install is not null)
        {
            config = Path.Combine(install, "vms", "config", key + ".config");
            adbEnabled = EmulatorConfigParsers.LdPlayerAdbEnabled(_env.ReadFile(config));
        }

        return new EmulatorInstance
        {
            Kind = EmulatorKind.LDPlayer,
            InstanceKey = key,
            Index = index,
            InstallDirectory = install,
            ProcessId = player?.Id ?? 0,
            ProcessName = player?.Name ?? "",
            AdbPort = EmulatorCatalog.LDPlayer.PortForIndex(index),
            PortSource = AdbPortSource.Formula,
            AdbEnabled = adbEnabled,
            ConfigPath = adbEnabled is null ? null : config,
        };
    }

    // ================================================================== Nox

    /// <summary>NoxPlayer: <c>Nox.exe -clone:Nox_N</c> per instance (no -clone = "nox"); port from <c>BignoxVMS\&lt;name&gt;\&lt;name&gt;.vbox</c>.</summary>
    private List<EmulatorInstance> DetectNox(List<ProcessInfo> players)
    {
        var result = new List<EmulatorInstance>();
        foreach (var p in players)
        {
            var bin = Path.GetDirectoryName(p.Path);
            var name = CommandLine.Option(CommandLine.Split(p.CommandLine), "-clone");
            if (string.IsNullOrWhiteSpace(name))
            {
                name = bin is not null && Path.GetFileName(Path.GetDirectoryName(bin) ?? "").Contains("64", StringComparison.Ordinal) ? "Nox64" : "nox";
            }

            var index = EmulatorConfigParsers.SuffixIndex(name, "Nox", "Nox64");
            int? port = null;
            string? config = null;
            if (bin is not null)
            {
                config = Path.Combine(bin, "BignoxVMS", name, name + ".vbox");
                port = EmulatorConfigParsers.VBoxAdbHostPort(_env.ReadFile(config));
            }

            result.Add(new EmulatorInstance
            {
                Kind = EmulatorKind.Nox,
                InstanceKey = name,
                Index = index,
                InstallDirectory = bin is null ? null : Path.GetDirectoryName(bin),
                ProcessId = p.Id,
                ProcessName = p.Name,
                AdbPort = port ?? (index is int i ? EmulatorCatalog.Nox.PortForIndex(i) : null),
                PortSource = port is not null ? AdbPortSource.Config : index is not null ? AdbPortSource.Formula : AdbPortSource.Unknown,
                ConfigPath = port is not null ? config : null,
            });
        }

        return result;
    }

    // ================================================================== MEmu

    /// <summary>MEmu: <c>MEmuHeadless.exe --comment MEmu_N</c> per VM; port from <c>MemuHyperv VMs\&lt;name&gt;\&lt;name&gt;.memu</c>.</summary>
    private List<EmulatorInstance> DetectMEmu(List<ProcessInfo> players, List<ProcessInfo> vms)
    {
        var result = new List<EmulatorInstance>();
        var install = players.Select(p => Path.GetDirectoryName(p.Path)).FirstOrDefault(d => !string.IsNullOrEmpty(d))
            ?? InstallDirsFromRegistry(EmulatorCatalog.MEmu).FirstOrDefault();
        var names = vms.Select(v => CommandLine.Option(CommandLine.Split(v.CommandLine), "--comment")).OfType<string>()
            .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0 && players.Count > 0)
        {
            // Player only (VM process not readable): the player's argument names the VM, default "MEmu".
            names = players.Select(p => CommandLine.Split(p.CommandLine).Skip(1).FirstOrDefault(a => EmulatorConfigParsers.SuffixIndex(a, "MEmu") is not null) ?? "MEmu")
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        var player = players.Count == 1 ? players[0] : null;
        foreach (var name in names)
        {
            var index = EmulatorConfigParsers.SuffixIndex(name, "MEmu");
            int? port = null;
            string? config = null;
            if (install is not null)
            {
                config = Path.Combine(install, "MemuHyperv VMs", name, name + ".memu");
                port = EmulatorConfigParsers.VBoxAdbHostPort(_env.ReadFile(config));
            }

            result.Add(new EmulatorInstance
            {
                Kind = EmulatorKind.MEmu,
                InstanceKey = name,
                Index = index,
                InstallDirectory = install,
                ProcessId = player?.Id ?? 0,
                ProcessName = player?.Name ?? EmulatorCatalog.MEmu.PlayerProcesses[0],
                AdbPort = port ?? (index is int i ? EmulatorCatalog.MEmu.PortForIndex(i) : null),
                PortSource = port is not null ? AdbPortSource.Config : index is not null ? AdbPortSource.Formula : AdbPortSource.Unknown,
                ConfigPath = port is not null ? config : null,
            });
        }

        return result;
    }

    // ================================================================== BlueStacks 5

    /// <summary>
    /// BlueStacks 5: <c>HD-Player.exe --instance Pie64_1</c> per instance; port from <c>bluestacks.conf</c> in the data
    /// directory (registry <c>UserDefinedDir</c>), read on every detection because Hyper-V builds change it per start.
    /// </summary>
    private List<EmulatorInstance> DetectBlueStacks(List<ProcessInfo> players)
    {
        var result = new List<EmulatorInstance>();
        foreach (var p in players)
        {
            var install = Path.GetDirectoryName(p.Path);
            var name = CommandLine.FirstOption(CommandLine.Split(p.CommandLine), "--instance", "-instance") ?? "Pie64";
            var conf = BlueStacksConfPath(install);
            var (enabled, instances) = EmulatorConfigParsers.BlueStacksConf(conf is null ? null : _env.ReadFile(conf));
            var cfg = instances.FirstOrDefault(c => c.Instance.Equals(name, StringComparison.OrdinalIgnoreCase));
            result.Add(new EmulatorInstance
            {
                Kind = EmulatorKind.BlueStacks,
                InstanceKey = name,
                Index = name.LastIndexOf('_') is var u and > 0 && int.TryParse(name.AsSpan(u + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : 0,
                DisplayName = cfg?.DisplayName ?? "",
                InstallDirectory = install,
                ProcessId = p.Id,
                ProcessName = p.Name,
                AdbPort = cfg?.AdbPort,
                PortSource = cfg?.AdbPort is null ? AdbPortSource.Unknown : AdbPortSource.Config,
                AdbEnabled = enabled,
                ConfigPath = cfg?.AdbPort is null ? null : conf,
            });
        }

        return result;
    }

    /// <summary>bluestacks.conf: next to the data directory from the registry, else the default ProgramData location.</summary>
    private string? BlueStacksConfPath(string? install)
    {
        var candidates = new List<string>();
        foreach (var key in new[] { @"SOFTWARE\BlueStacks_nxt", @"SOFTWARE\BlueStacks_nxt_cn", @"SOFTWARE\BlueStacks_msi5" })
        {
            var data = _env.ReadRegistryString(true, key, "UserDefinedDir");
            if (!string.IsNullOrWhiteSpace(data))
            {
                candidates.Add(Path.Combine(data, "bluestacks.conf"));
            }
        }

        var programData = _env.ExpandEnvironment("%ProgramData%");
        if (install is not null)
        {
            // The data folder is named like the program folder (BlueStacks_nxt / BlueStacks_nxt_cn).
            candidates.Add(Path.Combine(programData, Path.GetFileName(install), "bluestacks.conf"));
        }

        candidates.Add(Path.Combine(programData, "BlueStacks_nxt", "bluestacks.conf"));
        candidates.Add(Path.Combine(programData, "BlueStacks_nxt_cn", "bluestacks.conf"));
        return candidates.FirstOrDefault(_env.FileExists);
    }

    // ================================================================== Android Studio emulator

    /// <summary>
    /// Android Studio emulator: one <c>qemu-system-*.exe</c> per AVD, each publishing
    /// <c>%TEMP%\avd\running\pid_&lt;pid&gt;.ini</c> with its adb port; the AVD name is also on the command line (-avd).
    /// </summary>
    private List<EmulatorInstance> DetectAvd(List<ProcessInfo> players)
    {
        var result = new List<EmulatorInstance>();
        var runningDirs = new[]
        {
            _env.ExpandEnvironment(@"%TEMP%\avd\running"),
            _env.ExpandEnvironment(@"%LOCALAPPDATA%\Temp\avd\running"),
        }.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var p in players)
        {
            var args = CommandLine.Split(p.CommandLine);
            var ini = runningDirs
                .Select(d => Path.Combine(d, "pid_" + p.Id.ToString(CultureInfo.InvariantCulture) + ".ini"))
                .Select(path => (path, info: EmulatorConfigParsers.AvdRunningIni(_env.ReadFile(path))))
                .FirstOrDefault(t => t.info is not null);
            var avd = ini.info?.AvdName ?? CommandLine.Option(args, "-avd") ?? (args.FirstOrDefault(a => a.StartsWith('@'))?[1..]);
            var port = ini.info?.AdbPort;
            var source = port is not null ? AdbPortSource.Runtime : AdbPortSource.Unknown;
            if (port is null && CommandLine.IntOption(args, "-port") is int console)
            {
                port = console + 1;
                source = AdbPortSource.Formula;
            }

            if (string.IsNullOrWhiteSpace(avd) && port is null)
            {
                continue; // some other qemu (not an Android emulator)
            }

            var exeDir = Path.GetDirectoryName(p.Path);
            result.Add(new EmulatorInstance
            {
                Kind = EmulatorKind.AndroidStudio,
                InstanceKey = string.IsNullOrWhiteSpace(avd) ? "avd-" + p.Id.ToString(CultureInfo.InvariantCulture) : avd,
                Index = port is int ap ? (ap - 5555) / 2 : null,
                DisplayName = avd ?? "",
                // …\Sdk\emulator\qemu\windows-x86_64\qemu-system-x86_64.exe → …\Sdk
                InstallDirectory = exeDir is null ? null : Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..")),
                ProcessId = p.Id,
                ProcessName = p.Name,
                AdbPort = port,
                PortSource = source,
                ConfigPath = ini.info is not null ? ini.path : null,
            });
        }

        return result;
    }

    // ================================================================== windows

    /// <summary>The instance's main window: a window of its player process, preferring the known main window class.</summary>
    private static EmulatorInstance AttachWindow(EmulatorInstance instance, EmulatorDefinition def, IReadOnlyList<DesktopWindowInfo> windows)
    {
        if (instance.ProcessId == 0)
        {
            return instance;
        }

        var own = windows.Where(w => w.ProcessId == (uint)instance.ProcessId).ToList();
        if (own.Count == 0)
        {
            return instance;
        }

        var best = own
            .OrderByDescending(w => def.MainWindowClasses.Contains(w.ClassName, StringComparer.Ordinal))
            .ThenBy(w => w.IsMinimized)
            .ThenBy(w => w.Title.Length)
            .First();
        return instance with
        {
            WindowHandle = best.Handle,
            WindowTitle = best.Title,
            WindowClass = best.ClassName,
            WindowMinimized = best.IsMinimized,
            DisplayName = instance.DisplayName.Length > 0 ? instance.DisplayName : best.Title,
        };
    }

    // ================================================================== installed (registry)

    private IReadOnlyList<InstalledEmulator> DetectInstalled()
    {
        var result = new List<InstalledEmulator>();
        try
        {
            foreach (var def in EmulatorCatalog.All)
            {
                if (def.Kind == EmulatorKind.AndroidStudio)
                {
                    var sdk = _env.ExpandEnvironment(@"%LOCALAPPDATA%\Android\Sdk");
                    if (IsInstall(def, sdk))
                    {
                        result.Add(new InstalledEmulator(def.Kind, sdk));
                    }

                    continue;
                }

                if (InstallDirsFromRegistry(def).FirstOrDefault() is { } dir)
                {
                    result.Add(new InstalledEmulator(def.Kind, dir));
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogDebug(ex, "Could not read installed emulators");
        }

        return result;
    }

    /// <summary>Installation directories named by the registry (uninstall entries, LDPlayer / BlueStacks keys) that pass the marker check.</summary>
    private List<string> InstallDirsFromRegistry(EmulatorDefinition def)
    {
        var dirs = new List<string>();
        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                return;
            }

            var d = dir.Trim().Trim('"').TrimEnd('\\', '/');
            // UninstallString may point into a sub folder (…\MEmu\uninstall\uninstall.exe): walk up to the marker.
            for (var up = 0; up < 3 && d.Length > 3; up++)
            {
                if (IsInstall(def, d))
                {
                    if (!dirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                    {
                        dirs.Add(d);
                    }

                    return;
                }

                d = Path.GetDirectoryName(d) ?? "";
            }
        }

        if (def.Kind == EmulatorKind.LDPlayer)
        {
            foreach (var key in new[] { @"SOFTWARE\leidian\LDPlayer14", @"SOFTWARE\leidian\ldplayer9", @"SOFTWARE\leidian\ldplayer4", @"SOFTWARE\leidian\ldplayer" })
            {
                Add(_env.ReadRegistryString(false, key, "InstallDir"));
                Add(_env.ReadRegistryString(true, key, "InstallDir"));
            }
        }

        if (def.Kind == EmulatorKind.BlueStacks)
        {
            foreach (var key in new[] { @"SOFTWARE\BlueStacks_nxt", @"SOFTWARE\BlueStacks_nxt_cn", @"SOFTWARE\BlueStacks_msi5" })
            {
                Add(_env.ReadRegistryString(true, key, "InstallDir"));
            }
        }

        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var machine in new[] { true, false })
        {
            foreach (var sub in _env.GetRegistrySubKeys(machine, uninstall)
                         .Where(k => def.UninstallKeyPrefixes.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
            {
                var key = uninstall + "\\" + sub;
                Add(_env.ReadRegistryString(machine, key, "InstallLocation"));
                Add(ExecutableDirectory(_env.ReadRegistryString(machine, key, "UninstallString")));
                Add(ExecutableDirectory(_env.ReadRegistryString(machine, key, "DisplayIcon")));
            }
        }

        return dirs;
    }

    /// <summary>Directory of the executable at the start of a command (quoted or not, with or without arguments).</summary>
    internal static string? ExecutableDirectory(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var s = command.Trim();
        string exe;
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            exe = end > 1 ? s[1..end] : s.Trim('"');
        }
        else
        {
            var idx = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            exe = idx > 0 ? s[..(idx + 4)] : s.Split(' ')[0];
        }

        // DisplayIcon may be "path,0".
        var comma = exe.LastIndexOf(',');
        if (comma > 0 && int.TryParse(exe.AsSpan(comma + 1), out _))
        {
            exe = exe[..comma];
        }

        return Path.GetDirectoryName(exe);
    }

    private bool IsInstall(EmulatorDefinition def, string dir) =>
        def.InstallMarkers.Count == 0 || def.InstallMarkers.Any(m => _env.FileExists(Path.Combine(dir, m)));
}
