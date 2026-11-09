using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ClickZen.Core;
using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Device.Adb;
using Microsoft.Extensions.Logging;

namespace ClickZen.Device.Input;

/// <summary>
/// High-speed root touch/key injector that keeps a single persistent <c>uid=0</c> shell open over an ADB socket
/// (instead of spawning <c>su -c</c> per gesture) and paces binary <c>input_event</c> writes from the host monotonic
/// clock. Cuts per-tap overhead from ~80–150 ms down to ~1–2 ms and bypasses ROM <c>INJECT_EVENTS</c> blocks.
/// </summary>
public sealed class PersistentRootInjector : ITouchInjector, IAsyncDisposable
{
    private const string ReadyMarker = "__CZ_ROOT_OK__";

    private readonly Func<CancellationToken, Task<Stream>> _openRootStream;
    private readonly RootSendeventInjector _fallback;
    private readonly InputDeviceInfo _touch;
    private readonly bool _rawEvents;
    private readonly Func<int>? _displayId;
    private readonly IClock _clock;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Stream? _stream;
    private bool _disposed;

    public PersistentRootInjector(
        Func<CancellationToken, Task<Stream>> openRootStream,
        RootSendeventInjector fallback,
        InputDeviceInfo touch,
        bool rawEvents,
        Func<int>? displayId = null,
        IClock? clock = null,
        ILogger? logger = null)
    {
        _openRootStream = openRootStream ?? throw new ArgumentNullException(nameof(openRootStream));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _touch = touch ?? throw new ArgumentNullException(nameof(touch));
        _rawEvents = rawEvents;
        _displayId = displayId;
        _clock = clock ?? SystemClock.Instance;
        _logger = logger;
    }

    /// <summary>Creates a <see cref="PersistentRootInjector"/> backed by <paramref name="adbEndpoint"/> (<c>127.0.0.1:5037</c>).</summary>
    public static PersistentRootInjector ForAdbSocket(
        EndPoint adbEndpoint,
        string serial,
        RootSendeventInjector fallback,
        InputDeviceInfo touch,
        bool rawEvents,
        Func<int>? displayId = null,
        IClock? clock = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(adbEndpoint);
        ArgumentException.ThrowIfNullOrEmpty(serial);
        return new PersistentRootInjector(
            ct => OpenAdbRootShellStreamAsync(adbEndpoint, serial, ct),
            fallback,
            touch,
            rawEvents,
            displayId,
            clock,
            logger);
    }

    public string Name => _rawEvents ? "root (persistent)" : "root (persistent sendevent)";

    public bool SupportsMultiTouch => _fallback.SupportsMultiTouch;

    public Task TapAsync(PointI point, int durationMs, CancellationToken ct) =>
        PlayFingersAsync([[new TimedPoint(point.X, point.Y, 0), new TimedPoint(point.X, point.Y, Math.Max(1, durationMs))]], ct);

    public Task StrokeAsync(IReadOnlyList<TimedPoint> path, CancellationToken ct) =>
        path.Count == 0 ? Task.CompletedTask : PlayFingersAsync([path], ct);

    public Task MultiStrokeAsync(IReadOnlyList<IReadOnlyList<TimedPoint>> fingers, CancellationToken ct)
    {
        var usable = fingers.Where(f => f.Count > 0).Take(SupportsMultiTouch ? _touch.MaxSlot + 1 : 1).ToArray();
        return usable.Length == 0 ? Task.CompletedTask : PlayFingersAsync(usable, ct);
    }

    public async Task KeyAsync(int keyCode, CancellationToken ct)
    {
        var prefix = DisplayPrefix();
        var cmd = $"{prefix}keyevent {keyCode.ToString(CultureInfo.InvariantCulture)}\n";
        if (!await TryWriteCommandAsync(cmd, ct))
        {
            await _fallback.KeyAsync(keyCode, ct);
        }
    }

    public async Task TextAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var prefix = DisplayPrefix();
        var cmd = $"{prefix}text {AdbInputInjector.EscapeInputText(text)}\n";
        if (!await TryWriteCommandAsync(cmd, ct))
        {
            await _fallback.TextAsync(text, ct);
        }
    }

    private string DisplayPrefix()
    {
        var d = _displayId?.Invoke() ?? 0;
        return d > 0 ? $"input -d {d.ToString(CultureInfo.InvariantCulture)} " : "input ";
    }

    private async Task PlayFingersAsync(IReadOnlyList<IReadOnlyList<TimedPoint>> fingers, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var stream = await EnsureStreamCoreAsync(ct);
            if (stream is null)
            {
                await _fallback.MultiStrokeAsync(fingers, ct);
                return;
            }

            try
            {
                await StreamTimelineAsync(stream, fingers, ct);
            }
            catch (OperationCanceledException)
            {
                // Ensure no touch slot remains held down in the kernel input device when cancelled mid-gesture.
                await ReleaseAllSlotsQuietlyAsync(stream, fingers.Count);
                throw;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                _logger?.LogDebug(ex, "Persistent root shell dropped during gesture; falling back and resetting stream");
                await CloseStreamCoreAsync();
                await _fallback.MultiStrokeAsync(fingers, ct);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task StreamTimelineAsync(Stream stream, IReadOnlyList<IReadOnlyList<TimedPoint>> fingers, CancellationToken ct)
    {
        var sb = new StringBuilder(256);
        foreach (var item in _fallback.Timeline(fingers))
        {
            if (item.SleepMs > 0)
            {
                await _clock.DelayAsync(TimeSpan.FromMilliseconds(item.SleepMs), ct);
                continue;
            }

            sb.Clear();
            FormatBatchCommand(sb, item.Events);
            var bytes = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }
    }

    internal void FormatBatchCommand(StringBuilder sb, IReadOnlyList<(int Type, int Code, int Value)> events)
    {
        if (_rawEvents)
        {
            sb.Append("print -n \"");
            foreach (var (type, code, value) in events)
            {
                sb.Append("$z");
                AppendLe(sb, type, 2);
                AppendLe(sb, code, 2);
                AppendLe(sb, value, 4);
            }

            sb.Append("\">&4\n");
            return;
        }

        for (var i = 0; i < events.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(';');
            }

            var (type, code, value) = events[i];
            sb.Append(CultureInfo.InvariantCulture, $"sendevent {_touch.Path} {type} {code} {value}");
        }

        sb.Append('\n');
    }

    private async Task ReleaseAllSlotsQuietlyAsync(Stream stream, int fingerCount)
    {
        try
        {
            var cleanup = new List<(int Type, int Code, int Value)>();
            var slots = Math.Max(1, fingerCount);
            for (var s = 0; s < slots; s++)
            {
                if (_touch.HasSlots)
                {
                    cleanup.Add((3, InputDeviceInfo.AbsMtSlot, s));
                }

                cleanup.Add((3, InputDeviceInfo.AbsMtTrackingId, -1));
            }

            if (_touch.Keys.Contains(InputDeviceInfo.BtnTouch))
            {
                cleanup.Add((1, InputDeviceInfo.BtnTouch, 0));
            }

            cleanup.Add((0, 0, 0)); // SYN_REPORT
            var sb = new StringBuilder(128);
            FormatBatchCommand(sb, cleanup);
            var bytes = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(bytes, CancellationToken.None);
            await stream.FlushAsync(CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    private async Task<bool> TryWriteCommandAsync(string command, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var stream = await EnsureStreamCoreAsync(ct);
            if (stream is null)
            {
                return false;
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(command);
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                return true;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                await CloseStreamCoreAsync();
                return false;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Stream?> EnsureStreamCoreAsync(CancellationToken ct)
    {
        if (_disposed)
        {
            return null;
        }

        if (_stream is not null)
        {
            return _stream;
        }

        try
        {
            var stream = await _openRootStream(ct);
            var init = _rawEvents
                ? $"z='\\0\\0\\0\\0\\0\\0\\0\\0\\0\\0\\0\\0\\0\\0\\0\\0';exec 4>{_touch.Path} && echo {ReadyMarker}$(id -u)\n"
                : $"echo {ReadyMarker}$(id -u)\n";
            var initBytes = Encoding.ASCII.GetBytes(init);
            await stream.WriteAsync(initBytes, ct);
            await stream.FlushAsync(ct);

            using var verifyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            verifyCts.CancelAfter(TimeSpan.FromSeconds(4));
            if (!await WaitForReadyBannerAsync(stream, verifyCts.Token))
            {
                await stream.DisposeAsync();
                return null;
            }

            // Drain any subsequent stdout asynchronously so the kernel pipe never fills up.
            _ = DrainStdoutAsync(stream);
            _stream = stream;
            return stream;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger?.LogDebug(ex, "Could not open persistent root shell; using per-command fallback");
            return null;
        }
    }

    private static async Task<bool> WaitForReadyBannerAsync(Stream stream, CancellationToken ct)
    {
        var buf = new byte[256];
        var acc = new StringBuilder();
        while (acc.Length < 4096)
        {
            var n = await stream.ReadAsync(buf, ct);
            if (n <= 0)
            {
                return false;
            }

            acc.Append(Encoding.UTF8.GetString(buf.AsSpan(0, n)));
            var s = acc.ToString();
            var idx = s.IndexOf(ReadyMarker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var after = s[(idx + ReadyMarker.Length)..];
                if (after.StartsWith("0\n", StringComparison.Ordinal) || after.StartsWith("0\r", StringComparison.Ordinal))
                {
                    return true;
                }

                if (after.Contains('\n', StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (s.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
                || s.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return false;
    }

    private static async Task DrainStdoutAsync(Stream stream)
    {
        var discard = new byte[512];
        try
        {
            while (await stream.ReadAsync(discard, CancellationToken.None) > 0)
            {
            }
        }
        catch (Exception)
        {
        }
    }

    private async Task CloseStreamCoreAsync()
    {
        var s = _stream;
        _stream = null;
        if (s is not null)
        {
            try
            {
                await s.DisposeAsync();
            }
            catch (Exception)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lock.WaitAsync();
        try
        {
            if (_stream is { } s)
            {
                try
                {
                    var exit = Encoding.ASCII.GetBytes("exec 4>&- 2>/dev/null; exit\n");
                    await s.WriteAsync(exit, CancellationToken.None);
                    await s.FlushAsync(CancellationToken.None);
                }
                catch (Exception)
                {
                }
            }

            await CloseStreamCoreAsync();
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }

    private static async Task<Stream> OpenAdbRootShellStreamAsync(EndPoint adbEndpoint, string serial, CancellationToken ct)
    {
        // Try standard Magisk/KernelSU/SuperSU `su` first, then AOSP `su 0 sh` on userdebug images.
        foreach (var shellCmd in new[] { "shell:su", "shell:su 0 sh" })
        {
            Socket? tcp = null;
            try
            {
                tcp = new Socket(adbEndpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await tcp.ConnectAsync(adbEndpoint, ct);
                var net = new NetworkStream(tcp, ownsSocket: true);
                tcp = null;

                await SendAdbFrameAsync(net, $"host:transport:{serial}", ct);
                await ExpectOkayAsync(net, ct);
                await SendAdbFrameAsync(net, shellCmd, ct);
                await ExpectOkayAsync(net, ct);
                return net;
            }
            catch (Exception)
            {
                tcp?.Dispose();
            }
        }

        throw new IOException($"Could not open persistent root ADB shell for {serial}.");
    }

    private static async Task SendAdbFrameAsync(Stream stream, string payload, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var header = Encoding.ASCII.GetBytes(body.Length.ToString("x4", CultureInfo.InvariantCulture));
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task ExpectOkayAsync(Stream stream, CancellationToken ct)
    {
        var status = new byte[4];
        await stream.ReadExactlyAsync(status, ct);
        if (status[0] != (byte)'O' || status[1] != (byte)'K' || status[2] != (byte)'A' || status[3] != (byte)'Y')
        {
            throw new IOException("ADB server rejected shell request.");
        }
    }

    private static void AppendLe(StringBuilder sb, int value, int bytes)
    {
        var u = unchecked((uint)value);
        for (var i = 0; i < bytes; i++)
        {
            var b = (u >> (8 * i)) & 0xFF;
            sb.Append(b == 0 ? "\\0" : string.Create(CultureInfo.InvariantCulture, $"\\x{b:x2}"));
        }
    }
}
