using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ClickZen.Platform.Windows;

/// <summary>A top-level desktop window that can be bound as an emulator frame source.</summary>
public sealed record DesktopWindowInfo(nint Handle, string Title, string ClassName, uint ProcessId);

/// <summary>Enumerates user-visible top-level windows, skipping cloaked/minimized/tool windows.</summary>
public static class WindowEnumerator
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "WorkerW", "Windows.UI.Core.CoreWindow",
    };

    public static IReadOnlyList<DesktopWindowInfo> GetVisibleWindows(uint excludeProcessId = 0)
    {
        var result = new List<DesktopWindowInfo>();
        var shell = PInvoke.GetShellWindow();

        bool Visit(HWND hwnd)
        {
            if (hwnd == shell || !PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd))
            {
                return true;
            }

            var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            if ((exStyle & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0 || IsCloaked(hwnd))
            {
                return true;
            }

            var title = GetTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            var cls = GetClassName(hwnd);
            if (IgnoredClasses.Contains(cls))
            {
                return true;
            }

            _ = PInvoke.GetWindowThreadProcessId(hwnd, out var pid);
            if (excludeProcessId != 0 && pid == excludeProcessId)
            {
                return true;
            }

            result.Add(new DesktopWindowInfo(hwnd, title, cls, pid));
            return true;
        }

        WNDENUMPROC callback = (hwnd, _) => Visit(hwnd);
        PInvoke.EnumWindows(callback, default);
        GC.KeepAlive(callback);

        result.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    public static bool IsAlive(nint handle) => PInvoke.IsWindow(new HWND(handle));

    private static unsafe bool IsCloaked(HWND hwnd)
    {
        int cloaked = 0;
        var hr = PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int));
        return hr.Succeeded && cloaked != 0;
    }

    private static unsafe string GetTitle(HWND hwnd)
    {
        var len = PInvoke.GetWindowTextLength(hwnd);
        if (len <= 0)
        {
            return "";
        }

        var buffer = new char[len + 1];
        fixed (char* p = buffer)
        {
            var n = PInvoke.GetWindowText(hwnd, p, buffer.Length);
            return new string(p, 0, n);
        }
    }

    private static unsafe string GetClassName(HWND hwnd)
    {
        var buffer = new char[256];
        fixed (char* p = buffer)
        {
            var n = PInvoke.GetClassName(hwnd, p, buffer.Length);
            return new string(p, 0, n);
        }
    }
}
