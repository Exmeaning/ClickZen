using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;

namespace ClickZen.Core.Tests.Automation;

public sealed class TaskOrientationTests
{
    private static readonly SizeI Portrait = new(1080, 2400);
    private static readonly SizeI Landscape = new(2400, 1080);

    private static AutomationTask WithTap(int x, int y, TaskOrientation orientation = TaskOrientation.Auto) => new()
    {
        Orientation = orientation,
        Rules = [new Rule { Then = [new TapAction { Target = Target.At(x, y) }] }],
    };

    private static AutomationTask WithoutCoordinates(TaskOrientation orientation = TaskOrientation.Auto) => new()
    {
        Orientation = orientation,
        Rules = [new Rule { Then = [new KeyAction(), new WaitAction()] }],
    };

    // ------------------------------------------------------------------ sizes

    [Fact]
    public void Oriented_transposes_only_across_orientations()
    {
        Assert.Equal(Landscape, RefScaling.Oriented(Portrait, new SizeI(1600, 720)));
        Assert.Equal(Portrait, RefScaling.Oriented(Portrait, new SizeI(720, 1600)));
        Assert.Equal(Portrait, RefScaling.Oriented(Landscape, new SizeI(720, 1600)));
    }

    [Fact]
    public void Oriented_leaves_square_and_empty_sizes_alone()
    {
        Assert.Equal(Portrait, RefScaling.Oriented(Portrait, new SizeI(800, 800)));
        Assert.Equal(new SizeI(800, 800), RefScaling.Oriented(new SizeI(800, 800), Landscape));
        Assert.Equal(Portrait, RefScaling.Oriented(Portrait, default));
        Assert.Equal(default, RefScaling.Oriented(default, Landscape));
    }

    [Fact]
    public void Of_and_RefFor()
    {
        Assert.Equal(TaskOrientation.Portrait, TaskOrientations.Of(Portrait));
        Assert.Equal(TaskOrientation.Landscape, TaskOrientations.Of(Landscape));
        Assert.Equal(TaskOrientation.Auto, TaskOrientations.Of(new SizeI(500, 500)));
        Assert.Equal(TaskOrientation.Auto, TaskOrientations.Of(default));

        Assert.Equal(Landscape, TaskOrientations.RefFor(Portrait, TaskOrientation.Landscape));
        Assert.Equal(Portrait, TaskOrientations.RefFor(Portrait, TaskOrientation.Portrait));
        Assert.Equal(Portrait, TaskOrientations.RefFor(Landscape, TaskOrientation.Portrait));
        Assert.Equal(Portrait, TaskOrientations.RefFor(Portrait, TaskOrientation.Auto));
        Assert.Equal(default, TaskOrientations.RefFor(default, TaskOrientation.Landscape));
    }

    // ------------------------------------------------------------------ coordinates

    [Fact]
    public void Placed_coordinates_count_and_defaults_do_not()
    {
        Assert.True(TaskOrientations.HasCoordinates(WithTap(10, 20)));
        Assert.False(TaskOrientations.HasCoordinates(WithTap(0, 0)));
        Assert.False(TaskOrientations.HasCoordinates(WithoutCoordinates()));

        // A full-screen image search tapping the match has no coordinates; a search area has.
        var lastMatch = new AutomationTask
        {
            Rules =
            [
                new Rule
                {
                    When = new ConditionGroup { Conditions = [new ImageCondition { TemplateId = "t" }] },
                    Then = [new TapAction { Target = new Target { Kind = TargetKind.LastMatch, Offset = new PointI(5, 5) } }],
                },
            ],
        };
        Assert.False(TaskOrientations.HasCoordinates(lastMatch));
        ((ImageCondition)lastMatch.Rules[0].When.Conditions[0]).Area = new RectI(0, 0, 100, 100);
        Assert.True(TaskOrientations.HasCoordinates(lastMatch));
    }

    [Fact]
    public void Coordinates_are_found_in_preconditions_nested_groups_branches_and_swipes()
    {
        var color = new AutomationTask
        {
            Precondition = new ConditionGroup { Groups = [new ConditionGroup { Conditions = [new ColorCondition { Point = new PointI(3, 4) }] }] },
        };
        Assert.True(TaskOrientations.HasCoordinates(color));

        var branch = new AutomationTask
        {
            Rules = [new Rule { Pick = RulePick.RandomOne, Branches = [new ActionBranch { Actions = [new LongPressAction { Target = Target.At(1, 1) }] }] }],
        };
        Assert.True(TaskOrientations.HasCoordinates(branch));

        var swipe = new AutomationTask { Rules = [new Rule { Then = [new SwipeAction { To = Target.At(0, 9) }] }] };
        Assert.True(TaskOrientations.HasCoordinates(swipe));

        var area = new AutomationTask
        {
            Rules = [new Rule { Then = [new TapAction { Target = new Target { Kind = TargetKind.RandomInArea, Area = new RectI(0, 0, 10, 10) } }] }],
        };
        Assert.True(TaskOrientations.HasCoordinates(area));
    }

    // ------------------------------------------------------------------ effective orientation

    [Fact]
    public void Unset_task_with_coordinates_follows_the_scheme_reference()
    {
        Assert.Equal(TaskOrientation.Portrait, TaskOrientations.Effective(WithTap(5, 5), Portrait));
        Assert.Equal(TaskOrientation.Landscape, TaskOrientations.Effective(WithTap(5, 5), Landscape));
        Assert.Equal(TaskOrientation.Auto, TaskOrientations.Effective(WithTap(5, 5), default));
    }

    [Fact]
    public void Set_orientation_wins_and_tasks_without_coordinates_run_in_both()
    {
        Assert.Equal(TaskOrientation.Landscape, TaskOrientations.Effective(WithTap(5, 5, TaskOrientation.Landscape), Portrait));
        Assert.Equal(TaskOrientation.Landscape, TaskOrientations.Effective(WithoutCoordinates(TaskOrientation.Landscape), Portrait));
        Assert.Equal(TaskOrientation.Auto, TaskOrientations.Effective(WithoutCoordinates(), Portrait));

        Assert.True(TaskOrientations.RunsOn(WithoutCoordinates(), Portrait, Landscape));
        Assert.True(TaskOrientations.RunsOn(WithoutCoordinates(), Portrait, Portrait));
    }

    [Fact]
    public void RunsOn_matches_the_screen_orientation()
    {
        var portraitTask = WithTap(5, 5);
        var landscapeTask = WithTap(5, 5, TaskOrientation.Landscape);

        Assert.True(TaskOrientations.RunsOn(portraitTask, Portrait, new SizeI(720, 1600)));
        Assert.False(TaskOrientations.RunsOn(portraitTask, Portrait, new SizeI(1600, 720)));
        Assert.True(TaskOrientations.RunsOn(landscapeTask, Portrait, new SizeI(1600, 720)));
        Assert.False(TaskOrientations.RunsOn(landscapeTask, Portrait, Portrait));

        // A square or unknown screen has no orientation: nothing is skipped.
        Assert.True(TaskOrientations.RunsOn(landscapeTask, Portrait, new SizeI(900, 900)));
        Assert.True(TaskOrientations.RunsOn(landscapeTask, Portrait, default));
    }

    [Fact]
    public void Fit_tells_unbound_matching_and_other_orientation_apart()
    {
        Assert.Equal(OrientationFit.Unbound, TaskOrientations.Fit(WithoutCoordinates(), Portrait, Landscape));
        Assert.Equal(OrientationFit.Matches, TaskOrientations.Fit(WithTap(5, 5), Portrait, Portrait));
        Assert.Equal(OrientationFit.OtherOrientation, TaskOrientations.Fit(WithTap(5, 5), Portrait, Landscape));
        Assert.Equal(OrientationFit.Matches, TaskOrientations.Fit(WithoutCoordinates(TaskOrientation.Landscape), Portrait, Landscape));
    }

    [Fact]
    public void Stored_orientation_is_only_explicit_for_the_other_one()
    {
        Assert.Equal(TaskOrientation.Auto, TaskOrientations.StoredFor(TaskOrientation.Portrait, Portrait));
        Assert.Equal(TaskOrientation.Landscape, TaskOrientations.StoredFor(TaskOrientation.Landscape, Portrait));
        Assert.Equal(TaskOrientation.Portrait, TaskOrientations.StoredFor(TaskOrientation.Portrait, default));
    }

    [Fact]
    public void Template_orientation_defaults_to_the_scheme_reference()
    {
        Assert.Equal(TaskOrientation.Portrait, TaskOrientations.Effective(new TemplateAsset(), Portrait));
        Assert.Equal(TaskOrientation.Landscape, TaskOrientations.Effective(new TemplateAsset { SourceOrientation = TaskOrientation.Landscape }, Portrait));
    }

    // ------------------------------------------------------------------ import

    [Fact]
    public void Import_between_same_orientations_rescales_within_it()
    {
        var m = TaskOrientations.ForImport(TaskOrientation.Auto, TaskOrientation.Portrait, Portrait, new SizeI(720, 1600));
        Assert.Equal(Portrait, m.From);
        Assert.Equal(new SizeI(720, 1600), m.To);
        Assert.Equal(TaskOrientation.Auto, m.Orientation);
        Assert.True(m.Rescales);
    }

    [Fact]
    public void Import_of_a_landscape_scheme_into_a_portrait_one_keeps_landscape()
    {
        // Same device in the other orientation: nothing to rescale, but the task must become explicitly landscape.
        var same = TaskOrientations.ForImport(TaskOrientation.Auto, TaskOrientation.Landscape, Landscape, Portrait);
        Assert.Equal(TaskOrientation.Landscape, same.Orientation);
        Assert.Equal(Landscape, same.From);
        Assert.Equal(Landscape, same.To);
        Assert.False(same.Rescales);

        // Other device: rescaled from landscape to the target's landscape reference (its transpose).
        var other = TaskOrientations.ForImport(TaskOrientation.Auto, TaskOrientation.Landscape, new SizeI(1600, 720), Portrait);
        Assert.Equal(new SizeI(1600, 720), other.From);
        Assert.Equal(Landscape, other.To);
        Assert.True(other.Rescales);
    }

    [Fact]
    public void Import_of_an_explicit_main_orientation_task_keeps_its_setting()
    {
        // A portrait task explicitly set in a landscape scheme lands in a portrait scheme: still portrait.
        var m = TaskOrientations.ForImport(TaskOrientation.Portrait, TaskOrientation.Portrait, Landscape, Portrait);
        Assert.Equal(TaskOrientation.Portrait, m.Orientation);
        Assert.False(m.Rescales);
    }

    [Fact]
    public void Import_of_an_orientation_free_task_rescales_without_rotating()
    {
        var m = TaskOrientations.ForImport(TaskOrientation.Auto, TaskOrientation.Auto, Landscape, new SizeI(720, 1600));
        Assert.Equal(TaskOrientation.Auto, m.Orientation);
        Assert.Equal(Landscape, m.From);
        Assert.Equal(new SizeI(1600, 720), m.To);
    }

    // ------------------------------------------------------------------ format

    [Fact]
    public void Format_2_is_only_required_for_tasks_of_the_other_orientation()
    {
        var scheme = new Scheme { RefSize = Portrait, Tasks = [WithTap(5, 5), WithoutCoordinates()] };
        Assert.Equal(1, TaskOrientations.RequiredFormatVersion(scheme));

        scheme.Tasks.Add(WithTap(5, 5, TaskOrientation.Portrait));
        scheme.Templates.Add(new TemplateAsset { SourceOrientation = TaskOrientation.Landscape });
        Assert.Equal(1, TaskOrientations.RequiredFormatVersion(scheme));

        scheme.Tasks.Add(WithTap(5, 5, TaskOrientation.Landscape));
        Assert.Equal(2, TaskOrientations.RequiredFormatVersion(scheme));
    }
}
