using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;

namespace ClickZen.App.Services;

// Shared contract between the automation page (scheme document, workbench, run bar) and the task
// editor control. The editor edits Core model objects in place and reports every change through
// IAutomationEditorHost.MarkDirty; it never touches frames or devices directly.
//
// Coordinate space: every PointI / RectI stored in a scheme is in device pixels at Scheme.RefSize
// (the engine rescales to the live screen). The host converts workbench clicks into that space and
// sets Scheme.RefSize from the current device the first time a coordinate is authored.

/// <summary>What the task editor needs from the page that hosts it.</summary>
public interface IAutomationEditorHost
{
    /// <summary>The scheme being edited (templates, variables, recordings are looked up here).</summary>
    Scheme Scheme { get; }

    /// <summary>Ids of recordings embedded in the scheme package, for PlayRecording pickers.</summary>
    IReadOnlyCollection<string> EmbeddedRecordings { get; }

    /// <summary>Call after any edit; marks the document unsaved and refreshes list summaries.</summary>
    void MarkDirty();

    /// <summary>Lets the user click a point on the workbench. Null when cancelled or no picture.</summary>
    Task<PointI?> PickPointAsync();

    /// <summary>Lets the user drag a rectangle on the workbench. Null when cancelled or no picture.</summary>
    Task<RectI?> PickAreaAsync();

    /// <summary>
    /// Lets the user drag a rectangle, crops it from the current frame, scales it to RefSize device
    /// pixels, adds it to <see cref="Scheme.Templates"/> (unique id, user-editable name) and returns it.
    /// The returned area is the rectangle it was cut from (useful as the condition's search area seed).
    /// </summary>
    Task<(TemplateAsset Template, RectI Area)?> CaptureTemplateAsync();

    /// <summary>Evaluates an image or colour condition once on the current frame and shows the result on the workbench.</summary>
    Task<ConditionTestResult> TestConditionAsync(Condition condition);

    /// <summary>Highlights an area / point on the workbench (null clears). Used when an editor field gets focus.</summary>
    void Highlight(RectI? area, PointI? point = null);

    /// <summary>Shows a message in the page InfoBar.</summary>
    void ShowMessage(string message, bool isError = false);
}

/// <summary>Outcome of a one-shot condition test.</summary>
/// <param name="Matched">Whether the condition (including ExpectFound/ExpectMatch) holds.</param>
/// <param name="Score">Best template score (0–1) or colour distance, NaN when not applicable.</param>
/// <param name="Location">Where the template was found, RefSize device pixels.</param>
/// <param name="Message">Localised explanation for display ("no picture", "template missing"…), or null.</param>
public sealed record ConditionTestResult(bool Matched, double Score, RectI? Location, string? Message);
