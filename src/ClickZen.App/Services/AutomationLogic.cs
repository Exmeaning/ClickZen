using System.Globalization;
using System.Text.Json;
using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;
using ClickZen.Core.Recording;
using OpenCvSharp;

namespace ClickZen.App.Services;

// UI-free helpers for the automation page: coordinate conversions between the workbench (frame /
// device pixels) and the scheme (RefSize device pixels), one-shot condition probes that mirror
// AutomationEngine.EvalImage / EvalColor exactly, and scheme document surgery (clone, export, import).

/// <summary>Conversions between frame pixels, current device pixels and a scheme's RefSize pixels.</summary>
public static class AutomationCoords
{
    /// <summary>The RefSize the engine will use: the scheme's, or the current screen when the scheme has none.</summary>
    public static SizeI EffectiveRef(SizeI schemeRef, SizeI screen) => schemeRef.IsEmpty ? screen : schemeRef;

    /// <summary>False when the scheme was authored in the other orientation than the screen is in now.</summary>
    public static bool CanAuthor(SizeI refSize, SizeI screen) => !RefScaling.OrientationDiffers(refSize, screen);

    /// <summary>Current device pixel → RefSize pixel (inverse of <see cref="RefScaling.Scale(PointI, SizeI, SizeI)"/>), clamped into RefSize.</summary>
    public static PointI DeviceToRef(PointI p, SizeI screen, SizeI refSize)
    {
        if (refSize.IsEmpty || screen.IsEmpty || refSize == screen)
        {
            return p;
        }

        var x = (int)Math.Round((double)p.X * refSize.Width / screen.Width);
        var y = (int)Math.Round((double)p.Y * refSize.Height / screen.Height);
        return new PointI(Math.Clamp(x, 0, refSize.Width - 1), Math.Clamp(y, 0, refSize.Height - 1));
    }

    /// <summary>Current device rectangle → RefSize rectangle (clamped into RefSize).</summary>
    public static RectI DeviceToRef(RectI r, SizeI screen, SizeI refSize)
    {
        if (refSize.IsEmpty || screen.IsEmpty || refSize == screen)
        {
            return r;
        }

        var x0 = (int)Math.Round((double)r.X * refSize.Width / screen.Width);
        var y0 = (int)Math.Round((double)r.Y * refSize.Height / screen.Height);
        var x1 = (int)Math.Round((double)r.Right * refSize.Width / screen.Width);
        var y1 = (int)Math.Round((double)r.Bottom * refSize.Height / screen.Height);
        return RectI.FromCorners(new PointI(x0, y0), new PointI(x1, y1)).ClampTo(refSize);
    }

    public static PointI RefToDevice(PointI p, SizeI refSize, SizeI screen) => RefScaling.Scale(p, refSize, screen);

    public static RectI RefToDevice(RectI r, SizeI refSize, SizeI screen) => RefScaling.Scale(r, refSize, screen);

    /// <summary>Frame rectangle → RefSize rectangle (through device pixels).</summary>
    public static RectI FrameToRef(RectI frameRect, SizeI frame, SizeI screen, SizeI refSize)
    {
        var map = new FrameToDevice(frame, screen.IsEmpty ? frame : screen);
        return DeviceToRef(map.FrameToDeviceRect(frameRect), screen.IsEmpty ? frame : screen, refSize);
    }

    /// <summary>Factor the engine applies to a template authored at <paramref name="refSize"/> before matching on <paramref name="frame"/>.</summary>
    public static double TemplateScale(SizeI frame, SizeI refSize) =>
        refSize.IsEmpty ? 1 : ((double)frame.Width / refSize.Width + (double)frame.Height / refSize.Height) / 2;

    /// <summary>Pixel size of a template PNG cut for <paramref name="refArea"/>: exactly the area at RefSize.</summary>
    public static SizeI TemplateSize(RectI refArea) => new(Math.Max(1, refArea.Width), Math.Max(1, refArea.Height));
}

/// <summary>Result of evaluating an <see cref="ImageCondition"/> once.</summary>
/// <param name="Matched">Condition outcome including <see cref="ImageCondition.ExpectFound"/>.</param>
/// <param name="Found">Whether the template was found above the threshold.</param>
/// <param name="Score">Best score (0..1), NaN when nothing could be matched.</param>
/// <param name="RefLocation">Best location in RefSize pixels (also when below the threshold), or null.</param>
/// <param name="DeviceCenter">Centre of the match in current device pixels (what LastMatch taps), when found.</param>
/// <param name="Problem">Why nothing was matched: "template", "area" or null.</param>
public sealed record ImageProbeResult(bool Matched, bool Found, double Score, RectI? RefLocation, PointI? DeviceCenter, string? Problem);

/// <summary>Result of evaluating a <see cref="ColorCondition"/> once.</summary>
/// <param name="Distance">Largest per-channel difference, or -1 when the point was outside the frame.</param>
/// <param name="Actual">#RRGGBB at the point, or null.</param>
/// <param name="Problem">"color" (unparsable expected colour), "bounds" or null.</param>
public sealed record ColorProbeResult(bool Matched, int Distance, string? Actual, string? Problem);

/// <summary>Evaluates single conditions exactly like <see cref="AutomationEngine"/> does.</summary>
public static class ConditionProbe
{
    public static ImageProbeResult ProbeImage(Frame frame, SizeI screen, SizeI schemeRef, TemplateAsset? template,
        ImageCondition ic, IImageMatcher matcher)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(ic);
        ArgumentNullException.ThrowIfNull(matcher);
        screen = screen.IsEmpty ? frame.Size : screen;
        if (template is null || template.Png.Length == 0)
        {
            return new ImageProbeResult(!ic.ExpectFound, false, double.NaN, null, null, "template");
        }

        var refSize = AutomationCoords.EffectiveRef(schemeRef, screen);
        var map = new FrameToDevice(frame.Size, screen);
        var deviceArea = ic.Area.IsEmpty
            ? new RectI(0, 0, screen.Width, screen.Height)
            : RefScaling.Scale(ic.Area, refSize, screen);
        var frameArea = map.DeviceToFrameRect(deviceArea);
        if (frameArea.IsEmpty)
        {
            return new ImageProbeResult(!ic.ExpectFound, false, double.NaN, null, null, "area");
        }

        var scale = AutomationCoords.TemplateScale(frame.Size, refSize);
        var r = matcher.Match(frame, frameArea, template, scale, ic);
        RectI? refLoc = null;
        PointI? center = null;
        if (!r.Location.IsEmpty)
        {
            refLoc = AutomationCoords.DeviceToRef(map.FrameToDeviceRect(r.Location), screen, refSize);
        }

        if (r.Found)
        {
            center = map.ClampToDevice(map.FrameToDevicePoint(r.Center));
        }

        return new ImageProbeResult(ic.ExpectFound ? r.Found : !r.Found, r.Found, r.Score, refLoc, center, null);
    }

    public static ColorProbeResult ProbeColor(Frame frame, SizeI screen, SizeI schemeRef, ColorCondition cc)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(cc);
        if (!TryParseColor(cc.Color, out var r, out var g, out var b))
        {
            return new ColorProbeResult(false, -1, null, "color");
        }

        screen = screen.IsEmpty ? frame.Size : screen;
        var map = new FrameToDevice(frame.Size, screen);
        var device = RefScaling.Scale(cc.Point, AutomationCoords.EffectiveRef(schemeRef, screen), screen);
        var fp = map.DeviceToFramePoint(device).Round();
        if (fp.X < 0 || fp.Y < 0 || fp.X >= frame.Width || fp.Y >= frame.Height)
        {
            return new ColorProbeResult(false, -1, null, "bounds");
        }

        var (pr, pg, pb) = frame.PixelAt(fp.X, fp.Y);
        var distance = Math.Max(Math.Abs(pr - r), Math.Max(Math.Abs(pg - g), Math.Abs(pb - b)));
        var match = distance <= Math.Clamp(cc.Tolerance, 0, 255);
        return new ColorProbeResult(cc.ExpectMatch ? match : !match, distance, FormatColor(pr, pg, pb), null);
    }

    /// <summary>Same rules as the engine: "#RRGGBB" or "RRGGBB".</summary>
    public static bool TryParseColor(string? text, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var s = (text ?? "").Trim().TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
        {
            return false;
        }

        r = (byte)(v >> 16);
        g = (byte)(v >> 8);
        b = (byte)v;
        return true;
    }

    public static string FormatColor(byte r, byte g, byte b) => string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");
}

/// <summary>Template image operations (OpenCV, no UI).</summary>
public static class TemplateImaging
{
    /// <summary>
    /// Cuts <paramref name="frameRect"/> out of a frame and resizes it to <paramref name="size"/>
    /// (the area's size at RefSize), returning PNG bytes of exactly that size.
    /// </summary>
    public static byte[] CropScaled(Frame frame, RectI frameRect, SizeI size)
    {
        var png = Vision.ImageCodec.CropToPng(frame, frameRect);
        return ResizePng(png, size);
    }

    /// <summary>Resizes a PNG to an exact size (no-op when it already has that size).</summary>
    public static byte[] ResizePng(byte[] png, SizeI size)
    {
        using var src = Cv2.ImDecode(png, ImreadModes.Color);
        if (src.Empty())
        {
            throw new InvalidDataException("Template image could not be decoded.");
        }

        if (size.IsEmpty || (src.Width == size.Width && src.Height == size.Height))
        {
            return png;
        }

        using var dst = new Mat();
        var shrink = size.Width < src.Width || size.Height < src.Height;
        Cv2.Resize(src, dst, new OpenCvSharp.Size(size.Width, size.Height), 0, 0, shrink ? InterpolationFlags.Area : InterpolationFlags.Cubic);
        return dst.ImEncode(".png");
    }
}

/// <summary>What an import added to the scheme.</summary>
public sealed record TaskImportResult(IReadOnlyList<AutomationTask> Tasks, int Templates, int Recordings, bool Rescaled);

/// <summary>Scheme document surgery: cloning, extracting a task, merging tasks from another scheme.</summary>
public static class SchemeTools
{
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Deep copy (JSON round trip; template bytes are copied explicitly since they are not serialized).</summary>
    public static Scheme Clone(Scheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        var json = JsonSerializer.SerializeToUtf8Bytes(scheme, SchemePackage.JsonOptions);
        var copy = JsonSerializer.Deserialize<Scheme>(json, SchemePackage.JsonOptions) ?? new Scheme();
        for (var i = 0; i < copy.Templates.Count && i < scheme.Templates.Count; i++)
        {
            copy.Templates[i].Png = scheme.Templates[i].Png;
        }

        return copy;
    }

    /// <summary>Deep copy of a task; with <paramref name="newId"/> it gets a fresh id.</summary>
    public static AutomationTask CloneTask(AutomationTask task, bool newId)
    {
        ArgumentNullException.ThrowIfNull(task);
        var json = JsonSerializer.SerializeToUtf8Bytes(task, SchemePackage.JsonOptions);
        var copy = JsonSerializer.Deserialize<AutomationTask>(json, SchemePackage.JsonOptions) ?? new AutomationTask();
        if (newId)
        {
            copy.Id = NewId();
        }

        return copy;
    }

    public static IEnumerable<Condition> Conditions(AutomationTask task) =>
        SchemePackage.AllConditions(task.Precondition).Concat(task.Rules.SelectMany(r => SchemePackage.AllConditions(r.When)));

    public static IEnumerable<AutomationAction> Actions(AutomationTask task) =>
        task.Rules.SelectMany(r => r.Then.Concat(r.Branches.SelectMany(b => b.Actions)));

    public static IReadOnlySet<string> TemplateIds(AutomationTask task) =>
        Conditions(task).OfType<ImageCondition>().Select(c => c.TemplateId).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> RecordingRefs(AutomationTask task) =>
        Actions(task).OfType<PlayRecordingAction>().Select(a => a.Recording).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Tasks with an image condition that uses the template.</summary>
    public static IReadOnlyList<AutomationTask> TasksUsingTemplate(Scheme scheme, string templateId) =>
        scheme.Tasks.Where(t => TemplateIds(t).Contains(templateId)).ToList();

    /// <summary>A scheme holding a copy of one task plus only the templates, variables and embedded recordings it references.</summary>
    public static (Scheme Scheme, Dictionary<string, RecordingDocument> Recordings) ExtractTask(
        Scheme scheme, AutomationTask task, IReadOnlyDictionary<string, RecordingDocument>? recordings)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentNullException.ThrowIfNull(task);
        var copy = CloneTask(task, newId: false);
        var ids = TemplateIds(copy);
        var full = Clone(scheme);
        var result = new Scheme
        {
            Name = string.IsNullOrWhiteSpace(task.Name) ? scheme.Name : task.Name,
            Description = scheme.Description,
            RefSize = scheme.RefSize,
            Settings = full.Settings,
            Variables = full.Variables,
            Tasks = [copy],
            Templates = full.Templates.Where(t => ids.Contains(t.Id)).ToList(),
        };

        var recs = new Dictionary<string, RecordingDocument>(StringComparer.Ordinal);
        if (recordings is not null)
        {
            foreach (var id in RecordingRefs(copy))
            {
                if (recordings.TryGetValue(id, out var doc))
                {
                    recs[id] = doc;
                }
            }
        }

        return (result, recs);
    }

    /// <summary>
    /// Copies every task of <paramref name="source"/> into <paramref name="target"/>. Ids that already exist get
    /// fresh ones (and references are remapped); identical templates / recordings are shared. When the RefSizes
    /// differ (same orientation), coordinates and template images are rescaled to the target's RefSize.
    /// </summary>
    public static TaskImportResult MergeTasks(Scheme target, IDictionary<string, RecordingDocument> targetRecordings,
        Scheme source, IReadOnlyDictionary<string, RecordingDocument>? sourceRecordings, Func<byte[], SizeI, byte[]>? resizePng = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targetRecordings);
        ArgumentNullException.ThrowIfNull(source);
        var src = Clone(source);

        var rescale = !src.RefSize.IsEmpty && !target.RefSize.IsEmpty && src.RefSize != target.RefSize;
        if (target.RefSize.IsEmpty)
        {
            target.RefSize = src.RefSize;
        }

        // Templates
        var templateMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var addedTemplates = 0;
        foreach (var t in src.Templates)
        {
            if (rescale)
            {
                var size = ScaleSize(t.Size.IsEmpty ? Vision.ImageCodec.MeasurePng(t.Png) : t.Size, src.RefSize, target.RefSize);
                if (resizePng is not null && !size.IsEmpty)
                {
                    t.Png = resizePng(t.Png, size);
                }

                t.Size = size;
                t.SourceRect = RefScaling.Scale(t.SourceRect, src.RefSize, target.RefSize);
            }

            var existing = target.Templates.FirstOrDefault(x => x.Id == t.Id);
            if (existing is not null && existing.Png.AsSpan().SequenceEqual(t.Png))
            {
                templateMap[t.Id] = t.Id;
                continue;
            }

            var id = existing is null ? t.Id : NewId();
            templateMap[t.Id] = id;
            t.Id = id;
            t.Name = UniqueName(target.Templates.Select(x => x.Name), string.IsNullOrWhiteSpace(t.Name) ? id : t.Name);
            target.Templates.Add(t);
            addedTemplates++;
        }

        // Recordings
        var recordingMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var addedRecordings = 0;
        if (sourceRecordings is not null)
        {
            foreach (var (id, doc) in sourceRecordings)
            {
                if (targetRecordings.TryGetValue(id, out var existing))
                {
                    if (existing.ToJson() == doc.ToJson())
                    {
                        recordingMap[id] = id;
                        continue;
                    }

                    var fresh = NewId();
                    recordingMap[id] = fresh;
                    targetRecordings[fresh] = doc;
                }
                else
                {
                    recordingMap[id] = id;
                    targetRecordings[id] = doc;
                }

                addedRecordings++;
            }
        }

        // Variables (by name; existing declarations win)
        foreach (var v in src.Variables.Where(v => !target.Variables.Any(x => x.Name == v.Name)))
        {
            target.Variables.Add(v);
        }

        // Tasks
        var added = new List<AutomationTask>();
        foreach (var task in src.Tasks)
        {
            if (target.Tasks.Any(t => t.Id == task.Id))
            {
                task.Id = NewId();
            }

            foreach (var ic in Conditions(task).OfType<ImageCondition>())
            {
                if (templateMap.TryGetValue(ic.TemplateId, out var id))
                {
                    ic.TemplateId = id;
                }
            }

            foreach (var pr in Actions(task).OfType<PlayRecordingAction>())
            {
                if (recordingMap.TryGetValue(pr.Recording, out var id))
                {
                    pr.Recording = id;
                }
            }

            if (rescale)
            {
                RescaleTask(task, src.RefSize, target.RefSize);
            }

            target.Tasks.Add(task);
            added.Add(task);
        }

        return new TaskImportResult(added, addedTemplates, addedRecordings, rescale);
    }

    /// <summary>Rescales every coordinate of a task from one RefSize to another.</summary>
    public static void RescaleTask(AutomationTask task, SizeI from, SizeI to)
    {
        ArgumentNullException.ThrowIfNull(task);
        foreach (var c in Conditions(task))
        {
            switch (c)
            {
                case ImageCondition ic:
                    ic.Area = RefScaling.Scale(ic.Area, from, to);
                    break;
                case ColorCondition cc:
                    cc.Point = RefScaling.Scale(cc.Point, from, to);
                    break;
            }
        }

        foreach (var a in Actions(task))
        {
            switch (a)
            {
                case TapAction tap:
                    RescaleTarget(tap.Target, from, to);
                    break;
                case LongPressAction lp:
                    RescaleTarget(lp.Target, from, to);
                    break;
                case SwipeAction sw:
                    RescaleTarget(sw.From, from, to);
                    RescaleTarget(sw.To, from, to);
                    break;
            }
        }
    }

    private static void RescaleTarget(Target t, SizeI from, SizeI to)
    {
        t.Point = RefScaling.Scale(t.Point, from, to);
        t.Area = RefScaling.Scale(t.Area, from, to);
        t.Offset = RefScaling.Scale(t.Offset, from, to);
    }

    private static SizeI ScaleSize(SizeI s, SizeI from, SizeI to) =>
        s.IsEmpty || from.IsEmpty || to.IsEmpty
            ? s
            : new SizeI(Math.Max(1, (int)Math.Round((double)s.Width * to.Width / from.Width)),
                Math.Max(1, (int)Math.Round((double)s.Height * to.Height / from.Height)));

    /// <summary><paramref name="baseName"/>, or "baseName 2", "baseName 3"... whichever is not taken.</summary>
    public static string UniqueName(IEnumerable<string> existing, string baseName)
    {
        var taken = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName))
        {
            return baseName;
        }

        for (var i = 2; ; i++)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"{baseName} {i}");
            if (!taken.Contains(name))
            {
                return name;
            }
        }
    }
}
