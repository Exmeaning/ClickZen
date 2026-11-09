using System.Buffers.Binary;
using System.Net;
using System.Text;
using ClickZen.Core.Devices;
using ClickZen.Core.Persistence;
using ClickZen.Core.Wireless;
using ClickZen.Device.Adb;
using ClickZen.Device.Adb.Wireless;

namespace ClickZen.Device.Tests.Adb;

public sealed class WirelessMdnsAndCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cz-mdns-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeMdnsDiscovery(Func<int, IReadOnlyList<MdnsAdbService>> responder) : IAdbMdnsDiscovery
    {
        private int _calls;

        public Task<IReadOnlyList<MdnsAdbService>> DiscoverAsync(TimeSpan duration, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var idx = Interlocked.Increment(ref _calls);
            return Task.FromResult(responder(idx));
        }
    }

    [Fact]
    public void Builds_valid_mdns_query_and_parses_dns_sd_srv_and_a_records()
    {
        var query = MdnsPacket.BuildPtrQuery(unicastResponse: false);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(0, 2)));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4, 2))); // 3 PTR questions

        var resp = BuildSyntheticMdnsResponse(
            instance: "clickzen-abc123",
            service: "_adb-tls-pairing._tcp.local",
            targetHost: "pixel8.local",
            port: 39123,
            ip: IPAddress.Parse("192.168.1.77"));

        var parsed = MdnsPacket.ParseMessage(resp, IPAddress.Parse("192.168.1.1"));
        var svc = Assert.Single(parsed);
        Assert.Equal("clickzen-abc123", svc.InstanceName);
        Assert.Equal(MdnsAdbServiceKind.TlsPairing, svc.Kind);
        Assert.Equal("192.168.1.77", svc.Host);
        Assert.Equal(39123, svc.Port);
        Assert.Equal("192.168.1.77:39123", svc.Address);
    }

    [Fact]
    public void Parses_adb_mdns_services_cli_output()
    {
        const string sample = """
            List of discovered mdns services
            adb-R5CT1234-v2k9a	_adb-tls-connect._tcp.	192.168.31.88:43109
            clickzen-9988aabb	_adb-tls-pairing._tcp	192.168.31.88:37511
            legacy-phone	_adb._tcp.	10.0.0.18:5555
            """;

        var list = MdnsPacket.ParseAdbMdnsServicesOutput(sample);
        Assert.Equal(3, list.Count);
        Assert.Contains(list, s => s.Kind == MdnsAdbServiceKind.TlsConnect && s.Address == "192.168.31.88:43109");
        Assert.Contains(list, s => s.Kind == MdnsAdbServiceKind.TlsPairing && s.InstanceName == "clickzen-9988aabb" && s.Port == 37511);
        Assert.Contains(list, s => s.Kind == MdnsAdbServiceKind.LegacyTcp && s.Address == "10.0.0.18:5555");
    }

    [Fact]
    public async Task AutoConnector_heals_randomized_tls_port_via_mdns_and_updates_saved_store()
    {
        var store = new SavedDeviceStore(new AppPaths(_root));
        store.AddOrUpdate(new SavedDevice { Name = "My Xiaomi", Host = "192.168.1.66", Port = 38111, AutoConnect = true });

        var (client, fake) = FakeAdbClient.Create();
        fake.Connect = (addr, _) => Task.FromResult(
            addr == "192.168.1.66:44229"
                ? $"connected to {addr}"
                : $"cannot connect to {addr}: No connection could be made (10061)");
        var adb = new AdbService(client, null, null, null);
        var mdns = new FakeMdnsDiscovery(_ =>
        [
            new MdnsAdbService(MdnsAdbServiceKind.TlsConnect, "adb-xiaomi", "192.168.1.66", 44229),
        ]);

        var results = await new AutoConnector(adb, store, () => true, mdns, null).ConnectSavedAsync(TestContext.Current.CancellationToken);

        var r = Assert.Single(results);
        Assert.True(r.Success);
        Assert.Equal("192.168.1.66:44229", r.Address);

        // SavedDeviceStore must be updated to the new dynamic port so future connects succeed immediately.
        var updated = Assert.Single(store.GetAll());
        Assert.Equal("My Xiaomi", updated.Name);
        Assert.Equal(44229, updated.Port);
    }

    [Fact]
    public async Task Qr_pair_coordinator_pairs_and_connects_automatically_when_phone_scans_qr()
    {
        var creds = AdbWirelessQrCredentials.Create("clickzen-testpair01", "876543");
        string? pairedAddr = null;
        string? pairedCode = null;

        var (client, fake) = FakeAdbClient.Create();
        fake.Pair = (addr, code, _) =>
        {
            pairedAddr = addr;
            pairedCode = code;
            return Task.FromResult($"Successfully paired to {addr} [guid=adb-pixel]");
        };
        fake.Connect = (addr, _) => Task.FromResult($"connected to {addr}");
        var adb = new AdbService(client, null, null, null);

        var mdns = new FakeMdnsDiscovery(call => call switch
        {
            1 => [], // Phone hasn't scanned QR yet
            2 => [new MdnsAdbService(MdnsAdbServiceKind.TlsPairing, "clickzen-testpair01", "192.168.1.90", 37412)],
            _ => [new MdnsAdbService(MdnsAdbServiceKind.TlsConnect, "adb-pixel-xyz", "192.168.1.90", 41888)],
        });

        var stages = new List<WirelessQrPairStage>();
        var progress = new SynchronousProgress(p => stages.Add(p.Stage));
        var coordinator = new WirelessQrPairCoordinator(adb, mdns);

        var outcome = await coordinator.PairAndConnectAsync(creds, progress, TestContext.Current.CancellationToken);

        Assert.Equal(WirelessQrPairStage.Connected, outcome.Stage);
        Assert.Equal("192.168.1.90:37412", pairedAddr);
        Assert.Equal("876543", pairedCode);
        Assert.Equal("192.168.1.90:41888", outcome.ConnectAddress);
        Assert.Contains(WirelessQrPairStage.Pairing, stages);
        Assert.Contains(WirelessQrPairStage.Connecting, stages);
        Assert.Contains(WirelessQrPairStage.Connected, stages);
    }

    [Fact]
    public async Task EnableWirelessAsync_reads_wifi_ip_switches_tcpip_and_connects()
    {
        var (client, fake) = FakeAdbClient.Create();
        fake.Devices.Add(FakeAdbClient.MakeDevice("USB123456", AdvancedSharpAdbClient.Models.DeviceState.Online));
        fake.Shell = (_, cmd) => cmd.Contains("ip route", StringComparison.Ordinal)
            ? "192.168.50.0/24 dev wlan0 proto kernel scope link src 192.168.50.23"
            : "";
        fake.Connect = (addr, _) => Task.FromResult($"connected to {addr}");

        var tcpipSwitched = new List<(string Serial, int Port)>();
        var adb = new AdbService(client, null, null, null);

        var res = await adb.EnableWirelessAsync(
            "USB123456",
            5555,
            (serial, port, _) =>
            {
                tcpipSwitched.Add((serial, port));
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.True(res.Success);
        Assert.Equal("192.168.50.23:5555", res.Address);
        Assert.Equal([("USB123456", 5555)], tcpipSwitched);
        Assert.Contains("192.168.50.23:5555", fake.ConnectLog);
    }

    private sealed class SynchronousProgress(Action<WirelessQrPairProgress> handler) : IProgress<WirelessQrPairProgress>
    {
        public void Report(WirelessQrPairProgress value) => handler(value);
    }

    private static byte[] BuildSyntheticMdnsResponse(string instance, string service, string targetHost, ushort port, IPAddress ip)
    {
        using var ms = new MemoryStream();
        Span<byte> u16 = stackalloc byte[2];
        Span<byte> u32 = stackalloc byte[4];
        void WriteU16(ushort v) { BinaryPrimitives.WriteUInt16BigEndian(u16, v); ms.Write(u16); }
        void WriteU32(uint v) { BinaryPrimitives.WriteUInt32BigEndian(u32, v); ms.Write(u32); }
        void WriteName(string fqdn)
        {
            foreach (var label in fqdn.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                ms.WriteByte((byte)bytes.Length);
                ms.Write(bytes);
            }
            ms.WriteByte(0);
        }

        // Header: ID=0, Flags=0x8400 (response), QD=0, AN=2, NS=0, AR=1
        WriteU16(0);
        WriteU16(0x8400);
        WriteU16(0);
        WriteU16(2);
        WriteU16(0);
        WriteU16(1);

        var fullInstance = $"{instance}.{service}";

        // Answer 1: PTR service -> fullInstance
        WriteName(service);
        WriteU16(12); // PTR
        WriteU16(1);
        WriteU32(120);
        using (var rdata = new MemoryStream())
        {
            foreach (var label in fullInstance.Split('.'))
            {
                var b = Encoding.UTF8.GetBytes(label);
                rdata.WriteByte((byte)b.Length);
                rdata.Write(b);
            }
            rdata.WriteByte(0);
            var rb = rdata.ToArray();
            WriteU16((ushort)rb.Length);
            ms.Write(rb);
        }

        // Answer 2: SRV fullInstance -> targetHost:port
        WriteName(fullInstance);
        WriteU16(33); // SRV
        WriteU16(0x8001);
        WriteU32(120);
        using (var rdata = new MemoryStream())
        {
            Span<byte> hdr = stackalloc byte[6];
            BinaryPrimitives.WriteUInt16BigEndian(hdr[..2], 0);
            BinaryPrimitives.WriteUInt16BigEndian(hdr[2..4], 0);
            BinaryPrimitives.WriteUInt16BigEndian(hdr[4..6], port);
            rdata.Write(hdr);
            foreach (var label in targetHost.Split('.'))
            {
                var b = Encoding.UTF8.GetBytes(label);
                rdata.WriteByte((byte)b.Length);
                rdata.Write(b);
            }
            rdata.WriteByte(0);
            var rb = rdata.ToArray();
            WriteU16((ushort)rb.Length);
            ms.Write(rb);
        }

        // Additional 1: A targetHost -> ip
        WriteName(targetHost);
        WriteU16(1); // A
        WriteU16(0x8001);
        WriteU32(120);
        var ipBytes = ip.GetAddressBytes();
        WriteU16((ushort)ipBytes.Length);
        ms.Write(ipBytes);

        return ms.ToArray();
    }
}
