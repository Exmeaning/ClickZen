using ClickZen.Core.Automation;
using ClickZen.Core.Devices;
using ClickZen.Core.Geometry;
using ClickZen.Platform.Native;
using ClickZen.Platform.Windows;

namespace ClickZen.Platform.Capture;

/// <summary>
/// Fallback capture: <c>PrintWindow(hwnd, PW_RENDERFULLCONTENT)</c> into a reused top-down 32-bpp DIB section,
/// polled on a dedicated thread. Works for most windows including occluded ones, but is CPU-heavy and some
/// GPU-rendered surfaces come back black – then the other flag (0) is tried and whichever works is remembered.
/// The image covers GetWindowRect (including the invisible resize borders).
/// </summary>
public sealed class PrintWindowCapture : IWindowCapture
{
    private readonly Lock _gdiLock = new();
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private nint _memDc;
    private nint _bitmap;
    private nint _oldBitmap;
    private nint _bits;
    private SizeI _dibSize;
    private uint _flags = Win32.PW_RENDERFULLCONTENT;
    private bool _disposed;

    public PrintWindowCapture(nint hwnd, TimeSpan? interval = null)
    {
        WindowHandle = hwnd;
        Interval = interval ?? TimeSpan.FromMilliseconds(66);
    }

    public WindowCaptureMethod Method => WindowCaptureMethod.PrintWindow;

    public nint WindowHandle { get; }

    /// <summary>Polling period; default ≈ 15 fps.</summary>
    public TimeSpan Interval { get; set; }

    public CapturedImageProcessor? Processor { get; set; }

    public event Action<Frame>? FrameReady;

    public event Action<string>? Faulted;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null)
        {
            return;
        }

        if (!Win32.IsWindow(WindowHandle))
        {
            throw new InvalidOperationException("The window no longer exists.");
        }

        _thread = new Thread(Loop) { IsBackground = true, Name = "ClickZen PrintWindow capture" };
        _thread.Start();
    }

    /// <summary>Grabs one image synchronously and runs it through <paramref name="processor"/> (default: whole client area).</summary>
    public Frame? CaptureOnce(CapturedImageProcessor? processor = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var dpi = DpiScope.PerMonitorV2();
        return Grab(processor ?? Processor ?? CaptureDefaults.WholeClientArea, out _);
    }

    private void Loop()
    {
        using var dpi = DpiScope.PerMonitorV2();
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            Frame? frame;
            string? fault;
            try
            {
                frame = Grab(Processor ?? CaptureDefaults.WholeClientArea, out fault);
            }
            catch (Exception ex)
            {
                frame = null;
                fault = ex.Message;
            }

            if (fault is not null)
            {
                Faulted?.Invoke(fault);
                return;
            }

            if (frame is not null)
            {
                FrameReady?.Invoke(frame);
            }

            var wait = Interval.TotalMilliseconds - (Environment.TickCount64 - started);
            if (ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Max(1, wait))))
            {
                return;
            }
        }
    }

    /// <param name="fault">Set when the window is gone (permanent failure).</param>
    private unsafe Frame? Grab(CapturedImageProcessor processor, out string? fault)
    {
        fault = null;
        if (!WindowMetrics.TryQuery(WindowHandle, out var metrics))
        {
            fault = "The window was closed.";
            return null;
        }

        if (metrics.IsMinimized || metrics.WindowRect.IsEmpty)
        {
            return null; // nothing to render while minimised; keep polling
        }

        lock (_gdiLock)
        {
            if (_disposed || !EnsureDib(metrics.WindowRect.Size))
            {
                return null;
            }

            var size = _dibSize;
            var span = new ReadOnlySpan<byte>((void*)_bits, size.Width * size.Height * 4);
            if (!Render(_flags))
            {
                return null;
            }

            if (BgraBuffer.IsLikelyBlack(span, size.Width, size.Height))
            {
                var other = _flags == Win32.PW_RENDERFULLCONTENT ? 0u : Win32.PW_RENDERFULLCONTENT;
                if (Render(other) && !BgraBuffer.IsLikelyBlack(span, size.Width, size.Height))
                {
                    _flags = other;
                }
                else
                {
                    _ = Render(_flags);
                }
            }

            var image = new CapturedImage(span, size.Width * 4, size, metrics.WindowRect, metrics, alphaIsUndefined: true);
            return processor(image);
        }
    }

    private bool Render(uint flags)
    {
        var ok = Win32.PrintWindow(WindowHandle, _memDc, flags);
        _ = Win32.GdiFlush();
        return ok;
    }

    private bool EnsureDib(SizeI size)
    {
        if (_bitmap != 0 && size == _dibSize)
        {
            return true;
        }

        FreeDib();
        var screen = Win32.GetDC(0);
        try
        {
            _memDc = Win32.CreateCompatibleDC(screen);
            var header = new Win32.BITMAPINFOHEADER
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                biWidth = size.Width,
                biHeight = -size.Height, // top-down
                biPlanes = 1,
                biBitCount = 32,
            };
            _bitmap = Win32.CreateDIBSection(screen, header, Win32.DIB_RGB_COLORS, out _bits, 0, 0);
        }
        finally
        {
            _ = Win32.ReleaseDC(0, screen);
        }

        if (_memDc == 0 || _bitmap == 0 || _bits == 0)
        {
            FreeDib();
            return false;
        }

        _oldBitmap = Win32.SelectObject(_memDc, _bitmap);
        _dibSize = size;
        return true;
    }

    private void FreeDib()
    {
        if (_memDc != 0 && _oldBitmap != 0)
        {
            _ = Win32.SelectObject(_memDc, _oldBitmap);
        }

        if (_bitmap != 0)
        {
            _ = Win32.DeleteObject(_bitmap);
        }

        if (_memDc != 0)
        {
            _ = Win32.DeleteDC(_memDc);
        }

        _memDc = _bitmap = _oldBitmap = _bits = 0;
        _dibSize = default;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _stop.Cancel();
        if (_thread is not null && _thread != Thread.CurrentThread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        lock (_gdiLock)
        {
            _disposed = true;
            FreeDib();
        }

        _stop.Dispose();
    }
}
