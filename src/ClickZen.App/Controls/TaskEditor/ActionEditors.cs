using System.Globalization;
using ClickZen.Core.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClickZen.App.Controls.TaskEditing;

/// <summary>Builds action lists (add / reorder / enable / duplicate / delete) and per-type inline editors.</summary>
internal static class ActionEditors
{
    private static readonly ActionKind[][] MenuGroups =
    [
        [ActionKind.Tap, ActionKind.LongPress, ActionKind.Swipe, ActionKind.Key, ActionKind.Text],
        [ActionKind.Wait, ActionKind.PlayRecording, ActionKind.SetVariable, ActionKind.AdbShell, ActionKind.Log, ActionKind.Stop],
    ];

    /// <summary>An editable action list with an "Add action" menu at the bottom.</summary>
    public static FrameworkElement List(EditorContext ctx, List<AutomationAction> actions, Action touch)
    {
        var loc = ctx.Loc;
        var items = new StackPanel { Spacing = 6 };
        var empty = Ui.Hint(loc["Editor_NoActions"]);

        var menu = new MenuFlyout();
        for (var g = 0; g < MenuGroups.Length; g++)
        {
            if (g > 0)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
            }

            foreach (var k in MenuGroups[g])
            {
                menu.Items.Add(Ui.MenuItem(TaskEditorLogic.Glyph(k), TaskEditorLogic.KindText(loc, k), (_, _) =>
                {
                    var a = TaskEditorLogic.CreateAction(k);
                    actions.Add(a);
                    ctx.Expanded.Add(a);
                    Rebuild();
                    touch();
                }));
            }
        }

        var add = Ui.AddMenuButton(loc["Editor_AddAction"], menu);
        var root = Ui.VStack(8, items, empty, add);
        Rebuild();
        return root;

        void Rebuild()
        {
            items.Children.Clear();
            for (var i = 0; i < actions.Count; i++)
            {
                items.Children.Add(Item(ctx, actions, i, touch, Rebuild));
            }

            empty.Visibility = actions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            items.Visibility = actions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static FrameworkElement Item(EditorContext ctx, List<AutomationAction> list, int index, Action touch, Action rebuild)
    {
        var loc = ctx.Loc;
        var a = list[index];
        var kind = TaskEditorLogic.KindOf(a);
        EditorCard card = null!;
        card = new EditorCard(ctx, a, TaskEditorLogic.Glyph(kind), "EditorItemCardStyle", () => Body(ctx, a, Touch));
        card.AddItemCommands(a.Enabled,
            on =>
            {
                a.Enabled = on;
                Touch();
            },
            index, list.Count,
            delta =>
            {
                if (TaskEditorLogic.Move(list, index, delta))
                {
                    rebuild();
                    touch();
                }
            },
            () =>
            {
                list.Insert(index + 1, TaskEditorLogic.DeepClone(a));
                rebuild();
                touch();
            },
            () =>
            {
                ctx.Expanded.Remove(a);
                list.RemoveAt(index);
                rebuild();
                touch();
            });

        UpdateTitle();
        return card.Root;

        void Touch()
        {
            UpdateTitle();
            touch();
        }

        void UpdateTitle()
        {
            card.SetTitle(string.Format(CultureInfo.InvariantCulture, "{0}. {1}:  {2}",
                index + 1, TaskEditorLogic.KindText(loc, kind), TaskEditorLogic.Summary(loc, a, ctx.Scheme)));
            card.SetDimmed(!a.Enabled);
        }
    }

    private static FrameworkElement Body(EditorContext ctx, AutomationAction a, Action touch)
    {
        var loc = ctx.Loc;
        var panel = new StackPanel { Spacing = 10 };
        switch (a)
        {
            case TapAction t:
                panel.Children.Add(CoordinateFields.Target(ctx, loc["Editor_Target"], t.Target, touch));
                panel.Children.Add(Ui.Columns("*,*",
                    Ui.IntBox(loc["Editor_TapCount"], t.Count, 1, 1000, v => t.Count = v, touch),
                    Ui.IntBox(loc["Editor_IntervalMs"], t.IntervalMs, 0, 60_000, v => t.IntervalMs = v, touch, 10)));
                break;

            case LongPressAction lp:
                panel.Children.Add(CoordinateFields.Target(ctx, loc["Editor_Target"], lp.Target, touch));
                panel.Children.Add(Ui.Columns("*,*",
                    Ui.IntBox(loc["Editor_DurationMs"], lp.DurationMs, 1, 600_000, v => lp.DurationMs = v, touch, 50), null));
                break;

            case SwipeAction s:
                panel.Children.Add(CoordinateFields.Target(ctx, loc["Editor_SwipeFrom"], s.From, touch));
                panel.Children.Add(CoordinateFields.Target(ctx, loc["Editor_SwipeTo"], s.To, touch));
                panel.Children.Add(Ui.Columns("*,*",
                    Ui.IntBox(loc["Editor_DurationMs"], s.DurationMs, 1, 600_000, v => s.DurationMs = v, touch, 50), null));
                break;

            case KeyAction k:
                panel.Children.Add(KeyField(ctx, k, touch));
                break;

            case TextAction tx:
                panel.Children.Add(ExpressionFields.Interpolated(ctx, loc["Editor_Text"], tx.Text, v => tx.Text = v, touch, multiline: true));
                break;

            case WaitAction w:
                panel.Children.Add(Ui.Columns("*,*",
                    Ui.IntBox(loc["Editor_MinMs"], w.MinMs, 0, 86_400_000, v => w.MinMs = v, touch, 100),
                    Ui.IntBox(loc["Editor_MaxMs"], w.MaxMs, 0, 86_400_000, v => w.MaxMs = v, touch, 100)));
                panel.Children.Add(Ui.Hint(loc["Editor_WaitHint"]));
                break;

            case PlayRecordingAction pr:
                panel.Children.Add(Ui.EditableCombo(loc["Editor_Recording"], (ctx.Host?.EmbeddedRecordings ?? []).Order(StringComparer.CurrentCulture),
                    pr.Recording, loc["Editor_RecordingPlaceholder"], v =>
                    {
                        pr.Recording = v;
                        touch();
                    }));
                panel.Children.Add(Ui.Hint(loc["Editor_RecordingHint"]));
                panel.Children.Add(Ui.Columns("*,*",
                    Ui.DoubleBox(loc["Editor_Speed"], pr.Speed, 0.1, 10, 0.1, v => pr.Speed = v, touch),
                    Ui.IntBox(loc["Editor_Loops"], Math.Max(1, pr.Loops), 1, 100_000, v => pr.Loops = v, touch)));
                break;

            case SetVariableAction sv:
                panel.Children.Add(Ui.EditableCombo(loc["Editor_Variable"],
                    ctx.Scheme?.Variables.Select(v => v.Name).Where(n => n.Length > 0) ?? [],
                    sv.Variable, loc["Editor_VariablePlaceholder"], v =>
                    {
                        sv.Variable = v;
                        touch();
                    }));
                panel.Children.Add(ExpressionFields.Expression(ctx, loc["Editor_ValueExpression"], sv.Expression, v => sv.Expression = v, touch));
                panel.Children.Add(Ui.Hint(loc["Editor_ExpressionHint"]));
                break;

            case AdbShellAction sh:
                panel.Children.Add(ExpressionFields.Interpolated(ctx, loc["Editor_Command"], sh.Command, v => sh.Command = v, touch));
                panel.Children.Add(Ui.Check(loc["Editor_AsRoot"], sh.AsRoot, v => sh.AsRoot = v, touch));
                panel.Children.Add(Ui.EditableCombo(loc["Editor_OutputVariable"],
                    ctx.Scheme?.Variables.Select(v => v.Name).Where(n => n.Length > 0) ?? [],
                    sh.OutputVariable ?? "", loc["Editor_OutputVariablePlaceholder"], v =>
                    {
                        sh.OutputVariable = string.IsNullOrWhiteSpace(v) ? null : v;
                        touch();
                    }));
                break;

            case LogAction l:
                panel.Children.Add(ExpressionFields.Interpolated(ctx, loc["Editor_Message"], l.Message, v => l.Message = v, touch));
                break;

            case StopAction st:
                var scopes = new[] { StopScope.Task, StopScope.Scheme };
                panel.Children.Add(Ui.Combo(loc["Editor_StopScope"], scopes.Select(x => loc["Editor_Stop_" + x]), Array.IndexOf(scopes, st.Scope), i =>
                {
                    st.Scope = scopes[i];
                    touch();
                }));
                break;
        }

        panel.Children.Add(new MenuFlyoutSeparator());
        panel.Children.Add(Ui.Columns("*,*",
            Ui.NullableIntBox(loc["Editor_DelayAfter"], a.DelayAfterMs, 0, 86_400_000,
                loc.Format("Editor_DelayDefault", ctx.DefaultDelayMs), v => a.DelayAfterMs = v, touch),
            null));
        return panel;
    }

    private static FrameworkElement KeyField(EditorContext ctx, KeyAction k, Action touch)
    {
        var loc = ctx.Loc;
        var presets = TaskEditorLogic.KeyPresets;
        var error = new TextBlock { Style = ctx.Style("EditorErrorTextStyle"), Visibility = Visibility.Collapsed, Text = loc["Editor_Err_KeyCode"] };
        var combo = new ComboBox
        {
            Header = loc["Editor_Key"],
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0,
            PlaceholderText = loc["Editor_KeyPlaceholder"],
        };
        foreach (var p in presets)
        {
            combo.Items.Add(TaskEditorLogic.KeyPresetText(loc, p));
        }

        var idx = presets.ToList().FindIndex(p => p.Code == k.KeyCode);
        if (idx >= 0)
        {
            combo.SelectedIndex = idx;
        }
        else
        {
            combo.Text = k.KeyCode.ToString(CultureInfo.InvariantCulture);
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
            {
                Set(presets[combo.SelectedIndex].Code);
            }
        };
        combo.TextSubmitted += (_, e) =>
        {
            var code = TaskEditorLogic.ParseKeyCode(loc, e.Text);
            if (code is { } c)
            {
                Set(c);
                var p = presets.ToList().FindIndex(x => x.Code == c);
                if (p >= 0)
                {
                    combo.SelectedIndex = p;
                    e.Handled = true;
                }
            }
            else
            {
                error.Visibility = Visibility.Visible;
                e.Handled = true;
            }
        };
        combo.LostFocus += (_, _) =>
        {
            if (combo.SelectedIndex < 0)
            {
                var code = TaskEditorLogic.ParseKeyCode(loc, combo.Text);
                if (code is { } c)
                {
                    Set(c);
                }
                else
                {
                    error.Visibility = Visibility.Visible;
                }
            }
        };

        return Ui.VStack(4, combo, error, Ui.Hint(loc["Editor_KeyHint"]));

        void Set(int code)
        {
            error.Visibility = Visibility.Collapsed;
            if (code != k.KeyCode)
            {
                k.KeyCode = code;
                touch();
            }
        }
    }
}
