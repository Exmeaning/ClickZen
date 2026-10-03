using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Core.Recording;
using ClickZen.Device.Adb;
using ClickZen.Device.Adb.Parsing;

namespace ClickZen.Device.Input;

/// <summary>Everything needed to interpret one device's getevent stream.</summary>
/// <param name="Touchscreen">Input device whose events are recorded.</param>
/// <param name="NaturalSize">Screen size in the natural (rotation 0) orientation (<c>wm size</c>).</param>
/// <param name="Rotation">Display rotation at the start of the recording.</param>
/// <param name="Density">Screen density (dpi), 0 when unknown.</param>
public sealed record GeteventSession(InputDeviceInfo Touchscreen, SizeI NaturalSize, DisplayRotation Rotation, int Density)
{
    /// <summary>Screen size in the current orientation – the space recorded coordinates refer to.</summary>
    public SizeI ScreenSize => RotationMapper.RotatedSize(NaturalSize, Rotation);

    /// <summary>Pixels per dp (density / 160), 1 when the density is unknown.</summary>
    public double DpScale => Density > 0 ? Density / 160.0 : 1;

    /// <summary>Shell command streaming this touchscreen's events with kernel timestamps.</summary>
    public string Command => "getevent -t " + ShellQuoting.Quote(Touchscreen.Path);
}

public sealed record GeteventRecordingOptions
{
    /// <summary>Touchscreen to record; detected with <c>getevent -p</c> when null.</summary>
    public InputDeviceInfo? Touchscreen { get; init; }

    /// <summary>Natural-orientation screen size; read with <c>wm size</c> when null.</summary>
    public SizeI? NaturalSize { get; init; }

    /// <summary>Density in dpi; read with <c>wm density</c> when null.</summary>
    public int? Density { get; init; }

    /// <summary>Gesture thresholds. When null the defaults are used with the device's dp scale.</summary>
    public GestureRecognizerOptions? Recognizer { get; init; }

    /// <summary>Lift fingers still down when recording stops (true) or drop that unfinished gesture (false).</summary>
    public bool CompleteOpenGestures { get; init; } = true;

    public string? Name { get; init; }

    public string? DeviceModel { get; init; }
}

/// <summary>
/// Turns getevent output lines of one session into a <see cref="RecordingDocument"/>:
/// <see cref="GeteventEventParser"/> → <see cref="GeteventTouchDecoder"/> → <see cref="GestureRecognizer"/>.
/// Not thread-safe; <see cref="Build"/> may be called once.
/// </summary>
public sealed class GeteventRecordingBuilder
{
    private readonly GeteventSession _session;
    private readonly GeteventRecordingOptions _options;
    private readonly GeteventTouchDecoder _decoder;
    private readonly GestureRecognizer _recognizer;
    private readonly Action<RawTouchEvent>? _onTouch;
    private RecordingDocument? _built;

    public GeteventRecordingBuilder(GeteventSession session, GeteventRecordingOptions? options = null,
        Action<RawTouchEvent>? onTouch = null, Func<long>? clockMs = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _options = options ?? new GeteventRecordingOptions();
        _onTouch = onTouch;
        _decoder = new GeteventTouchDecoder(session.Touchscreen, session.NaturalSize, session.Rotation, clockMs);
        _recognizer = new GestureRecognizer(_options.Recognizer ?? new GestureRecognizerOptions { DpScale = session.DpScale });
    }

    public GeteventSession Session => _session;

    /// <summary>Gestures completed so far.</summary>
    public IReadOnlyList<Gesture> Gestures => _recognizer.Gestures;

    /// <summary>Raised whenever a gesture completes.</summary>
    public event EventHandler<Gesture>? GestureRecognized
    {
        add => _recognizer.GestureRecognized += value;
        remove => _recognizer.GestureRecognized -= value;
    }

    /// <summary>Feeds one output line; returns false for lines that are not input events (ignored).</summary>
    public bool FeedLine(string line)
    {
        if (!GeteventEventParser.TryParse(line, out var e))
        {
            return false;
        }

        Feed(e);
        return true;
    }

    public void Feed(GeteventEvent e)
    {
        if (_built is not null)
        {
            throw new InvalidOperationException("The recording has already been built.");
        }

        Dispatch(_decoder.Feed(e));
    }

    /// <summary>Ends the stream and returns the document (the same instance on repeated calls).</summary>
    public RecordingDocument Build()
    {
        if (_built is not null)
        {
            return _built;
        }

        if (_options.CompleteOpenGestures)
        {
            Dispatch(_decoder.Finish());
        }
        else
        {
            _decoder.Finish();
            _recognizer.Reset();
        }

        _built = new RecordingDocument
        {
            Name = _options.Name ?? "",
            CreatedAt = DateTimeOffset.Now,
            ScreenSize = _session.ScreenSize,
            DeviceModel = _options.DeviceModel,
            Gestures = _recognizer.Gestures.OrderBy(g => g.StartMs).ToList(),
        };
        return _built;
    }

    private void Dispatch(IReadOnlyList<RawTouchEvent> events)
    {
        foreach (var t in events)
        {
            _onTouch?.Invoke(t);
            _recognizer.Feed(t);
        }
    }
}

/// <summary>
/// Records touches made on the device's own screen by streaming <c>adb shell getevent -t &lt;touchscreen&gt;</c>.
/// Works without root (the shell user is in the <c>input</c> group). The rotation is read once when the
/// recording starts; rotating the device mid-recording is not tracked.
/// </summary>
public sealed class GeteventRecorder
{
    private readonly Func<string, string, CancellationToken, Task<string>> _shell;
    private readonly Func<string, string, Action<string>, CancellationToken, Task> _stream;
    private readonly IDisplayRotationResolver _rotation;

    public GeteventRecorder(AdbService adb, IDisplayRotationResolver? rotationResolver = null)
        : this(Require(adb).ShellAsync, adb.ExecuteStreamingAsync, rotationResolver ?? new DumpsysRotationResolver(adb))
    {
    }

    /// <param name="shell">Runs a short shell command and returns its output.</param>
    /// <param name="stream">Runs a long-lived command, calling back per line, until it exits or is cancelled.</param>
    /// <param name="rotationResolver">Supplies the display rotation.</param>
    public GeteventRecorder(Func<string, string, CancellationToken, Task<string>> shell,
        Func<string, string, Action<string>, CancellationToken, Task> stream,
        IDisplayRotationResolver rotationResolver)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _rotation = rotationResolver ?? throw new ArgumentNullException(nameof(rotationResolver));
    }

    private static AdbService Require(AdbService adb) => adb ?? throw new ArgumentNullException(nameof(adb));

    /// <summary>
    /// Resolves touchscreen, natural screen size, density and rotation (each skipped when given in
    /// <paramref name="options"/>). Throws <see cref="InvalidOperationException"/> without a usable touchscreen
    /// or screen size.
    /// </summary>
    public async Task<GeteventSession> PrepareAsync(string serial, GeteventRecordingOptions? options = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(serial);
        options ??= new GeteventRecordingOptions();

        var touch = options.Touchscreen;
        if (touch is null)
        {
            var output = await _shell(serial, "getevent -p", ct);
            touch = GeteventDeviceParser.PickTouchscreen(GeteventDeviceParser.Parse(output))
                    ?? throw new InvalidOperationException("No multi-touch touchscreen found in getevent -p output.");
        }
        else if (!touch.IsMultiTouch)
        {
            throw new InvalidOperationException($"{touch.Path} does not report ABS_MT_POSITION_X/Y.");
        }

        var size = options.NaturalSize ?? default;
        var density = options.Density ?? 0;
        if (options.NaturalSize is null || options.Density is null)
        {
            var wm = await _shell(serial, "wm size; wm density", ct);
            if (options.NaturalSize is null)
            {
                // wm size always reports the natural orientation.
                size = WmParser.ParseSize(wm).Effective;
            }

            if (options.Density is null)
            {
                density = WmParser.ParseDensity(wm).Effective;
            }
        }

        if (size.IsEmpty)
        {
            throw new InvalidOperationException("Could not determine the screen size (wm size).");
        }

        var rotation = await _rotation.GetRotationAsync(serial, ct);
        return new GeteventSession(touch, size, rotation, density);
    }

    /// <summary>
    /// Streams pointer events (device pixels, current orientation, kernel-clock milliseconds) until
    /// <paramref name="ct"/> is cancelled – then throws <see cref="OperationCanceledException"/> – or getevent exits.
    /// <paramref name="onTouch"/> runs on a background thread.
    /// </summary>
    public async Task CaptureAsync(string serial, GeteventSession session, Action<RawTouchEvent> onTouch, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(serial);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(onTouch);
        var decoder = new GeteventTouchDecoder(session.Touchscreen, session.NaturalSize, session.Rotation);
        var gate = new object();
        var done = false;
        try
        {
            await _stream(serial, session.Command, line =>
            {
                if (!GeteventEventParser.TryParse(line, out var e))
                {
                    return;
                }

                lock (gate)
                {
                    if (done)
                    {
                        return;
                    }

                    foreach (var t in decoder.Feed(e))
                    {
                        onTouch(t);
                    }
                }
            }, ct);
        }
        finally
        {
            lock (gate)
            {
                done = true;
            }
        }
    }

    /// <summary>
    /// Records until <paramref name="stopToken"/> is cancelled (or getevent exits) and returns the recording.
    /// Cancelling during preparation throws <see cref="OperationCanceledException"/>; cancelling while recording
    /// is the normal way to stop and returns what was recorded. adb failures are rethrown.
    /// </summary>
    /// <param name="onGesture">Called for each completed gesture (on a background thread).</param>
    public async Task<RecordingDocument> RecordAsync(string serial, GeteventRecordingOptions? options = null,
        Action<Gesture>? onGesture = null, CancellationToken stopToken = default)
    {
        var session = await PrepareAsync(serial, options, stopToken);
        return await RecordSessionAsync(serial, session, options, onGesture, stopToken);
    }

    /// <summary>Like <see cref="RecordAsync"/> with an already prepared session (see <see cref="PrepareAsync"/>).</summary>
    public async Task<RecordingDocument> RecordSessionAsync(string serial, GeteventSession session, GeteventRecordingOptions? options = null,
        Action<Gesture>? onGesture = null, CancellationToken stopToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(serial);
        ArgumentNullException.ThrowIfNull(session);
        var builder = new GeteventRecordingBuilder(session, options);
        if (onGesture is not null)
        {
            builder.GestureRecognized += (_, g) => onGesture(g);
        }

        // Lines may still arrive on the adb thread after cancellation returned control here.
        var gate = new object();
        var built = false;
        try
        {
            await _stream(serial, session.Command, line =>
            {
                lock (gate)
                {
                    if (!built)
                    {
                        builder.FeedLine(line);
                    }
                }
            }, stopToken);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            // Stopped by the caller.
        }

        lock (gate)
        {
            built = true;
            return builder.Build();
        }
    }
}
