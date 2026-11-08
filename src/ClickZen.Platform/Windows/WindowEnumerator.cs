using ClickZen.Core.Devices;
using ClickZen.Core.Emulators;
using ClickZen.Platform.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ClickZen.Platform.Windows;

/// <summary>A top-level desktop window that can be bound as an emulator frame source.</summary>
/// <param name="ProcessName">Executable name without ".exe" (e.g. "MuMuPlayer"); "" when it could not be read.</param>
/// <param name="IsMinimized">The window is minimized (only reported by <see cref="WindowEnumerator.GetTopLevelWindows"/>).</param>
public sealed record DesktopWindowInfo(nint Handle, string Title, string ClassName, uint ProcessId, string ProcessName = "", bool IsMinimized = false);

/// <summary>Enumerates user-visible top-level windows, skipping cloaked/minimized/tool windows.</summary>
public static class WindowEnumerator
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "WorkerW", "Windows.UI.Core.CoreWindow",
        "IME", "Default IME", "MSCTFIME UI", "NVOpenGLPbuffer",
    };

    private static readonly HashSet<string> AuxiliaryTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "HintWnd", "MessageWnd", "__wglDummyWindowFodder",
    };

    /// <summary>True when a window title/class is an invisible/helper popup owned by an emulator process (e.g. MuMu Qt helpers).</summary>
    public static bool IsAuxiliaryEmulatorWindow(string? title, string? className) =>
        (!string.IsNullOrEmpty(title) && AuxiliaryTitles.Contains(title.Trim()))
        || (!string.IsNullOrEmpty(className) && (IgnoredClasses.Contains(className) || className.EndsWith("ToolSaveBits", StringComparison.Ordinal)));

    /// <summary>Visible, non-minimized top-level windows with a title (what can be captured right now).</summary>
    public static IReadOnlyList<DesktopWindowInfo> GetVisibleWindows(uint excludeProcessId = 0) => Enumerate(excludeProcessId, includeMinimized: false);

    /// <summary>Like <see cref="GetVisibleWindows"/> but also minimized windows (flagged with <see cref="DesktopWindowInfo.IsMinimized"/>).</summary>
    public static IReadOnlyList<DesktopWindowInfo> GetTopLevelWindows(uint excludeProcessId = 0) => Enumerate(excludeProcessId, includeMinimized: true);

    private static List<DesktopWindowInfo> Enumerate(uint excludeProcessId, bool includeMinimized)
    {
        var result = new List<DesktopWindowInfo>();
        var names = new Dictionary<uint, string>();
        var shell = PInvoke.GetShellWindow();

        bool Visit(HWND hwnd)
        {
            if (hwnd == shell || !PInvoke.IsWindowVisible(hwnd) || IsCloaked(hwnd))
            {
                return true;
            }

            var minimized = PInvoke.IsIconic(hwnd);
            if (minimized && !includeMinimized)
            {
                return true;
            }

            var cls = GetClassName(hwnd);
            if (IgnoredClasses.Contains(cls) || cls.EndsWith("ToolSaveBits", StringComparison.Ordinal))
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

            var isEmulatorPlayer = !string.IsNullOrEmpty(processName) && EmulatorCatalog.All.Any(d => d.IsPlayerProcess(processName));

            var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            if ((exStyle & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0
                && !(isEmulatorPlayer && (minimized || HasSubstantialClientArea(hwnd))))
            {
                return true;
            }

            var title = GetTitle(hwnd);
            if (IsAuxiliaryEmulatorWindow(title, cls))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                if (!isEmulatorPlayer || (!minimized && !HasSubstantialClientArea(hwnd)))
                {
                    return true;
                }

                title = processName;
            }

            result.Add(new DesktopWindowInfo(hwnd, title, cls, pid, processName, minimized));
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

    private static bool HasSubstantialClientArea(HWND hwnd) =>
        PInvoke.GetClientRect(hwnd, out var rc) && (rc.right - rc.left) >= 200 && (rc.bottom - rc.top) >= 200;

    private static unsafe bool IsCloaked(HWND hwnd)
    {
        int cloaked = 0;
        var hr = PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int));
        return hr.Succeeded && cloaked != 0;
    }

    private static unsafe string GetTitle(HWND hwnd)
    {
        char* fast = stackalloc char[512];
        var n = PInvoke.GetWindowText(hwnd, fast, 512);
        if (n <= 0)
        {
            return "";
        }

        if (n < 511)
        {
            return new string(fast, 0, n);
        }

        var len = PInvoke.GetWindowTextLength(hwnd);
        if (len <= n)
        {
            return new string(fast, 0, n);
        }

        var buffer = new char[len + 1];
        fixed (char* p = buffer)
        {
            var full = PInvoke.GetWindowText(hwnd, p, buffer.Length);
            return full > 0 ? new string(p, 0, full) : new string(fast, 0, n);
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
