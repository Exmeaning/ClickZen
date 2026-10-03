using System.Collections.ObjectModel;
using System.Globalization;
using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Core.Persistence;
using ClickZen.Core.Recording;
using ClickZen.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace ClickZen.App.Services;

/// <summary>Where the touch events of a recording come from.</summary>
public enum RecordingInputSource
{
    /// <summary>Pointer input on a <see cref="Controls.MirrorView"/> (timestamps: <see cref="Environment.TickCount64"/>).</summary>
    Mirror,

    /// <summary>Touches made on the device itself (e.g. a getevent reader). Time 0 is the first event.</summary>
    Device,
}

/// <summary>How a playback run ended.</summary>
public enum PlaybackOutcome
{
    Completed,
    Stopped,
}

/// <summary>
/// Captures touches made on the device's own screen (e.g. by parsing <c>adb shell getevent</c>).
/// Implemented outside the recording page; register it in DI and the page offers "record on device".
/// </summary>
public interface IDeviceTouchCapture
{
    /// <summary>True when the device can be captured right now (online, touchscreen detected...).</summary>
    bool CanCapture(DeviceEntry entry);

    /// <summary>
    /// Streams touches until <paramref name="ct"/> is cancelled (then completes or throws
    /// <see cref="OperationCanceledException"/>). Events are in device pixels of the current orientation
    /// (same space as <see cref="RecordingService.CurrentScreen"/>) with monotonic millisecond timestamps,
    /// one pointer id per finger, and may be raised on any thread.
    /// </summary>
    Task CaptureAsync(DeviceEntry entry, Action<RawTouchEvent> onTouch, CancellationToken ct);
}

/// <summary>One row of the gesture timeline.</summary>
public sealed partial class GestureItem : ObservableObject
{
    public GestureItem(Gesture gesture, int number, ILocalizer loc)
    {
        Gesture = gesture;
        Number = number;
        KindText = loc[KindKey(gesture.Kind)];
        Glyph = KindGlyph(gesture.Kind);
        TimeText = FormatTime(gesture.StartMs);
        DurationText = gesture.Kind is GestureKind.Key or GestureKind.Text
            ? ""
            : gesture.DurationMs.ToString(CultureInfo.InvariantCulture) + " ms";
        DetailText = Describe(gesture, loc);
    }

    public Gesture Gesture { get; }

    /// <summary>1-based position in the timeline.</summary>
    [ObservableProperty]
    public partial int Number { get; set; }

    /// <summary>True while playback is executing this gesture.</summary>
    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    public string KindText { get; }

    public string Glyph { get; }

    public string TimeText { get; }

    public string DurationText { get; }

    public string DetailText { get; }

    public bool IsTouch => IsTouchKind(Gesture.Kind);

    public static bool IsTouchKind(GestureKind kind) =>
        kind is GestureKind.Tap or GestureKind.LongPress or GestureKind.Swipe or GestureKind.MultiTouch;

    /// <summary>mm:ss.fff</summary>
    public static string FormatTime(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}");
    }

    private static string KindKey(GestureKind kind) => kind switch
    {
        GestureKind.Tap => "Rec_Kind_Tap",
        GestureKind.LongPress => "Rec_Kind_LongPress",
        GestureKind.Swipe => "Rec_Kind_Swipe",
        GestureKind.MultiTouch => "Rec_Kind_MultiTouch",
        GestureKind.Key => "Rec_Kind_Key",
        _ => "Rec_Kind_Text",
    };

    private static string KindGlyph(GestureKind kind) => kind switch
    {
        GestureKind.Tap => "\uE7C9",
        GestureKind.LongPress => "\uE916",
        GestureKind.Swipe => "\uE8AB",
        GestureKind.MultiTouch => "\uE80A",
        GestureKind.Key => "\uE765",
        _ => "\uE8D2",
    };

    private static string Pt(PointD p) => p.Round().ToString();

    private static string Describe(Gesture g, ILocalizer loc) => g.Kind switch
    {
        GestureKind.Tap or GestureKind.LongPress when g.Fingers.Count > 0 => $"({Pt(g.Fingers[0].Start)})",
        GestureKind.Swipe when g.Fingers.Count > 0 => $"({Pt(g.Fingers[0].Start)}) -> ({Pt(g.Fingers[0].End)})",
        GestureKind.MultiTouch => loc.Format("Rec_Fingers", g.Fingers.Count),
        GestureKind.Key => KeyName(g.KeyCode, loc),
        GestureKind.Text => "\"" + (g.Text ?? "") + "\"",
        _ => "",
    };

    private static string KeyName(int code, ILocalizer loc) => code switch
    {
        KeyCodes.Back => loc["Rec_Key_Back"],
        KeyCodes.Home => loc["Rec_Key_Home"],
        KeyCodes.AppSwitch => loc["Rec_Key_Recents"],
        KeyCodes.VolumeUp => loc["Rec_Key_VolumeUp"],
        KeyCodes.VolumeDown => loc["Rec_Key_VolumeDown"],
        KeyCodes.Power => loc["Rec_Key_Power"],
        KeyCodes.Enter => "Enter",
        KeyCodes.Delete => "Backspace",
        112 => "Delete",
        61 => "Tab",
        19 => "Up",
        20 => "Down",
        21 => "Left",
        22 => "Right",
        _ => loc.Format("Rec_Key_Code", code),
    };
}

/// <summary>
/// The open .czrec document plus the recording and playback machinery for it. Singleton, so the
/// document, a running playback and the playback options survive page navigation.
/// <para>
/// Threading: every member must be used on the UI thread. Recorders running elsewhere (a device-side
/// getevent reader) marshal through <see cref="Post"/>.
/// </para>
/// </summary>
public sealed partial class RecordingService : ObservableObject, IDisposable
{
    /// <summary>Gap inserted between the existing timeline and newly appended gestures.</summary>
    public const int AppendGapMs = 500;

    /// <summary>Consecutive characters typed within this window are merged into one text gesture.</summary>
    public const int TextMergeWindowMs = 1500;

    private readonly DeviceHub _hub;
    private readonly SettingsService _settings;
    private readonly AppPaths _paths;
    private readonly ILocalizer _loc;
    private readonly ILogger<RecordingService> _log;
    private readonly IDeviceTouchCapture? _deviceCapture;

    private GestureRecognizer? _recognizer;
    private CancellationTokenSource? _deviceCts;
    private long _recordStartTick;
    private long _baseOffsetMs;
    private double _scaleX = 1;
    private double _scaleY = 1;
    private Gesture? _lastText;
    private long _lastTextTick;
    private CancellationTokenSource? _playCts;

    public RecordingService(DeviceHub hub, SettingsService settings, AppPaths paths, ILocalizer loc, ILogger<RecordingService> log,
        IEnumerable<IDeviceTouchCapture> deviceCaptures)
    {
        _hub = hub;
        _settings = settings;
        _paths = paths;
        _loc = loc;
        _log = log;
        _deviceCapture = deviceCaptures.LastOrDefault();
        Speed = Math.Clamp(settings.Current.Recording.DefaultPlaybackSpeed, 0.1, 10);
    }

    public RecordingDocument Document { get; private set; } = new();

    /// <summary>Timeline rows, in <see cref="RecordingDocument.Gestures"/> order.</summary>
    public ObservableCollection<GestureItem> Items { get; } = [];

    /// <summary>Raised after a gesture was recorded and inserted into <see cref="Items"/>.</summary>
    public event EventHandler<GestureItem>? GestureAdded;

    /// <summary>Raised after <see cref="Items"/> was rebuilt; the argument is what should be selected.</summary>
    public event EventHandler<IReadOnlyList<Gesture>>? ItemsReset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial string? FilePath { get; private set; }

    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    [ObservableProperty]
    public partial bool IsRecording { get; private set; }

    /// <summary>Source of the running recording; null when not recording.</summary>
    [ObservableProperty]
    public partial RecordingInputSource? RecordingSource { get; private set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; private set; }

    /// <summary>Index into <see cref="Items"/> being played, or -1.</summary>
    [ObservableProperty]
    public partial int PlaybackIndex { get; private set; } = -1;

    /// <summary>Current loop (1-based) while playing.</summary>
    [ObservableProperty]
    public partial int PlaybackLoop { get; private set; }

    // Playback options (kept here so they survive navigation).
    [ObservableProperty]
    public partial double Speed { get; set; }

    /// <summary>0 = loop forever.</summary>
    [ObservableProperty]
    public partial double Loops { get; set; } = 1;

    [ObservableProperty]
    public partial bool Randomize { get; set; }

    public string DisplayName => FilePath is null ? _loc["Rec_Untitled"] : Path.GetFileNameWithoutExtension(FilePath);

    public TimeSpan RecordingElapsed => IsRecording && RecordingSource == RecordingInputSource.Mirror
        ? TimeSpan.FromMilliseconds(Environment.TickCount64 - _recordStartTick)
        : TimeSpan.Zero;

    public string DefaultDirectory
    {
        get
        {
            Directory.CreateDirectory(_paths.RecordingsDirectory);
            return _paths.RecordingsDirectory;
        }
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread (for recorders on background threads).</summary>
    public static bool Post(Action action) =>
        App.Current.MainWindow?.DispatcherQueue.TryEnqueue(() => action()) ?? false;

    // ------------------------------------------------------------------ document

    public void New()
    {
        StopRecording();
        Replace(new RecordingDocument(), null);
    }

    public void Open(string path)
    {
        var doc = RecordingDocument.Load(path);
        StopRecording();
        Replace(doc, path);
    }

    public void Save(string path)
    {
        if (!path.EndsWith(RecordingDocument.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            path += RecordingDocument.FileExtension;
        }

        Document.Name = Path.GetFileNameWithoutExtension(path);
        Document.Save(path);
        FilePath = path;
        IsDirty = false;
        _log.LogInformation("Recording saved to {Path} ({Count} gestures)", path, Document.Gestures.Count);
    }

    private void Replace(RecordingDocument doc, string? path)
    {
        Document = doc;
        FilePath = path;
        IsDirty = false;
        Rebuild([]);
    }

    // ------------------------------------------------------------------ recording

    /// <summary>
    /// Starts appending gestures to the document. When the document is empty it adopts
    /// <paramref name="screen"/>; otherwise new coordinates are rescaled to the document's screen.
    /// </summary>
    /// <param name="screen">Device screen size (current orientation) that incoming coordinates refer to.</param>
    public void StartRecording(DeviceEntry entry, SizeI screen, RecordingInputSource source)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (IsRecording || IsPlaying)
        {
            return;
        }

        if (Document.Gestures.Count == 0)
        {
            Document.ScreenSize = screen;
            Document.DeviceModel = string.IsNullOrWhiteSpace(entry.Info.Model) ? entry.Info.DisplayName : entry.Info.Model;
            Document.CreatedAt = DateTimeOffset.Now;
            _baseOffsetMs = 0;
        }
        else
        {
            _baseOffsetMs = Document.DurationMs + AppendGapMs;
        }

        var docScreen = Document.ScreenSize;
        _scaleX = docScreen.IsEmpty || screen.IsEmpty ? 1 : (double)docScreen.Width / screen.Width;
        _scaleY = docScreen.IsEmpty || screen.IsEmpty ? 1 : (double)docScreen.Height / screen.Height;

        var r = _settings.Current.Recording;
        _recognizer = new GestureRecognizer(new GestureRecognizerOptions
        {
            SwipeThresholdDp = r.SwipeThresholdDp,
            LongPressThresholdMs = r.LongPressThresholdMs,
            DpScale = entry.Info.DpScale,
            SimplifyEpsilonDp = r.KeepFullTrajectory ? 0 : 1.0,
        });
        _recognizer.GestureRecognized += OnGestureRecognized;

        // Time 0 is the first input event, so idle time before it is not recorded (and an appended
        // take starts exactly AppendGapMs after the existing timeline).
        _recordStartTick = Environment.TickCount64;
        _lastText = null;
        RecordingSource = source;
        IsRecording = true;
        _log.LogInformation("Recording started on {Serial} ({Source}, screen {Screen})", entry.Serial, source, screen);
    }

    /// <summary>True when an <see cref="IDeviceTouchCapture"/> is registered and can capture this device.</summary>
    public bool CanRecordOnDevice(DeviceEntry? entry) => entry is not null && _deviceCapture?.CanCapture(entry) == true;

    /// <summary>
    /// Records touches made on the device itself until <see cref="StopRecording"/> is called or the
    /// capture ends. Completes when recording has stopped; capture errors are rethrown.
    /// </summary>
    public async Task RecordOnDeviceAsync(DeviceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_deviceCapture is null || !_deviceCapture.CanCapture(entry))
        {
            throw new InvalidOperationException(_loc["Rec_DeviceCaptureUnavailable"]);
        }

        if (IsRecording || IsPlaying)
        {
            return;
        }

        StartRecording(entry, CurrentScreen(entry), RecordingInputSource.Device);
        var cts = new CancellationTokenSource();
        _deviceCts = cts;
        var recognizer = _recognizer;
        try
        {
            await _deviceCapture.CaptureAsync(entry, e => Post(() =>
            {
                // Late events after a stop/restart belong to no recording.
                if (ReferenceEquals(_recognizer, recognizer))
                {
                    FeedTouch(e);
                }
            }), cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Stopped by the user.
        }
        finally
        {
            if (ReferenceEquals(_deviceCts, cts))
            {
                _deviceCts = null;
                if (ReferenceEquals(_recognizer, recognizer))
                {
                    StopRecording();
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>Stops recording; a gesture whose fingers are still down is discarded.</summary>
    public void StopRecording()
    {
        if (_deviceCts is { } device)
        {
            _deviceCts = null;
            try
            {
                device.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Capture already finished.
            }
        }

        if (_recognizer is null)
        {
            return;
        }

        _recognizer.Reset();
        _recognizer.GestureRecognized -= OnGestureRecognized;
        _recognizer = null;
        _lastText = null;
        IsRecording = false;
        RecordingSource = null;
        _log.LogInformation("Recording stopped, {Count} gestures in document", Document.Gestures.Count);
    }

    /// <summary>Feeds one raw pointer event (device pixels). Ignored when not recording.</summary>
    public void FeedTouch(RawTouchEvent e) => _recognizer?.Feed(e);

    public void FeedKey(int keyCode, long timestampMs) => _recognizer?.AddKey(keyCode, timestampMs);

    public void FeedText(string text, long timestampMs)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _recognizer?.AddText(text, timestampMs);
        }
    }

    private void OnGestureRecognized(object? sender, Gesture raw)
    {
        var g = raw with { StartMs = raw.StartMs + _baseOffsetMs, Fingers = Scale(raw.Fingers, _scaleX, _scaleY) };
        var now = Environment.TickCount64;

        if (g.Kind == GestureKind.Text && _lastText is { } prev && now - _lastTextTick <= TextMergeWindowMs)
        {
            var i = IndexOf(prev);
            if (i >= 0)
            {
                var merged = prev with { Text = prev.Text + g.Text };
                Document.Gestures[i] = merged;
                Items[i] = new GestureItem(merged, i + 1, _loc);
                _lastText = merged;
                _lastTextTick = now;
                IsDirty = true;
                GestureAdded?.Invoke(this, Items[i]);
                return;
            }
        }

        var at = Document.Gestures.FindLastIndex(x => x.StartMs <= g.StartMs) + 1;
        Document.Gestures.Insert(at, g);
        var item = new GestureItem(g, at + 1, _loc);
        Items.Insert(at, item);
        Renumber(at + 1);
        _lastText = g.Kind == GestureKind.Text ? g : null;
        _lastTextTick = now;
        IsDirty = true;
        GestureAdded?.Invoke(this, item);
    }

    private static IReadOnlyList<FingerStroke> Scale(IReadOnlyList<FingerStroke> fingers, double sx, double sy)
    {
        if (sx == 1 && sy == 1)
        {
            return fingers;
        }

        return fingers.Select(f => new FingerStroke(f.Points.Select(p => new TimedPoint(p.X * sx, p.Y * sy, p.OffsetMs)).ToArray())).ToList();
    }

    // ------------------------------------------------------------------ editing

    public void Delete(IReadOnlyCollection<Gesture> gestures)
    {
        if (gestures.Count == 0)
        {
            return;
        }

        var removed = Document.Gestures.RemoveAll(g => gestures.Any(x => ReferenceEquals(x, g)));
        if (removed > 0)
        {
            _lastText = null;
            IsDirty = true;
            Rebuild([]);
        }
    }

    /// <summary>Moves gestures in time. With <paramref name="ripple"/> every later gesture moves too.</summary>
    public void ShiftTime(IReadOnlyCollection<Gesture> gestures, long deltaMs, bool ripple)
    {
        if (gestures.Count == 0 || deltaMs == 0)
        {
            return;
        }

        var targets = new HashSet<Gesture>(gestures, ReferenceEqualityComparer.Instance);
        if (ripple)
        {
            var from = gestures.Min(g => g.StartMs);
            foreach (var g in Document.Gestures.Where(g => g.StartMs >= from))
            {
                targets.Add(g);
            }
        }

        var selected = new List<Gesture>();
        for (var i = 0; i < Document.Gestures.Count; i++)
        {
            var g = Document.Gestures[i];
            if (!targets.Contains(g))
            {
                continue;
            }

            var moved = g with { StartMs = Math.Max(0, g.StartMs + deltaMs) };
            Document.Gestures[i] = moved;
            if (gestures.Any(x => ReferenceEquals(x, g)))
            {
                selected.Add(moved);
            }
        }

        Document.Gestures = Document.Gestures.OrderBy(g => g.StartMs).ToList();
        IsDirty = true;
        Rebuild(selected);
    }

    /// <summary>Sets the start time of one gesture (optionally moving everything after it by the same amount).</summary>
    public void SetStartTime(Gesture gesture, long startMs, bool ripple) =>
        ShiftTime([gesture], Math.Max(0, startMs) - gesture.StartMs, ripple);

    /// <summary>Translates touch gestures by (dx, dy) device pixels, clamped to the screen.</summary>
    public void Translate(IReadOnlyCollection<Gesture> gestures, double dx, double dy)
    {
        if (gestures.Count == 0 || (dx == 0 && dy == 0))
        {
            return;
        }

        var screen = Document.ScreenSize;
        var maxX = screen.IsEmpty ? double.MaxValue : screen.Width - 1;
        var maxY = screen.IsEmpty ? double.MaxValue : screen.Height - 1;
        var selected = new List<Gesture>();
        for (var i = 0; i < Document.Gestures.Count; i++)
        {
            var g = Document.Gestures[i];
            if (!gestures.Any(x => ReferenceEquals(x, g)))
            {
                continue;
            }

            if (!GestureItem.IsTouchKind(g.Kind))
            {
                selected.Add(g);
                continue;
            }

            var moved = g with
            {
                Fingers = g.Fingers.Select(f => new FingerStroke(f.Points.Select(p => new TimedPoint(
                    Math.Clamp(p.X + dx, 0, maxX), Math.Clamp(p.Y + dy, 0, maxY), p.OffsetMs)).ToArray())).ToList(),
            };
            Document.Gestures[i] = moved;
            selected.Add(moved);
        }

        IsDirty = true;
        Rebuild(selected);
    }

    /// <summary>Moves a touch gesture so its first point lands on (x, y); its shape is kept.</summary>
    public void MoveTo(Gesture gesture, double x, double y)
    {
        if (!GestureItem.IsTouchKind(gesture.Kind) || gesture.Fingers.Count == 0)
        {
            return;
        }

        var start = gesture.Fingers[0].Start;
        Translate([gesture], x - start.X, y - start.Y);
    }

    private int IndexOf(Gesture g) => Document.Gestures.FindIndex(x => ReferenceEquals(x, g));

    private void Renumber(int from)
    {
        for (var i = from; i < Items.Count; i++)
        {
            Items[i].Number = i + 1;
        }
    }

    private void Rebuild(IReadOnlyList<Gesture> select)
    {
        PlaybackIndex = -1;
        Items.Clear();
        for (var i = 0; i < Document.Gestures.Count; i++)
        {
            Items.Add(new GestureItem(Document.Gestures[i], i + 1, _loc));
        }

        ItemsReset?.Invoke(this, select);
    }

    // ------------------------------------------------------------------ playback

    /// <summary>Screen size playback coordinates are scaled to.</summary>
    public static SizeI CurrentScreen(DeviceEntry entry) =>
        entry.Session is { } s && !s.DeviceSize.IsEmpty ? s.DeviceSize : entry.Info.PhysicalSize;

    /// <summary>True when the recording was made in the other orientation than the device is in now.</summary>
    public bool OrientationDiffers(DeviceEntry entry)
    {
        var current = CurrentScreen(entry);
        return !Document.ScreenSize.IsEmpty && !current.IsEmpty && RefScaling.OrientationDiffers(Document.ScreenSize, current);
    }

    /// <summary>Plays the document (from <paramref name="startIndex"/>) on a device with the current options.</summary>
    public async Task<PlaybackOutcome> PlayAsync(DeviceEntry entry, int startIndex)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (IsPlaying || IsRecording)
        {
            throw new InvalidOperationException(_loc["Rec_Busy"]);
        }

        if (Document.Gestures.Count == 0)
        {
            throw new InvalidOperationException(_loc["Rec_Empty"]);
        }

        var ui = DispatcherQueue.GetForCurrentThread();
        var snapshot = new RecordingDocument { ScreenSize = Document.ScreenSize, Gestures = Document.Gestures.ToList() };
        var a = _settings.Current.Automation;
        var options = new PlaybackOptions
        {
            Speed = Math.Clamp(double.IsFinite(Speed) ? Speed : 1, 0.1, 10),
            Loops = double.IsFinite(Loops) ? Math.Max(0, (int)Loops) : 1,
            StartIndex = Math.Clamp(startIndex, 0, snapshot.Gestures.Count - 1),
            Humanize = Randomize
                ? new HumanizeOptions
                {
                    PositionJitterDp = a.DefaultPositionJitterDp,
                    DelayJitterPercent = a.DefaultDelayJitterPercent,
                    DurationJitterPercent = a.DefaultDurationJitterPercent,
                }
                : HumanizeOptions.None,
        };

        var cts = new CancellationTokenSource();
        _playCts = cts;
        IsPlaying = true;
        PlaybackLoop = 1;
        try
        {
            var injector = await _hub.GetInjectorAsync(entry, cts.Token);
            var screen = CurrentScreen(entry);
            var player = new RecordingPlayer(injector);
            player.Progress += (_, p) => ui?.TryEnqueue(() =>
            {
                if (IsPlaying)
                {
                    PlaybackLoop = p.Loop;
                    SetPlaybackIndex(p.GestureIndex);
                }
            });
            _log.LogInformation("Playback on {Serial} via {Injector}: speed {Speed}, loops {Loops}, from {Start}, randomize {Rand}",
                entry.Serial, injector.Name, options.Speed, options.Loops, options.StartIndex, Randomize);
            await Task.Run(() => player.PlayAsync(snapshot, options, screen, entry.Info.DpScale, cts.Token), cts.Token);
            return PlaybackOutcome.Completed;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return PlaybackOutcome.Stopped;
        }
        finally
        {
            if (ReferenceEquals(_playCts, cts))
            {
                _playCts = null;
            }

            cts.Dispose();
            IsPlaying = false;
            PlaybackLoop = 0;
            SetPlaybackIndex(-1);
        }
    }

    public void StopPlayback()
    {
        try
        {
            _playCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }
    }

    private void SetPlaybackIndex(int index)
    {
        if (PlaybackIndex >= 0 && PlaybackIndex < Items.Count)
        {
            Items[PlaybackIndex].IsPlaying = false;
        }

        // Indices refer to the snapshot; they only match while the timeline is unchanged.
        PlaybackIndex = index >= 0 && index < Items.Count ? index : -1;
        if (PlaybackIndex >= 0)
        {
            Items[PlaybackIndex].IsPlaying = true;
        }
    }

    public void Dispose()
    {
        StopPlayback();
        StopRecording();
    }
}
