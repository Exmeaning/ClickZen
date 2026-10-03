using System.Runtime.InteropServices;
using ClickZen.Platform.Native;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace ClickZen.Platform.Capture;

/// <summary>
/// The COM glue Windows.Graphics.Capture needs from a desktop app, done with raw vtable calls so it does not
/// depend on built-in COM interop or newer SDK projections:
/// <list type="bullet">
/// <item>IGraphicsCaptureItemInterop.CreateForWindow (HWND → GraphicsCaptureItem)</item>
/// <item>IDirect3DDxgiInterfaceAccess.GetInterface (IDirect3DSurface → ID3D11Texture2D)</item>
/// <item>IGraphicsCaptureSession3.put_IsBorderRequired (Windows 11+, absent from the 19041 projection)</item>
/// </list>
/// </summary>
internal static unsafe class WinRtCaptureInterop
{
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IidGraphicsCaptureSession3 = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");

    public static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        nint hstring;
        fixed (char* p = className)
        {
            Marshal.ThrowExceptionForHR(Win32.WindowsCreateString(p, (uint)className.Length, out hstring));
        }

        nint factory = 0;
        nint itemPtr = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Win32.RoGetActivationFactory(hstring, IidGraphicsCaptureItemInterop, out factory));

            // IUnknown (3 slots), then CreateForWindow(HWND, REFIID, void**), CreateForMonitor.
            var vtbl = *(void***)factory;
            var createForWindow = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtbl[3];
            var iid = IidGraphicsCaptureItem;
            var hr = createForWindow(factory, hwnd, &iid, &itemPtr);
            Marshal.ThrowExceptionForHR(hr);
            return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
        }
        finally
        {
            if (itemPtr != 0)
            {
                Marshal.Release(itemPtr);
            }

            if (factory != 0)
            {
                Marshal.Release(factory);
            }

            _ = Win32.WindowsDeleteString(hstring);
        }
    }

    /// <summary>Wraps a DXGI device as the WinRT <see cref="IDirect3DDevice"/> WGC expects.</summary>
    public static IDirect3DDevice CreateDirect3DDevice(Vortice.DXGI.IDXGIDevice dxgiDevice)
    {
        Marshal.ThrowExceptionForHR(Win32.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable));
        try
        {
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>Returns the ID3D11Texture2D behind a capture frame's surface (caller disposes).</summary>
    public static Vortice.Direct3D11.ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var surfacePtr = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        nint access = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePtr, IidDxgiInterfaceAccess, out access));
            var vtbl = *(void***)access;
            var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtbl[3];
            var iid = typeof(Vortice.Direct3D11.ID3D11Texture2D).GUID;
            nint texture = 0;
            Marshal.ThrowExceptionForHR(getInterface(access, &iid, &texture));
            return new Vortice.Direct3D11.ID3D11Texture2D(texture);
        }
        finally
        {
            if (access != 0)
            {
                Marshal.Release(access);
            }

            Marshal.Release(surfacePtr);
        }
    }

    /// <summary>Hides the yellow capture border (Windows 11 / IGraphicsCaptureSession3). False when unsupported or refused.</summary>
    public static bool TrySetBorderRequired(GraphicsCaptureSession session, bool required)
    {
        nint sessionPtr = 0;
        nint session3 = 0;
        try
        {
            sessionPtr = WinRT.MarshalInspectable<GraphicsCaptureSession>.FromManaged(session);
            if (Marshal.QueryInterface(sessionPtr, IidGraphicsCaptureSession3, out session3) < 0 || session3 == 0)
            {
                return false;
            }

            // IInspectable (6 slots), then get_IsBorderRequired, put_IsBorderRequired.
            var vtbl = *(void***)session3;
            var put = (delegate* unmanaged[Stdcall]<nint, byte, int>)vtbl[7];
            return put(session3, required ? (byte)1 : (byte)0) >= 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (session3 != 0)
            {
                Marshal.Release(session3);
            }

            if (sessionPtr != 0)
            {
                Marshal.Release(sessionPtr);
            }
        }
    }
}
