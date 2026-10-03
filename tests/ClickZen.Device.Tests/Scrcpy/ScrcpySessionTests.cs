using System.Net;
using System.Net.Sockets;
using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;
using ClickZen.Device.Decoding;
using ClickZen.Device.Scrcpy;
using ClickZen.Device.Scrcpy.Protocol;
using ClickZen.Device.Tests.Decoding;

namespace ClickZen.Device.Tests.Scrcpy;

/// <summary>
/// Full session pipeline without a device: a fake transport starts an in-process "server" that
/// streams a real H.264 sample; the session must decode it, publish frames, and reconnect when the
/// server goes away.
/// </summary>
public sealed class ScrcpySessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string? FfmpegDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ClickZen.slnx")))
        {
            dir = dir.Parent;
        }

        var c = dir is null ? null : Path.Combine(dir.FullName, ".cache", "scrcpy-4.1", "scrcpy-win64-v4.1");
        return c is not null && File.Exists(Path.Combine(c, "avcodec-62.dll")) ? c : null;
    }

    /// <summary>Each StartAsync spins up a loopback "device" that sends the sample, then optionally hangs up.</summary>
    private sealed class FakeTransport : IScrcpyTransport
    {
        private readonly List<(bool Config, bool Key, byte[] Data)> _packets;

        public FakeTransport(List<(bool, bool, byte[])> packets) => _packets = packets;

        public int Starts;
        public int FailFirst;
        public bool HangUpAfterStream;
        public List<ScrcpyServerOptions> Options { get; } = [];

        public async Task<ScrcpyStartResult> StartAsync(string serial, ScrcpyServerOptions options, CancellationToken ct)
        {
            Options.Add(options);
            if (Interlocked.Increment(ref Starts) <= FailFirst)
            {
                throw new IOException("device not ready");
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var serverCts = new CancellationTokenSource();
            var server = Task.Run(async () =>
            {
                var video = await listener.AcceptSocketAsync(serverCts.Token);
                await video.SendAsync(new byte[] { 0 }, SocketFlags.None, serverCts.Token);
                var name = new byte[64];
                "FakePhone"u8.CopyTo(name);
                await video.SendAsync(name, SocketFlags.None, serverCts.Token);
                await video.SendAsync("h264"u8.ToArray(), SocketFlags.None, serverCts.Token);
                var control = await listener.AcceptSocketAsync(serverCts.Token);

                var hdr = new byte[12];
                StreamProtocol.WriteSessionHeader(hdr, 176, 144);
                await video.SendAsync(hdr, SocketFlags.None, serverCts.Token);
                long pts = 0;
                foreach (var (config, key, data) in _packets)
                {
                    StreamProtocol.WriteMediaHeader(hdr, config ? 0 : pts += 33_333, config, key, data.Length);
                    await video.SendAsync(hdr, SocketFlags.None, serverCts.Token);
                    await video.SendAsync(data, SocketFlags.None, serverCts.Token);
                }

                if (HangUpAfterStream)
                {
                    video.Shutdown(SocketShutdown.Both);
                    video.Dispose();
                    control.Dispose();
                    return;
                }

                await Task.Delay(Timeout.Infinite, serverCts.Token);
            }, CancellationToken.None);

            var conn = await ScrcpyConnection.ConnectAsync((IPEndPoint)listener.LocalEndpoint, video: true, TimeSpan.FromSeconds(5), ct);
            listener.Stop();
            return new ScrcpyStartResult(conn, server.ContinueWith(_ => { }, TaskScheduler.Default), async () =>
            {
                await serverCts.CancelAsync();
                serverCts.Dispose();
            });
        }
    }

    private static List<(bool, bool, byte[])> Sample() =>
        FfmpegVideoDecoderTests.ScrcpyPackets(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Samples", "testsrc2_176x144_12f.h264")));

    [Fact]
    public async Task Streams_and_publishes_decoded_frames()
    {
        var dir = FfmpegDir();
        Assert.SkipWhen(dir is null, "Bundled FFmpeg not available.");
        FfmpegVideoDecoder.Initialize(dir!);

        var transport = new FakeTransport(Sample());
        await using var session = new ScrcpySession("emulator-5554", transport, new ScrcpySessionOptions { MaxSize = 800 });
        var frames = 0;
        session.FrameArrived += (_, _) => Interlocked.Increment(ref frames);

        await session.StartAsync(Ct);
        Assert.Equal(SessionState.Streaming, session.State);
        Assert.Equal("FakePhone", session.DeviceName);

        var frame = await session.WaitForFrameAsync(-1, TimeSpan.FromSeconds(10), Ct);
        Assert.NotNull(frame);
        Assert.Equal(new SizeI(176, 144), frame.Size);

        // Wait until the whole sample was decoded.
        var last = frame;
        for (var i = 0; i < 50 && Volatile.Read(ref frames) < 12; i++)
        {
            last = await session.WaitForFrameAsync(last!.Sequence, TimeSpan.FromMilliseconds(200), Ct) ?? last;
        }

        Assert.Equal(12, Volatile.Read(ref frames));
        Assert.Equal(12, session.Latest!.Sequence);
        Assert.Equal(new SizeI(176, 144), session.VideoSize);
        Assert.Equal(800, transport.Options[0].MaxSize);
        Assert.True(transport.Options[0].Video);
    }

    [Fact]
    public async Task Wait_for_newer_frame_times_out_with_latest()
    {
        var dir = FfmpegDir();
        Assert.SkipWhen(dir is null, "Bundled FFmpeg not available.");
        FfmpegVideoDecoder.Initialize(dir!);

        await using var session = new ScrcpySession("s", new FakeTransport(Sample()));
        await session.StartAsync(Ct);
        for (var i = 0; i < 100 && (session.Latest?.Sequence ?? 0) < 12; i++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.Equal(12, session.Latest!.Sequence);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var again = await session.WaitForFrameAsync(session.Latest!.Sequence, TimeSpan.FromMilliseconds(150), Ct);
        Assert.Same(session.Latest, again);
        Assert.InRange(sw.ElapsedMilliseconds, 100, 3000);
    }

    [Fact]
    public async Task Reconnects_after_the_device_hangs_up()
    {
        var dir = FfmpegDir();
        Assert.SkipWhen(dir is null, "Bundled FFmpeg not available.");
        FfmpegVideoDecoder.Initialize(dir!);

        var transport = new FakeTransport(Sample()) { HangUpAfterStream = true };
        await using var session = new ScrcpySession("s", transport, new ScrcpySessionOptions { MaxBackoff = TimeSpan.FromSeconds(1) });
        var states = new List<SessionState>();
        session.StateChanged += (_, s) => { lock (states) { states.Add(s); } };

        await session.StartAsync(Ct);
        for (var i = 0; i < 100 && Volatile.Read(ref transport.Starts) < 2; i++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(transport.Starts >= 2, "Session should have reconnected.");
        lock (states)
        {
            Assert.Contains(SessionState.Reconnecting, states);
        }

        Assert.NotNull(session.LastError);
    }

    [Fact]
    public async Task First_attempt_failure_reports_and_keeps_retrying()
    {
        var transport = new FakeTransport([]) { FailFirst = 100 };
        await using var session = new ScrcpySession("s", transport, new ScrcpySessionOptions { MaxBackoff = TimeSpan.FromMilliseconds(50) });

        await session.StartAsync(Ct);

        Assert.Equal("device not ready", session.LastError);
        Assert.Contains(session.State, new[] { SessionState.Connecting, SessionState.Reconnecting });
        Assert.Throws<InvalidOperationException>(() => session.Injector.KeyAsync(3, Ct).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task Without_auto_reconnect_a_failure_is_terminal()
    {
        var transport = new FakeTransport([]) { FailFirst = 1 };
        await using var session = new ScrcpySession("s", transport, new ScrcpySessionOptions { AutoReconnect = false });
        await session.StartAsync(Ct);
        for (var i = 0; i < 40 && session.State != SessionState.Faulted; i++)
        {
            await Task.Delay(25, Ct);
        }

        Assert.Equal(SessionState.Faulted, session.State);
        Assert.Equal(1, transport.Starts);
    }

    [Fact]
    public void Device_size_follows_video_orientation()
    {
        var session = new ScrcpySession("s", new FakeTransport([])) { DeviceSizeOverride = new SizeI(1080, 2400) };
        Assert.Equal(new SizeI(1080, 2400), session.DeviceSize);
    }
}
