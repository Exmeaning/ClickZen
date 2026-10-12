using ClickZen.Core.Devices;
using ClickZen.Platform.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ClickZen.Platform.Emulators;

/// <summary>A process as seen by emulator detection.</summary>
/// <param name="Name">Executable name without ".exe".</param>
/// <param name="Path">Full path of the executable; "" when it could not be read.</param>
/// <param name="CommandLine">Full command line; null when it could not be read (e.g. a process running elevated).</param>
public sealed record ProcessInfo(int Id, string Name, string Path, string? CommandLine);

/// <summary>Process queries used by emulator detection.</summary>
public static class ProcessQuery
{
    /// <summary>
    /// Running processes whose executable name satisfies <paramref name="filter"/>, with full path and command line.
    /// Only filtered processes are opened, so this stays cheap however many processes run.
    /// </summary>
    public static IReadOnlyList<ProcessInfo> Find(Func<string, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var result = new List<ProcessInfo>();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            using (p)
            {
                string name;
                int id;
                try
                {
                    name = p.ProcessName;
                    id = p.Id;
                }
                catch (InvalidOperationException)
                {
                    continue; // exited meanwhile
                }

                if (!filter(name))
                {
                    continue;
                }

                result.Add(new ProcessInfo(id, name, GetImagePath((uint)id) ?? "", GetCommandLine((uint)id)));
            }
        }

        return result;
    }

    /// <summary>Full path of a process executable, or null (access denied / gone).</summary>
    public static unsafe string? GetImagePath(uint processId)
    {
        var handle = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var buffer = stackalloc char[1024];
            uint size = 1024;
            return Win32.QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0 ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            _ = Win32.CloseHandle(handle);
        }
    }

    /// <summary>
    /// The command line of a process via NtQueryInformationProcess(ProcessCommandLineInformation), which needs only
    /// PROCESS_QUERY_LIMITED_INFORMATION (no PEB reading, works across bitness). Null when it cannot be read.
    /// </summary>
    public static unsafe string? GetCommandLine(uint processId)
    {
        var handle = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            uint length = 1024;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = new byte[Math.Max(length, (uint)sizeof(Win32.UNICODE_STRING))];
                fixed (byte* p = buffer)
                {
                    var status = Win32.NtQueryInformationProcess(handle, Win32.ProcessCommandLineInformation, p, (uint)buffer.Length, out var needed);
                    if (status is Win32.StatusInfoLengthMismatch or Win32.StatusBufferTooSmall or Win32.StatusBufferOverflow
                        && needed > buffer.Length && needed < 1 << 20)
                    {
                        length = needed;
                        continue;
                    }

                    if (status < 0)
                    {
                        return null;
                    }

                    var us = (Win32.UNICODE_STRING*)p;
                    if (us->Buffer == 0 || us->Length == 0)
                    {
                        return "";
                    }

                    // The string lives inside our buffer, right after the header.
                    var start = (byte*)us->Buffer;
                    if (start < p || start + us->Length > p + buffer.Length)
                    {
                        return null;
                    }

                    return new string((char*)start, 0, us->Length / 2);
                }
            }

            return null;
        }
        finally
        {
            _ = Win32.CloseHandle(handle);
        }
    }

    /// <summary>Executable name without directory and ".exe".</summary>
    public static string NameOf(string path) => WindowMatchRule.NormalizeProcessName(path);
}

/// <summary>A child window of an emulator's main window.</summary>
/// <param name="Rect">The child's rectangle in the parent's client coordinates (physical pixels).</param>
public sealed record ChildWindowInfo(nint Handle, string ClassName, string Title, Core.Geometry.RectI Rect, bool Visible);

/// <summary>Child window enumeration (render windows of emulators).</summary>
public static class ChildWindows
{
    /// <summary>All descendants of <paramref name="parent"/> with class, title and rectangle in the parent's client area.</summary>
    public static IReadOnlyList<ChildWindowInfo> Enumerate(nint parent, int max = 256)
    {
        var result = new List<ChildWindowInfo>();
        if (parent == 0 || !Win32.IsWindow(parent))
        {
            return result;
        }

        using var dpi = DpiScope.PerMonitorV2();
        var origin = new Win32.POINT();
        if (!Win32.ClientToScreen(parent, ref origin))
        {
            return result;
        }

        bool Visit(HWND hwnd)
        {
            var h = (nint)hwnd;
            var topLeft = new Win32.POINT();
            if (Win32.GetClientRect(h, out var rc) && Win32.ClientToScreen(h, ref topLeft))
            {
                // Child client area in screen pixels → parent client coordinates.
                var rect = new Core.Geometry.RectI(topLeft.X - origin.X, topLeft.Y - origin.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
                result.Add(new ChildWindowInfo(h, ClassOf(hwnd), TitleOf(hwnd), rect, PInvoke.IsWindowVisible(hwnd)));
            }

            return result.Count < max;
        }

        WNDENUMPROC callback = (hwnd, _) => Visit(hwnd);
        _ = PInvoke.EnumChildWindows(new HWND(parent), callback, default);
        GC.KeepAlive(callback);
        return result;
    }

    private static unsafe string ClassOf(HWND hwnd)
    {
        var buffer = stackalloc char[256];
        var n = PInvoke.GetClassName(hwnd, buffer, 256);
        return n > 0 ? new string(buffer, 0, n) : "";
    }

    private static unsafe string TitleOf(HWND hwnd)
    {
        var buffer = stackalloc char[256];
        var n = PInvoke.GetWindowText(hwnd, buffer, 256);
        return n > 0 ? new string(buffer, 0, n) : "";
    }
}
