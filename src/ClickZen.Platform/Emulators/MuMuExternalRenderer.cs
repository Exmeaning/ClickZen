using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ClickZen.Core.Geometry;

namespace ClickZen.Platform.Emulators;

/// <summary>
/// Optional bridge to MuMu 12's official <c>external_renderer_ipc.dll</c> SDK (modeled on MaaFramework's
/// <c>MuMuPlayerExtras</c> and MAA's <c>MumuExtras</c>). Resolves the active virtual display id in multi-tab /
/// <c>app_keptlive</c> mode and can capture secondary virtual displays directly when <c>screencap</c> only sees display 0.
/// </summary>
public static class MuMuExternalRenderer
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NemuConnectDelegate([MarshalAs(UnmanagedType.LPWStr)] string folder, int index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NemuDisconnectDelegate(int handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NemuGetDisplayIdDelegate(int handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string pkg, int appIndex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NemuCaptureDisplayDelegate(int handle, uint displayId, int bufferSize, ref int width, ref int height, nint pixels);

    private sealed record SdkApi(
        NemuConnectDelegate Connect,
        NemuDisconnectDelegate Disconnect,
        NemuGetDisplayIdDelegate? GetDisplayId,
        NemuCaptureDisplayDelegate CaptureDisplay);

    private static readonly ConcurrentDictionary<string, SdkApi?> LoadedSdks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Locates <c>external_renderer_ipc.dll</c> inside a MuMu installation root, checking MuMu 6.x (<c>nx_device\15.0</c>),
    /// MuMu 5.x (<c>nx_device\12.0</c> or newer engines), <c>nx_main\sdk</c>, and MuMu 4.x (<c>shell\sdk</c>).
    /// </summary>
    public static string? FindSdkDll(
        string? installDir,
        Func<string, IReadOnlyList<string>>? getDirectories = null,
        Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return null;
        }

        var exists = fileExists ?? File.Exists;
        var listDirs = getDirectories ?? SafeGetDirectories;
        var root = installDir.Trim().TrimEnd('\\', '/');

        // Explicit engine order matching MAA (15.0 -> 12.0), plus any future nx_device\<ver>\shell\sdk\ directory.
        var candidates = new List<string>
        {
            Path.Combine(root, "nx_device", "15.0", "shell", "sdk", "external_renderer_ipc.dll"),
            Path.Combine(root, "nx_device", "12.0", "shell", "sdk", "external_renderer_ipc.dll"),
        };

        var nxDevice = Path.Combine(root, "nx_device");
        foreach (var dir in listDirs(nxDevice).OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var dynamicDll = Path.Combine(dir, "shell", "sdk", "external_renderer_ipc.dll");
            if (!candidates.Contains(dynamicDll, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(dynamicDll);
            }
        }

        candidates.Add(Path.Combine(root, "nx_main", "sdk", "external_renderer_ipc.dll"));
        candidates.Add(Path.Combine(root, "shell", "sdk", "external_renderer_ipc.dll"));

        return candidates.FirstOrDefault(exists);
    }

    /// <summary>
    /// Queries MuMu's host IPC for the active virtual display id of instance <paramref name="instanceIndex"/>.
    /// Checks <paramref name="foregroundPackage"/> first when given, then MuMu's <c>"default"</c> sentinel (the
    /// currently selected tab in MuMu's UI). Returns null if the SDK DLL is unavailable or the call fails.
    /// </summary>
    public static int? TryGetActiveDisplayId(string? installDir, int instanceIndex, string? foregroundPackage = null)
    {
        if (string.IsNullOrWhiteSpace(installDir) || instanceIndex < 0)
        {
            return null;
        }

        var api = LoadApi(installDir);
        if (api?.GetDisplayId is null)
        {
            return null;
        }

        var root = installDir.Trim().TrimEnd('\\', '/');
        var handle = 0;
        try
        {
            handle = api.Connect(root, instanceIndex);
            if (handle <= 0)
            {
                return null;
            }

            // "default" returns the frontmost tab currently selected in MuMu's UI (per MAA MumuExtras.cpp).
            var front = api.GetDisplayId(handle, "default", 0);
            if (front > 0)
            {
                return front;
            }

            if (!string.IsNullOrWhiteSpace(foregroundPackage))
            {
                var pkgId = api.GetDisplayId(handle, foregroundPackage, 0);
                if (pkgId >= 0)
                {
                    return pkgId;
                }
            }

            return front == 0 ? 0 : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            if (handle > 0)
            {
                try
                {
                    api.Disconnect(handle);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                }
            }
        }
    }

    /// <summary>
    /// Captures <paramref name="displayId"/> of a local MuMu instance directly as top-down BGRA pixels via
    /// <c>external_renderer_ipc.dll</c>, or returns null if unavailable.
    /// </summary>
    public static (SizeI Size, byte[] Bgra)? TryCaptureDisplayBgra(string? installDir, int instanceIndex, int displayId)
    {
        if (string.IsNullOrWhiteSpace(installDir) || instanceIndex < 0 || displayId < 0)
        {
            return null;
        }

        var api = LoadApi(installDir);
        if (api is null)
        {
            return null;
        }

        var root = installDir.Trim().TrimEnd('\\', '/');
        var handle = 0;
        try
        {
            handle = api.Connect(root, instanceIndex);
            if (handle <= 0)
            {
                return null;
            }

            int width = 0, height = 0;
            if (api.CaptureDisplay(handle, (uint)displayId, 0, ref width, ref height, 0) != 0 || width <= 0 || height <= 0 || width > 8192 || height > 8192)
            {
                return null;
            }

            var byteCount = width * height * 4;
            var raw = new byte[byteCount];
            var pinned = GCHandle.Alloc(raw, GCHandleType.Pinned);
            int rc;
            try
            {
                rc = api.CaptureDisplay(handle, (uint)displayId, byteCount, ref width, ref height, pinned.AddrOfPinnedObject());
            }
            finally
            {
                pinned.Free();
            }

            if (rc != 0)
            {
                return null;
            }

            return (new SizeI(width, height), ConvertBottomUpRgbaToTopDownBgra(raw, width, height));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            if (handle > 0)
            {
                try
                {
                    api.Disconnect(handle);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                }
            }
        }
    }

    /// <summary>
    /// MuMu's <c>nemu_capture_display</c> writes OpenGL-style bottom-up RGBA; ClickZen frames are top-down BGRA.
    /// Pure helper (tested directly).
    /// </summary>
    internal static byte[] ConvertBottomUpRgbaToTopDownBgra(byte[] rgbaBottomUp, int width, int height)
    {
        var stride = width * 4;
        var bgra = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var srcRow = (height - 1 - y) * stride;
            var dstRow = y * stride;
            for (var x = 0; x < stride; x += 4)
            {
                bgra[dstRow + x] = rgbaBottomUp[srcRow + x + 2];     // B
                bgra[dstRow + x + 1] = rgbaBottomUp[srcRow + x + 1]; // G
                bgra[dstRow + x + 2] = rgbaBottomUp[srcRow + x];     // R
                bgra[dstRow + x + 3] = rgbaBottomUp[srcRow + x + 3]; // A
            }
        }

        return bgra;
    }

    private static SdkApi? LoadApi(string installDir)
    {
        var dll = FindSdkDll(installDir);
        return dll is null ? null : LoadedSdks.GetOrAdd(dll, static path =>
        {
            try
            {
                if (!NativeLibrary.TryLoad(path, out var lib))
                {
                    return null;
                }

                if (!NativeLibrary.TryGetExport(lib, "nemu_connect", out var pConnect)
                    || !NativeLibrary.TryGetExport(lib, "nemu_disconnect", out var pDisconnect)
                    || !NativeLibrary.TryGetExport(lib, "nemu_capture_display", out var pCapture))
                {
                    return null;
                }

                var getDisplayId = NativeLibrary.TryGetExport(lib, "nemu_get_display_id", out var pGetDisplay)
                    ? Marshal.GetDelegateForFunctionPointer<NemuGetDisplayIdDelegate>(pGetDisplay)
                    : null;

                return new SdkApi(
                    Marshal.GetDelegateForFunctionPointer<NemuConnectDelegate>(pConnect),
                    Marshal.GetDelegateForFunctionPointer<NemuDisconnectDelegate>(pDisconnect),
                    getDisplayId,
                    Marshal.GetDelegateForFunctionPointer<NemuCaptureDisplayDelegate>(pCapture));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return null;
            }
        });
    }

    private static IReadOnlyList<string> SafeGetDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}
