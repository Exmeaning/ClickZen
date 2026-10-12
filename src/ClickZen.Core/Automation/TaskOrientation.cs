using ClickZen.Core.Geometry;

namespace ClickZen.Core.Automation;

/// <summary>
/// The screen orientation a task's coordinates belong to, and the only one it runs on.
/// A scheme's <see cref="Scheme.RefSize"/> stands for both orientations of the device (the other one is its
/// transpose), so portrait and landscape tasks can live in one scheme.
/// </summary>
public enum TaskOrientation
{
    /// <summary>
    /// Not set. A task with coordinates follows the orientation of <see cref="Scheme.RefSize"/> (every task of a
    /// scheme made before per-task orientation); a task without coordinates runs in both orientations.
    /// Also used for sizes that have no orientation (empty or square).
    /// </summary>
    Auto,
    Portrait,
    Landscape,
}

/// <summary>How a screen relates to the orientation of the task a coordinate is picked for.</summary>
public enum OrientationFit
{
    /// <summary>The task has no orientation yet (no coordinates, not set explicitly).</summary>
    Unbound,

    /// <summary>The task belongs to the screen's orientation (or the screen has none).</summary>
    Matches,

    /// <summary>The task belongs to the other orientation.</summary>
    OtherOrientation,
}

/// <summary>Where an imported task or template comes from and goes to, see <see cref="TaskOrientations.ForImport"/>.</summary>
/// <param name="From">Reference size its coordinates are relative to in the source scheme.</param>
/// <param name="To">Reference size they must be relative to in the target scheme.</param>
/// <param name="Orientation">The orientation to store in the target scheme.</param>
public readonly record struct OrientationImport(SizeI From, SizeI To, TaskOrientation Orientation)
{
    /// <summary>True when coordinates (and template images) have to be rescaled.</summary>
    public bool Rescales => !From.IsEmpty && !To.IsEmpty && From != To;
}

/// <summary>Orientation rules shared by the engine, the editor and scheme import (no UI).</summary>
public static class TaskOrientations
{
    /// <summary>Orientation of a screen or reference size; <see cref="TaskOrientation.Auto"/> when it has none (empty or square).</summary>
    public static TaskOrientation Of(SizeI size) =>
        size.IsEmpty || size.Width == size.Height ? TaskOrientation.Auto
        : size.IsLandscape ? TaskOrientation.Landscape
        : TaskOrientation.Portrait;

    /// <summary>The other orientation (Auto stays Auto).</summary>
    public static TaskOrientation Other(TaskOrientation orientation) => orientation switch
    {
        TaskOrientation.Portrait => TaskOrientation.Landscape,
        TaskOrientation.Landscape => TaskOrientation.Portrait,
        _ => TaskOrientation.Auto,
    };

    /// <summary>The scheme's reference size in <paramref name="orientation"/> (unchanged for Auto or a square reference).</summary>
    public static SizeI RefFor(SizeI schemeRef, TaskOrientation orientation) => orientation switch
    {
        TaskOrientation.Landscape when !schemeRef.IsEmpty && schemeRef.Width < schemeRef.Height => schemeRef.Transposed,
        TaskOrientation.Portrait when !schemeRef.IsEmpty && schemeRef.Width > schemeRef.Height => schemeRef.Transposed,
        _ => schemeRef,
    };

    /// <summary>
    /// True when the task stores a screen position that was set: a search area, a colour point, or the point / area
    /// of a tap, long press or swipe target. Default values (0, 0 and empty areas) count as not set; offsets are relative.
    /// </summary>
    public static bool HasCoordinates(AutomationTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var conditions = SchemePackage.AllConditions(task.Precondition)
            .Concat(task.Rules.SelectMany(r => SchemePackage.AllConditions(r.When)));
        if (conditions.Any(c => c is ImageCondition { Area.IsEmpty: false } || (c is ColorCondition cc && cc.Point != default)))
        {
            return true;
        }

        return task.Rules.SelectMany(r => r.Then.Concat(r.Branches.SelectMany(b => b.Actions))).Any(a => a switch
        {
            TapAction t => IsPlaced(t.Target),
            LongPressAction lp => IsPlaced(lp.Target),
            SwipeAction s => IsPlaced(s.From) || IsPlaced(s.To),
            _ => false,
        });
    }

    private static bool IsPlaced(Target? t) => t is not null && (t.Kind == TargetKind.RandomInArea ? !t.Area.IsEmpty : t.Point != default);

    /// <summary>
    /// The orientation the task runs on: the one set on it; for an unset task with coordinates the orientation of
    /// <paramref name="schemeRef"/>; <see cref="TaskOrientation.Auto"/> (both) otherwise.
    /// </summary>
    public static TaskOrientation Effective(AutomationTask task, SizeI schemeRef)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Orientation != TaskOrientation.Auto)
        {
            return task.Orientation;
        }

        return HasCoordinates(task) ? Of(schemeRef) : TaskOrientation.Auto;
    }

    /// <summary>The orientation a template was cut in (unset = the orientation of <paramref name="schemeRef"/>).</summary>
    public static TaskOrientation Effective(TemplateAsset template, SizeI schemeRef)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.SourceOrientation != TaskOrientation.Auto ? template.SourceOrientation : Of(schemeRef);
    }

    /// <summary>True when <paramref name="orientation"/> may run or be shown on a screen of size <paramref name="screen"/>.</summary>
    public static bool Allows(TaskOrientation orientation, SizeI screen)
    {
        var s = Of(screen);
        return orientation == TaskOrientation.Auto || s == TaskOrientation.Auto || s == orientation;
    }

    /// <summary>Whether the engine evaluates the task on a screen of size <paramref name="screen"/>.</summary>
    public static bool RunsOn(AutomationTask task, SizeI schemeRef, SizeI screen) => Allows(Effective(task, schemeRef), screen);

    /// <summary>How a coordinate picked on <paramref name="screen"/> fits the task.</summary>
    public static OrientationFit Fit(AutomationTask task, SizeI schemeRef, SizeI screen)
    {
        var o = Effective(task, schemeRef);
        if (o == TaskOrientation.Auto)
        {
            return OrientationFit.Unbound;
        }

        return Allows(o, screen) ? OrientationFit.Matches : OrientationFit.OtherOrientation;
    }

    /// <summary>
    /// The value to store for something authored in <paramref name="orientation"/>: Auto when that is the orientation
    /// of <paramref name="schemeRef"/> (so single-orientation schemes stay as they were), the orientation otherwise.
    /// </summary>
    public static TaskOrientation StoredFor(TaskOrientation orientation, SizeI schemeRef) =>
        orientation == Of(schemeRef) ? TaskOrientation.Auto : orientation;

    /// <summary>
    /// How a task or template moves from a scheme with reference <paramref name="sourceRef"/> into one with
    /// <paramref name="targetRef"/>: coordinates are rescaled within their own orientation, and an unset item whose
    /// orientation is not the target's main one is set explicitly so it keeps its meaning.
    /// </summary>
    /// <param name="stored">The item's stored orientation.</param>
    /// <param name="effective">Its effective orientation in the source scheme.</param>
    public static OrientationImport ForImport(TaskOrientation stored, TaskOrientation effective, SizeI sourceRef, SizeI targetRef)
    {
        if (effective == TaskOrientation.Auto)
        {
            return new OrientationImport(sourceRef, RefScaling.Oriented(targetRef, sourceRef), stored);
        }

        var orientation = stored == TaskOrientation.Auto && effective != Of(targetRef) ? effective : stored;
        return new OrientationImport(RefFor(sourceRef, effective), RefFor(targetRef, effective), orientation);
    }

    /// <summary>
    /// The oldest package format that runs <paramref name="scheme"/> correctly: 2 when a task is set to the orientation
    /// other than the one of <see cref="Scheme.RefSize"/> (a format 1 reader would take its coordinates as main-orientation
    /// ones and tap in the wrong places), 1 otherwise so older versions keep opening it. Template orientations only affect
    /// highlighting and import, so they do not raise the format.
    /// </summary>
    public static int RequiredFormatVersion(Scheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        var main = Of(scheme.RefSize);
        return main != TaskOrientation.Auto && scheme.Tasks.Any(t => t.Orientation != TaskOrientation.Auto && t.Orientation != main)
            ? Scheme.CurrentFormatVersion
            : 1;
    }
}
