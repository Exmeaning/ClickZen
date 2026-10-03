using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClickZen.App.Controls.TaskEditing;

/// <summary>Builds coordinate inputs (point, area, target) with "pick on picture" buttons and workbench highlighting.</summary>
internal static class CoordinateFields
{
    private const double MaxCoord = 100_000;

    // Compact layout: the axis name is a small prefix label instead of a header row.
    private static NumberBox CoordBox(string header, int value, double min, Action<int> set, Action touch)
    {
        var box = Ui.IntBox(header, value, min, MaxCoord, set, touch);
        box.Header = null;
        box.MinWidth = 56;
        box.SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, header);
        return box;
    }

    private static TextBlock Axis(string text)
    {
        var t = Ui.Hint(text);
        t.TextWrapping = TextWrapping.NoWrap;
        return t;
    }

    /// <summary>X / Y boxes plus an optional pick button. Focus highlights the point on the workbench.</summary>
    public static FrameworkElement Point(EditorContext ctx, string label, Func<PointI> get, Action<PointI> set, Action touch,
        bool pickable = true, bool highlight = true, bool allowNegative = false)
    {
        var loc = ctx.Loc;
        var min = allowNegative ? -MaxCoord : 0;
        var focused = false;
        var x = CoordBox(loc["Editor_X"], get().X, min, v => set(get() with { X = v }), Touch);
        var y = CoordBox(loc["Editor_Y"], get().Y, min, v => set(get() with { Y = v }), Touch);

        Button? pick = null;
        if (pickable)
        {
            pick = Ui.IconButton(Ui.GlyphPickPoint, loc["Editor_PickPoint"], async (_, _) =>
            {
                var p = await ctx.PickPointAsync();
                if (p is { } pt)
                {
                    x.Value = pt.X;
                    y.Value = pt.Y;
                    if (highlight)
                    {
                        ctx.Highlight(null, get());
                    }
                }
            });
            pick.IsEnabled = ctx.HasHost;
        }

        var row = Ui.Columns("Auto,*,Auto,*,Auto", Axis(loc["Editor_X"]), x, Axis(loc["Editor_Y"]), y, pick);
        var root = Ui.VStack(2, Ui.Hint(label), row);
        if (highlight)
        {
            root.GotFocus += (_, _) =>
            {
                focused = true;
                ctx.Highlight(null, get());
            };
            root.LostFocus += (_, _) =>
            {
                focused = false;
                ctx.ClearHighlight();
            };
        }

        return root;

        void Touch()
        {
            touch();
            if (highlight && focused)
            {
                ctx.Highlight(null, get());
            }
        }
    }

    /// <summary>X / Y / W / H boxes plus pick-area (and optional clear) buttons. Focus highlights the area.</summary>
    public static FrameworkElement Area(EditorContext ctx, string label, Func<RectI> get, Action<RectI> set, Action touch,
        string? emptyHint = null)
    {
        var loc = ctx.Loc;
        TextBlock? hint = null;
        var focused = false;
        var x = CoordBox(loc["Editor_X"], get().X, 0, v => set(get() with { X = v }), Touch);
        var y = CoordBox(loc["Editor_Y"], get().Y, 0, v => set(get() with { Y = v }), Touch);
        var w = CoordBox(loc["Editor_W"], get().Width, 0, v => set(get() with { Width = v }), Touch);
        var h = CoordBox(loc["Editor_H"], get().Height, 0, v => set(get() with { Height = v }), Touch);

        var pick = Ui.IconButton(Ui.GlyphPickArea, loc["Editor_PickArea"], async (_, _) =>
        {
            var r = await ctx.PickAreaAsync();
            if (r is { } rect)
            {
                Apply(rect);
                ctx.Highlight(get());
            }
        });
        pick.IsEnabled = ctx.HasHost;

        Button? clear = null;
        if (emptyHint is not null)
        {
            clear = Ui.IconButton(Ui.GlyphClear, loc["Editor_ClearArea"], (_, _) => Apply(default));
            hint = Ui.Hint(emptyHint);
        }

        var row = Ui.Columns("Auto,*,Auto,*,Auto,*,Auto,*,Auto,Auto",
            Axis(loc["Editor_X"]), x, Axis(loc["Editor_Y"]), y, Axis(loc["Editor_W"]), w, Axis(loc["Editor_H"]), h, pick, clear);
        row.ColumnSpacing = 6;
        var root = Ui.VStack(2, Ui.Hint(label), row);
        if (hint is not null)
        {
            root.Children.Add(hint);
            UpdateHint();
        }

        root.GotFocus += (_, _) =>
        {
            focused = true;
            HighlightArea();
        };
        root.LostFocus += (_, _) =>
        {
            focused = false;
            ctx.ClearHighlight();
        };
        return root;

        void Apply(RectI r)
        {
            x.Value = r.X;
            y.Value = r.Y;
            w.Value = r.Width;
            h.Value = r.Height;
        }

        void Touch()
        {
            UpdateHint();
            touch();
            if (focused)
            {
                HighlightArea();
            }
        }

        void UpdateHint()
        {
            if (hint is not null)
            {
                hint.Visibility = get().IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        void HighlightArea()
        {
            var r = get();
            if (r.IsEmpty)
            {
                ctx.ClearHighlight();
            }
            else
            {
                ctx.Highlight(r);
            }
        }
    }

    /// <summary>Target kind switch with the fields each kind uses, plus the offset.</summary>
    public static FrameworkElement Target(EditorContext ctx, string label, Target target, Action touch)
    {
        var loc = ctx.Loc;
        var body = new StackPanel { Spacing = 8 };
        var kinds = new[] { TargetKind.Point, TargetKind.LastMatch, TargetKind.RandomInArea };
        var kind = Ui.Combo(label, kinds.Select(k => loc["Editor_Target_" + k]), Array.IndexOf(kinds, target.Kind), i =>
        {
            target.Kind = kinds[i];
            Fill();
            touch();
        });

        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(kind);
        root.Children.Add(body);
        Fill();
        return root;

        void Fill()
        {
            body.Children.Clear();
            switch (target.Kind)
            {
                case TargetKind.Point:
                    body.Children.Add(Point(ctx, loc["Editor_Point"], () => target.Point, p => target.Point = p, touch));
                    break;
                case TargetKind.LastMatch:
                    body.Children.Add(Ui.Hint(loc["Editor_Target_LastMatchHint"]));
                    body.Children.Add(Point(ctx, loc["Editor_FallbackPoint"], () => target.Point, p => target.Point = p, touch));
                    break;
                case TargetKind.RandomInArea:
                    body.Children.Add(Area(ctx, loc["Editor_Area"], () => target.Area, r => target.Area = r, touch));
                    break;
            }

            body.Children.Add(Point(ctx, loc["Editor_Offset"], () => target.Offset, p => target.Offset = p, touch,
                pickable: false, highlight: false, allowNegative: true));
        }
    }
}
