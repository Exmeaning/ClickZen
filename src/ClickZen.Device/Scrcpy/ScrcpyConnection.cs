using System.Buffers;
using System.Net;
using System.Net.Sockets;
using ClickZen.Device.Scrcpy.Protocol;

namespace ClickZen.Device.Scrcpy;

/// <summary>A media packet from the video socket.</summary>
public sealed record VideoPacket(bool IsConfig, bool IsKeyFrame, long Pts, byte[] Data);

/// <summary>Events raised while reading the video socket.</summary>
public interface IVideoStreamSink
{
    /// <summary>New capture session (start, rotation, resize): frames after this have the given size.</summary>
    void OnSession(int width, int height);

    void OnPacket(VideoPacket packet);
}

/// <summary>How the PC reaches the device's scrcpy sockets.</summary>
public enum TunnelMode
{
    /// <summary>adb reverse: the device connects to a port the PC listens on (scrcpy default).</summary>
    Reverse,
    /// <summary>adb forward: the PC connects to a local port forwarded to the device's abstract socket.</summary>
    Forward,
}

/// <summary>
/// The socket side of a scrcpy 4.x session (no adb here). Given a tunnel that is already set up,
/// it accepts/opens the video and control sockets in protocol order, reads the device name,
/// pumps the video stream to a sink and serialises control messages.
/// </summary>
public sealed class ScrcpyConnection : IAsyncDisposable
{
    private readonly Socket? _video;
    private readonly Socket _control;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _writeBuffer = new byte[ControlMessageWriter.MaxMessageSize];
    private int _disposed;

    private ScrcpyConnection(Socket? video, Socket control, string deviceName, VideoCodecId? codec)
    {
        _video = video;
        _control = control;
        DeviceName = deviceName;
        Codec = codec;
    }

    public string DeviceName { get; }

    public VideoCodecId? Codec { get; }

    public bool HasVideo => _video is not null;

    // ------------------------------------------------------------------ establishing

    /// <summary>Reverse tunnel: listen on <paramref name="listener"/> and accept the device's sockets.</summary>
    public static async Task<ScrcpyConnection> AcceptAsync(TcpListener listener, bool video, CancellationToken ct)
    {
        Socket? videoSocket = null;
        Socket? controlSocket = null;
        try
        {
            if (video)
            {
                videoSocket = await listener.AcceptSocketAsync(ct);
                Configure(videoSocket);
            }

            controlSocket = await listener.AcceptSocketAsync(ct);
            Configure(controlSocket);
            return await HandshakeAsync(videoSocket, controlSocket, expectDummyByte: false, ct);
        }
        catch
        {
            videoSocket?.Dispose();
            controlSocket?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Forward tunnel: connect to <paramref name="endpoint"/> once per socket. The first socket
    /// receives a dummy byte which proves the server is really there (an adb forward accepts
    /// connections even when nothing listens on the device).
    /// </summary>
    public static async Task<ScrcpyConnection> ConnectAsync(IPEndPoint endpoint, bool video, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        // The server may not be listening yet right after app_process starts: retry the first socket.
        Socket first;
        var attempt = 0;
        while (true)
        {
            first = await OpenAsync(endpoint, timeoutCts.Token);
            try
            {
                var dummy = new byte[1];
                await ReceiveExactAsync(first, dummy, timeoutCts.Token);
                break;
            }
            catch (Exception ex) when ((ex is IOException || ex is SocketException) && !timeoutCts.IsCancellationRequested)
            {
                first.Dispose();
                attempt++;
                await Task.Delay(Math.Min(100 * attempt, 500), timeoutCts.Token);
            }
        }

        Socket? second = null;
        try
        {
            if (video)
            {
                second = await OpenAsync(endpoint, timeoutCts.Token);
                return await HandshakeAsync(first, second, expectDummyByte: false, timeoutCts.Token);
            }

            return await HandshakeAsync(null, first, expectDummyByte: false, timeoutCts.Token);
        }
        catch
        {
            first.Dispose();
            second?.Dispose();
            throw;
        }
    }

    private static async Task<Socket> OpenAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var s = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await s.ConnectAsync(endpoint, ct);
            Configure(s);
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    private static void Configure(Socket s) => s.NoDelay = true;

    private static async Task<ScrcpyConnection> HandshakeAsync(Socket? video, Socket control, bool expectDummyByte, CancellationToken ct)
    {
        var first = video ?? control;
        if (expectDummyByte)
        {
            await ReceiveExactAsync(first, new byte[1], ct);
        }

        var name = new byte[StreamProtocol.DeviceNameFieldLength];
        await ReceiveExactAsync(first, name, ct);

        VideoCodecId? codec = null;
        if (video is not null)
        {
            var id = new byte[4];
            await ReceiveExactAsync(video, id, ct);
            codec = StreamProtocol.ParseCodecId(id);
            if (codec is not (VideoCodecId.H264 or VideoCodecId.H265))
            {
                throw new InvalidDataException($"Unexpected video codec 0x{(uint)codec:x8}.");
            }
        }

        return new ScrcpyConnection(video, control, StreamProtocol.ParseDeviceName(name), codec);
    }

    // ------------------------------------------------------------------ video

    /// <summary>Reads the video socket until it closes or <paramref name="ct"/> fires.</summary>
    public async Task ReadVideoAsync(IVideoStreamSink sink, CancellationToken ct)
    {
        if (_video is null)
        {
            throw new InvalidOperationException("This connection has no video socket.");
        }

        var header = new byte[StreamProtocol.HeaderSize];
        while (!ct.IsCancellationRequested)
        {
            await ReceiveExactAsync(_video, header, ct);
            var h = StreamProtocol.ParseHeader(header);
            if (h.IsSession)
            {
                sink.OnSession(h.Width, h.Height);
                continue;
            }

            var data = new byte[h.PacketSize];
            await ReceiveExactAsync(_video, data, ct);
            sink.OnPacket(new VideoPacket(h.IsConfig, h.IsKeyFrame, h.Pts, data));
        }
    }

    // ------------------------------------------------------------------ control

    /// <summary>Serialises a message with <paramref name="write"/> and sends it. Calls are serialised.</summary>
    public async Task SendAsync(Func<byte[], int> write, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _writeLock.WaitAsync(ct);
        try
        {
            var n = write(_writeBuffer);
            var sent = 0;
            while (sent < n)
            {
                sent += await _control.SendAsync(_writeBuffer.AsMemory(sent, n - sent), SocketFlags.None, ct);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Drains (and ignores) device→client messages such as clipboard notifications so the socket buffer never fills.</summary>
    public async Task DrainControlAsync(CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await _control.ReceiveAsync(buf, SocketFlags.None, ct);
                if (n == 0)
                {
                    return;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    // ------------------------------------------------------------------ helpers

    internal static async Task ReceiveExactAsync(Socket s, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReceiveAsync(buffer[read..], SocketFlags.None, ct);
            if (n == 0)
            {
                throw new IOException("scrcpy socket closed by the device.");
            }

            read += n;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        foreach (var s in new[] { _video, _control })
        {
            if (s is null)
            {
                continue;
            }

            try { s.Shutdown(SocketShutdown.Both); } catch (SocketException) { } catch (ObjectDisposedException) { }
            s.Dispose();
        }

        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
