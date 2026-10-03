using ClickZen.Core.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClickZen.App.Controls.TaskEditing;

/// <summary>Builds the rule list: one card per rule (When group → Then actions or weighted branches).</summary>
internal static class RuleEditors
{
    private static readonly RulePick[] Picks = [RulePick.Sequential, RulePick.RandomOne];

    public static FrameworkElement List(EditorContext ctx, List<Rule> rules, Action touch)
    {
        var loc = ctx.Loc;
        var items = new StackPanel { Spacing = 8 };
        var empty = Ui.Hint(loc["Editor_NoRules"]);
        var add = Ui.LabeledButton(Ui.GlyphAdd, loc["Editor_AddRule"], (_, _) =>
        {
            var r = TaskEditorLogic.CreateRule(loc.Format("Editor_RuleN", rules.Count + 1));
            rules.Add(r);
            ctx.Expanded.Add(r);
            Rebuild();
            touch();
        });
        var root = Ui.VStack(8, items, empty, add);
        Rebuild();
        return root;

        void Rebuild()
        {
            items.Children.Clear();
            for (var i = 0; i < rules.Count; i++)
            {
                items.Children.Add(Rule(ctx, rules, i, touch, Rebuild));
            }

            empty.Visibility = rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            items.Visibility = rules.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static FrameworkElement Rule(EditorContext ctx, List<Rule> list, int index, Action touch, Action rebuild)
    {
        var loc = ctx.Loc;
        var rule = list[index];
        EditorCard card = null!;
        card = new EditorCard(ctx, rule, Ui.GlyphRule, "EditorRuleCardStyle", () => Body(ctx, rule, Touch));
        card.AddItemCommands(rule.Enabled,
            on =>
            {
                rule.Enabled = on;
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
                var copy = TaskEditorLogic.DeepClone(rule);
                copy.Name = loc.Format("Editor_CopyOf", string.IsNullOrWhiteSpace(rule.Name) ? loc.Format("Editor_RuleN", index + 1) : rule.Name);
                list.Insert(index + 1, copy);
                ctx.Expanded.Add(copy);
                rebuild();
                touch();
            },
            () =>
            {
                ctx.Expanded.Remove(rule);
                list.RemoveAt(index);
                rebuild();
                touch();
            });
        card.Title.Style = Ui.AppStyle("BodyStrongTextBlockStyle");
        UpdateTitle();
        return card.Root;

        void Touch()
        {
            UpdateTitle();
            touch();
        }

        void UpdateTitle()
        {
            card.SetTitle(TaskEditorLogic.Summary(loc, rule, index + 1));
            card.SetDimmed(!rule.Enabled);
        }
    }

    private static FrameworkElement Body(EditorContext ctx, Rule rule, Action touch)
    {
        var loc = ctx.Loc;
        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(Ui.TextField(loc["Editor_RuleName"], rule.Name, v => rule.Name = v, touch));

        panel.Children.Add(Section(loc["Editor_When"], loc["Editor_WhenHint"]));
        panel.Children.Add(ConditionEditors.Group(ctx, rule.When, touch));

        panel.Children.Add(Section(loc["Editor_Then"], null));
        var actionsHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false };
        var pick = Ui.Combo(loc["Editor_Pick"], Picks.Select(p => loc["Editor_Pick_" + p]), Array.IndexOf(Picks, rule.Pick), i =>
        {
            rule.Pick = Picks[i];
            if (rule.Pick == RulePick.RandomOne && rule.Branches.Count == 0)
            {
                // Seed branches from the existing sequence so switching modes loses nothing.
                var first = TaskEditorLogic.CreateBranch(loc.Format("Editor_BranchN", 1));
                first.Actions.AddRange(rule.Then.Select(TaskEditorLogic.DeepClone));
                rule.Branches.Add(first);
                rule.Branches.Add(TaskEditorLogic.CreateBranch(loc.Format("Editor_BranchN", 2)));
                ctx.Expanded.Add(first);
            }

            FillActions();
            touch();
        });
        var stop = Ui.Check(loc["Editor_StopAfterMatch"], rule.StopAfterMatch, v => rule.StopAfterMatch = v, touch);
        stop.VerticalAlignment = VerticalAlignment.Bottom;
        Ui.SetTip(stop, loc["Editor_StopAfterMatchTip"]);
        panel.Children.Add(Ui.Columns("*,Auto", pick, stop));
        panel.Children.Add(actionsHost);
        FillActions();
        return panel;

        void FillActions()
        {
            actionsHost.Content = rule.Pick == RulePick.Sequential
                ? Ui.VStack(6, Ui.Hint(loc["Editor_SequentialHint"]), ActionEditors.List(ctx, rule.Then, touch))
                : Branches(ctx, rule.Branches, touch);
        }
    }

    private static FrameworkElement Branches(EditorContext ctx, List<ActionBranch> branches, Action touch)
    {
        var loc = ctx.Loc;
        var items = new StackPanel { Spacing = 8 };
        var cards = new List<(EditorCard Card, ActionBranch Branch)>();
        var add = Ui.LabeledButton(Ui.GlyphAdd, loc["Editor_AddBranch"], (_, _) =>
        {
            var b = TaskEditorLogic.CreateBranch(loc.Format("Editor_BranchN", branches.Count + 1));
            branches.Add(b);
            ctx.Expanded.Add(b);
            Rebuild();
            touch();
        });
        var root = Ui.VStack(8, Ui.Hint(loc["Editor_RandomHint"]), items, add);
        Rebuild();
        return root;

        void Touch()
        {
            // Weights change every branch's share, so refresh all titles.
            foreach (var (c, b) in cards)
            {
                c.SetTitle(TaskEditorLogic.Summary(loc, b, branches, branches.IndexOf(b) + 1));
            }

            touch();
        }

        void Rebuild()
        {
            items.Children.Clear();
            cards.Clear();
            for (var i = 0; i < branches.Count; i++)
            {
                var index = i;
                var b = branches[i];
                var card = new EditorCard(ctx, b, Ui.GlyphBranch, "EditorBranchCardStyle", () => BranchBody(ctx, b, Touch));
                var up = Ui.IconButton(Ui.GlyphUp, loc["Editor_MoveUp"], (_, _) => Move(index, -1));
                up.IsEnabled = index > 0;
                var down = Ui.IconButton(Ui.GlyphDown, loc["Editor_MoveDown"], (_, _) => Move(index, 1));
                down.IsEnabled = index < branches.Count - 1;
                var flyout = new MenuFlyout();
                flyout.Items.Add(Ui.MenuItem(Ui.GlyphCopy, loc["Editor_Duplicate"], (_, _) =>
                {
                    var copy = TaskEditorLogic.DeepClone(b);
                    copy.Name = loc.Format("Editor_CopyOf", string.IsNullOrWhiteSpace(b.Name) ? loc.Format("Editor_BranchN", index + 1) : b.Name);
                    branches.Insert(index + 1, copy);
                    Rebuild();
                    touch();
                }));
                flyout.Items.Add(new MenuFlyoutSeparator());
                flyout.Items.Add(Ui.MenuItem(Ui.GlyphDelete, loc["Editor_Delete"], (_, _) =>
                {
                    ctx.Expanded.Remove(b);
                    branches.RemoveAt(index);
                    Rebuild();
                    touch();
                }));
                var more = Ui.IconButton(Ui.GlyphMore, loc["Editor_More"], (_, _) => { });
                more.Flyout = flyout;
                card.AddHeader(up);
                card.AddHeader(down);
                card.AddHeader(more);
                card.SetTitle(TaskEditorLogic.Summary(loc, b, branches, index + 1));
                cards.Add((card, b));
                items.Children.Add(card.Root);
            }

            items.Visibility = branches.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        void Move(int index, int delta)
        {
            if (TaskEditorLogic.Move(branches, index, delta))
            {
                Rebuild();
                touch();
            }
        }
    }

    private static FrameworkElement BranchBody(EditorContext ctx, ActionBranch b, Action touch)
    {
        var loc = ctx.Loc;
        var weight = Ui.DoubleBox(loc["Editor_Weight"], b.Weight, 0, 1_000_000, 1, v => b.Weight = v, touch);
        Ui.SetTip(weight, loc["Editor_WeightTip"]);
        return Ui.VStack(10,
            Ui.Columns("2*,*", Ui.TextField(loc["Editor_BranchName"], b.Name, v => b.Name = v, touch), weight),
            ActionEditors.List(ctx, b.Actions, touch));
    }

    private static FrameworkElement Section(string title, string? hint)
    {
        var t = Ui.Label(title);
        t.Margin = new Thickness(0, 6, 0, 0);
        if (hint is null)
        {
            return t;
        }

        return Ui.VStack(2, t, Ui.Hint(hint));
    }
}
