using ClickZen.Core.Automation;
using ClickZen.Core.Devices;
using ClickZen.Core.Geometry;
using ClickZen.Platform.Windows;

namespace ClickZen.Platform.Capture;

/// <summary>
/// One raw image produced by a capture backend, valid only for the duration of the
/// <see cref="CapturedImageProcessor"/> call (it may point into mapped GPU memory).
/// </summary>
public readonly ref struct CapturedImage
{
    public CapturedImage(ReadOnlySpan<byte> pixels, int rowPitch, SizeI size, RectI captureBoundsScreen, WindowMetrics metrics, bool alphaIsUndefined)
    {
        Pixels = pixels;
        RowPitch = rowPitch;
        Size = size;
        CaptureBoundsScreen = captureBoundsScreen;
        Metrics = metrics;
        AlphaIsUndefined = alphaIsUndefined;
    }

    /// <summary>BGRA rows, <see cref="RowPitch"/> bytes apart.</summary>
    public ReadOnlySpan<byte> Pixels { get; }

    public int RowPitch { get; }

    public SizeI Size { get; }

    /// <summary>Screen rectangle (physical px) the image represents: DWM extended frame bounds for WGC,
    /// GetWindowRect for PrintWindow.</summary>
    public RectI CaptureBoundsScreen { get; }

    /// <summary>Window metrics queried right after the image was taken.</summary>
    public WindowMetrics Metrics { get; }

    /// <summary>Alpha is not meaningful (GDI leaves 0, translucent windows via WGC) and is forced to 255 when copying.</summary>
    public bool AlphaIsUndefined { get; }

    /// <summary>Region of this image covering <paramref name="crop"/> (client coordinates, null = whole client area).</summary>
    public RectI RegionFor(RectI? crop) =>
        CaptureGeometry.ClientCropInCapture(
            new PointI(CaptureBoundsScreen.X, CaptureBoundsScreen.Y), CaptureBoundsScreen.Size, Size,
            Metrics.ClientOrigin, Metrics.ClientSize, crop);

    /// <summary>Copies a region into a new tightly packed BGRA frame.</summary>
    public Frame ToFrame(RectI region, long sequence, long timestampMs)
    {
        var bytes = BgraBuffer.CopyRegion(Pixels, RowPitch, region, AlphaIsUndefined);
        return new Frame(region.Width, region.Height, bytes, sequence, timestampMs);
    }
}

/// <summary>Turns a raw capture into a frame (e.g. crops it); null drops the image.</summary>
public delegate Frame? CapturedImageProcessor(in CapturedImage image);

/// <summary>A window capture backend (<see cref="WgcWindowCapture"/> or <see cref="PrintWindowCapture"/>).</summary>
public interface IWindowCapture : IDisposable
{
    WindowCaptureMethod Method { get; }

    nint WindowHandle { get; }

    /// <summary>Converts raw images to frames. Defaults to "whole client area".</summary>
    CapturedImageProcessor? Processor { get; set; }

    /// <summary>Raised on a capture thread (outside internal locks) for each processed frame.</summary>
    event Action<Frame>? FrameReady;

    /// <summary>Raised once when capture can no longer continue (window closed, device lost…).</summary>
    event Action<string>? Faulted;

    /// <summary>Begins delivering frames. Throws when the backend cannot capture this window.</summary>
    void Start();
}

/// <summary>Shared defaults for backends.</summary>
internal static class CaptureDefaults
{
    public static Frame? WholeClientArea(in CapturedImage image)
    {
        var region = image.RegionFor(null);
        return region.IsEmpty ? null : image.ToFrame(region, 0, Environment.TickCount64);
    }
}
