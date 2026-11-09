using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ClickZen.Core.Wireless;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClickZen.Device.Adb.Wireless;

/// <summary>Discovers Android Wireless Debugging endpoints (<c>_adb-tls-connect</c>, <c>_adb-tls-pairing</c>, <c>_adb</c>) on the LAN.</summary>
public interface IAdbMdnsDiscovery
{
    Task<IReadOnlyList<MdnsAdbService>> DiscoverAsync(TimeSpan duration, CancellationToken ct = default);
}

/// <summary>
/// Pure-C# multicast mDNS querier/listener (<c>224.0.0.251:5353</c> across active IPv4 LAN interfaces, plus
/// <c>adb mdns services</c> CLI supplement when available) so Wireless Debugging QR pairing and random-port
/// auto-recovery work out of the box on Windows without installing Bonjour.
/// </summary>
public sealed class AdbMdnsDiscovery : IAdbMdnsDiscovery
{
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint MulticastEndpoint = new(MulticastGroup, 5353);

    private readonly AdbServerHost? _serverHost;
    private readonly ILogger _logger;

    public AdbMdnsDiscovery(AdbServerHost? serverHost = null, ILogger<AdbMdnsDiscovery>? logger = null)
    {
        _serverHost = serverHost;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public async Task<IReadOnlyList<MdnsAdbService>> DiscoverAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var found = new ConcurrentDictionary<(MdnsAdbServiceKind, string, string, int), MdnsAdbService>();
        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        windowCts.CancelAfter(duration < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : duration);

        var tasks = new List<Task>
        {
            ProbeUdpMulticastAsync(found, windowCts.Token),
            ProbeAdbMdnsCliAsync(found, windowCts.Token),
        };

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }

        return found.Values
            .OrderBy(s => s.Kind)
            .ThenBy(s => s.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Port)
            .ToArray();
    }

    private async Task ProbeUdpMulticastAsync(
        ConcurrentDictionary<(MdnsAdbServiceKind, string, string, int), MdnsAdbService> found,
        CancellationToken ct)
    {
        var sockets = new List<Socket>();
        try
        {
            // Shared multicast listener on UDP 5353 so standard multicast replies reach us.
            try
            {
                var shared = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                shared.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                shared.Bind(new IPEndPoint(IPAddress.Any, 5353));
                foreach (var ifaceIp in GetLanIpv4Addresses())
                {
                    try
                    {
                        shared.SetSocketOption(
                            SocketOptionLevel.IP,
                            SocketOptionName.AddMembership,
                            new MulticastOption(MulticastGroup, ifaceIp));
                    }
                    catch (SocketException)
                    {
                    }
                }

                sockets.Add(shared);
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "Could not bind shared UDP 5353 socket; relying on per-interface QU queries");
            }

            // Per-interface ephemeral sockets sending QU (unicast-response) + QM queries out of the right NIC.
            foreach (var ifaceIp in GetLanIpv4Addresses())
            {
                try
                {
                    var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    s.Bind(new IPEndPoint(ifaceIp, 0));
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    sockets.Add(s);
                }
                catch (SocketException)
                {
                }
            }

            if (sockets.Count == 0)
            {
                return;
            }

            var receivers = sockets.Select(s => ReceiveLoopAsync(s, found, ct)).ToArray();
            var qmPacket = MdnsPacket.BuildPtrQuery(unicastResponse: false);
            var quPacket = MdnsPacket.BuildPtrQuery(unicastResponse: true);

            while (!ct.IsCancellationRequested)
            {
                foreach (var s in sockets)
                {
                    try
                    {
                        var payload = ((IPEndPoint)s.LocalEndPoint!).Port == 5353 ? qmPacket : quPacket;
                        _ = await s.SendToAsync(payload, SocketFlags.None, MulticastEndpoint, ct);
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                    {
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(450), ct);
            }

            await Task.WhenAll(receivers);
        }
        finally
        {
            foreach (var s in sockets)
            {
                try
                {
                    s.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    private static async Task ReceiveLoopAsync(
        Socket socket,
        ConcurrentDictionary<(MdnsAdbServiceKind, string, string, int), MdnsAdbService> found,
        CancellationToken ct)
    {
        var buffer = new byte[4096];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct);
                if (received.ReceivedBytes <= 0)
                {
                    continue;
                }

                var senderIp = (received.RemoteEndPoint as IPEndPoint)?.Address;
                foreach (var svc in MdnsPacket.ParseMessage(buffer.AsSpan(0, received.ReceivedBytes), senderIp))
                {
                    found[(svc.Kind, svc.InstanceName, svc.Host, svc.Port)] = svc;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                break;
            }
        }
    }

    private async Task ProbeAdbMdnsCliAsync(
        ConcurrentDictionary<(MdnsAdbServiceKind, string, string, int), MdnsAdbService> found,
        CancellationToken ct)
    {
        if (_serverHost is null)
        {
            return;
        }

        var adbPath = _serverHost.ResolveAdbPath();
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
        {
            return;
        }

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = "mdns services",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            if (!proc.Start())
            {
                return;
            }

            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            foreach (var svc in MdnsPacket.ParseAdbMdnsServicesOutput(output))
            {
                found[(svc.Kind, svc.InstanceName, svc.Host, svc.Port)] = svc;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "adb mdns services probe skipped");
        }
    }

    private static IReadOnlyList<IPAddress> GetLanIpv4Addresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var uni in nic.GetIPProperties().UnicastAddresses)
                {
                    if (uni.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(uni.Address))
                    {
                        list.Add(uni.Address);
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        return list;
    }
}

public enum WirelessQrPairStage
{
    WaitingForScan,
    Pairing,
    Connecting,
    Connected,
    PairedNeedsConnectPort,
    Failed,
}

public sealed record WirelessQrPairProgress(
    WirelessQrPairStage Stage,
    string? PairAddress = null,
    string? ConnectAddress = null,
    string? ErrorMessage = null);

/// <summary>
/// Coordinates Android 11+ QR Code Wireless Debugging pairing: watches LAN mDNS for the phone's
/// <c>&lt;serviceName&gt;._adb-tls-pairing._tcp</c> broadcast when the QR code is scanned, pairs automatically,
/// then resolves and connects to the phone's <c>_adb-tls-connect._tcp</c> port on the same IP.
/// </summary>
public sealed class WirelessQrPairCoordinator
{
    private readonly AdbService _adb;
    private readonly IAdbMdnsDiscovery _mdns;

    public WirelessQrPairCoordinator(AdbService adb, IAdbMdnsDiscovery mdns)
    {
        _adb = adb ?? throw new ArgumentNullException(nameof(adb));
        _mdns = mdns ?? throw new ArgumentNullException(nameof(mdns));
    }

    public async Task<WirelessQrPairProgress> PairAndConnectAsync(
        AdbWirelessQrCredentials credentials,
        IProgress<WirelessQrPairProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        progress?.Report(new WirelessQrPairProgress(WirelessQrPairStage.WaitingForScan));

        MdnsAdbService? pairingSvc = null;
        while (!ct.IsCancellationRequested && pairingSvc is null)
        {
            var services = await _mdns.DiscoverAsync(TimeSpan.FromMilliseconds(950), ct);
            pairingSvc = services.FirstOrDefault(s =>
                s.Kind == MdnsAdbServiceKind.TlsPairing
                && (string.Equals(s.InstanceName, credentials.ServiceName, StringComparison.OrdinalIgnoreCase)
                    || s.InstanceName.Contains(credentials.ServiceName, StringComparison.OrdinalIgnoreCase)));
        }

        ct.ThrowIfCancellationRequested();
        var pairAddr = pairingSvc!.Address;
        progress?.Report(new WirelessQrPairProgress(WirelessQrPairStage.Pairing, PairAddress: pairAddr));

        var pairResult = await _adb.PairAsync(pairAddr, credentials.Password, ct);
        if (!pairResult.Success)
        {
            var fail = new WirelessQrPairProgress(WirelessQrPairStage.Failed, PairAddress: pairAddr, ErrorMessage: pairResult.Message);
            progress?.Report(fail);
            return fail;
        }

        progress?.Report(new WirelessQrPairProgress(WirelessQrPairStage.Connecting, PairAddress: pairAddr));

        // After pairing succeeds, find the phone's _adb-tls-connect._tcp service on the same host IP (up to ~6s).
        for (var attempt = 0; attempt < 6 && !ct.IsCancellationRequested; attempt++)
        {
            var services = await _mdns.DiscoverAsync(TimeSpan.FromMilliseconds(900), ct);
            var connectSvc = services.FirstOrDefault(s =>
                s.Kind is MdnsAdbServiceKind.TlsConnect or MdnsAdbServiceKind.LegacyTcp
                && string.Equals(s.Host, pairingSvc.Host, StringComparison.OrdinalIgnoreCase));
            if (connectSvc is not null)
            {
                var connectResult = await _adb.ConnectAsync(connectSvc.Address, ct);
                if (connectResult.Success)
                {
                    var done = new WirelessQrPairProgress(WirelessQrPairStage.Connected, pairAddr, connectResult.Address);
                    progress?.Report(done);
                    return done;
                }
            }
        }

        var partial = new WirelessQrPairProgress(WirelessQrPairStage.PairedNeedsConnectPort, PairAddress: pairAddr);
        progress?.Report(partial);
        return partial;
    }
}
