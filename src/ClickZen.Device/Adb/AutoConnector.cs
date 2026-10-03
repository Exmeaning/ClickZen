using ClickZen.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClickZen.Device.Adb;

/// <summary>Connects saved wireless devices that have <c>AutoConnect</c> enabled. Never throws (except on caller cancellation).</summary>
public sealed class AutoConnector
{
    private readonly AdbService _adb;
    private readonly SavedDeviceStore _store;
    private readonly Func<bool> _enabled;
    private readonly ILogger _logger;

    public AutoConnector(AdbService adb, SavedDeviceStore store, SettingsService settings, ILogger<AutoConnector>? logger = null)
        : this(adb, store, () => settings.Current.Devices.AutoConnectSaved, logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
    }

    internal AutoConnector(AdbService adb, SavedDeviceStore store, Func<bool> enabled, ILogger? logger)
    {
        _adb = adb ?? throw new ArgumentNullException(nameof(adb));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _enabled = enabled;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Tries every saved device with <c>AutoConnect = true</c> in parallel, unless
    /// <c>Devices.AutoConnectSaved</c> is off. Returns one result per attempted device.
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
        var results = await Task.WhenAll(targets.Select(async d =>
        {
            try
            {
                var r = await _adb.ConnectAsync(d.Address, ct);
                if (r.Success)
                {
                    _logger.LogInformation("Auto-connect {Name} ({Address}): {Message}", d.Name, r.Address, r.Message);
                }
                else
                {
                    _logger.LogWarning("Auto-connect {Name} ({Address}) failed [{Kind}]: {Message}", d.Name, r.Address, r.Kind, r.Message);
                }

                return r;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-connect {Name} ({Address}) failed", d.Name, d.Address);
                return new AdbConnectResult(false, Parsing.ConnectResultKind.Failed, ex.Message, d.Address);
            }
        }));

        return results;
    }
}
