using AdvancedSharpAdbClient.Models;
using ClickZen.Core.Devices;
using ClickZen.Core.Geometry;
using ClickZen.Device.Adb;
using ClickZen.Device.Adb.Parsing;
using AsacDeviceNotFound = AdvancedSharpAdbClient.Exceptions.DeviceNotFoundException;

namespace ClickZen.Device.Tests.Adb;

public sealed class AdbServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (AdbService Service, FakeAdbClient Fake, ManualTimeProvider Time) Create()
    {
        var (client, fake) = FakeAdbClient.Create();
        var time = new ManualTimeProvider();
        return (new AdbService(client, null, null, time), fake, time);
    }

    private static IEnumerable<string> Lines(string text) => text.Replace("\r", "", StringComparison.Ordinal).Split('\n');

    private static Task<IEnumerable<string>> StandardShell(string serial, string cmd, CancellationToken ct)
    {
        if (cmd == PropParser.Command)
        {
            return Task.FromResult(Lines($"ro.product.brand=google\nro.product.manufacturer=Google\nro.product.model=Pixel {serial.Length}\nro.build.version.release=14\nro.build.version.sdk=34"));
        }

        if (cmd.StartsWith("wm size", StringComparison.Ordinal))
        {
            return Task.FromResult(Lines("Physical size: 1080x2400\nOverride size: 720x1600\nPhysical density: 420"));
        }

        return Task.FromResult(Enumerable.Empty<string>());
    }

    [Fact]
    public async Task GetDevices_enriches_online_devices_and_caches()
    {
        var (svc, fake, _) = Create();
        fake.Devices.Add(new DeviceData { Serial = "R5CT", State = DeviceState.Online, Model = "SM_G991B" });
        fake.Devices.Add(new DeviceData { Serial = "192.168.1.9:5555", State = DeviceState.Unauthorized });
        fake.Devices.Add(new DeviceData { Serial = "emulator-5554", State = DeviceState.Online });
        fake.Shell = StandardShell;

        var list = await svc.GetDevicesAsync(Ct);

        Assert.Equal(3, list.Count);
        var usb = list[0];
        Assert.Equal(DeviceAdbState.Online, usb.State);
        Assert.Equal(ConnectionKind.Usb, usb.Kind);
        Assert.Equal("Google", usb.Brand);
        Assert.Equal("Pixel 4", usb.Model);
        Assert.Equal("14", usb.AndroidVersion);
        Assert.Equal(34, usb.SdkLevel);
        Assert.Equal(new SizeI(720, 1600), usb.PhysicalSize);
        Assert.Equal(420, usb.Density);

        Assert.Equal(DeviceAdbState.Unauthorized, list[1].State);
        Assert.Equal(ConnectionKind.Wireless, list[1].Kind);
        Assert.Equal("", list[1].Brand);

        Assert.Equal(ConnectionKind.Emulator, list[2].Kind);
        Assert.Equal(4, fake.ShellLog.Count); // 2 online devices × (props + display)

        await svc.GetDevicesAsync(Ct);
        Assert.Equal(4, fake.ShellLog.Count); // served from cache
    }

    [Fact]
    public async Task Display_cache_expires_but_props_do_not()
    {
        var (svc, fake, time) = Create();
        fake.Devices.Add(new DeviceData { Serial = "R5CT", State = DeviceState.Online });
        fake.Shell = StandardShell;

        await svc.GetDevicesAsync(Ct);
        time.Advance(AdbService.DisplayCacheTtl + TimeSpan.FromSeconds(1));
        await svc.GetDevicesAsync(Ct);

        var log = fake.ShellLog.ToArray();
        Assert.Equal(1, log.Count(l => l.Contains("getprop", StringComparison.Ordinal)));
        Assert.Equal(2, log.Count(l => l.Contains("wm size", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Property_queries_run_in_parallel()
    {
        var (svc, fake, _) = Create();
        for (var i = 0; i < 4; i++)
        {
            fake.Devices.Add(new DeviceData { Serial = $"dev{i}", State = DeviceState.Online });
        }

        var running = 0;
        var peak = 0;
        fake.Shell = async (s, c, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(150, ct);
            Interlocked.Decrement(ref running);
            return await StandardShell(s, c, ct);
        };

        await svc.GetDevicesAsync(Ct);
        Assert.True(peak >= 4, $"peak concurrency was {peak}");
    }

    [Fact]
    public async Task Failed_enrichment_still_returns_device()
    {
        var (svc, fake, _) = Create();
        fake.Devices.Add(new DeviceData { Serial = "R5CT", State = DeviceState.Online, Model = "Fallback_Model" });
        fake.Shell = (_, _, _) => throw new AsacDeviceNotFound("R5CT");

        var d = Assert.Single(await svc.GetDevicesAsync(Ct));
        Assert.Equal("Fallback Model", d.Model);
        Assert.True(d.PhysicalSize.IsEmpty);
    }

    [Fact]
    public async Task Shell_returns_complete_output()
    {
        var (svc, fake, _) = Create();
        var lines = Enumerable.Range(0, 5000).Select(i => $"line {i}").ToArray();
        fake.Shell = (_, _, _) => Task.FromResult<IEnumerable<string>>(lines);

        var output = await svc.ShellAsync("R5CT", "dumpsys", Ct);
        Assert.Equal(string.Join('\n', lines), output);
    }

    [Fact]
    public async Task Shell_timeout_throws_timeout_kind()
    {
        var (svc, fake, _) = Create();
        fake.Shell = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        };

        var ex = await Assert.ThrowsAsync<AdbException>(() => svc.ShellAsync("R5CT", "sleep 100", TimeSpan.FromMilliseconds(100), Ct));
        Assert.Equal(AdbErrorKind.Timeout, ex.Kind);
        Assert.Equal("R5CT", ex.Serial);
    }

    [Fact]
    public async Task Shell_caller_cancellation_is_not_a_timeout()
    {
        var (svc, fake, _) = Create();
        fake.Shell = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.ShellAsync("R5CT", "sleep 100", cts.Token));
    }

    [Fact]
    public async Task Shell_maps_device_not_found()
    {
        var (svc, fake, _) = Create();
        fake.Shell = (_, _, _) => throw new AsacDeviceNotFound("gone");
        var ex = await Assert.ThrowsAsync<AdbException>(() => svc.ShellAsync("gone", "id", Ct));
        Assert.Equal(AdbErrorKind.DeviceNotFound, ex.Kind);
    }

    [Fact]
    public async Task Root_shell_quotes_command()
    {
        var (svc, fake, _) = Create();
        await svc.RootShellAsync("R5CT", "echo 'a b' > /data/x", Ct);
        Assert.Equal(@"R5CT: su -c 'echo '\''a b'\'' > /data/x'", Assert.Single(fake.ShellLog));
    }

    [Fact]
    public async Task Check_root_success()
    {
        var (svc, fake, _) = Create();
        fake.Shell = (_, _, _) => Task.FromResult<IEnumerable<string>>(["uid=0(root) gid=0(root) context=u:r:magisk:s0"]);
        var r = await svc.CheckRootAsync("R5CT", Ct);
        Assert.True(r.IsRooted);
        Assert.Equal(RootStatus.Rooted, r.Status);
    }

    [Fact]
    public async Task Check_root_falls_back_to_aosp_su()
    {
        var (svc, fake, _) = Create();
        fake.Shell = (_, cmd, _) => Task.FromResult<IEnumerable<string>>(cmd.StartsWith("su 0", StringComparison.Ordinal)
            ? ["uid=0(root) gid=0(root)"]
            : ["su: invalid uid/gid '-c'"]);

        var r = await svc.CheckRootAsync("emulator-5554", Ct);
        Assert.True(r.IsRooted);

        await svc.RootShellAsync("emulator-5554", "id", Ct);
        Assert.Equal("emulator-5554: su 0 sh -c 'id'", fake.ShellLog.Last());
    }

    [Fact]
    public async Task Check_root_timeout_is_a_status()
    {
        var (svc, fake, _) = Create();
        fake.Shell = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        };

        var r = await svc.CheckRootAsync("R5CT", TimeSpan.FromMilliseconds(100), Ct);
        Assert.False(r.IsRooted);
        Assert.Equal(RootStatus.Timeout, r.Status);
    }

    [Fact]
    public async Task Check_root_su_missing()
    {
        var (svc, fake, _) = Create();
        fake.Shell = (_, _, _) => Task.FromResult<IEnumerable<string>>(["/system/bin/sh: su: inaccessible or not found"]);
        var r = await svc.CheckRootAsync("R5CT", Ct);
        Assert.Equal(RootStatus.SuNotFound, r.Status);
    }

    [Fact]
    public async Task Streaming_delivers_lines_until_end()
    {
        var (svc, fake, _) = Create();
        fake.Shell = (_, _, _) => Task.FromResult<IEnumerable<string>>(["a", "b", "c"]);
        var got = new List<string>();
        await svc.ExecuteStreamingAsync("R5CT", "getevent -lt", got.Add, Ct);
        Assert.Equal(["a", "b", "c"], got);
    }

    [Fact]
    public async Task Streaming_cancellation_throws_oce()
    {
        var (svc, fake, _) = Create();
        fake.Shell = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.ExecuteStreamingAsync("R5CT", "getevent", _ => { }, cts.Token));
    }

    [Fact]
    public async Task Streaming_callback_exception_is_rethrown()
    {
        var (svc, fake, _) = Create();
        fake.Shell = (_, _, _) => Task.FromResult<IEnumerable<string>>(["a", "boom", "c"]);
        var got = new List<string>();
        var ex = await Assert.ThrowsAsync<FormatException>(() => svc.ExecuteStreamingAsync("R5CT", "x", l =>
        {
            if (l == "boom")
            {
                throw new FormatException("bad line");
            }

            got.Add(l);
        }, Ct));
        Assert.Equal("bad line", ex.Message);
        Assert.Equal(["a"], got);
    }

    [Fact]
    public async Task Connect_success_and_deconstruct()
    {
        var (svc, fake, _) = Create();
        fake.Connect = (a, _) => Task.FromResult($"connected to {a}");
        var (ok, message) = await svc.ConnectAsync("192.168.1.9", Ct);
        Assert.True(ok);
        Assert.Equal("connected to 192.168.1.9:5555", message);
        Assert.Equal("192.168.1.9:5555", Assert.Single(fake.ConnectLog));
    }

    [Fact]
    public async Task Connect_invalid_address_does_not_call_adb()
    {
        var (svc, fake, _) = Create();
        var r = await svc.ConnectAsync("not an address", Ct);
        Assert.False(r.Success);
        Assert.Equal(ConnectResultKind.InvalidAddress, r.Kind);
        Assert.Empty(fake.ConnectLog);
    }

    [Fact]
    public async Task Connect_refused_is_reported_not_thrown()
    {
        var (svc, _, _) = Create();
        var r = await svc.ConnectAsync("10.0.0.2:5555", Ct);
        Assert.False(r.Success);
        Assert.Equal(ConnectResultKind.Refused, r.Kind);
    }

    [Fact]
    public async Task Pair_requires_port_and_does_not_connect()
    {
        var (svc, fake, _) = Create();
        var bad = await svc.PairAsync("192.168.1.9", "123456", Ct);
        Assert.Equal(ConnectResultKind.InvalidAddress, bad.Kind);

        fake.Pair = (a, p, c, _) => Task.FromResult($"Successfully paired to {a} [guid=adb-x]");
        var ok = await svc.PairAsync("192.168.1.9:37099", " 123456 ", Ct);
        Assert.True(ok.Success);
        Assert.Equal(ConnectResultKind.Paired, ok.Kind);
        Assert.Equal("192.168.1.9:37099", ok.Address);
        Assert.Empty(fake.ConnectLog);

        var empty = await svc.PairAsync("192.168.1.9:37099", "  ", Ct);
        Assert.Equal(ConnectResultKind.WrongPairingCode, empty.Kind);
    }

    [Fact]
    public async Task Scan_emulators_connects_in_parallel_and_skips_known()
    {
        var (svc, fake, _) = Create();
        fake.Devices.Add(new DeviceData { Serial = "emulator-5554", State = DeviceState.Online });
        var concurrent = 0;
        var peak = 0;
        fake.Connect = async (a, ct) =>
        {
            InterlockedMax(ref peak, Interlocked.Increment(ref concurrent));
            try
            {
                await Task.Delay(100, ct);
                return a is "127.0.0.1:16384" or "127.0.0.1:5555"
                    ? $"connected to {a}"
                    : $"cannot connect to {a}: 由于目标计算机积极拒绝，无法连接。 (10061)";
            }
            finally
            {
                Interlocked.Decrement(ref concurrent);
            }
        };

        var found = await svc.ScanEmulatorsAsync(Ct);

        Assert.Equal(["127.0.0.1:16384"], found);
        Assert.DoesNotContain("127.0.0.1:5555", fake.ConnectLog);
        Assert.Equal(EmulatorPresets.AllPorts.Count - 1, fake.ConnectLog.Count);
        Assert.True(peak > 4, $"peak concurrency was {peak}");
    }

    [Fact]
    public async Task Scan_emulators_respects_per_port_timeout()
    {
        var (svc, fake, _) = Create();
        fake.Connect = async (a, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var found = await svc.ScanEmulatorsAsync(Ct);
        Assert.Empty(found);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"scan took {sw.Elapsed}");
    }

    [Fact]
    public async Task Push_missing_file_throws_file_not_found()
    {
        var (svc, _, _) = Create();
        await Assert.ThrowsAsync<FileNotFoundException>(() => svc.PushAsync("R5CT", @"C:\definitely\missing.jar", "/data/local/tmp/x", ct: Ct));
    }

    [Fact]
    public async Task Device_ip_from_wlan0_then_route()
    {
        var (svc, fake, _) = Create();
        fake.Shell = (_, cmd, _) => Task.FromResult(cmd.StartsWith("ip addr", StringComparison.Ordinal)
            ? Lines(SampleFiles.Read("ip/ip_addr_wlan0_down.txt"))
            : Lines(SampleFiles.Read("ip/ip_route.txt")));
        Assert.Equal("192.168.1.23", await svc.GetDeviceIpAsync("R5CT", Ct));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

/// <summary>Minimal manual clock for cache TTL tests.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
