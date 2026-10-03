using System.Runtime.CompilerServices;
using ClickZen.Core;
using ClickZen.Core.Automation;
using ClickZen.Core.Devices;
using ClickZen.Core.Geometry;
using ClickZen.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace ClickZen.Platform.Capture;

public enum WindowCaptureState
{
    Stopped,
    Starting,
    /// <summary>A backend is running (frames arrive whenever the window content changes).</summary>
    Capturing,
    /// <summary>The window was closed or capture failed permanently; see <see cref="WindowFrameSource.LastError"/>.</summary>
    Faulted,
}

public sealed record WindowFrameSourceOptions
{
    /// <summary>Auto = WGC with automatic PrintWindow fallback.</summary>
    public WindowCaptureMethod Method { get; init; } = WindowCaptureMethod.Auto;

    /// <summary>Crop in client-area physical pixels; null = whole client area. Can be changed later via <see cref="WindowFrameSource.CropRect"/>.</summary>
    public RectI? CropRect { get; init; }

    /// <summary>PrintWindow polling period (default ≈ 15 fps).</summary>
    public TimeSpan PrintWindowInterval { get; init; } = TimeSpan.FromMilliseconds(66);

    /// <summary>In Auto mode: fall back to PrintWindow when WGC delivers no frame within this time.</summary>
    public TimeSpan WgcFirstFrameTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Hide the yellow WGC border where the OS allows it (Windows 11).</summary>
    public bool HideBorder { get; init; } = true;

    public bool CaptureCursor { get; init; }

    public static WindowFrameSourceOptions FromProfile(EmulatorProfile profile) => new()
    {
        Method = profile.CaptureMethod,
        CropRect = profile.CropRect,
    };
}

/// <summary>
/// <see cref="IFrameSource"/> backed by a desktop window (emulator window mode).
/// <para>Emitted frames contain only the crop region (client-area physical pixels, see <see cref="CaptureGeometry"/>);
/// frame pixel (x, y) = client pixel (crop.X + x, crop.Y + y). Map frame → adb input coordinates with
/// <see cref="EmulatorProfile.CreateFrameMapping"/>.</para>
/// <para>WGC only produces frames when the window content changes, so a static window yields no new frames:
/// <see cref="WaitForFrameAsync"/> then times out and returns the latest frame, which is still accurate.</para>
/// </summary>
public sealed class WindowFrameSource : IFrameSource, IAsyncDisposable
{
    private readonly WindowFrameSourceOptions _options;
    private readonly ILogger? _logger;
    private readonly IClock _clock;
    private readonly Lock _frameLock = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Frame? _latest;
    private long _sequence;
    private TaskCompletionSource _frameSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile IWindowCapture? _backend;
    private volatile StrongBox<RectI>? _cropBox;
    private WindowCaptureState _state;
    private SizeI _clientSize;
    private RectI _effectiveCrop;
    private int _fpsCount;
    private long _fpsWindowStart;
    private bool _disposed;

    public WindowFrameSource(nint windowHandle, WindowFrameSourceOptions? options = null, ILogger? logger = null, IClock? clock = null)
    {
        WindowHandle = windowHandle;
        _options = options ?? new WindowFrameSourceOptions();
        CropRect = _options.CropRect;
        _logger = logger;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>Source configured from a profile for the window <see cref="WindowMatcher.FindBestMatch(EmulatorProfile, IEnumerable{DesktopWindowInfo}, nint)"/> found.</summary>
    public static WindowFrameSource ForProfile(EmulatorProfile profile, nint windowHandle, ILogger? logger = null) =>
        new(windowHandle, WindowFrameSourceOptions.FromProfile(profile), logger);

    public nint WindowHandle { get; }

    /// <summary>Crop in client-area physical pixels (null = whole client area). Takes effect with the next frame.</summary>
    public RectI? CropRect
    {
        get => _cropBox?.Value;
        set => _cropBox = value is { } r ? new StrongBox<RectI>(r) : null;
    }

    /// <summary>Backend currently delivering frames (Wgc or PrintWindow); Auto before start.</summary>
    public WindowCaptureMethod ActiveMethod => _backend?.Method ?? WindowCaptureMethod.Auto;

    /// <summary>Client-area size seen with the last frame (physical px).</summary>
    public SizeI ClientSize => _clientSize;

    /// <summary>Crop actually applied to the last frame (requested crop clamped to the client area).</summary>
    public RectI EffectiveCrop => _effectiveCrop;

    public WindowCaptureState State
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

    public event EventHandler<WindowCaptureState>? StateChanged;

    /// <summary>Raised on a capture thread for each new (cropped) frame.</summary>
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
            if (remaining <= 0 || State == WindowCaptureState.Faulted)
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

    /// <summary>
    /// Starts capturing. In Auto mode tries WGC first and falls back to PrintWindow when WGC is unsupported,
    /// throws, or delivers no frame within <see cref="WindowFrameSourceOptions.WgcFirstFrameTimeout"/>.
    /// Returns once a backend is running (it does not throw for capture failures: check <see cref="State"/>/<see cref="LastError"/>).
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_backend is not null)
            {
                return;
            }

            LastError = null;
            State = WindowCaptureState.Starting;
            if (!WindowEnumerator.IsAlive(WindowHandle))
            {
                Fault("The window does not exist.");
                return;
            }

            var method = _options.Method;
            if (method is WindowCaptureMethod.Auto or WindowCaptureMethod.Wgc)
            {
                var ok = await TryStartWgcAsync(requireFrame: method == WindowCaptureMethod.Auto, ct);
                if (ok)
                {
                    State = WindowCaptureState.Capturing;
                    return;
                }

                if (method == WindowCaptureMethod.Wgc)
                {
                    Fault(LastError ?? "Windows.Graphics.Capture failed.");
                    return;
                }

                _logger?.LogInformation("WGC unavailable for window {Hwnd:X} ({Error}); falling back to PrintWindow", WindowHandle, LastError);
            }

            var pw = new PrintWindowCapture(WindowHandle, _options.PrintWindowInterval);
            Attach(pw);
            try
            {
                pw.Start();
            }
            catch (Exception ex)
            {
                Detach(pw);
                Fault(ex.Message);
                return;
            }

            LastError = null;
            State = WindowCaptureState.Capturing;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task<bool> TryStartWgcAsync(bool requireFrame, CancellationToken ct)
    {
        if (!WgcWindowCapture.IsSupported)
        {
            LastError = "Windows.Graphics.Capture is not supported on this system.";
            return false;
        }

        var wgc = new WgcWindowCapture(WindowHandle) { HideBorder = _options.HideBorder, CaptureCursor = _options.CaptureCursor };
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFirst(Frame _) => first.TrySetResult();
        wgc.FrameReady += OnFirst;
        Attach(wgc);
        try
        {
            wgc.Start();
        }
        catch (Exception ex)
        {
            wgc.FrameReady -= OnFirst;
            Detach(wgc);
            LastError = ex.Message;
            _logger?.LogWarning(ex, "WGC start failed for window {Hwnd:X}", WindowHandle);
            return false;
        }

        if (!requireFrame)
        {
            wgc.FrameReady -= OnFirst;
            return true;
        }

        try
        {
            await first.Task.WaitAsync(_options.WgcFirstFrameTimeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            // A minimised window legitimately produces nothing; keep WGC, frames resume on restore.
            if (WindowMetrics.TryQuery(WindowHandle, out var m) && m.IsMinimized)
            {
                return true;
            }

            Detach(wgc);
            LastError = "Windows.Graphics.Capture delivered no frame.";
            return false;
        }
        catch (OperationCanceledException)
        {
            Detach(wgc);
            State = WindowCaptureState.Stopped;
            throw;
        }
        finally
        {
            wgc.FrameReady -= OnFirst;
        }
    }

    /// <summary>Stops capturing; <see cref="Latest"/> keeps the last frame. Can be started again.</summary>
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            var b = _backend;
            if (b is not null)
            {
                Detach(b);
            }

            if (State != WindowCaptureState.Faulted)
            {
                State = WindowCaptureState.Stopped;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync();
        _disposed = true;
        _lifecycle.Dispose();
    }

    private void Attach(IWindowCapture backend)
    {
        backend.Processor = Process;
        backend.FrameReady += f => OnBackendFrame(backend, f);
        backend.Faulted += msg => OnBackendFaulted(backend, msg);
        _backend = backend;
    }

    private void Detach(IWindowCapture backend)
    {
        if (ReferenceEquals(_backend, backend))
        {
            _backend = null;
        }

        backend.Dispose();
    }

    private Frame? Process(in CapturedImage image)
    {
        var crop = CropRect;
        _clientSize = image.Metrics.ClientSize;
        _effectiveCrop = CaptureGeometry.EffectiveCrop(crop, image.Metrics.ClientSize);
        var region = image.RegionFor(crop);
        return region.IsEmpty ? null : image.ToFrame(region, 0, 0);
    }

    private void OnBackendFrame(IWindowCapture backend, Frame raw)
    {
        if (!ReferenceEquals(_backend, backend))
        {
            return;
        }

        Frame frame;
        TaskCompletionSource signal;
        lock (_frameLock)
        {
            frame = new Frame(raw.Width, raw.Height, raw.Bgra, ++_sequence, _clock.ElapsedMs);
            _latest = frame;
            signal = _frameSignal;
            _frameSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.TrySetResult();
        UpdateFps();
        FrameArrived?.Invoke(this, frame);
    }

    private void OnBackendFaulted(IWindowCapture backend, string message)
    {
        if (!ReferenceEquals(_backend, backend))
        {
            return;
        }

        _logger?.LogWarning("Window capture ({Method}) of {Hwnd:X} stopped: {Error}", backend.Method, WindowHandle, message);
        _backend = null;
        // Dispose off the capture thread (the backend may be inside its own callback).
        ThreadPool.QueueUserWorkItem(_ => backend.Dispose());
        Fault(message);
    }

    private void Fault(string message)
    {
        LastError = message;
        State = WindowCaptureState.Faulted;
        TaskCompletionSource signal;
        lock (_frameLock)
        {
            signal = _frameSignal;
            _frameSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.TrySetResult(); // wake waiters so they return promptly
    }

    private void UpdateFps()
    {
        var now = _clock.ElapsedMs;
        var count = Interlocked.Increment(ref _fpsCount);
        if (_fpsWindowStart == 0)
        {
            _fpsWindowStart = now;
            return;
        }

        var span = now - _fpsWindowStart;
        if (span >= 1000)
        {
            Fps = count * 1000.0 / span;
            Interlocked.Exchange(ref _fpsCount, 0);
            _fpsWindowStart = now;
        }
    }

    // ------------------------------------------------------------------ one-shot

    /// <summary>
    /// Captures a single frame of <paramref name="windowHandle"/> (for the binding wizard's thumbnail and crop selection).
    /// With <paramref name="crop"/> null the frame is the whole client area. Returns null on timeout or failure.
    /// </summary>
    public static async Task<Frame?> CaptureOnceAsync(nint windowHandle, RectI? crop = null,
        WindowCaptureMethod method = WindowCaptureMethod.Auto, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(3);
        if (method == WindowCaptureMethod.PrintWindow)
        {
            return await Task.Run(() => PrintWindowOnce(windowHandle, crop), ct);
        }

        var options = new WindowFrameSourceOptions
        {
            Method = method,
            CropRect = crop,
            WgcFirstFrameTimeout = TimeSpan.FromTicks(Math.Max(limit.Ticks / 2, TimeSpan.FromMilliseconds(200).Ticks)),
        };
        var source = new WindowFrameSource(windowHandle, options);
        await using (source)
        {
            await source.StartAsync(ct);
            if (source.State == WindowCaptureState.Faulted)
            {
                return null;
            }

            if (source.ActiveMethod == WindowCaptureMethod.PrintWindow)
            {
                await source.StopAsync();
                return source.Latest ?? await Task.Run(() => PrintWindowOnce(windowHandle, crop), ct);
            }

            return await source.WaitForFrameAsync(0, limit, ct);
        }
    }

    private static Frame? PrintWindowOnce(nint windowHandle, RectI? crop)
    {
        using var pw = new PrintWindowCapture(windowHandle);
        return pw.CaptureOnce((in CapturedImage image) =>
        {
            var region = image.RegionFor(crop);
            return region.IsEmpty ? null : image.ToFrame(region, 1, Environment.TickCount64);
        });
    }
}
