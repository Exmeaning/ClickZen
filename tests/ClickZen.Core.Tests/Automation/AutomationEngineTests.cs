using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Core.Recording;
using ClickZen.Core.Tests.Fakes;
using ClickZen.Core.Variables;

namespace ClickZen.Core.Tests.Automation;

public sealed class AutomationEngineTests
{
    private readonly FakeClock _clock = new();
    private readonly FakeFrameSource _frames = new(100, 200);
    private readonly FakeMatcher _matcher = new();
    private readonly RecordingInjector _input;

    public AutomationEngineTests()
    {
        _input = new RecordingInjector(_clock);
        _frames.SetColor(0, 0, 0);
    }

    private static readonly SizeI Device = new(1000, 2000);

    private static Scheme NewScheme(params AutomationTask[] tasks) => new()
    {
        Name = "test",
        RefSize = Device,
        Settings = new SchemeSettings { CheckIntervalMs = 100, DefaultDelayAfterMs = 0, Humanize = HumanizeOptions.None },
        Templates = [new TemplateAsset { Id = "btn", Name = "button", Png = [1], Size = new SizeI(50, 20) }],
        Tasks = tasks.ToList(),
    };

    private AutomationEngine Engine(Scheme s, VariableStore? globals = null, Func<string, RecordingDocument?>? recordings = null) =>
        new(s, new EngineEnvironment
        {
            Frames = _frames,
            Input = _input,
            Matcher = _matcher,
            ScreenSize = () => Device,
            Clock = _clock,
            Random = new Random(1),
            GlobalVariables = globals,
            ResolveRecording = recordings,
        });

    private static AutomationTask Task(string name, ConditionGroup when, params AutomationAction[] then) => new()
    {
        Id = name,
        Name = name,
        CooldownMs = 0,
        Rules = [new Rule { Name = "r", When = when, Then = then.ToList() }],
    };

    private static ConditionGroup Image(string id = "btn", bool expectFound = true, GroupLogic logic = GroupLogic.All) =>
        new() { Logic = logic, Conditions = [new ImageCondition { TemplateId = id, ExpectFound = expectFound }] };

    private static ConditionGroup Expr(string e) => new() { Conditions = [new ExpressionCondition { Expression = e }] };

    private static readonly ConditionGroup Always = new();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Image_match_taps_the_match_centre_in_device_pixels()
    {
        // Frame is 100x200, device 1000x2000 → ×10. Match box (40,60,20,10) centre (50,65) → device (500,650).
        _matcher.Visible["btn"] = new RectI(40, 60, 20, 10);
        var engine = Engine(NewScheme(Task("t", Image(), new TapAction { Target = new Target { Kind = TargetKind.LastMatch } })));

        await engine.RunAsync(Ct, maxRounds: 1);

        var tap = Assert.Single(_input.Calls);
        Assert.Equal(new PointI(500, 650), tap.Point);
        Assert.Equal(EngineState.Stopped, engine.State);
    }

    [Fact]
    public async Task Not_found_does_not_fire()
    {
        var engine = Engine(NewScheme(Task("t", Image(), new TapAction { Target = Target.At(1, 1) })));
        await engine.RunAsync(Ct, maxRounds: 3);
        Assert.Empty(_input.Calls);
    }

    [Fact]
    public async Task Single_condition_with_None_logic_is_negated()
    {
        // 1.x bug: with one condition the logic (NOT) was ignored.
        var engine = Engine(NewScheme(Task("t", Image(logic: GroupLogic.None), new TapAction { Target = Target.At(1, 1) })));
        await engine.RunAsync(Ct, maxRounds: 1);
        Assert.Single(_input.Calls);

        _input.Calls.Clear();
        _matcher.Visible["btn"] = new RectI(0, 0, 10, 10);
        await engine.RunAsync(Ct, maxRounds: 1);
        Assert.Empty(_input.Calls);
    }

    [Fact]
    public async Task Expect_not_found_fires_when_absent()
    {
        var engine = Engine(NewScheme(Task("t", Image(expectFound: false), new KeyAction { KeyCode = KeyCodes.Back })));
        await engine.RunAsync(Ct, maxRounds: 1);
        Assert.Equal("key", Assert.Single(_input.Calls).Kind);
    }

    [Fact]
    public async Task Cooldown_counts_from_end_of_last_run()
    {
        var task = Task("t", Always, new TapAction { Target = Target.At(1, 1) });
        task.CooldownMs = 1000;
        var engine = Engine(NewScheme(task));

        await engine.RunAsync(Ct, maxRounds: 25); // 25 rounds × 100 ms interval ≈ 2.4 s

        Assert.Equal(3, _input.Calls.Count);
        Assert.Equal(new long[] { 0, 1000, 2000 }, _input.Calls.Select(c => c.AtMs));
    }

    [Fact]
    public async Task Higher_priority_runs_first()
    {
        var low = Task("low", Always, new TextAction { Text = "low" });
        var high = Task("high", Always, new TextAction { Text = "high" });
        high.Priority = 5;
        var engine = Engine(NewScheme(low, high));

        await engine.RunAsync(Ct, maxRounds: 1);

        Assert.Equal(new[] { "high", "low" }, _input.Calls.Select(c => c.Text));
    }

    [Fact]
    public async Task Later_tasks_see_a_fresh_frame_after_an_input_action()
    {
        // Task A taps; the screen then changes so the button appears. Task B must see it in the same round.
        var a = Task("a", Always, new TapAction { Target = Target.At(1, 1) });
        a.CooldownMs = 60_000;
        var b = Task("b", Image(), new TextAction { Text = "seen" });
        _frames.OnWaitForNewer = f =>
        {
            _matcher.Visible["btn"] = new RectI(0, 0, 10, 10);
            f.SetColor(255, 255, 255);
        };
        var engine = Engine(NewScheme(a, b));

        await engine.RunAsync(Ct, maxRounds: 1);

        Assert.Equal(new[] { "tap", "text" }, _input.Calls.Select(c => c.Kind));
    }

    [Fact]
    public async Task Stop_after_match_skips_remaining_rules()
    {
        var task = new AutomationTask
        {
            Id = "t", Name = "t", CooldownMs = 0,
            Rules =
            [
                new Rule { Name = "first", When = Always, Then = [new TextAction { Text = "1" }], StopAfterMatch = true },
                new Rule { Name = "second", When = Always, Then = [new TextAction { Text = "2" }] },
            ],
        };
        var engine = Engine(NewScheme(task));
        await engine.RunAsync(Ct, maxRounds: 1);
        Assert.Equal(new[] { "1" }, _input.Calls.Select(c => c.Text));
    }

    [Fact]
    public async Task All_matching_rules_run_without_stop_flag()
    {
        var task = new AutomationTask
        {
            Id = "t", Name = "t", CooldownMs = 0,
            Rules =
            [
                new Rule { When = Always, Then = [new TextAction { Text = "1" }] },
                new Rule { When = Expr("false"), Then = [new TextAction { Text = "x" }] },
                new Rule { When = Always, Then = [new TextAction { Text = "2" }] },
            ],
        };
        await Engine(NewScheme(task)).RunAsync(Ct, maxRounds: 1);
        Assert.Equal(new[] { "1", "2" }, _input.Calls.Select(c => c.Text));
    }

    [Fact]
    public async Task Variables_drive_conditions_and_are_typed()
    {
        var scheme = NewScheme(
            Task("count", Expr("count < 3"), new SetVariableAction { Variable = "count", Expression = "count + 1" }),
            Task("done", Expr("count == 3"), new TextAction { Text = "done {count}" }, new StopAction()));
        scheme.Variables.Add(new VariableDefinition { Name = "count", Type = VariableType.Int, Initial = 0 });
        var engine = Engine(scheme);

        await engine.RunAsync(Ct, maxRounds: 100);

        Assert.Equal("done 3", Assert.Single(_input.Calls).Text);
        Assert.True(engine.Rounds <= 4);
    }

    [Fact]
    public async Task Run_limit_caps_executions()
    {
        var task = Task("t", Always, new TapAction { Target = Target.At(1, 1) });
        task.RunLimit = 2;
        await Engine(NewScheme(task)).RunAsync(Ct, maxRounds: 10);
        Assert.Equal(2, _input.Calls.Count);
    }

    [Fact]
    public async Task Stop_task_disables_only_that_task()
    {
        var a = Task("a", Always, new TextAction { Text = "a" }, new StopAction { Scope = StopScope.Task });
        var b = Task("b", Always, new TextAction { Text = "b" });
        await Engine(NewScheme(a, b)).RunAsync(Ct, maxRounds: 3);
        Assert.Equal(new[] { "a", "b", "b", "b" }, _input.Calls.Select(c => c.Text));
    }

    [Fact]
    public async Task Random_branch_runs_exactly_one_branch()
    {
        var task = new AutomationTask
        {
            Id = "t", Name = "t", CooldownMs = 0,
            Rules =
            [
                new Rule
                {
                    When = Always, Pick = RulePick.RandomOne,
                    Branches =
                    [
                        new ActionBranch { Weight = 1, Actions = [new TextAction { Text = "a" }] },
                        new ActionBranch { Weight = 0, Actions = [new TextAction { Text = "never" }] },
                        new ActionBranch { Weight = 1, Actions = [new TextAction { Text = "b" }] },
                    ],
                },
            ],
        };
        await Engine(NewScheme(task)).RunAsync(Ct, maxRounds: 200);

        Assert.Equal(200, _input.Calls.Count);
        Assert.DoesNotContain(_input.Calls, c => c.Text == "never");
        Assert.Contains(_input.Calls, c => c.Text == "a");
        Assert.Contains(_input.Calls, c => c.Text == "b");
    }

    [Fact]
    public async Task Coordinates_are_rescaled_from_ref_size()
    {
        var scheme = NewScheme(Task("t", Always, new TapAction { Target = Target.At(500, 1000) }));
        scheme.RefSize = new SizeI(2000, 4000);
        await Engine(scheme).RunAsync(Ct, maxRounds: 1);
        Assert.Equal(new PointI(250, 500), _input.Calls[0].Point);
    }

    [Fact]
    public async Task Search_area_is_converted_to_frame_pixels()
    {
        var cond = new ImageCondition { TemplateId = "btn", Area = new RectI(100, 200, 300, 400) };
        var scheme = NewScheme(Task("t", new ConditionGroup { Conditions = [cond] }, new TapAction()));
        await Engine(scheme).RunAsync(Ct, maxRounds: 1);

        var call = Assert.Single(_matcher.Calls);
        Assert.Equal(new RectI(10, 20, 30, 40), call.Area);
        Assert.Equal(0.1, call.Scale, 6);
    }

    [Fact]
    public async Task Color_condition_checks_pixel_with_tolerance()
    {
        _frames.SetColor(200, 100, 50);
        var hit = new ColorCondition { Point = new PointI(500, 500), Color = "#C86432", Tolerance = 5 };
        var miss = new ColorCondition { Point = new PointI(500, 500), Color = "#000000", Tolerance = 5 };
        var scheme = NewScheme(
            Task("hit", new ConditionGroup { Conditions = [hit] }, new TextAction { Text = "hit" }),
            Task("miss", new ConditionGroup { Conditions = [miss] }, new TextAction { Text = "miss" }));
        await Engine(scheme).RunAsync(Ct, maxRounds: 1);
        Assert.Equal(new[] { "hit" }, _input.Calls.Select(c => c.Text));
    }

    [Fact]
    public async Task Nested_groups_combine()
    {
        // All( expr true, Any( false, true ) ) → fires
        var g = new ConditionGroup
        {
            Logic = GroupLogic.All,
            Conditions = [new ExpressionCondition { Expression = "1 == 1" }],
            Groups = [new ConditionGroup { Logic = GroupLogic.Any, Conditions = [new ExpressionCondition { Expression = "false" }, new ExpressionCondition { Expression = "true" }] }],
        };
        var emptyAny = new ConditionGroup { Logic = GroupLogic.Any };
        var scheme = NewScheme(Task("nested", g, new TextAction { Text = "n" }), Task("emptyAny", emptyAny, new TextAction { Text = "e" }));
        await Engine(scheme).RunAsync(Ct, maxRounds: 1);
        Assert.Equal(new[] { "n" }, _input.Calls.Select(c => c.Text));
    }

    [Fact]
    public async Task Global_variables_are_shared_between_engines()
    {
        var globals = new VariableStore();
        var scheme = NewScheme(Task("inc", Always, new SetVariableAction { Variable = "shared", Expression = "shared + 1" }));
        scheme.Variables.Add(new VariableDefinition { Name = "shared", Scope = VariableScope.Global, Type = VariableType.Int });

        await Engine(scheme, globals).RunAsync(Ct, maxRounds: 2);
        await Engine(scheme, globals).RunAsync(Ct, maxRounds: 3);

        Assert.Equal(5, globals.Get("shared").AsInt);
    }

    [Fact]
    public async Task Clear_on_stop_resets_scheme_variables()
    {
        var scheme = NewScheme(Task("set", Always, new SetVariableAction { Variable = "x", Expression = "42" }));
        var engine = Engine(scheme);
        await engine.RunAsync(Ct, maxRounds: 1);
        Assert.False(engine.Variables.Contains("x"));

        scheme.Settings.ClearVariablesOnStop = false;
        var engine2 = Engine(scheme);
        await engine2.RunAsync(Ct, maxRounds: 1);
        Assert.Equal(42, engine2.Variables.Get("x").AsInt);
    }

    [Fact]
    public async Task Failing_action_is_logged_and_does_not_stop_the_scheme()
    {
        var scheme = NewScheme(Task("t", Always, new SetVariableAction { Variable = "x", Expression = "1 // 0" }, new TextAction { Text = "after" }));
        var engine = Engine(scheme);
        var logs = new List<EngineLogEntry>();
        engine.Log += (_, e) => logs.Add(e);

        await engine.RunAsync(Ct, maxRounds: 1);

        Assert.Equal("after", Assert.Single(_input.Calls).Text);
        Assert.Contains(logs, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning && l.Message.Contains("Division", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_stops_promptly_inside_a_wait()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var scheme = NewScheme(Task("t", Always, new WaitAction { MinMs = 60_000 }));
        var engine = Engine(scheme);
        engine.TaskFired += (_, _) => cts.Cancel();

        await engine.RunAsync(cts.Token);

        Assert.Equal(EngineState.Stopped, engine.State);
        Assert.True(_clock.ElapsedMs < 60_000);
    }

    [Fact]
    public async Task Play_recording_action_uses_resolver()
    {
        var rec = new RecordingDocument
        {
            ScreenSize = Device,
            Gestures = [Gesture.KeyPress(0, KeyCodes.Home)],
        };
        var scheme = NewScheme(Task("t", Always, new PlayRecordingAction { Recording = "intro" }));
        await Engine(scheme, recordings: id => id == "intro" ? rec : null).RunAsync(Ct, maxRounds: 1);
        Assert.Equal(KeyCodes.Home, Assert.Single(_input.Calls).KeyCode);
    }

    [Fact]
    public async Task Text_interpolation_evaluates_expressions_and_escapes()
    {
        var scheme = NewScheme(Task("t", Always, new TextAction { Text = "{{n}} = {1 + 2}" }));
        await Engine(scheme).RunAsync(Ct, maxRounds: 1);
        Assert.Equal("{n} = 3", _input.Calls[0].Text);
    }
}
