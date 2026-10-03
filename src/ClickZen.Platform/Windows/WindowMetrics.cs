using ClickZen.Core.Geometry;
using ClickZen.Platform.Native;

namespace ClickZen.Platform.Windows;

/// <summary>
/// Snapshot of a window's geometry in physical screen pixels.
/// </summary>
/// <param name="WindowRect">GetWindowRect – includes the invisible resize borders on Windows 10/11.</param>
/// <param name="ExtendedFrameBounds">DWMWA_EXTENDED_FRAME_BOUNDS – the visible window; what WGC captures.</param>
/// <param name="ClientOrigin">ClientToScreen(0, 0).</param>
/// <param name="ClientSize">GetClientRect size.</param>
/// <param name="IsMinimized">IsIconic.</param>
public readonly record struct WindowMetrics(RectI WindowRect, RectI ExtendedFrameBounds, PointI ClientOrigin, SizeI ClientSize, bool IsMinimized)
{
    /// <summary>Client area in screen coordinates.</summary>
    public RectI ClientRectScreen => new(ClientOrigin.X, ClientOrigin.Y, ClientSize.Width, ClientSize.Height);

    /// <summary>
    /// Queries the metrics with the calling thread temporarily switched to Per-Monitor V2 awareness, so every
    /// value is in physical pixels even when the window sits on a scaled monitor. False if the window is gone.
    /// </summary>
    public static bool TryQuery(nint hwnd, out WindowMetrics metrics)
    {
        metrics = default;
        if (hwnd == 0 || !Win32.IsWindow(hwnd))
        {
            return false;
        }

        using var dpi = DpiScope.PerMonitorV2();
        if (!Win32.GetWindowRect(hwnd, out var wr) || !Win32.GetClientRect(hwnd, out var cr))
        {
            return false;
        }

        var window = ToRect(wr);
        var frame = Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out var fb, 16) >= 0
            ? ToRect(fb)
            : window;
        if (frame.IsEmpty)
        {
            frame = window;
        }

        var origin = new Win32.POINT();
        if (!Win32.ClientToScreen(hwnd, ref origin))
        {
            return false;
        }

        metrics = new WindowMetrics(window, frame, new PointI(origin.X, origin.Y),
            new SizeI(cr.Right - cr.Left, cr.Bottom - cr.Top), Win32.IsIconic(hwnd));
        return true;
    }

    private static RectI ToRect(Win32.RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}
