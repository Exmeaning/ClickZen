using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ClickZen.Core;
using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Device.Scrcpy;
using ClickZen.Device.Scrcpy.Protocol;

namespace ClickZen.Device.Tests.Scrcpy;

/// <summary>
/// End-to-end tests of the socket protocol against an in-process fake scrcpy-server
/// (behaving like the device side of an adb forward tunnel).
/// </summary>
public sealed class ScrcpyConnectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Fake device: accepts two sockets (video, control) and writes the scrcpy preamble.</summary>
    private sealed class FakeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public FakeServer() => _listener.Start();

        public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

        public Socket? Video { get; private set; }

        public Socket? Control { get; private set; }

        public async Task AcceptAsync(bool video, string deviceName, CancellationToken ct)
        {
            var first = await _listener.AcceptSocketAsync(ct);
            await first.SendAsync(new byte[] { 0 }, SocketFlags.None, ct); // dummy byte (forward tunnel)
            var name = new byte[64];
            System.Text.Encoding.UTF8.GetBytes(deviceName).CopyTo(name, 0);
            await first.SendAsync(name, SocketFlags.None, ct);
            if (video)
            {
                Video = first;
                await Video.SendAsync("h264"u8.ToArray(), SocketFlags.None, ct);
                Control = await _listener.AcceptSocketAsync(ct);
            }
            else
            {
                Control = first;
            }
        }

        public async Task SendSessionAsync(int w, int h, CancellationToken ct)
        {
            var buf = new byte[12];
            StreamProtocol.WriteSessionHeader(buf, w, h);
            await Video!.SendAsync(buf, SocketFlags.None, ct);
        }

        public async Task SendPacketAsync(byte[] data, bool config, bool key, long pts, CancellationToken ct)
        {
            var buf = new byte[12 + data.Length];
            StreamProtocol.WriteMediaHeader(buf, pts, config, key, data.Length);
            data.CopyTo(buf, 12);
            // Split in odd chunks to exercise partial reads.
            for (var i = 0; i < buf.Length; i += 7)
            {
                await Video!.SendAsync(buf.AsMemory(i, Math.Min(7, buf.Length - i)), SocketFlags.None, ct);
            }
        }

        /// <summary>Reads control messages until <paramref name="count"/> touch events were received.</summary>
        public async Task<List<(MotionAction Action, long Pointer, int X, int Y, int W, int H)>> ReadTouchesAsync(int count, CancellationToken ct)
        {
            var result = new List<(MotionAction, long, int, int, int, int)>();
            var msg = new byte[32];
            while (result.Count < count)
            {
                await ScrcpyConnection.ReceiveExactAsync(Control!, msg.AsMemory(0, 1), ct);
                Assert.Equal((byte)ControlMessageType.InjectTouchEvent, msg[0]);
                await ScrcpyConnection.ReceiveExactAsync(Control!, msg.AsMemory(1, 31), ct);
                result.Add(((MotionAction)msg[1], BinaryPrimitives.ReadInt64BigEndian(msg.AsSpan(2)),
                    BinaryPrimitives.ReadInt32BigEndian(msg.AsSpan(10)), BinaryPrimitives.ReadInt32BigEndian(msg.AsSpan(14)),
                    BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(18)), BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(20))));
            }

            return result;
        }

        public async ValueTask DisposeAsync()
        {
            Video?.Dispose();
            Control?.Dispose();
            _listener.Stop();
            await Task.CompletedTask;
        }
    }

    private sealed class CollectingSink : IVideoStreamSink
    {
        public List<(int W, int H)> Sessions { get; } = [];
        public List<VideoPacket> Packets { get; } = [];
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Expect { get; init; }

        public void OnSession(int width, int height) => Sessions.Add((width, height));

        public void OnPacket(VideoPacket packet)
        {
            Packets.Add(packet);
            if (Packets.Count >= Expect)
            {
                Done.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task Forward_handshake_reads_name_codec_and_video_packets()
    {
        await using var server = new FakeServer();
        var accept = server.AcceptAsync(video: true, "Pixel 9", Ct);
        await using var conn = await ScrcpyConnection.ConnectAsync(server.EndPoint, video: true, TimeSpan.FromSeconds(5), Ct);
        await accept;

        Assert.Equal("Pixel 9", conn.DeviceName);
        Assert.Equal(VideoCodecId.H264, conn.Codec);

        var sink = new CollectingSink { Expect = 2 };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var reader = conn.ReadVideoAsync(sink, cts.Token);

        await server.SendSessionAsync(720, 1600, Ct);
        await server.SendPacketAsync([1, 2, 3], config: true, key: false, 0, Ct);
        await server.SendPacketAsync(Enumerable.Range(0, 100).Select(i => (byte)i).ToArray(), config: false, key: true, 123456, Ct);
        await sink.Done.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader);

        Assert.Equal([(720, 1600)], sink.Sessions);
        Assert.True(sink.Packets[0].IsConfig);
        Assert.Equal(new byte[] { 1, 2, 3 }, sink.Packets[0].Data);
        Assert.True(sink.Packets[1].IsKeyFrame);
        Assert.Equal(123456, sink.Packets[1].Pts);
        Assert.Equal(100, sink.Packets[1].Data.Length);
    }

    [Fact]
    public async Task Control_only_connection_has_no_video()
    {
        await using var server = new FakeServer();
        var accept = server.AcceptAsync(video: false, "emu", Ct);
        await using var conn = await ScrcpyConnection.ConnectAsync(server.EndPoint, video: false, TimeSpan.FromSeconds(5), Ct);
        await accept;

        Assert.False(conn.HasVideo);
        Assert.Equal("emu", conn.DeviceName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => conn.ReadVideoAsync(new CollectingSink(), Ct));
    }

    [Fact]
    public async Task Device_closing_the_video_socket_surfaces_as_IOException()
    {
        await using var server = new FakeServer();
        var accept = server.AcceptAsync(video: true, "x", Ct);
        await using var conn = await ScrcpyConnection.ConnectAsync(server.EndPoint, video: true, TimeSpan.FromSeconds(5), Ct);
        await accept;

        var reader = conn.ReadVideoAsync(new CollectingSink(), Ct);
        server.Video!.Shutdown(SocketShutdown.Both);
        server.Video.Dispose();
        await Assert.ThrowsAsync<IOException>(() => reader);
    }

    [Fact]
    public async Task Tap_through_injector_sends_down_and_up_in_video_coordinates()
    {
        await using var server = new FakeServer();
        var accept = server.AcceptAsync(video: false, "x", Ct);
        await using var conn = await ScrcpyConnection.ConnectAsync(server.EndPoint, video: false, TimeSpan.FromSeconds(5), Ct);
        await accept;

        // Video is half the device resolution: device (500, 1000) → video (250, 500).
        var injector = new ScrcpyTouchInjector(conn.SendAsync, () => new SizeI(540, 1200), () => new SizeI(1080, 2400));
        var read = server.ReadTouchesAsync(2, Ct);
        await injector.TapAsync(new PointI(500, 1000), 20, Ct);
        var touches = await read.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(MotionAction.Down, touches[0].Action);
        Assert.Equal(MotionAction.Up, touches[1].Action);
        Assert.Equal((250, 500, 540, 1200), (touches[0].X, touches[0].Y, touches[0].W, touches[0].H));
    }

    [Fact]
    public async Task Control_only_mode_uses_device_size_as_screen_size()
    {
        await using var server = new FakeServer();
        var accept = server.AcceptAsync(video: false, "x", Ct);
        await using var conn = await ScrcpyConnection.ConnectAsync(server.EndPoint, video: false, TimeSpan.FromSeconds(5), Ct);
        await accept;

        var injector = new ScrcpyTouchInjector(conn.SendAsync, () => default, () => new SizeI(1080, 2400));
        var read = server.ReadTouchesAsync(2, Ct);
        await injector.TapAsync(new PointI(10, 20), 1, Ct);
        var t = await read.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal((10, 20, 1080, 2400), (t[0].X, t[0].Y, t[0].W, t[0].H));
    }
}

public sealed class ScrcpyTouchInjectorTimelineTests
{
    private static TimedPoint P(double x, double y, int t) => new(x, y, t);

    [Fact]
    public void Single_stroke_is_down_moves_up_with_resampled_moves()
    {
        var steps = ScrcpyTouchInjector.BuildTimeline([[P(0, 0, 0), P(100, 0, 40)]]);
        Assert.Equal(MotionAction.Down, steps[0].Action);
        Assert.Equal(MotionAction.Up, steps[^1].Action);
        Assert.All(steps.Skip(1).SkipLast(1), s => Assert.Equal(MotionAction.Move, s.Action));
        Assert.True(steps.Count >= 1 + 40 / ScrcpyTouchInjector.MoveIntervalMs + 1);
        Assert.Equal(40, steps[^1].AtMs);
    }

    [Fact]
    public void Two_fingers_interleave_by_time_with_distinct_pointer_ids()
    {
        var steps = ScrcpyTouchInjector.BuildTimeline(
        [
            [P(400, 1000, 0), P(300, 1000, 100)],
            [P(600, 1000, 20), P(700, 1000, 100)],
        ]);

        Assert.Equal((MotionAction.Down, 0), (steps[0].Action, steps[0].Pointer));
        var secondDown = steps.FindIndex(s => s.Action == MotionAction.Down && s.Pointer == 1);
        Assert.Equal(20, steps[secondDown].AtMs);
        Assert.True(steps.Zip(steps.Skip(1)).All(p => p.First.AtMs <= p.Second.AtMs));

        // Each finger: exactly one DOWN and one UP, DOWN first.
        foreach (var id in new[] { 0, 1 })
        {
            var mine = steps.Where(s => s.Pointer == id).ToList();
            Assert.Equal(MotionAction.Down, mine[0].Action);
            Assert.Equal(MotionAction.Up, mine[^1].Action);
            Assert.Single(mine, s => s.Action == MotionAction.Down);
        }
    }

    [Fact]
    public async Task Cancellation_mid_stroke_still_lifts_the_finger()
    {
        var sent = new List<byte[]>();
        using var cts = new CancellationTokenSource();
        var clock = new FakeClock();
        var injector = new ScrcpyTouchInjector((write, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var buf = new byte[64];
            var n = write(buf);
            sent.Add(buf[..n]);
            if (sent.Count == 3)
            {
                cts.Cancel();
            }

            return Task.CompletedTask;
        }, () => new SizeI(100, 100), () => new SizeI(100, 100), clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            injector.StrokeAsync([P(0, 0, 0), P(90, 0, 200)], cts.Token));

        Assert.Equal((byte)MotionAction.Up, sent[^1][1]);
    }
}
