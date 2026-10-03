using ClickZen.Core.Automation;
using ClickZen.Core.Devices;
using ClickZen.Core.Geometry;
using ClickZen.Platform.Native;
using ClickZen.Platform.Windows;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace ClickZen.Platform.Capture;

/// <summary>
/// Primary capture: Windows.Graphics.Capture on a window handle. Works for occluded/background windows and
/// GPU-rendered content. Frames arrive on a free-threaded frame pool, are copied into a CPU-readable staging
/// texture and read back as BGRA (honouring RowPitch). The capture image covers the window's DWM extended
/// frame bounds (title bar and borders included); the <see cref="Processor"/> cuts out the client crop.
/// </summary>
public sealed class WgcWindowCapture : IWindowCapture
{
    private static readonly TimeSpan WatchdogPeriod = TimeSpan.FromMilliseconds(500);
    private readonly Lock _gpuLock = new();
    private Timer? _watchdog;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _staging;
    private SizeInt32 _poolSize;
    private int _faulted;
    private long _framesReceived;
    private bool _disposed;

    public WgcWindowCapture(nint hwnd)
    {
        WindowHandle = hwnd;
    }

    /// <summary>True when the OS supports Windows.Graphics.Capture (Windows 10 1903+ with a usable GPU stack).</summary>
    public static bool IsSupported
    {
        get
        {
            try
            {
                return GraphicsCaptureSession.IsSupported();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public WindowCaptureMethod Method => WindowCaptureMethod.Wgc;

    public nint WindowHandle { get; }

    /// <summary>Try to hide the yellow capture border (Windows 11). Must be set before <see cref="Start"/>.</summary>
    public bool HideBorder { get; init; } = true;

    /// <summary>Include the mouse cursor in the capture (off by default). Must be set before <see cref="Start"/>.</summary>
    public bool CaptureCursor { get; init; }

    /// <summary>Whether the border was actually hidden (false on Windows 10 or when the API refused).</summary>
    public bool BorderHidden { get; private set; }

    /// <summary>Last exception thrown while reading back a frame (diagnostics; transient errors do not stop capture).</summary>
    public string? LastFrameError { get; private set; }

    /// <summary>Number of frames the pool delivered (including dropped ones).</summary>
    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    public CapturedImageProcessor? Processor { get; set; }

    public event Action<Frame>? FrameReady;

    public event Action<string>? Faulted;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is not null)
        {
            return;
        }

        if (!IsSupported)
        {
            throw new NotSupportedException("Windows.Graphics.Capture is not supported on this system.");
        }

        if (!Win32.IsWindow(WindowHandle))
        {
            throw new InvalidOperationException("The window no longer exists.");
        }

        try
        {
            CreateDevice();
            _item = WinRtCaptureInterop.CreateItemForWindow(WindowHandle);
            _item.Closed += OnItemClosed;
            _poolSize = ClampSize(_item.Size);
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            _pool.FrameArrived += OnFrameArrived;
            _session = _pool.CreateCaptureSession(_item);
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                try
                {
                    _session.IsCursorCaptureEnabled = CaptureCursor;
                }
                catch (Exception)
                {
                    // Property missing on this build – the cursor is then always captured.
                }
            }

            if (HideBorder)
            {
                BorderHidden = WinRtCaptureInterop.TrySetBorderRequired(_session, false);
            }

            _session.StartCapture();

            // GraphicsCaptureItem.Closed is not reliably raised without a DispatcherQueue on the creating
            // thread, and a static window produces no frames: poll IsWindow so closing is always noticed.
            _watchdog = new Timer(_ =>
            {
                if (!Win32.IsWindow(WindowHandle))
                {
                    RaiseFault("The window was closed.");
                }
            }, null, WatchdogPeriod, WatchdogPeriod);
        }
        catch
        {
            ReleaseAll();
            throw;
        }
    }

    private void CreateDevice()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        var result = D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
            out ID3D11Device device, out ID3D11DeviceContext context);
        if (result.Failure)
        {
            // No usable GPU (RDP sessions, basic display driver): WARP still works with WGC.
            D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels,
                out device, out context).CheckError();
        }

        _device = device;
        _context = context;
        using (var mt = device.QueryInterfaceOrNull<ID3D11Multithread>())
        {
            mt?.SetMultithreadProtected(true);
        }

        using var dxgi = device.QueryInterface<IDXGIDevice>();
        _winrtDevice = WinRtCaptureInterop.CreateDirect3DDevice(dxgi);
    }

    private static SizeInt32 ClampSize(SizeInt32 s) => new() { Width = Math.Max(1, s.Width), Height = Math.Max(1, s.Height) };

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => RaiseFault("The window was closed.");

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Frame? output = null;
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            Interlocked.Increment(ref _framesReceived);
            lock (_gpuLock)
            {
                if (_disposed || _pool is null)
                {
                    return;
                }

                var content = frame.ContentSize;
                if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
                {
                    // Window resized: rebuild buffers at the new size; this frame is still valid (top-left part).
                    _poolSize = ClampSize(content);
                    _pool.Recreate(_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
                }

                if (!WindowMetrics.TryQuery(WindowHandle, out var metrics))
                {
                    RaiseFaultLater("The window was closed.");
                    return;
                }

                if (metrics.IsMinimized)
                {
                    return;
                }

                output = ReadBack(frame, content, metrics);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LastFrameError = ex.ToString();
            if (!Win32.IsWindow(WindowHandle))
            {
                RaiseFault("The window was closed.");
            }
            else if (_device?.DeviceRemovedReason.Failure == true)
            {
                RaiseFault("The graphics device was lost: " + ex.Message);
            }

            return;
        }

        if (output is not null)
        {
            FrameReady?.Invoke(output);
        }
    }

    private unsafe Frame? ReadBack(Direct3D11CaptureFrame frame, SizeInt32 content, WindowMetrics metrics)
    {
        using var texture = WinRtCaptureInterop.GetTexture(frame.Surface);
        var desc = texture.Description;
        if (_staging is null || _staging.Description.Width != desc.Width || _staging.Description.Height != desc.Height)
        {
            _staging?.Dispose();
            _staging = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });
        }

        _context!.CopyResource(_staging, texture);
        var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var size = new SizeI(Math.Min(content.Width, (int)desc.Width), Math.Min(content.Height, (int)desc.Height));
            var pitch = (int)mapped.RowPitch;
            var span = new ReadOnlySpan<byte>((void*)mapped.DataPointer, pitch * (int)desc.Height);

            // The capture image is anchored at the DWM extended frame bounds (what WGC renders for a window).
            var bounds = metrics.ExtendedFrameBounds;
            // Translucent (Mica/acrylic) windows come back with alpha < 255; consumers expect opaque pixels.
            var image = new CapturedImage(span, pitch, size, bounds, metrics, alphaIsUndefined: true);
            return (Processor ?? CaptureDefaults.WholeClientArea)(image);
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }

    private void RaiseFault(string message)
    {
        if (Interlocked.Exchange(ref _faulted, 1) == 0)
        {
            Faulted?.Invoke(message);
        }
    }

    // Called while holding the GPU lock: hop off so handlers may dispose us.
    private void RaiseFaultLater(string message) => ThreadPool.QueueUserWorkItem(_ => RaiseFault(message));

    private void ReleaseAll()
    {
        ReleaseCapture();
        ReleaseGpu();
    }

    private void ReleaseCapture()
    {
        _watchdog?.Dispose();
        _watchdog = null;
        var pool = _pool;
        var session = _session;
        var item = _item;
        _pool = null;
        _session = null;
        _item = null;
        if (pool is not null)
        {
            pool.FrameArrived -= OnFrameArrived;
        }

        if (item is not null)
        {
            item.Closed -= OnItemClosed;
        }

        session?.Dispose();
        pool?.Dispose();
    }

    private void ReleaseGpu()
    {
        (_winrtDevice as IDisposable)?.Dispose();
        _winrtDevice = null;
        _staging?.Dispose();
        _staging = null;
        _context?.ClearState();
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        lock (_gpuLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true; // in-flight callbacks bail out from here on
        }

        // Close the session/pool outside the lock so a callback waiting for it cannot deadlock with us.
        ReleaseCapture();
        lock (_gpuLock)
        {
            ReleaseGpu();
        }
    }
}
