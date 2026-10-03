using AdvancedSharpAdbClient;
using ClickZen.Core.Devices;
using ClickZen.Device.Adb.Parsing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClickZen.Device.Adb;

/// <summary>
/// Keeps an up-to-date device list using adb's <c>host:track-devices</c> push notifications.
/// <para>
/// Implemented directly on AdvancedSharpAdbClient's <see cref="AdbSocket"/> rather than its <c>DeviceMonitor</c>:
/// the latter restarts the server through the static <c>AdbServer.Instance</c> (ignoring our adb path / reuse
/// policy) and spins on an empty string when the server closes the connection cleanly.
/// </para>
/// <para>
/// <see cref="DevicesChanged"/> is raised on a background thread, serialized (never concurrently). UI code must
/// marshal to its dispatcher itself.
/// </para>
/// </summary>
public sealed class DeviceWatcher : IAsyncDisposable
{
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
    ];

    private readonly AdbService _adb;
    private readonly AdbServerHost? _host;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _eventGate = new(1, 1);
    private IReadOnlyList<DeviceInfo> _devices = [];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private TaskCompletionSource? _firstList;
    private bool _disposed;

    public DeviceWatcher(AdbService adb, AdbServerHost? host = null, ILogger<DeviceWatcher>? logger = null)
    {
        _adb = adb ?? throw new ArgumentNullException(nameof(adb));
        _host = host;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Snapshot of the current devices (immutable; a new list is published on every change).</summary>
    public IReadOnlyList<DeviceInfo> Devices => Volatile.Read(ref _devices);

    /// <summary>True while connected to the adb server's tracking stream.</summary>
    public bool IsTracking { get; private set; }

    /// <summary>Raised on a background thread whenever the list or a device's properties change.</summary>
    public event EventHandler<DeviceListChangedEventArgs>? DevicesChanged;

    /// <summary>Raised (background thread) when tracking is lost or restored; argument is <see cref="IsTracking"/>.</summary>
    public event EventHandler<bool>? TrackingChanged;

    /// <summary>
    /// Starts watching. Completes once the first device list has been received, or after the first connection
    /// attempt failed (the list is then empty and the watcher keeps retrying with back-off in the background).
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        TaskCompletionSource first;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loop is not null)
            {
                first = _firstList ?? new TaskCompletionSource();
            }
            else
            {
                _cts = new CancellationTokenSource();
                first = _firstList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var token = _cts.Token;
                _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
            }
        }

        await first.Task.WaitAsync(ct);
    }

    /// <summary>Re-reads properties (bypassing the display cache) for every online device and publishes updates.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        _adb.InvalidateDisplayCache();
        await Task.WhenAll(Devices.Where(d => d.State == DeviceAdbState.Online).Select(d => EnrichAndPublishAsync(d, ct)));
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            loop = _loop;
            cts = _cts;
        }

        if (cts is not null)
        {
            await cts.CancelAsync();
        }

        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts?.Dispose();
        _eventGate.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TrackOnceAsync(() => failures = 0, ct);
                // Server closed the stream cleanly (adb kill-server).
                _logger.LogInformation("adb track-devices stream ended");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // Socket torn down by our cancellation registration.
                break;
            }
            catch (Exception ex)
            {
                var kind = AdbErrorMapper.Classify(ex);
                if (failures == 0)
                {
                    _logger.LogWarning(ex, "Device tracking interrupted ({Kind})", kind);
                }
                else
                {
                    _logger.LogDebug(ex, "Device tracking reconnect failed ({Kind})", kind);
                }
            }

            // Never leave StartAsync hanging when the server is unreachable: it reports "no devices" and we keep retrying.
            _firstList?.TrySetResult();
            SetTracking(false);
            if (ct.IsCancellationRequested)
            {
                break;
            }

            // Devices are unknown while disconnected from the server.
            await PublishSnapshotAsync([], ct);

            var delay = Backoff[Math.Min(failures, Backoff.Length - 1)];
            failures++;
            try
            {
                await Task.Delay(delay, ct);
                if (_host is not null && _adb.AutoStartServer && failures >= 2)
                {
                    await _host.EnsureStartedAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "adb server restart attempt failed");
            }
        }

        SetTracking(false);
        _firstList?.TrySetCanceled(ct);
    }

    private async Task TrackOnceAsync(Action onConnected, CancellationToken ct)
    {
        using var socket = new AdbSocket(_adb.EndPoint, null);
        await using var reg = ct.Register(() => socket.Dispose());
        await socket.SendAdbRequestAsync("host:track-devices", ct);
        _ = await socket.ReadAdbResponseAsync(ct);
        SetTracking(true);
        onConnected();

        var header = new byte[4];
        while (!ct.IsCancellationRequested)
        {
            // Each notification is a 4-hex-digit length followed by the payload. A 0-byte read means EOF.
            if (await socket.ReadAsync(header, ct) < header.Length)
            {
                return;
            }

            if (!int.TryParse(System.Text.Encoding.ASCII.GetString(header), System.Globalization.NumberStyles.HexNumber, null, out var len))
            {
                throw new IOException("Malformed track-devices frame header.");
            }

            var payload = "";
            if (len > 0)
            {
                var buf = new byte[len];
                if (await socket.ReadAsync(buf, ct) < len)
                {
                    return;
                }

                payload = System.Text.Encoding.UTF8.GetString(buf);
            }

            var snapshot = ParseSnapshot(payload);
            await PublishSnapshotAsync(snapshot, ct);
            _firstList?.TrySetResult();
        }
    }

    internal static IReadOnlyList<DeviceInfo> ParseSnapshot(string payload) =>
        DeviceListParser.Parse(payload)
            .Select(e => new DeviceInfo
            {
                Serial = e.Serial,
                State = e.State,
                Kind = DeviceClassifier.Classify(e.Serial),
                Model = e.Model,
            })
            .ToArray();

    private async Task PublishSnapshotAsync(IReadOnlyList<DeviceInfo> snapshot, CancellationToken ct)
    {
        DeviceListChangedEventArgs change;
        await _eventGate.WaitAsync(ct);
        try
        {
            (var next, change) = DeviceListDiff.Apply(Devices, snapshot);
            if (change.IsEmpty)
            {
                return;
            }

            Volatile.Write(ref _devices, next.ToArray());
            foreach (var removed in change.Removed)
            {
                _adb.InvalidateCache(removed.Serial);
            }

            Raise(change);
        }
        finally
        {
            _eventGate.Release();
        }

        foreach (var d in change.Added.Concat(change.StateChanged.Select(c => c.Device)))
        {
            if (d.State == DeviceAdbState.Online)
            {
                _ = EnrichAndPublishAsync(d, ct);
            }
        }
    }

    private async Task EnrichAndPublishAsync(DeviceInfo device, CancellationToken ct)
    {
        try
        {
            var enriched = await _adb.EnrichAsync(device, ct);
            await _eventGate.WaitAsync(ct);
            try
            {
                var (next, updated) = DeviceListDiff.ApplyEnriched(Devices, enriched);
                if (updated is null)
                {
                    return;
                }

                Volatile.Write(ref _devices, next.ToArray());
                Raise(new DeviceListChangedEventArgs(next.ToArray(), [], [], [], [updated]));
            }
            finally
            {
                _eventGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enrich {Serial}", device.Serial);
        }
    }

    private void Raise(DeviceListChangedEventArgs change)
    {
        try
        {
            DevicesChanged?.Invoke(this, change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DevicesChanged handler threw");
        }
    }

    private void SetTracking(bool value)
    {
        if (IsTracking == value)
        {
            return;
        }

        IsTracking = value;
        try
        {
            TrackingChanged?.Invoke(this, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrackingChanged handler threw");
        }
    }
}
