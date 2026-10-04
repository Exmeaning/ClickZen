using ClickZen.Core.Geometry;

namespace ClickZen.Core.Automation;

/// <summary>A decoded screen image in BGRA order (4 bytes per pixel, no row padding).</summary>
public sealed class Frame
{
    public Frame(int width, int height, byte[] bgra, long sequence = 0, long timestampMs = 0)
    {
        if (bgra.Length < width * height * 4)
        {
            throw new ArgumentException("Buffer too small for the given size.", nameof(bgra));
        }

        Width = width;
        Height = height;
        Bgra = bgra;
        Sequence = sequence;
        TimestampMs = timestampMs;
    }

    public int Width { get; }
    public int Height { get; }
    public SizeI Size => new(Width, Height);
    public byte[] Bgra { get; }

    /// <summary>Monotonic frame counter of the source; lets consumers tell whether the image changed.</summary>
    public long Sequence { get; }

    public long TimestampMs { get; }

    /// <summary>(R, G, B) at a pixel; caller ensures bounds.</summary>
    public (byte R, byte G, byte B) PixelAt(int x, int y)
    {
        var i = (y * Width + x) * 4;
        return (Bgra[i + 2], Bgra[i + 1], Bgra[i]);
    }
}

/// <summary>Anything that can produce the current screen image (video decoder, window capture, test fake).</summary>
public interface IFrameSource
{
    /// <summary>Latest available frame or null if none yet.</summary>
    Frame? Latest { get; }

    /// <summary>
    /// Waits for a frame whose <see cref="Frame.Sequence"/> is greater than <paramref name="afterSequence"/>
    /// (i.e. captured after the caller's last action) or until <paramref name="timeout"/>; returns the newest frame either way.
    /// </summary>
    Task<Frame?> WaitForFrameAsync(long afterSequence, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// A continuously running <see cref="IFrameSource"/> (scrcpy video, window capture) that also pushes frames,
/// so a view can render them as they arrive.
/// </summary>
public interface ILiveFrameSource : IFrameSource
{
    /// <summary>Raised on a background thread for every new frame.</summary>
    event EventHandler<Frame>? FrameArrived;

    /// <summary>Frames per second over the last second.</summary>
    double Fps { get; }
}

/// <summary>Result of looking for a template.</summary>
public readonly record struct MatchResult(bool Found, double Score, RectI Location)
{
    public PointI Center => Location.Center;

    public static MatchResult NotFound(double score = 0) => new(false, score, default);
}

/// <summary>
/// Template matching backend (OpenCV in production). Implementations cache decoded templates by id.
/// All rectangles are in frame pixels.
/// </summary>
public interface IImageMatcher
{
    /// <param name="templateScale">Factor applied to the template before matching (frame size / authored size).</param>
    MatchResult Match(Frame frame, RectI searchArea, TemplateAsset template, double templateScale, ImageCondition options);

    /// <summary>Drops cached decoded templates (call when a scheme is unloaded or a template replaced).</summary>
    void ClearCache();
}
