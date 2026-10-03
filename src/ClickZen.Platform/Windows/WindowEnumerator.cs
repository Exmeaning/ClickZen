using ClickZen.Core.Devices;
using ClickZen.Platform.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ClickZen.Platform.Windows;

/// <summary>A top-level desktop window that can be bound as an emulator frame source.</summary>
/// <param name="ProcessName">Executable name without ".exe" (e.g. "MuMuPlayer"); "" when it could not be read.</param>
public sealed record DesktopWindowInfo(nint Handle, string Title, string ClassName, uint ProcessId, string ProcessName = "");

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
        var names = new Dictionary<uint, string>();
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

            if (!names.TryGetValue(pid, out var processName))
            {
                processName = GetProcessName(pid);
                names[pid] = processName;
            }

            result.Add(new DesktopWindowInfo(hwnd, title, cls, pid, processName));
            return true;
        }

        WNDENUMPROC callback = (hwnd, _) => Visit(hwnd);
        PInvoke.EnumWindows(callback, default);
        GC.KeepAlive(callback);

        result.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    public static bool IsAlive(nint handle) => PInvoke.IsWindow(new HWND(handle));

    /// <summary>
    /// Executable name without directory and ".exe" for a process id; "" when access is denied or the process
    /// is gone. Uses QueryFullProcessImageName with PROCESS_QUERY_LIMITED_INFORMATION (works for most
    /// elevated processes too), falling back to <see cref="System.Diagnostics.Process"/>.
    /// </summary>
    public static unsafe string GetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return "";
        }

        var handle = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle != 0)
        {
            try
            {
                var buffer = stackalloc char[1024];
                uint size = 1024;
                if (Win32.QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0)
                {
                    return WindowMatchRule.NormalizeProcessName(new string(buffer, 0, (int)size));
                }
            }
            finally
            {
                _ = Win32.CloseHandle(handle);
            }
        }

        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)processId);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

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
