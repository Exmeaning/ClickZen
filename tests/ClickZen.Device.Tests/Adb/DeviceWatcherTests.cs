using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using AdvancedSharpAdbClient.Models;
using ClickZen.Core.Devices;
using ClickZen.Device.Adb;

namespace ClickZen.Device.Tests.Adb;

public sealed class DeviceListDiffTests
{
    private static DeviceInfo D(string serial, DeviceAdbState state = DeviceAdbState.Online, string model = "") =>
        new() { Serial = serial, State = state, Model = model };

    [Fact]
    public void Added_removed_and_state_changes()
    {
        var current = new[] { D("a", model: "Enriched A"), D("b"), D("c", DeviceAdbState.Unauthorized) };
        var snapshot = new[] { D("a"), D("c", DeviceAdbState.Online), D("d") };

        var (next, change) = DeviceListDiff.Apply(current, snapshot);

        Assert.Equal(["a", "c", "d"], next.Select(d => d.Serial));
        Assert.Equal("Enriched A", next[0].Model); // enrichment survives
        Assert.Equal(["d"], change.Added.Select(d => d.Serial));
        Assert.Equal(["b"], change.Removed.Select(d => d.Serial));
        var sc = Assert.Single(change.StateChanged);
        Assert.Equal(("c", DeviceAdbState.Unauthorized, DeviceAdbState.Online), (sc.Device.Serial, sc.OldState, sc.NewState));
        Assert.False(change.IsEmpty);
    }

    [Fact]
    public void Identical_snapshot_is_empty_change()
    {
        var current = new[] { D("a", model: "X") };
        var (_, change) = DeviceListDiff.Apply(current, [D("a")]);
        Assert.True(change.IsEmpty);
    }

    [Fact]
    public void Enriched_applies_only_when_state_matches()
    {
        var current = new[] { D("a"), D("b", DeviceAdbState.Offline) };
        var (next, updated) = DeviceListDiff.ApplyEnriched(current, D("a", model: "Pixel"));
        Assert.NotNull(updated);
        Assert.Equal("Pixel", next[0].Model);

        Assert.Null(DeviceListDiff.ApplyEnriched(current, D("b", model: "late")).Updated);
        Assert.Null(DeviceListDiff.ApplyEnriched(current, D("gone", model: "late")).Updated);
        Assert.Null(DeviceListDiff.ApplyEnriched(current, current[0]).Updated);
    }
}

public sealed class AsyncTtlCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Concurrent_callers_share_one_load()
    {
        var cache = new ClickZen.Device.Adb.AsyncTtlCache<string, int>();
        var calls = 0;
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> Factory(string _)
        {
            Interlocked.Increment(ref calls);
            return gate.Task;
        }

        var t1 = cache.GetOrAddAsync("k", Factory, TimeSpan.FromMinutes(1));
        var t2 = cache.GetOrAddAsync("k", Factory, TimeSpan.FromMinutes(1));
        gate.SetResult(7);
        var results = await Task.WhenAll(t1, t2).WaitAsync(Ct);
        Assert.Equal([7, 7], results);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Failures_are_not_cached_and_ttl_expires()
    {
        var time = new ManualTimeProvider();
        var cache = new ClickZen.Device.Adb.AsyncTtlCache<string, int>(time);
        var n = 0;
        Task<int> Factory(string _) => ++n == 1 ? Task.FromException<int>(new IOException()) : Task.FromResult(n);

        await Assert.ThrowsAsync<IOException>(() => cache.GetOrAddAsync("k", Factory, TimeSpan.FromSeconds(10)));
        Assert.Equal(2, await cache.GetOrAddAsync("k", Factory, TimeSpan.FromSeconds(10)));
        Assert.Equal(2, await cache.GetOrAddAsync("k", Factory, TimeSpan.FromSeconds(10)));
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(3, await cache.GetOrAddAsync("k", Factory, TimeSpan.FromSeconds(10)));
        cache.Invalidate("k");
        Assert.Equal(4, await cache.GetOrAddAsync("k", Factory, TimeSpan.FromSeconds(10)));
    }
}

/// <summary>
/// Exercises <see cref="DeviceWatcher"/> against an in-process fake adb server that speaks the
/// <c>host:track-devices</c> wire protocol – no real adb or device involved.
/// </summary>
public sealed class DeviceWatcherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tracks_devices_and_enriches()
    {
        await using var server = new FakeTrackServer();
        var (client, fake) = FakeAdbClient.Create();
        fake.EndPoint = server.EndPoint;
        fake.Shell = (_, cmd, _) => Task.FromResult<IEnumerable<string>>(
            cmd.StartsWith("wm", StringComparison.Ordinal)
                ? ["Physical size: 1080x2400", "Physical density: 440"]
                : ["ro.product.brand=Xiaomi", "ro.product.model=2201123C", "ro.build.version.release=13", "ro.build.version.sdk=33"]);
        var adb = new AdbService(client, null, null, null) { AutoStartServer = false };
        await using var watcher = new DeviceWatcher(adb);
        var events = Channel.CreateUnbounded<DeviceListChangedEventArgs>();
        watcher.DevicesChanged += (_, e) => events.Writer.TryWrite(e);

        server.Push("R5CT\tunauthorized\n");
        await watcher.StartAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var first = await NextAsync(events);
        Assert.Equal("R5CT", Assert.Single(first.Added).Serial);
        Assert.Equal(DeviceAdbState.Unauthorized, watcher.Devices[0].State);

        server.Push("R5CT\tdevice\n127.0.0.1:16384\tdevice\n");
        var change = await NextAsync(events);
        Assert.Single(change.StateChanged);
        Assert.Equal("127.0.0.1:16384", Assert.Single(change.Added).Serial);
        Assert.Equal(ConnectionKind.Emulator, change.Added[0].Kind);

        // Two enrichment updates follow (one per online device).
        var enriched = new List<DeviceInfo>();
        while (enriched.Count < 2)
        {
            enriched.AddRange((await NextAsync(events)).Updated);
        }

        Assert.All(watcher.Devices, d =>
        {
            Assert.Equal("Xiaomi", d.Brand);
            Assert.Equal(440, d.Density);
        });

        server.Push("127.0.0.1:16384\tdevice\n");
        var removal = await NextAsync(events);
        Assert.Equal("R5CT", Assert.Single(removal.Removed).Serial);
        Assert.Single(watcher.Devices);
    }

    [Fact]
    public async Task Reconnects_after_server_drops_connection()
    {
        await using var server = new FakeTrackServer();
        var (client, fake) = FakeAdbClient.Create();
        fake.EndPoint = server.EndPoint;
        var adb = new AdbService(client, null, null, null) { AutoStartServer = false };
        await using var watcher = new DeviceWatcher(adb);
        var events = Channel.CreateUnbounded<DeviceListChangedEventArgs>();
        watcher.DevicesChanged += (_, e) => events.Writer.TryWrite(e);

        server.Push("A\toffline\n");
        await watcher.StartAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Single((await NextAsync(events)).Added);

        server.DropCurrentConnection();
        var cleared = await NextAsync(events);
        Assert.Equal("A", Assert.Single(cleared.Removed).Serial);

        server.Push("A\toffline\nB\toffline\n");
        var back = await NextAsync(events);
        Assert.Equal(2, back.Added.Count);
        Assert.True(server.Connections >= 2);
    }

    [Fact]
    public async Task Start_completes_when_no_server_is_listening()
    {
        // Find a free port and do not listen on it.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var endPoint = (IPEndPoint)probe.LocalEndpoint;
        probe.Stop();

        var (client, fake) = FakeAdbClient.Create();
        fake.EndPoint = endPoint;
        var adb = new AdbService(client, null, null, null) { AutoStartServer = false };
        await using var watcher = new DeviceWatcher(adb);
        await watcher.StartAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Empty(watcher.Devices);
        Assert.False(watcher.IsTracking);
    }

    [Fact]
    public void Parse_snapshot_maps_kind_and_state()
    {
        var list = DeviceWatcher.ParseSnapshot("emulator-5554\tdevice\n10.0.0.2:5555\toffline\nXYZ\tunauthorized\n");
        Assert.Equal(
            [("emulator-5554", ConnectionKind.Emulator, DeviceAdbState.Online), ("10.0.0.2:5555", ConnectionKind.Wireless, DeviceAdbState.Offline), ("XYZ", ConnectionKind.Usb, DeviceAdbState.Unauthorized)],
            list.Select(d => (d.Serial, d.Kind, d.State)));
    }

    [Fact]
    public void State_mapping_from_asac()
    {
        Assert.Equal(DeviceAdbState.Online, AdbService.MapState(DeviceState.Online));
        Assert.Equal(DeviceAdbState.Offline, AdbService.MapState(DeviceState.Offline));
        Assert.Equal(DeviceAdbState.Unauthorized, AdbService.MapState(DeviceState.Unauthorized));
        Assert.Equal(DeviceAdbState.Unauthorized, AdbService.MapState(DeviceState.Authorizing));
        Assert.Equal(DeviceAdbState.Other, AdbService.MapState(DeviceState.Recovery));
    }

    private static async Task<DeviceListChangedEventArgs> NextAsync(Channel<DeviceListChangedEventArgs> ch) =>
        await ch.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

    /// <summary>Accepts adb client connections, answers OKAY to host:track-devices and streams queued payloads.</summary>
    private sealed class FakeTrackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Channel<string> _payloads = Channel.CreateUnbounded<string>();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private CancellationTokenSource? _connectionCts;
        private int _connections;

        public FakeTrackServer()
        {
            _listener.Start();
            EndPoint = (IPEndPoint)_listener.LocalEndpoint;
            _loop = Task.Run(AcceptLoopAsync);
        }

        public IPEndPoint EndPoint { get; }

        public int Connections => Volatile.Read(ref _connections);

        public void Push(string payload) => _payloads.Writer.TryWrite(payload);

        public void DropCurrentConnection() => Volatile.Read(ref _connectionCts)?.Cancel();

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (Exception)
            {
            }

            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception)
                {
                    return;
                }

                Interlocked.Increment(ref _connections);
                using var connCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                Volatile.Write(ref _connectionCts, connCts);
                var ct = connCts.Token;
                using (tcp)
                {
                    try
                    {
                        var stream = tcp.GetStream();
                        var header = new byte[4];
                        await stream.ReadExactlyAsync(header, ct);
                        var len = int.Parse(Encoding.ASCII.GetString(header), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        var req = new byte[len];
                        await stream.ReadExactlyAsync(req, ct);
                        if (Encoding.ASCII.GetString(req) != "host:track-devices")
                        {
                            continue;
                        }

                        await stream.WriteAsync("OKAY"u8.ToArray(), ct);
                        while (!ct.IsCancellationRequested)
                        {
                            // Cancelled reads do not consume a payload, so nothing is lost when the connection drops.
                            var payload = await _payloads.Reader.ReadAsync(ct);
                            var bytes = Encoding.UTF8.GetBytes(payload);
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(bytes.Length.ToString("x4", CultureInfo.InvariantCulture)), ct);
                            await stream.WriteAsync(bytes, ct);
                        }
                    }
                    catch (Exception)
                    {
                        // connection dropped or shutting down
                    }
                    finally
                    {
                        Volatile.Write(ref _connectionCts, null);
                    }
                }
            }
        }
    }
}
