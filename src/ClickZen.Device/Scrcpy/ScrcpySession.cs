using ClickZen.Core;
using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;
using ClickZen.Device.Decoding;
using ClickZen.Device.Scrcpy.Protocol;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace ClickZen.Device.Scrcpy;

public enum SessionState
{
    Disconnected,
    Connecting,
    /// <summary>Control channel up, no video (control-only mode).</summary>
    Connected,
    /// <summary>Video frames are flowing.</summary>
    Streaming,
    /// <summary>Lost the connection; retrying with back-off.</summary>
    Reconnecting,
    /// <summary>Gave up (or a non-retryable error); see <see cref="ScrcpySession.LastError"/>.</summary>
    Faulted,
}

public sealed record ScrcpySessionOptions
{
    public bool Video { get; init; } = true;
    public int MaxSize { get; init; } = 1280;
    public int VideoBitRate { get; init; } = 8_000_000;
    public int MaxFps { get; init; } = 60;
    public bool StayAwake { get; init; } = true;
    public bool ShowTouches { get; init; }
    public bool AutoReconnect { get; init; } = true;
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// One mirroring/control session with a device:
/// connects through an <see cref="IScrcpyTransport"/>, decodes video on a dedicated task and
/// exposes the newest frame as an <see cref="IFrameSource"/>; offers an <see cref="ScrcpyTouchInjector"/>.
/// Reconnects with exponential back-off (1 s → 30 s) when the connection drops.
/// </summary>
public sealed class ScrcpySession : ILiveFrameSource, IAsyncDisposable
{
    private readonly IScrcpyTransport _transport;
    private readonly ScrcpySessionOptions _options;
    private readonly ILogger? _logger;
    private readonly IClock _clock;
    private readonly Lock _frameLock = new();
    private readonly CancellationTokenSource _life = new();
    private Frame? _latest;
    private long _sequence;
    private TaskCompletionSource _frameSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile ScrcpyConnection? _connection;
    private Task? _runner;
    private SessionState _state = SessionState.Disconnected;
    private SizeI _videoSize;
    private int _fpsCount;
    private long _fpsWindowStart;

    public ScrcpySession(string serial, IScrcpyTransport transport, ScrcpySessionOptions? options = null,
        ILogger<ScrcpySession>? logger = null, IClock? clock = null)
    {
        Serial = serial;
        _transport = transport;
        _options = options ?? new ScrcpySessionOptions();
        _logger = logger;
        _clock = clock ?? SystemClock.Instance;
        Injector = new ScrcpyTouchInjector(SendAsync, () => _videoSize, () => DeviceSize, _clock);
    }

    public string Serial { get; }

    public string DeviceName { get; private set; } = "";

    public ScrcpyTouchInjector Injector { get; }

    /// <summary>Device screen size in its current orientation. Set by the owner from adb properties;
    /// when unknown, the video size is used (scrcpy video keeps the aspect ratio).</summary>
    public SizeI DeviceSize
    {
        get
        {
            var d = DeviceSizeOverride;
            var v = _videoSize;
            if (d.IsEmpty)
            {
                return v;
            }

            // adb reports the natural orientation; follow the video's orientation.
            return !v.IsEmpty && v.IsLandscape != d.IsLandscape ? d.Transposed : d;
        }
    }

    /// <summary>Physical screen size (any orientation) used to map video pixels to device pixels.</summary>
    public SizeI DeviceSizeOverride { get; set; }

    public SizeI VideoSize => _videoSize;

    public SessionState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                StateChanged?.Invoke(this, value);
            }
        }
    }

    public string? LastError { get; private set; }

    /// <summary>Frames per second over the last second.</summary>
    public double Fps { get; private set; }

    public event EventHandler<SessionState>? StateChanged;

    /// <summary>Raised on the decode thread for every new frame.</summary>
    public event EventHandler<Frame>? FrameArrived;

    // ------------------------------------------------------------------ IFrameSource

    public Frame? Latest
    {
        get
        {
            lock (_frameLock)
            {
                return _latest;
            }
        }
    }

    public async Task<Frame?> WaitForFrameAsync(long afterSequence, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = _clock.ElapsedMs + (long)timeout.TotalMilliseconds;
        while (true)
        {
            Task signal;
            lock (_frameLock)
            {
                if (_latest is not null && _latest.Sequence > afterSequence)
                {
                    return _latest;
                }

                signal = _frameSignal.Task;
            }

            var remaining = deadline - _clock.ElapsedMs;
            if (remaining <= 0)
            {
                return Latest;
            }

            try
            {
                await signal.WaitAsync(TimeSpan.FromMilliseconds(remaining), ct);
            }
            catch (TimeoutException)
            {
                return Latest;
            }
        }
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Starts connecting in the background; returns once the first connection attempt finished.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_runner is not null)
        {
            return;
        }

        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner = Task.Run(() => RunAsync(firstAttempt, _life.Token), CancellationToken.None);
        using var reg = ct.Register(() => firstAttempt.TrySetCanceled(ct));
        await firstAttempt.Task;
    }

    private async Task RunAsync(TaskCompletionSource firstAttempt, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            State = _state == SessionState.Disconnected ? SessionState.Connecting : SessionState.Reconnecting;
            ScrcpyStartResult? started = null;
            try
            {
                var options = new ScrcpyServerOptions
                {
                    Scid = ScrcpyServerOptions.NewScid(),
                    Video = _options.Video,
                    MaxSize = _options.MaxSize,
                    VideoBitRate = _options.VideoBitRate,
                    MaxFps = _options.MaxFps,
                    StayAwake = _options.StayAwake,
                    ShowTouches = _options.ShowTouches,
                };
                started = await _transport.StartAsync(Serial, options, ct);
                _connection = started.Connection;
                DeviceName = started.Connection.DeviceName;
                LastError = null;
                backoff = TimeSpan.FromSeconds(1);
                State = started.Connection.HasVideo ? SessionState.Streaming : SessionState.Connected;
                firstAttempt.TrySetResult();
                _logger?.LogInformation("scrcpy session for {Serial} ({Name}) connected, video={Video}", Serial, DeviceName, started.Connection.HasVideo);

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var tasks = new List<Task> { started.ServerExited, started.Connection.DrainControlAsync(linked.Token) };
                if (started.Connection.HasVideo)
                {
                    tasks.Add(Task.Run(() => PumpVideoAsync(started.Connection, linked.Token), CancellationToken.None));
                }

                var finished = await Task.WhenAny(tasks);
                await linked.CancelAsync();
                if (!ct.IsCancellationRequested)
                {
                    LastError = finished.Exception?.GetBaseException().Message ?? "Connection closed by the device.";
                    _logger?.LogWarning("scrcpy session for {Serial} ended: {Error}", Serial, LastError);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _logger?.LogWarning(ex, "scrcpy session for {Serial} failed to start", Serial);
                firstAttempt.TrySetResult();
            }
            finally
            {
                _connection = null;
                if (started is not null)
                {
                    await started.DisposeAsync();
                }
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (!_options.AutoReconnect)
            {
                State = SessionState.Faulted;
                return;
            }

            State = SessionState.Reconnecting;
            try
            {
                await _clock.DelayAsync(backoff, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.MaxBackoff.Ticks));
        }

        State = SessionState.Disconnected;
    }

    private sealed class DecoderSink : IVideoStreamSink
    {
        private readonly ScrcpySession _owner;
        private readonly FfmpegVideoDecoder _decoder;

        public DecoderSink(ScrcpySession owner, FfmpegVideoDecoder decoder)
        {
            _owner = owner;
            _decoder = decoder;
        }

        public void OnSession(int width, int height)
        {
            _owner._videoSize = new SizeI(width, height);
            _decoder.Flush();
        }

        public void OnPacket(VideoPacket packet)
        {
            var pic = _decoder.Decode(packet.Data, packet.IsConfig, packet.IsKeyFrame, packet.Pts);
            if (pic is not null)
            {
                _owner.Publish(pic);
            }
        }
    }

    private async Task PumpVideoAsync(ScrcpyConnection connection, CancellationToken ct)
    {
        var codec = connection.Codec == VideoCodecId.H265 ? AVCodecID.AV_CODEC_ID_HEVC : AVCodecID.AV_CODEC_ID_H264;
        using var decoder = new FfmpegVideoDecoder(codec);
        await connection.ReadVideoAsync(new DecoderSink(this, decoder), ct);
    }

    private void Publish(DecodedPicture pic)
    {
        if (_videoSize.IsEmpty || _videoSize.Width != pic.Width || _videoSize.Height != pic.Height)
        {
            _videoSize = new SizeI(pic.Width, pic.Height);
        }

        Frame frame;
        TaskCompletionSource signal;
        lock (_frameLock)
        {
            frame = new Frame(pic.Width, pic.Height, pic.Bgra, ++_sequence, _clock.ElapsedMs);
            _latest = frame;
            signal = _frameSignal;
            _frameSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.TrySetResult();
        UpdateFps();
        FrameArrived?.Invoke(this, frame);
    }

    private void UpdateFps()
    {
        var now = _clock.ElapsedMs;
        if (_fpsWindowStart == 0)
        {
            _fpsWindowStart = now;
        }

        _fpsCount++;
        var span = now - _fpsWindowStart;
        if (span >= 1000)
        {
            Fps = _fpsCount * 1000.0 / span;
            _fpsCount = 0;
            _fpsWindowStart = now;
        }
    }

    private Task SendAsync(Func<byte[], int> write, CancellationToken ct)
    {
        var c = _connection ?? throw new InvalidOperationException("Device is not connected.");
        return c.SendAsync(write, ct);
    }

    public bool IsControlAvailable => _connection is not null;

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync();
        if (_runner is not null)
        {
            try
            {
                await _runner;
            }
            catch (Exception)
            {
                // Already logged.
            }
        }

        _life.Dispose();
    }
}
