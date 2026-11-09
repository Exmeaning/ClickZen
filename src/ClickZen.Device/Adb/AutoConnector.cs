using System.Net;
using ClickZen.Core.Devices;
using ClickZen.Core.Settings;
using ClickZen.Device.Adb.Wireless;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClickZen.Device.Adb;

/// <summary>Connects saved wireless devices that have <c>AutoConnect</c> enabled. Never throws (except on caller cancellation).</summary>
public sealed class AutoConnector
{
    private readonly AdbService _adb;
    private readonly SavedDeviceStore _store;
    private readonly Func<bool> _enabled;
    private readonly IAdbMdnsDiscovery? _mdns;
    private readonly ILogger _logger;

    public AutoConnector(AdbService adb, SavedDeviceStore store, SettingsService settings, IAdbMdnsDiscovery? mdns = null, ILogger<AutoConnector>? logger = null)
        : this(adb, store, () => settings.Current.Devices.AutoConnectSaved, mdns, logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
    }

    internal AutoConnector(AdbService adb, SavedDeviceStore store, Func<bool> enabled, IAdbMdnsDiscovery? mdns = null, ILogger? logger = null)
    {
        _adb = adb ?? throw new ArgumentNullException(nameof(adb));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _enabled = enabled;
        _mdns = mdns;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Tries every saved device with <c>AutoConnect = true</c> in parallel, unless
    /// <c>Devices.AutoConnectSaved</c> is off. When a saved LAN device refuses its previous port (Android 11+
    /// randomizes Wireless Debugging ports across Wi-Fi/reboot cycles), probes mDNS once to heal the port.
    /// </summary>
    public async Task<IReadOnlyList<AdbConnectResult>> ConnectSavedAsync(CancellationToken ct = default)
    {
        if (!_enabled())
        {
            _logger.LogDebug("Auto-connect of saved devices is disabled in settings");
            return [];
        }

        var targets = _store.GetAll().Where(d => d.AutoConnect).ToArray();
        if (targets.Length == 0)
        {
            return [];
        }

        _logger.LogInformation("Auto-connecting {Count} saved device(s)", targets.Length);
        var results = await Task.WhenAll(targets.Select(d => TryConnectOneAsync(d, ct)));

        // If any non-loopback LAN target failed and mDNS discovery is available, sweep mDNS once to heal shifted TLS ports.
        if (_mdns is not null)
        {
            var failedIndices = Enumerable.Range(0, targets.Length)
                .Where(i => !results[i].Success && IsLanHost(targets[i].Host))
                .ToArray();
            if (failedIndices.Length > 0)
            {
                IReadOnlyList<MdnsAdbService> discovered = [];
                try
                {
                    discovered = await _mdns.DiscoverAsync(TimeSpan.FromMilliseconds(1100), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "mDNS fallback sweep during auto-connect failed");
                }

                foreach (var idx in failedIndices)
                {
                    var d = targets[idx];
                    var match = discovered.FirstOrDefault(s =>
                        s.Kind is MdnsAdbServiceKind.TlsConnect or MdnsAdbServiceKind.LegacyTcp
                        && s.Port != d.Port
                        && string.Equals(s.Host, d.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        continue;
                    }

                    _logger.LogInformation("mDNS discovered updated port for {Name}: {OldAddress} -> {NewAddress}", d.Name, d.Address, match.Address);
                    var healed = await TryConnectAddressAsync(d.Name, match.Address, ct);
                    if (healed.Success)
                    {
                        _ = _store.UpdatePortForHost(d.Host, d.Port, match.Port);
                        results[idx] = healed;
                    }
                }
            }
        }

        return results;
    }

    private Task<AdbConnectResult> TryConnectOneAsync(SavedDevice d, CancellationToken ct) =>
        TryConnectAddressAsync(d.Name, d.Address, ct);

    private async Task<AdbConnectResult> TryConnectAddressAsync(string name, string address, CancellationToken ct)
    {
        try
        {
            var r = await _adb.ConnectAsync(address, ct);
            if (r.Success)
            {
                _logger.LogInformation("Auto-connect {Name} ({Address}): {Message}", name, r.Address, r.Message);
            }
            else
            {
                _logger.LogWarning("Auto-connect {Name} ({Address}) failed [{Kind}]: {Message}", name, r.Address, r.Kind, r.Message);
            }

            return r;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-connect {Name} ({Address}) failed", name, address);
            return new AdbConnectResult(false, Parsing.ConnectResultKind.Failed, ex.Message, address);
        }
    }

    private static bool IsLanHost(string host)
    {
        var trimmed = host.Trim().Trim('[', ']');
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IPAddress.TryParse(trimmed, out var ip) || !IPAddress.IsLoopback(ip);
    }
}
