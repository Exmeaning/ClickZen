using System.Collections.Concurrent;
using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;
using OpenCvSharp;

namespace ClickZen.Vision;

/// <summary>
/// OpenCV template matching (normalised cross-correlation, TM_CCOEFF_NORMED).
/// <list type="bullet">
/// <item>Templates are decoded once and cached by id, together with grayscale and rescaled variants.</item>
/// <item>The template is rescaled by <c>templateScale</c> (frame size / authored size) so a template cut from a
///       1080p screenshot still matches a 720p video frame – the 1.x version silently failed in that case.</item>
/// <item>Multi-scale tries 0.8–1.2 around the expected scale and keeps the best score.</item>
/// <item>Uniform-colour templates (zero variance) fall back to TM_SQDIFF_NORMED because CCOEFF is undefined for them.</item>
/// </list>
/// Thread-safe: each call uses its own Mats; the cache is concurrent.
/// </summary>
public sealed class OpenCvImageMatcher : IImageMatcher, IDisposable
{
    private static readonly double[] MultiScaleFactors = [1.0, 0.9, 1.1, 0.8, 1.2];

    private readonly ConcurrentDictionary<string, Mat> _decoded = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Id, int ScaleKey, bool Gray), Mat> _scaled = new();

    /// <summary>Maximum number of rescaled variants kept (each template × scale × colour mode).</summary>
    public int MaxScaledCacheEntries { get; init; } = 256;

    public MatchResult Match(Frame frame, RectI searchArea, TemplateAsset template, double templateScale, ImageCondition options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(options);

        var area = searchArea.IsEmpty ? new RectI(0, 0, frame.Width, frame.Height) : searchArea.ClampTo(frame.Size);
        if (area.IsEmpty || template.Png.Length == 0)
        {
            return MatchResult.NotFound();
        }

        using var full = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Bgra);
        using var roiBgra = new Mat(full, new Rect(area.X, area.Y, area.Width, area.Height));
        using var haystack = new Mat();
        Cv2.CvtColor(roiBgra, haystack, options.Grayscale ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGRA2BGR);

        var factors = options.MultiScale ? MultiScaleFactors : [1.0];
        var best = MatchResult.NotFound();
        foreach (var f in factors)
        {
            var needle = GetScaled(template, templateScale * f, options.Grayscale);
            if (needle is null || needle.Width > haystack.Width || needle.Height > haystack.Height)
            {
                continue;
            }

            var r = MatchOnce(haystack, needle, area);
            if (r.Score > best.Score)
            {
                best = r;
            }

            if (best.Score >= 0.99)
            {
                break;
            }
        }

        return best.Score >= options.Threshold
            ? best with { Found = true }
            : MatchResult.NotFound(best.Score);
    }

    private static MatchResult MatchOnce(Mat haystack, Mat needle, RectI area)
    {
        using var result = new Mat();
        var uniform = IsUniform(needle);
        Cv2.MatchTemplate(haystack, needle, result, uniform ? TemplateMatchModes.SqDiffNormed : TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out var minVal, out var maxVal, out var minLoc, out var maxLoc);

        double score;
        Point loc;
        if (uniform)
        {
            score = 1 - minVal;
            loc = minLoc;
        }
        else
        {
            score = maxVal;
            loc = maxLoc;
        }

        if (double.IsNaN(score) || double.IsInfinity(score))
        {
            score = 0;
        }

        return new MatchResult(false, Math.Clamp(score, 0, 1), new RectI(area.X + loc.X, area.Y + loc.Y, needle.Width, needle.Height));
    }

    private static bool IsUniform(Mat m)
    {
        Cv2.MeanStdDev(m, out _, out var std);
        return std.Val0 < 1e-3 && std.Val1 < 1e-3 && std.Val2 < 1e-3;
    }

    private Mat? GetScaled(TemplateAsset template, double scale, bool gray)
    {
        if (scale <= 0 || double.IsNaN(scale))
        {
            scale = 1;
        }

        var key = (template.Id, (int)Math.Round(scale * 1000), gray);
        if (_scaled.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var decoded = _decoded.GetOrAdd(template.Id, _ => Cv2.ImDecode(template.Png, ImreadModes.Color));
        if (decoded.Empty())
        {
            return null;
        }

        using var colored = new Mat();
        if (gray)
        {
            Cv2.CvtColor(decoded, colored, ColorConversionCodes.BGR2GRAY);
        }
        else
        {
            decoded.CopyTo(colored);
        }

        var w = Math.Max(1, (int)Math.Round(colored.Width * scale));
        var h = Math.Max(1, (int)Math.Round(colored.Height * scale));
        var scaled = new Mat();
        if (w == colored.Width && h == colored.Height)
        {
            colored.CopyTo(scaled);
        }
        else
        {
            Cv2.Resize(colored, scaled, new Size(w, h), 0, 0, scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
        }

        if (_scaled.Count >= MaxScaledCacheEntries)
        {
            ClearScaled();
        }

        if (!_scaled.TryAdd(key, scaled))
        {
            scaled.Dispose();
            return _scaled[key];
        }

        return scaled;
    }

    public void ClearCache()
    {
        ClearScaled();
        foreach (var k in _decoded.Keys.ToArray())
        {
            if (_decoded.TryRemove(k, out var m))
            {
                m.Dispose();
            }
        }
    }

    private void ClearScaled()
    {
        foreach (var k in _scaled.Keys.ToArray())
        {
            if (_scaled.TryRemove(k, out var m))
            {
                m.Dispose();
            }
        }
    }

    public void Dispose() => ClearCache();
}
