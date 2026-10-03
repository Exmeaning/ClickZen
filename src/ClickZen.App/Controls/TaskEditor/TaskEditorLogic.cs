using System.Globalization;
using System.Text.Json;
using ClickZen.App.Services;
using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Core.Variables;

namespace ClickZen.App.Controls.TaskEditing;

/// <summary>The condition types the editor can create.</summary>
public enum ConditionKind
{
    Image,
    Color,
    Expression,
    Elapsed,
}

/// <summary>The action types the editor can create.</summary>
public enum ActionKind
{
    Tap,
    LongPress,
    Swipe,
    Key,
    Text,
    Wait,
    PlayRecording,
    SetVariable,
    AdbShell,
    Log,
    Stop,
}

/// <summary>A <c>{expr}</c> placeholder inside an interpolated string.</summary>
/// <param name="Start">Index of the opening brace.</param>
/// <param name="Expression">Text between the braces.</param>
public readonly record struct Placeholder(int Start, string Expression);

/// <summary>Result of scanning an interpolated string (same rules as the engine's Interpolate).</summary>
/// <param name="Placeholders">Every <c>{expr}</c> found, in order.</param>
/// <param name="UnclosedAt">Index of a <c>{</c> without a closing brace (kept literally by the engine), or -1.</param>
public sealed record PlaceholderScan(IReadOnlyList<Placeholder> Placeholders, int UnclosedAt);

/// <summary>A common Android key offered by the Key action editor.</summary>
public readonly record struct KeyPreset(int Code, string TextKey);

/// <summary>
/// UI-independent logic of the task editor: default objects, summaries, validation and list helpers.
/// Nothing here touches WinUI so it can be unit tested.
/// </summary>
public static class TaskEditorLogic
{
    /// <summary>Common keys for the Key action picker (label resource keys).</summary>
    public static IReadOnlyList<KeyPreset> KeyPresets { get; } =
    [
        new(KeyCodes.Back, "Editor_Key_Back"),
        new(KeyCodes.Home, "Editor_Key_Home"),
        new(KeyCodes.AppSwitch, "Editor_Key_AppSwitch"),
        new(KeyCodes.Power, "Editor_Key_Power"),
        new(KeyCodes.VolumeUp, "Editor_Key_VolumeUp"),
        new(KeyCodes.VolumeDown, "Editor_Key_VolumeDown"),
        new(KeyCodes.Enter, "Editor_Key_Enter"),
        new(KeyCodes.Delete, "Editor_Key_Delete"),
    ];

    // ------------------------------------------------------------------ factories

    public static Condition CreateCondition(ConditionKind kind, Scheme? scheme = null) => kind switch
    {
        ConditionKind.Image => new ImageCondition { TemplateId = scheme?.Templates.Count == 1 ? scheme.Templates[0].Id : "" },
        ConditionKind.Color => new ColorCondition(),
        ConditionKind.Expression => new ExpressionCondition(),
        ConditionKind.Elapsed => new ElapsedCondition(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static AutomationAction CreateAction(ActionKind kind) => kind switch
    {
        ActionKind.Tap => new TapAction(),
        ActionKind.LongPress => new LongPressAction(),
        ActionKind.Swipe => new SwipeAction(),
        ActionKind.Key => new KeyAction(),
        ActionKind.Text => new TextAction(),
        ActionKind.Wait => new WaitAction(),
        ActionKind.PlayRecording => new PlayRecordingAction(),
        ActionKind.SetVariable => new SetVariableAction(),
        ActionKind.AdbShell => new AdbShellAction(),
        ActionKind.Log => new LogAction(),
        ActionKind.Stop => new StopAction(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static Rule CreateRule(string name) => new() { Name = name };

    public static ActionBranch CreateBranch(string name) => new() { Name = name, Weight = 1 };

    /// <summary>Deep copy through the scheme serializer (polymorphic conditions/actions are preserved).</summary>
    public static T DeepClone<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, SchemePackage.JsonOptions), SchemePackage.JsonOptions)
        ?? throw new InvalidOperationException("Clone failed.");

    public static ConditionKind KindOf(Condition c) => c switch
    {
        ImageCondition => ConditionKind.Image,
        ColorCondition => ConditionKind.Color,
        ExpressionCondition => ConditionKind.Expression,
        ElapsedCondition => ConditionKind.Elapsed,
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    public static ActionKind KindOf(AutomationAction a) => a switch
    {
        TapAction => ActionKind.Tap,
        LongPressAction => ActionKind.LongPress,
        SwipeAction => ActionKind.Swipe,
        KeyAction => ActionKind.Key,
        TextAction => ActionKind.Text,
        WaitAction => ActionKind.Wait,
        PlayRecordingAction => ActionKind.PlayRecording,
        SetVariableAction => ActionKind.SetVariable,
        AdbShellAction => ActionKind.AdbShell,
        LogAction => ActionKind.Log,
        StopAction => ActionKind.Stop,
        _ => throw new ArgumentOutOfRangeException(nameof(a)),
    };

    /// <summary>Segoe Fluent Icons glyph for a condition type.</summary>
    public static string Glyph(ConditionKind kind) => kind switch
    {
        ConditionKind.Image => "\uE8B9",
        ConditionKind.Color => "\uE790",
        ConditionKind.Expression => "\uE943",
        _ => "\uE916",
    };

    /// <summary>Segoe Fluent Icons glyph for an action type.</summary>
    public static string Glyph(ActionKind kind) => kind switch
    {
        ActionKind.Tap => "\uE7C9",
        ActionKind.LongPress => "\uEDA4",
        ActionKind.Swipe => "\uED5F",
        ActionKind.Key => "\uE765",
        ActionKind.Text => "\uE8D2",
        ActionKind.Wait => "\uE823",
        ActionKind.PlayRecording => "\uE768",
        ActionKind.SetVariable => "\uE8EF",
        ActionKind.AdbShell => "\uE756",
        ActionKind.Log => "\uE8BD",
        _ => "\uE71A",
    };

    public static string KindText(ILocalizer loc, ConditionKind kind) => loc["Editor_Cond_" + kind];

    public static string KindText(ILocalizer loc, ActionKind kind) => loc["Editor_Act_" + kind];

    public static string LogicText(ILocalizer loc, GroupLogic logic) => loc["Editor_Logic_" + logic];

    // ------------------------------------------------------------------ list helpers

    /// <summary>Moves the item at <paramref name="index"/> by <paramref name="delta"/>; false when it cannot move.</summary>
    public static bool Move<T>(IList<T> list, int index, int delta)
    {
        var target = index + delta;
        if (index < 0 || index >= list.Count || target < 0 || target >= list.Count || delta == 0)
        {
            return false;
        }

        var item = list[index];
        list.RemoveAt(index);
        list.Insert(target, item);
        return true;
    }

    /// <summary>Share of each branch weight (0..1); non-positive weights never win. All zero → uniform.</summary>
    public static double BranchShare(IReadOnlyList<ActionBranch> branches, int index)
    {
        if (index < 0 || index >= branches.Count)
        {
            return 0;
        }

        var total = branches.Where(b => b.Weight > 0).Sum(b => b.Weight);
        if (total <= 0)
        {
            return 1.0 / branches.Count;
        }

        return branches[index].Weight > 0 ? branches[index].Weight / total : 0;
    }

    /// <summary>Counts conditions in a group tree (including nested groups' conditions).</summary>
    public static int CountConditions(ConditionGroup group) =>
        group.Conditions.Count + group.Groups.Sum(CountConditions);

    // ------------------------------------------------------------------ validation

    /// <summary>Null when <paramref name="source"/> parses, otherwise the parser's message.</summary>
    public static string? ValidateExpression(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "empty";
        }

        return Expression.TryParse(source, out _, out var error) ? null : error;
    }

    /// <summary>Scans <c>{expr}</c> placeholders exactly like the engine (<c>{{</c>/<c>}}</c> are literal braces).</summary>
    public static PlaceholderScan ScanPlaceholders(string? text)
    {
        var found = new List<Placeholder>();
        if (string.IsNullOrEmpty(text))
        {
            return new PlaceholderScan(found, -1);
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if ((c == '{' || c == '}') && i + 1 < text.Length && text[i + 1] == c)
            {
                i++;
            }
            else if (c == '{')
            {
                var end = text.IndexOf('}', i + 1);
                if (end < 0)
                {
                    return new PlaceholderScan(found, i);
                }

                found.Add(new Placeholder(i, text[(i + 1)..end]));
                i = end;
            }
        }

        return new PlaceholderScan(found, -1);
    }

    /// <summary>Validates every placeholder; returns a list of problems (empty when fine).</summary>
    public static IReadOnlyList<string> ValidateInterpolated(ILocalizer loc, string? text)
    {
        var problems = new List<string>();
        var scan = ScanPlaceholders(text);
        foreach (var p in scan.Placeholders)
        {
            var error = ValidateExpression(p.Expression);
            if (error is not null)
            {
                problems.Add(string.IsNullOrWhiteSpace(p.Expression)
                    ? loc.Format("Editor_Err_EmptyPlaceholder", p.Start + 1)
                    : loc.Format("Editor_Err_Placeholder", p.Expression, error));
            }
        }

        if (scan.UnclosedAt >= 0)
        {
            problems.Add(loc.Format("Editor_Err_Unclosed", scan.UnclosedAt + 1));
        }

        return problems;
    }

    /// <summary>Variables an expression reads that the scheme does not declare (built-ins starting with $ are skipped).</summary>
    public static IReadOnlyList<string> UndeclaredVariables(string? source, Scheme? scheme)
    {
        if (string.IsNullOrWhiteSpace(source) || !Expression.TryParse(source, out var expr, out _) || expr is null)
        {
            return [];
        }

        var declared = new HashSet<string>(scheme?.Variables.Select(v => v.Name) ?? [], StringComparer.Ordinal);
        return expr.Variables
            .Where(v => !v.StartsWith('$') && !declared.Contains(v))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Normalises "#rgb"-ish input to "#RRGGBB"; null when it is not a 6-digit hex colour.</summary>
    public static string? NormalizeColor(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var s = text.Trim().TrimStart('#');
        if (s.Length == 3 && s.All(Uri.IsHexDigit))
        {
            s = string.Concat(s.Select(ch => new string(ch, 2)));
        }

        if (s.Length != 6 || !s.All(Uri.IsHexDigit))
        {
            return null;
        }

        return "#" + s.ToUpperInvariant();
    }

    /// <summary>RGB bytes of a "#RRGGBB" colour (after <see cref="NormalizeColor"/>).</summary>
    public static bool TryParseColor(string? text, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var n = NormalizeColor(text);
        if (n is null)
        {
            return false;
        }

        var v = uint.Parse(n.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        r = (byte)(v >> 16);
        g = (byte)(v >> 8);
        b = (byte)v;
        return true;
    }

    /// <summary>
    /// Search area seeded from the rectangle a template was cut from: grown by a margin so small UI
    /// shifts still match, and clamped to the reference screen when it is known.
    /// </summary>
    public static RectI SeedSearchArea(RectI source, SizeI refSize)
    {
        if (source.IsEmpty)
        {
            return source;
        }

        var mx = Math.Max(16, source.Width / 4);
        var my = Math.Max(16, source.Height / 4);
        var grown = new RectI(source.X - mx, source.Y - my, source.Width + 2 * mx, source.Height + 2 * my);
        if (refSize.IsEmpty)
        {
            var x0 = Math.Max(0, grown.X);
            var y0 = Math.Max(0, grown.Y);
            return new RectI(x0, y0, grown.Right - x0, grown.Bottom - y0);
        }

        var clamped = grown.ClampTo(refSize);
        return clamped.IsEmpty ? source : clamped;
    }

    /// <summary>Parses the Key editor's text: a preset label, "Label (code)" or a bare number.</summary>
    public static int? ParseKeyCode(ILocalizer loc, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim();
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
        {
            return code is >= 0 and <= 1000 ? code : null;
        }

        foreach (var p in KeyPresets)
        {
            if (string.Equals(t, KeyPresetText(loc, p), StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, loc[p.TextKey], StringComparison.OrdinalIgnoreCase))
            {
                return p.Code;
            }
        }

        // "Something (123)"
        var open = t.LastIndexOf('(');
        if (open >= 0 && t.EndsWith(')')
            && int.TryParse(t.AsSpan(open + 1, t.Length - open - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out code)
            && code is >= 0 and <= 1000)
        {
            return code;
        }

        return null;
    }

    public static string KeyPresetText(ILocalizer loc, KeyPreset p) =>
        string.Format(CultureInfo.InvariantCulture, "{0} ({1})", loc[p.TextKey], p.Code);

    public static string KeyText(ILocalizer loc, int code)
    {
        foreach (var p in KeyPresets)
        {
            if (p.Code == code)
            {
                return loc[p.TextKey];
            }
        }

        return code.ToString(CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ summaries

    public static string TemplateName(Scheme? scheme, string templateId)
    {
        if (string.IsNullOrEmpty(templateId))
        {
            return "";
        }

        var t = scheme?.Templates.FirstOrDefault(x => x.Id == templateId);
        return t is null ? templateId : string.IsNullOrWhiteSpace(t.Name) ? t.Id : t.Name;
    }

    public static string Summary(ILocalizer loc, Condition c, Scheme? scheme)
    {
        var body = c switch
        {
            ImageCondition ic => loc.Format(ic.ExpectFound ? "Editor_Sum_Image" : "Editor_Sum_ImageAbsent",
                string.IsNullOrEmpty(ic.TemplateId) ? loc["Editor_NoTemplate"] : TemplateName(scheme, ic.TemplateId),
                ic.Threshold.ToString("0.00", CultureInfo.InvariantCulture),
                ic.Area.IsEmpty ? loc["Editor_WholeScreen"] : AreaText(ic.Area)),
            ColorCondition cc => loc.Format(cc.ExpectMatch ? "Editor_Sum_Color" : "Editor_Sum_ColorNot",
                cc.Point.ToString(), cc.Color, cc.Tolerance),
            ExpressionCondition ec => loc.Format("Editor_Sum_Expression", ec.Expression),
            ElapsedCondition el => loc.Format(el.Since == ElapsedSince.SchemeStart ? "Editor_Sum_ElapsedStart" : "Editor_Sum_ElapsedTask",
                DurationText(el.AtLeastMs)),
            _ => c.GetType().Name,
        };
        return string.IsNullOrWhiteSpace(c.Label) ? body : c.Label + "  ·  " + body;
    }

    public static string Summary(ILocalizer loc, ConditionGroup g)
    {
        var count = g.Conditions.Count + g.Groups.Count;
        return count == 0
            ? loc.Format("Editor_Sum_GroupEmpty", LogicText(loc, g.Logic))
            : loc.Format("Editor_Sum_Group", LogicText(loc, g.Logic), count);
    }

    public static string Summary(ILocalizer loc, AutomationAction a, Scheme? scheme)
    {
        var text = a switch
        {
            TapAction t => t.Count > 1
                ? loc.Format("Editor_Sum_TapN", TargetText(loc, t.Target), t.Count)
                : loc.Format("Editor_Sum_Tap", TargetText(loc, t.Target)),
            LongPressAction lp => loc.Format("Editor_Sum_LongPress", TargetText(loc, lp.Target), lp.DurationMs),
            SwipeAction s => loc.Format("Editor_Sum_Swipe", TargetText(loc, s.From), TargetText(loc, s.To), s.DurationMs),
            KeyAction k => loc.Format("Editor_Sum_Key", KeyText(loc, k.KeyCode)),
            TextAction tx => loc.Format("Editor_Sum_Text", Ellipsis(tx.Text)),
            WaitAction w => w.MaxMs > w.MinMs
                ? loc.Format("Editor_Sum_WaitRange", w.MinMs, w.MaxMs)
                : loc.Format("Editor_Sum_Wait", w.MinMs),
            PlayRecordingAction pr => loc.Format("Editor_Sum_PlayRecording",
                string.IsNullOrWhiteSpace(pr.Recording) ? loc["Editor_NotSet"] : pr.Recording,
                pr.Speed.ToString("0.##", CultureInfo.InvariantCulture), Math.Max(1, pr.Loops)),
            SetVariableAction sv => loc.Format("Editor_Sum_SetVariable",
                string.IsNullOrWhiteSpace(sv.Variable) ? loc["Editor_NotSet"] : sv.Variable, Ellipsis(sv.Expression)),
            AdbShellAction sh => loc.Format(sh.AsRoot ? "Editor_Sum_AdbShellRoot" : "Editor_Sum_AdbShell", Ellipsis(sh.Command)),
            LogAction l => loc.Format("Editor_Sum_Log", Ellipsis(l.Message)),
            StopAction st => loc[st.Scope == StopScope.Task ? "Editor_Sum_StopTask" : "Editor_Sum_StopScheme"],
            _ => a.GetType().Name,
        };
        _ = scheme;
        return a.DelayAfterMs is { } d ? text + "  ·  " + loc.Format("Editor_Sum_Delay", d) : text;
    }

    public static string Summary(ILocalizer loc, Rule r, int number)
    {
        var name = string.IsNullOrWhiteSpace(r.Name) ? loc.Format("Editor_RuleN", number) : r.Name;
        var actions = r.Pick == RulePick.Sequential
            ? loc.Format("Editor_Sum_RuleActions", r.Then.Count)
            : loc.Format("Editor_Sum_RuleBranches", r.Branches.Count);
        var when = r.When.IsEmpty ? loc["Editor_Sum_Always"] : loc.Format("Editor_Sum_RuleConditions", CountConditions(r.When));
        return $"{name}  ·  {when} → {actions}";
    }

    public static string Summary(ILocalizer loc, ActionBranch b, IReadOnlyList<ActionBranch> all, int number)
    {
        var name = string.IsNullOrWhiteSpace(b.Name) ? loc.Format("Editor_BranchN", number) : b.Name;
        var share = BranchShare(all, number - 1);
        return loc.Format("Editor_Sum_Branch", name,
            b.Weight.ToString("0.##", CultureInfo.InvariantCulture),
            share.ToString("P0", CultureInfo.CurrentCulture),
            b.Actions.Count);
    }

    public static string TargetText(ILocalizer loc, Target t)
    {
        var baseText = t.Kind switch
        {
            TargetKind.LastMatch => loc["Editor_Target_LastMatchShort"],
            TargetKind.RandomInArea => loc.Format("Editor_Target_RandomShort", AreaText(t.Area)),
            _ => "(" + t.Point + ")",
        };
        return t.Offset == default
            ? baseText
            : baseText + string.Format(CultureInfo.InvariantCulture, " {0:+0;-0;0},{1:+0;-0;0}", t.Offset.X, t.Offset.Y);
    }

    public static string AreaText(RectI r) =>
        string.Format(CultureInfo.InvariantCulture, "({0}, {1}, {2}×{3})", r.X, r.Y, r.Width, r.Height);

    public static string DurationText(int ms) =>
        ms >= 1000 && ms % 100 == 0
            ? (ms / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s"
            : ms.ToString(CultureInfo.InvariantCulture) + " ms";

    /// <summary>Formats a one-shot condition test for display under the Test button.</summary>
    public static string TestResultText(ILocalizer loc, Condition c, ConditionTestResult r)
    {
        var verdict = loc[r.Matched ? "Editor_Test_Matched" : "Editor_Test_NotMatched"];
        var parts = new List<string> { verdict };
        if (!double.IsNaN(r.Score))
        {
            parts.Add(c is ColorCondition
                ? loc.Format("Editor_Test_Distance", r.Score.ToString("0", CultureInfo.InvariantCulture))
                : loc.Format("Editor_Test_Score", r.Score.ToString("0.000", CultureInfo.InvariantCulture)));
        }

        if (r.Location is { } loc2 && c is ImageCondition)
        {
            parts.Add(loc.Format("Editor_Test_At", AreaText(loc2)));
        }

        if (!string.IsNullOrWhiteSpace(r.Message))
        {
            parts.Add(r.Message);
        }

        return string.Join("  ·  ", parts);
    }

    private static string Ellipsis(string? s, int max = 32)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "\"\"";
        }

        var one = s.ReplaceLineEndings(" ");
        return one.Length <= max ? one : one[..(max - 1)] + "…";
    }
}
