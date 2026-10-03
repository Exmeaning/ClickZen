using System.Globalization;
using System.Text;
using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Core.Recording;
using ClickZen.Core.Variables;
using Microsoft.Extensions.Logging;

namespace ClickZen.Core.Automation;

/// <summary>
/// Executes a <see cref="Scheme"/> against one device.
/// <para>Round semantics:</para>
/// <list type="number">
/// <item>Take the newest frame (a frame captured after the last input action, if there was one).</item>
/// <item>Visit enabled tasks by priority (desc), then list order. Skip tasks that are disabled,
///       over their run limit, or still cooling down (cooldown counts from the end of the last run).</item>
/// <item>Check the task's precondition, then each rule in order; a matching rule runs its actions.
///       As soon as an action touched the screen, later conditions use a fresh frame.</item>
/// <item>Sleep <see cref="SchemeSettings.CheckIntervalMs"/>.</item>
/// </list>
/// Cancellation stops immediately, including inside waits and recordings.
/// </summary>
public sealed class AutomationEngine
{
    private readonly Scheme _scheme;
    private readonly EngineEnvironment _env;
    private readonly Humanizer _humanizer;
    private readonly VariableStore _globals;
    private readonly Dictionary<string, TemplateAsset> _templates;
    private readonly Dictionary<string, TaskRuntime> _runtime = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Expression?> _expressions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _warnedAt = new(StringComparer.Ordinal);
    private long _startMs;
    private bool _needFreshFrame;
    private long _actionFrameSequence = -1;
    private EngineState _state = EngineState.Idle;

    public AutomationEngine(Scheme scheme, EngineEnvironment env)
    {
        _scheme = scheme ?? throw new ArgumentNullException(nameof(scheme));
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _humanizer = new Humanizer(env.Random);
        _globals = env.GlobalVariables ?? new VariableStore();
        Variables = new VariableStore(_globals);
        _templates = scheme.Templates.Where(t => !string.IsNullOrEmpty(t.Id))
            .GroupBy(t => t.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var t in scheme.Tasks)
        {
            _runtime[t.Id] = new TaskRuntime();
        }
    }

    /// <summary>Scheme-scope variables (falls through to the global store).</summary>
    public VariableStore Variables { get; }

    public Scheme Scheme => _scheme;

    public EngineState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                StateChanged?.Invoke(this, value);
            }
        }
    }

    public long Rounds { get; private set; }

    public IReadOnlyDictionary<string, TaskRuntime> Runtime => _runtime;

    public event EventHandler<EngineLogEntry>? Log;
    public event EventHandler<TaskFiredEventArgs>? TaskFired;
    public event EventHandler<EngineState>? StateChanged;

    /// <summary>Initialises variables and per-task state; called automatically by <see cref="RunAsync"/>.</summary>
    public void Reset()
    {
        foreach (var def in _scheme.Variables.Where(d => !string.IsNullOrWhiteSpace(d.Name)))
        {
            var initial = def.Initial.ConvertTo(def.Type);
            if (def.Scope == VariableScope.Global)
            {
                // Another device may already be running with this global – do not reset it.
                if (!_globals.Contains(def.Name))
                {
                    _globals.Declare(def.Name, def.Type, initial);
                }
            }
            else
            {
                Variables.Declare(def.Name, def.Type, initial);
            }
        }

        foreach (var rt in _runtime.Values)
        {
            rt.LastRunEndMs = null;
            rt.RunCount = 0;
            rt.Disabled = false;
            rt.LastRule = null;
        }

        Rounds = 0;
        _startMs = _env.Clock.ElapsedMs;
        _needFreshFrame = false;
        _actionFrameSequence = -1;
    }

    /// <summary>Runs until cancelled, a Stop action fires, the time limit elapses, or <paramref name="maxRounds"/> is reached.</summary>
    public async Task RunAsync(CancellationToken ct, int maxRounds = 0)
    {
        Reset();
        State = EngineState.Running;
        Emit(LogLevel.Information, $"Scheme \"{_scheme.Name}\" started", null);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var keepGoing = await RunRoundAsync(ct);
                if (!keepGoing)
                {
                    break;
                }

                if (maxRounds > 0 && Rounds >= maxRounds)
                {
                    break;
                }

                if (_scheme.Settings.MaxRunSeconds > 0 && _env.Clock.ElapsedMs - _startMs >= _scheme.Settings.MaxRunSeconds * 1000L)
                {
                    Emit(LogLevel.Information, "Time limit reached", null);
                    break;
                }

                await _env.Clock.DelayAsync(TimeSpan.FromMilliseconds(Math.Max(0, _scheme.Settings.CheckIntervalMs)), ct);
            }

            State = EngineState.Stopped;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            State = EngineState.Stopped;
        }
        catch (Exception ex)
        {
            State = EngineState.Faulted;
            Emit(LogLevel.Error, $"Engine stopped by an error: {ex.Message}", null);
            _env.Logger?.LogError(ex, "Automation engine faulted ({Device})", _env.DeviceLabel);
        }
        finally
        {
            if (_scheme.Settings.ClearVariablesOnStop)
            {
                Variables.Clear();
            }

            Emit(LogLevel.Information, $"Scheme \"{_scheme.Name}\" stopped after {Rounds} round(s)", null);
        }
    }

    /// <summary>Evaluates every task once. Returns false when a Stop(Scheme) action fired.</summary>
    public async Task<bool> RunRoundAsync(CancellationToken ct)
    {
        Rounds++;
        var frame = await NextFrameAsync(ct);
        if (frame is null)
        {
            WarnOnce("noframe", "No screen image available yet – waiting for the device.", null);
            return true;
        }

        var ordered = _scheme.Tasks.Select((t, i) => (t, i))
            .Where(x => x.t.Enabled)
            .OrderByDescending(x => x.t.Priority).ThenBy(x => x.i)
            .Select(x => x.t);

        foreach (var task in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var rt = _runtime.TryGetValue(task.Id, out var r) ? r : _runtime[task.Id] = new TaskRuntime();
            if (rt.Disabled || (task.RunLimit > 0 && rt.RunCount >= task.RunLimit))
            {
                continue;
            }

            var now = _env.Clock.ElapsedMs;
            if (rt.LastRunEndMs is { } last && now - last < task.CooldownMs)
            {
                continue;
            }

            var ctx = new EvalContext(frame, ScreenSize(frame), task, rt);
            if (!EvalGroup(task.Precondition, ctx))
            {
                continue;
            }

            // A match found by the precondition is visible to every rule; a rule's own match only to itself.
            var preMatch = ctx.LastMatch;
            var preScore = ctx.LastScore;
            var fired = false;
            foreach (var rule in task.Rules.Where(r => r.Enabled))
            {
                ctx.LastMatch = preMatch;
                ctx.LastScore = preScore;
                if (!EvalGroup(rule.When, ctx))
                {
                    continue;
                }

                fired = true;
                rt.LastRule = rule.Name;
                TaskFired?.Invoke(this, new TaskFiredEventArgs(task.Id, task.Name, rule.Name,
                    ctx.LastMatch is { } m ? new MatchResult(true, ctx.LastScore, new RectI(m.X, m.Y, 0, 0)) : null));
                Emit(LogLevel.Information, string.IsNullOrEmpty(rule.Name) ? $"{task.Name}: triggered" : $"{task.Name} / {rule.Name}: triggered", task.Id);

                await ExecuteRuleAsync(rule, ctx, ct);
                if (ctx.StopScheme)
                {
                    Emit(LogLevel.Information, $"{task.Name}: stop requested", task.Id);
                    rt.RunCount++;
                    rt.LastRunEndMs = _env.Clock.ElapsedMs;
                    return false;
                }

                if (ctx.TouchedScreen)
                {
                    frame = await NextFrameAsync(ct) ?? frame;
                    ctx.Frame = frame;
                    ctx.TouchedScreen = false;
                }

                if (rule.StopAfterMatch || rt.Disabled)
                {
                    break;
                }
            }

            if (fired)
            {
                rt.RunCount++;
                rt.LastRunEndMs = _env.Clock.ElapsedMs;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------ frames

    private async Task<Frame?> NextFrameAsync(CancellationToken ct)
    {
        if (_needFreshFrame)
        {
            _needFreshFrame = false;
            return await _env.Frames.WaitForFrameAsync(_actionFrameSequence, _env.FreshFrameTimeout, ct);
        }

        return _env.Frames.Latest ?? await _env.Frames.WaitForFrameAsync(-1, _env.FreshFrameTimeout, ct);
    }

    private SizeI ScreenSize(Frame frame)
    {
        var s = _env.ScreenSize();
        return s.IsEmpty ? frame.Size : s;
    }

    private SizeI RefSize(SizeI screen) => _scheme.RefSize.IsEmpty ? screen : _scheme.RefSize;

    // ------------------------------------------------------------------ conditions

    private sealed class EvalContext(Frame frame, SizeI screen, AutomationTask task, TaskRuntime runtime)
    {
        public Frame Frame { get; set; } = frame;
        public SizeI Screen { get; } = screen;
        public AutomationTask Task { get; } = task;
        public TaskRuntime Runtime { get; } = runtime;

        /// <summary>Centre of the latest successful image match, in current device pixels.</summary>
        public PointI? LastMatch { get; set; }

        public double LastScore { get; set; }
        public bool TouchedScreen { get; set; }
        public bool StopScheme { get; set; }
    }

    /// <summary>
    /// All: true unless a child is false (empty → true).
    /// Any: true if a child is true (empty → false).
    /// None: true unless a child is true (empty → true).
    /// Children are evaluated left to right and short-circuit.
    /// </summary>
    private bool EvalGroup(ConditionGroup group, EvalContext ctx)
    {
        foreach (var c in group.Conditions.Where(c => c.Enabled))
        {
            var v = EvalCondition(c, ctx);
            switch (group.Logic)
            {
                case GroupLogic.All when !v: return false;
                case GroupLogic.Any when v: return true;
                case GroupLogic.None when v: return false;
            }
        }

        foreach (var g in group.Groups)
        {
            var v = EvalGroup(g, ctx);
            switch (group.Logic)
            {
                case GroupLogic.All when !v: return false;
                case GroupLogic.Any when v: return true;
                case GroupLogic.None when v: return false;
            }
        }

        return group.Logic != GroupLogic.Any;
    }

    private bool EvalCondition(Condition c, EvalContext ctx) => c switch
    {
        ImageCondition ic => EvalImage(ic, ctx),
        ColorCondition cc => EvalColor(cc, ctx),
        ExpressionCondition ec => EvalExpression(ec.Expression, ctx, ctx.Task.Id)?.AsBool ?? false,
        ElapsedCondition el => EvalElapsed(el, ctx),
        _ => false,
    };

    private bool EvalImage(ImageCondition ic, EvalContext ctx)
    {
        if (!_templates.TryGetValue(ic.TemplateId, out var template) || template.Png.Length == 0)
        {
            WarnOnce("tpl:" + ic.TemplateId, $"{ctx.Task.Name}: template is missing", ctx.Task.Id);
            return !ic.ExpectFound;
        }

        var frame = ctx.Frame;
        var refSize = RefSize(ctx.Screen);
        var map = new FrameToDevice(frame.Size, ctx.Screen);
        var deviceArea = ic.Area.IsEmpty
            ? new RectI(0, 0, ctx.Screen.Width, ctx.Screen.Height)
            : RefScaling.Scale(ic.Area, refSize, ctx.Screen);
        var frameArea = map.DeviceToFrameRect(deviceArea);
        if (frameArea.IsEmpty)
        {
            return !ic.ExpectFound;
        }

        var scale = ((double)frame.Width / refSize.Width + (double)frame.Height / refSize.Height) / 2;
        var result = _env.Matcher.Match(frame, frameArea, template, scale, ic);
        if (result.Found)
        {
            ctx.LastMatch = map.ClampToDevice(map.FrameToDevicePoint(result.Center));
            ctx.LastScore = result.Score;
        }

        return ic.ExpectFound ? result.Found : !result.Found;
    }

    private bool EvalColor(ColorCondition cc, EvalContext ctx)
    {
        if (!TryParseColor(cc.Color, out var r, out var g, out var b))
        {
            WarnOnce("color:" + cc.Color, $"{ctx.Task.Name}: invalid colour \"{cc.Color}\"", ctx.Task.Id);
            return false;
        }

        var map = new FrameToDevice(ctx.Frame.Size, ctx.Screen);
        var device = RefScaling.Scale(cc.Point, RefSize(ctx.Screen), ctx.Screen);
        var fp = map.DeviceToFramePoint(device).Round();
        if (fp.X < 0 || fp.Y < 0 || fp.X >= ctx.Frame.Width || fp.Y >= ctx.Frame.Height)
        {
            return false;
        }

        var (pr, pg, pb) = ctx.Frame.PixelAt(fp.X, fp.Y);
        var tol = Math.Clamp(cc.Tolerance, 0, 255);
        var match = Math.Abs(pr - r) <= tol && Math.Abs(pg - g) <= tol && Math.Abs(pb - b) <= tol;
        return cc.ExpectMatch ? match : !match;
    }

    internal static bool TryParseColor(string text, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var s = text.Trim().TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
        {
            return false;
        }

        r = (byte)(v >> 16);
        g = (byte)(v >> 8);
        b = (byte)v;
        return true;
    }

    private bool EvalElapsed(ElapsedCondition el, EvalContext ctx)
    {
        var now = _env.Clock.ElapsedMs;
        return el.Since switch
        {
            ElapsedSince.SchemeStart => now - _startMs >= el.AtLeastMs,
            _ => ctx.Runtime.LastRunEndMs is not { } last || now - last >= el.AtLeastMs,
        };
    }

    private VariableValue? EvalExpression(string source, EvalContext ctx, string? taskId)
    {
        if (!_expressions.TryGetValue(source, out var expr))
        {
            if (!Expression.TryParse(source, out expr, out var err))
            {
                WarnOnce("parse:" + source, $"Invalid expression \"{source}\": {err}", taskId);
            }

            _expressions[source] = expr;
        }

        if (expr is null)
        {
            return null;
        }

        try
        {
            return expr.Evaluate(new StoreExpressionContext(Variables, BuildExtras(ctx), _env.Random));
        }
        catch (ExpressionException ex)
        {
            WarnOnce("eval:" + source, $"Expression \"{source}\" failed: {ex.Message}", taskId);
            return null;
        }
    }

    private Dictionary<string, VariableValue> BuildExtras(EvalContext ctx)
    {
        var d = new Dictionary<string, VariableValue>(StringComparer.Ordinal)
        {
            ["$task.runs"] = ctx.Runtime.RunCount,
            ["$elapsed"] = _env.Clock.ElapsedMs - _startMs,
            ["$screen.width"] = ctx.Screen.Width,
            ["$screen.height"] = ctx.Screen.Height,
        };
        if (ctx.LastMatch is { } m)
        {
            d["$match.x"] = m.X;
            d["$match.y"] = m.Y;
            d["$match.score"] = ctx.LastScore;
        }

        return d;
    }

    // ------------------------------------------------------------------ actions

    private async Task ExecuteRuleAsync(Rule rule, EvalContext ctx, CancellationToken ct)
    {
        IReadOnlyList<AutomationAction> actions = rule.Then;
        if (rule.Pick == RulePick.RandomOne)
        {
            var branches = rule.Branches;
            var idx = _humanizer.PickWeighted(branches.Select(b => b.Weight).ToArray());
            if (idx < 0)
            {
                return;
            }

            actions = branches[idx].Actions;
            if (!string.IsNullOrEmpty(branches[idx].Name))
            {
                Emit(LogLevel.Debug, $"  branch: {branches[idx].Name}", ctx.Task.Id);
            }
        }

        foreach (var action in actions.Where(a => a.Enabled))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ExecuteActionAsync(action, ctx, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Emit(LogLevel.Warning, $"{ctx.Task.Name}: {ActionName(action)} failed: {ex.Message}", ctx.Task.Id);
                _env.Logger?.LogWarning(ex, "Action {Action} failed ({Device})", ActionName(action), _env.DeviceLabel);
            }

            if (ctx.StopScheme || ctx.Runtime.Disabled)
            {
                return;
            }

            var delay = _humanizer.Delay(action.DelayAfterMs ?? _scheme.Settings.DefaultDelayAfterMs, _scheme.Settings.Humanize);
            if (delay > 0)
            {
                await _env.Clock.DelayAsync(TimeSpan.FromMilliseconds(delay), ct);
            }
        }
    }

    private async Task ExecuteActionAsync(AutomationAction action, EvalContext ctx, CancellationToken ct)
    {
        var h = _scheme.Settings.Humanize;
        switch (action)
        {
            case TapAction tap:
            {
                for (var i = 0; i < Math.Max(1, tap.Count); i++)
                {
                    var p = ResolveTarget(tap.Target, ctx, jitter: true);
                    await _env.Input.TapAsync(p, Math.Max(1, _humanizer.Duration(60, h)), ct);
                    MarkTouched(ctx);
                    if (i < tap.Count - 1)
                    {
                        await _env.Clock.DelayAsync(TimeSpan.FromMilliseconds(_humanizer.Delay(tap.IntervalMs, h)), ct);
                    }
                }

                break;
            }

            case LongPressAction lp:
                await _env.Input.TapAsync(ResolveTarget(lp.Target, ctx, jitter: true), Math.Max(50, _humanizer.Duration(lp.DurationMs, h)), ct);
                MarkTouched(ctx);
                break;

            case SwipeAction sw:
            {
                var from = ResolveTarget(sw.From, ctx, jitter: true);
                var to = ResolveTarget(sw.To, ctx, jitter: true);
                var path = _humanizer.SwipePath(from, to, Math.Max(20, _humanizer.Duration(sw.DurationMs, h)), h);
                await _env.Input.StrokeAsync(path, ct);
                MarkTouched(ctx);
                break;
            }

            case KeyAction key:
                await _env.Input.KeyAsync(key.KeyCode, ct);
                MarkTouched(ctx);
                break;

            case TextAction text:
                await _env.Input.TextAsync(Interpolate(text.Text, ctx), ct);
                MarkTouched(ctx);
                break;

            case WaitAction wait:
            {
                var ms = wait.MaxMs > wait.MinMs ? _humanizer.Between(wait.MinMs, wait.MaxMs) : _humanizer.Delay(wait.MinMs, h);
                await _env.Clock.DelayAsync(TimeSpan.FromMilliseconds(Math.Max(0, ms)), ct);
                break;
            }

            case PlayRecordingAction pr:
            {
                var doc = _env.ResolveRecording?.Invoke(pr.Recording);
                if (doc is null)
                {
                    Emit(LogLevel.Warning, $"{ctx.Task.Name}: recording \"{pr.Recording}\" not found", ctx.Task.Id);
                    break;
                }

                var player = new RecordingPlayer(_env.Input, _env.Clock, _humanizer);
                await player.PlayAsync(doc, new PlaybackOptions { Speed = pr.Speed, Loops = Math.Max(1, pr.Loops), Humanize = h },
                    ctx.Screen, _env.DpScale, ct);
                MarkTouched(ctx);
                break;
            }

            case SetVariableAction sv:
            {
                var value = EvalExpression(sv.Expression, ctx, ctx.Task.Id);
                if (value is { } v && !string.IsNullOrWhiteSpace(sv.Variable))
                {
                    Variables.Set(sv.Variable, v, VariableChangeSource.Engine);
                    Emit(LogLevel.Debug, $"  {sv.Variable} = {Variables.Get(sv.Variable)}", ctx.Task.Id);
                }

                break;
            }

            case AdbShellAction sh:
            {
                if (_env.Shell is null)
                {
                    WarnOnce("noshell", "ADB shell is not available for this device", ctx.Task.Id);
                    break;
                }

                var output = await _env.Shell.ShellAsync(Interpolate(sh.Command, ctx), sh.AsRoot, ct);
                if (!string.IsNullOrWhiteSpace(sh.OutputVariable))
                {
                    Variables.Set(sh.OutputVariable, VariableValue.Parse(output.Trim()), VariableChangeSource.Engine);
                }

                break;
            }

            case LogAction log:
                Emit(LogLevel.Information, $"{ctx.Task.Name}: {Interpolate(log.Message, ctx)}", ctx.Task.Id);
                break;

            case StopAction stop:
                if (stop.Scope == StopScope.Scheme)
                {
                    ctx.StopScheme = true;
                }
                else
                {
                    ctx.Runtime.Disabled = true;
                    Emit(LogLevel.Information, $"{ctx.Task.Name}: task disabled", ctx.Task.Id);
                }

                break;
        }
    }

    private void MarkTouched(EvalContext ctx)
    {
        ctx.TouchedScreen = true;
        _needFreshFrame = true;
        _actionFrameSequence = Math.Max(_actionFrameSequence, ctx.Frame.Sequence);
    }

    private PointI ResolveTarget(Target t, EvalContext ctx, bool jitter)
    {
        var refSize = RefSize(ctx.Screen);
        PointI p;
        switch (t.Kind)
        {
            case TargetKind.LastMatch when ctx.LastMatch is { } m:
                p = m;
                break;
            case TargetKind.RandomInArea when !t.Area.IsEmpty:
                p = _humanizer.PointIn(RefScaling.Scale(t.Area, refSize, ctx.Screen));
                jitter = false;
                break;
            default:
                p = RefScaling.Scale(t.Point, refSize, ctx.Screen);
                break;
        }

        if (t.Offset != default)
        {
            var off = RefScaling.Scale(t.Offset, refSize, ctx.Screen);
            p = new PointI(p.X + off.X, p.Y + off.Y);
        }

        p = new PointI(Math.Clamp(p.X, 0, Math.Max(0, ctx.Screen.Width - 1)), Math.Clamp(p.Y, 0, Math.Max(0, ctx.Screen.Height - 1)));
        return jitter ? _humanizer.Jitter(p, _scheme.Settings.Humanize, _env.DpScale, ctx.Screen) : p;
    }

    /// <summary>Replaces <c>{expr}</c> with its value; <c>{{</c> and <c>}}</c> are literal braces.</summary>
    private string Interpolate(string text, EvalContext ctx)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('{', StringComparison.Ordinal) < 0)
        {
            return text.Replace("}}", "}", StringComparison.Ordinal);
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{' && i + 1 < text.Length && text[i + 1] == '{')
            {
                sb.Append('{');
                i++;
            }
            else if (c == '}' && i + 1 < text.Length && text[i + 1] == '}')
            {
                sb.Append('}');
                i++;
            }
            else if (c == '{')
            {
                var end = text.IndexOf('}', i + 1);
                if (end < 0)
                {
                    sb.Append(text, i, text.Length - i);
                    break;
                }

                var value = EvalExpression(text[(i + 1)..end], ctx, ctx.Task.Id);
                sb.Append(value?.AsString ?? "");
                i = end;
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string ActionName(AutomationAction a) => a.GetType().Name.Replace("Action", "", StringComparison.Ordinal);

    // ------------------------------------------------------------------ logging

    private void Emit(LogLevel level, string message, string? taskId)
    {
        Log?.Invoke(this, new EngineLogEntry(_env.Clock.ElapsedMs, level, message, taskId));
        _env.Logger?.Log(level, "[{Device}] {Message}", _env.DeviceLabel, message);
    }

    /// <summary>Emits a warning at most once every 10 seconds per key.</summary>
    private void WarnOnce(string key, string message, string? taskId)
    {
        var now = _env.Clock.ElapsedMs;
        if (_warnedAt.TryGetValue(key, out var at) && now - at < 10_000)
        {
            return;
        }

        _warnedAt[key] = now;
        Emit(LogLevel.Warning, message, taskId);
    }
}
