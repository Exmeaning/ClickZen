using ClickZen.Core.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace ClickZen.App.Controls.TaskEditing;

/// <summary>Builds the visual condition tree: groups (All/Any/None, nestable) and per-type condition editors.</summary>
internal static class ConditionEditors
{
    private static readonly GroupLogic[] Logics = [GroupLogic.All, GroupLogic.Any, GroupLogic.None];
    private static readonly ConditionKind[] Kinds = [ConditionKind.Image, ConditionKind.Color, ConditionKind.Expression, ConditionKind.Elapsed];

    /// <summary>
    /// Editor for a condition group. <paramref name="removeSelf"/> is null for the root group of a
    /// task / rule (it cannot be deleted).
    /// </summary>
    public static FrameworkElement Group(EditorContext ctx, ConditionGroup group, Action touch, Action? removeSelf = null, int depth = 0)
    {
        var loc = ctx.Loc;
        var items = new StackPanel { Spacing = 6 };
        var summary = Ui.Hint("");

        var logic = new ComboBox { MinWidth = 150, VerticalAlignment = VerticalAlignment.Center };
        foreach (var l in Logics)
        {
            logic.Items.Add(loc["Editor_LogicLong_" + l]);
        }

        logic.SelectedIndex = Array.IndexOf(Logics, group.Logic);
        Ui.SetTip(logic, loc["Editor_LogicTip"]);
        logic.SelectionChanged += (_, _) =>
        {
            if (logic.SelectedIndex >= 0 && Logics[logic.SelectedIndex] != group.Logic)
            {
                group.Logic = Logics[logic.SelectedIndex];
                Touch();
            }
        };

        var addMenu = new MenuFlyout();
        foreach (var k in Kinds)
        {
            addMenu.Items.Add(Ui.MenuItem(TaskEditorLogic.Glyph(k), TaskEditorLogic.KindText(loc, k), (_, _) =>
            {
                var c = TaskEditorLogic.CreateCondition(k, ctx.Scheme);
                group.Conditions.Add(c);
                ctx.Expanded.Add(c);
                Rebuild();
                Touch();
            }));
        }

        addMenu.Items.Add(new MenuFlyoutSeparator());
        addMenu.Items.Add(Ui.MenuItem(Ui.GlyphGroup, loc["Editor_AddSubgroup"], (_, _) =>
        {
            group.Groups.Add(new ConditionGroup { Logic = group.Logic == GroupLogic.All ? GroupLogic.Any : GroupLogic.All });
            Rebuild();
            Touch();
        }));
        var add = Ui.AddMenuButton(loc["Editor_AddCondition"], addMenu);

        var header = Ui.Columns("Auto,*,Auto", logic, summary, add);
        header.ColumnSpacing = 12;
        if (removeSelf is not null)
        {
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var del = Ui.IconButton(Ui.GlyphDelete, loc["Editor_DeleteGroup"], (_, _) => removeSelf());
            Grid.SetColumn(del, 3);
            header.Children.Add(del);
        }

        var root = new Border
        {
            Style = ctx.Style(depth == 0 ? "EditorRootGroupStyle" : "EditorSubGroupStyle"),
            Child = Ui.VStack(8, header, items),
        };
        Rebuild();
        UpdateSummary();
        return root;

        void Touch()
        {
            UpdateSummary();
            touch();
        }

        void UpdateSummary()
        {
            summary.Text = group.IsEmpty
                ? loc[group.Logic == GroupLogic.Any ? "Editor_GroupEmptyAny" : "Editor_GroupEmptyPass"]
                : loc["Editor_LogicHint_" + group.Logic];
        }

        void Rebuild()
        {
            items.Children.Clear();
            for (var i = 0; i < group.Conditions.Count; i++)
            {
                items.Children.Add(Condition(ctx, group.Conditions, i, Touch, Rebuild));
            }

            for (var i = 0; i < group.Groups.Count; i++)
            {
                var sub = group.Groups[i];
                items.Children.Add(Group(ctx, sub, Touch, () =>
                {
                    group.Groups.Remove(sub);
                    Rebuild();
                    Touch();
                }, depth + 1));
            }

            items.Visibility = items.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static FrameworkElement Condition(EditorContext ctx, List<Condition> list, int index, Action touch, Action rebuild)
    {
        var loc = ctx.Loc;
        var c = list[index];
        var kind = TaskEditorLogic.KindOf(c);
        EditorCard card = null!;
        card = new EditorCard(ctx, c, TaskEditorLogic.Glyph(kind), "EditorItemCardStyle", () => Body(ctx, c, Touch));
        card.AddItemCommands(c.Enabled,
            on =>
            {
                c.Enabled = on;
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
                var copy = TaskEditorLogic.DeepClone(c);
                list.Insert(index + 1, copy);
                rebuild();
                touch();
            },
            () =>
            {
                ctx.Expanded.Remove(c);
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
            card.SetTitle(TaskEditorLogic.KindText(loc, kind) + ":  " + TaskEditorLogic.Summary(loc, c, ctx.Scheme));
            card.SetDimmed(!c.Enabled);
        }
    }

    private static FrameworkElement Body(EditorContext ctx, Condition c, Action touch)
    {
        var loc = ctx.Loc;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Ui.TextField(loc["Editor_Label"], c.Label, v => c.Label = v, touch, placeholder: loc["Editor_LabelPlaceholder"]));
        switch (c)
        {
            case ImageCondition ic:
                ImageBody(ctx, ic, panel, touch);
                break;
            case ColorCondition cc:
                ColorBody(ctx, cc, panel, touch);
                break;
            case ExpressionCondition ec:
                var box = ExpressionFields.Expression(ctx, loc["Editor_Expression"], ec.Expression, v => ec.Expression = v, touch);
                panel.Children.Add(box);
                panel.Children.Add(Ui.Hint(loc["Editor_ExpressionHint"]));
                break;
            case ElapsedCondition el:
                var sinces = new[] { ElapsedSince.TaskLastRun, ElapsedSince.SchemeStart };
                panel.Children.Add(Ui.Columns("*,*",
                    Ui.Combo(loc["Editor_ElapsedSince"], sinces.Select(s => loc["Editor_Since_" + s]), Array.IndexOf(sinces, el.Since), i =>
                    {
                        el.Since = sinces[i];
                        touch();
                    }),
                    Ui.IntBox(loc["Editor_AtLeastMs"], el.AtLeastMs, 0, 86_400_000, v => el.AtLeastMs = v, touch, 100)));
                break;
        }

        return panel;
    }

    // ------------------------------------------------------------------ image

    private static void ImageBody(EditorContext ctx, ImageCondition ic, StackPanel panel, Action touch)
    {
        var loc = ctx.Loc;
        var templates = new ComboBox { Header = loc["Editor_Template"], HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0, PlaceholderText = loc["Editor_NoTemplate"] };
        var preview = new Image { MaxHeight = 64, MaxWidth = 160, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        var previewHost = new Border { Style = ctx.Style("EditorPreviewStyle"), Child = preview, Visibility = Visibility.Collapsed };
        var ids = new List<string>();
        var filling = false;
        var signature = "";
        string Signature() => string.Join("\n", (ctx.Scheme?.Templates ?? []).Select(t => t.Id + "\t" + t.Name));

        var areaHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false };

        var capture = Ui.LabeledButton(Ui.GlyphCapture, loc["Editor_CaptureTemplate"], async (_, _) =>
        {
            var r = await ctx.CaptureTemplateAsync();
            if (r is not { } got)
            {
                return;
            }

            ic.TemplateId = got.Template.Id;
            if (ic.Area.IsEmpty)
            {
                ic.Area = TaskEditorLogic.SeedSearchArea(got.Area, ctx.Scheme?.RefSize ?? default);
                BuildArea();
            }

            FillTemplates();
            touch();
        });
        capture.IsEnabled = ctx.HasHost;

        templates.SelectionChanged += (_, _) =>
        {
            if (filling || templates.SelectedIndex < 0 || ids[templates.SelectedIndex] == ic.TemplateId)
            {
                return;
            }

            ic.TemplateId = ids[templates.SelectedIndex];
            UpdatePreview();
            touch();
        };
        templates.DropDownOpened += (_, _) =>
        {
            // Templates can be added or renamed elsewhere (another condition, the workbench); refresh lazily.
            if (Signature() != signature)
            {
                FillTemplates();
            }
        };

        panel.Children.Add(Ui.Columns("*,Auto", templates, capture));
        panel.Children.Add(previewHost);
        panel.Children.Add(areaHost);
        FillTemplates();
        BuildArea();

        // Threshold slider 0.50–1.00
        var thresholdText = Ui.Text("");
        var slider = new Slider
        {
            Minimum = 0.5,
            Maximum = 1.0,
            StepFrequency = 0.01,
            SmallChange = 0.01,
            LargeChange = 0.05,
            Value = Math.Clamp(ic.Threshold, 0.5, 1.0),
            TickFrequency = 0.05,
            TickPlacement = TickPlacement.Outside,
        };
        slider.ValueChanged += (_, e) =>
        {
            var v = Math.Round(e.NewValue, 2);
            if (v.Equals(ic.Threshold))
            {
                return;
            }

            ic.Threshold = v;
            UpdateThreshold();
            touch();
        };
        UpdateThreshold();
        panel.Children.Add(Ui.VStack(0, Ui.Columns("*,Auto", Ui.Hint(loc["Editor_Threshold"]), thresholdText), slider));

        var expect = new[] { true, false };
        panel.Children.Add(Ui.Columns("*,Auto,Auto",
            Ui.Combo(loc["Editor_Expect"], [loc["Editor_ExpectFound"], loc["Editor_ExpectAbsent"]], ic.ExpectFound ? 0 : 1, i =>
            {
                ic.ExpectFound = expect[i];
                touch();
            }),
            WithBottom(Ui.Check(loc["Editor_Grayscale"], ic.Grayscale, v => ic.Grayscale = v, touch)),
            WithBottom(Ui.Check(loc["Editor_MultiScale"], ic.MultiScale, v => ic.MultiScale = v, touch))));

        panel.Children.Add(TestRow(ctx, ic, () => string.IsNullOrEmpty(ic.TemplateId) ? loc["Editor_Test_NeedTemplate"] : null));

        void UpdateThreshold() => thresholdText.Text = ic.Threshold.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        void BuildArea()
        {
            areaHost.Content = CoordinateFields.Area(ctx, loc["Editor_SearchArea"], () => ic.Area, r => ic.Area = r, touch, loc["Editor_WholeScreenHint"]);
        }

        void FillTemplates()
        {
            filling = true;
            signature = Signature();
            templates.Items.Clear();
            ids.Clear();
            foreach (var t in ctx.Scheme?.Templates ?? [])
            {
                ids.Add(t.Id);
                templates.Items.Add(string.IsNullOrWhiteSpace(t.Name)
                    ? t.Id
                    : loc.Format("Editor_TemplateItem", t.Name, t.Size.Width, t.Size.Height));
            }

            if (!string.IsNullOrEmpty(ic.TemplateId) && !ids.Contains(ic.TemplateId))
            {
                ids.Add(ic.TemplateId);
                templates.Items.Add(loc.Format("Editor_TemplateMissing", ic.TemplateId));
            }

            templates.SelectedIndex = ids.IndexOf(ic.TemplateId);
            filling = false;
            UpdatePreview();
        }

        async void UpdatePreview()
        {
            var t = ctx.Scheme?.Templates.FirstOrDefault(x => x.Id == ic.TemplateId);
            if (t is null || t.Png.Length == 0)
            {
                previewHost.Visibility = Visibility.Collapsed;
                preview.Source = null;
                return;
            }

            try
            {
                var bmp = new BitmapImage();
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(t.Png.AsBuffer());
                stream.Seek(0);
                await bmp.SetSourceAsync(stream);
                preview.Source = bmp;
                previewHost.Visibility = Visibility.Visible;
            }
            catch (Exception)
            {
                previewHost.Visibility = Visibility.Collapsed;
            }
        }
    }

    // ------------------------------------------------------------------ colour

    private static void ColorBody(EditorContext ctx, ColorCondition cc, StackPanel panel, Action touch)
    {
        var loc = ctx.Loc;
        panel.Children.Add(CoordinateFields.Point(ctx, loc["Editor_Point"], () => cc.Point, p => cc.Point = p, touch));

        var swatch = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Bottom };
        swatch.Style = ctx.Style("EditorSwatchStyle");
        var error = new TextBlock { Style = ctx.Style("EditorErrorTextStyle"), Visibility = Visibility.Collapsed };
        var color = new TextBox
        {
            Header = loc["Editor_Color"],
            Text = cc.Color,
            PlaceholderText = "#RRGGBB",
            MaxLength = 7,
            FontFamily = (FontFamily)Application.Current.Resources["MonoFontFamily"],
        };
        color.TextChanged += (_, _) =>
        {
            var n = TaskEditorLogic.NormalizeColor(color.Text);
            if (n is not null && n != cc.Color)
            {
                cc.Color = n;
                touch();
            }

            UpdateSwatch(n is not null || error.Visibility == Visibility.Collapsed);
        };
        color.LostFocus += (_, _) =>
        {
            var n = TaskEditorLogic.NormalizeColor(color.Text);
            if (n is not null)
            {
                color.Text = n;
            }

            UpdateSwatch(true);
        };

        var tolerance = Ui.IntBox(loc["Editor_Tolerance"], cc.Tolerance, 0, 255, v => cc.Tolerance = v, touch, 1);
        panel.Children.Add(Ui.Columns("Auto,*,*", swatch, color, tolerance));
        panel.Children.Add(error);
        panel.Children.Add(Ui.Hint(loc["Editor_ColorHint"]));
        panel.Children.Add(Ui.Combo(loc["Editor_Expect"], [loc["Editor_ExpectMatch"], loc["Editor_ExpectNoMatch"]], cc.ExpectMatch ? 0 : 1, i =>
        {
            cc.ExpectMatch = i == 0;
            touch();
        }));
        panel.Children.Add(TestRow(ctx, cc, () => TaskEditorLogic.NormalizeColor(cc.Color) is null ? loc["Editor_Err_Color"] : null));
        UpdateSwatch(true);

        void UpdateSwatch(bool showError)
        {
            var ok = TaskEditorLogic.TryParseColor(color.Text, out var r, out var g, out var b);
            swatch.Background = ok ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b)) : null;
            error.Text = loc["Editor_Err_Color"];
            error.Visibility = !ok && showError ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ------------------------------------------------------------------ test

    private static FrameworkElement TestRow(EditorContext ctx, Condition c, Func<string?> precheck)
    {
        var loc = ctx.Loc;
        var ring = new ProgressRing { IsActive = false, Width = 16, Height = 16, Visibility = Visibility.Collapsed };
        var icon = Ui.Icon(Ui.GlyphOk);
        icon.Visibility = Visibility.Collapsed;
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Button test = null!;
        test = Ui.LabeledButton(Ui.GlyphTest, loc["Editor_Test"], async (_, _) =>
        {
            var problem = precheck();
            if (problem is not null)
            {
                Show(false, problem, error: true);
                return;
            }

            test.IsEnabled = false;
            ring.IsActive = true;
            ring.Visibility = Visibility.Visible;
            icon.Visibility = Visibility.Collapsed;
            result.Text = "";
            try
            {
                var r = await ctx.TestAsync(c);
                if (r is null)
                {
                    Show(false, loc["Editor_Test_Unavailable"], error: true);
                }
                else
                {
                    Show(r.Matched, TaskEditorLogic.TestResultText(loc, c, r), error: false);
                }
            }
            finally
            {
                ring.IsActive = false;
                ring.Visibility = Visibility.Collapsed;
                test.IsEnabled = true;
            }
        });
        test.IsEnabled = ctx.HasHost;
        Ui.SetTip(test, loc["Editor_TestTip"]);
        var row = Ui.Columns("Auto,Auto,Auto,*", test, ring, icon, result);
        row.ColumnSpacing = 8;
        return row;

        void Show(bool matched, string text, bool error)
        {
            icon.Visibility = Visibility.Visible;
            icon.Glyph = matched ? Ui.GlyphOk : Ui.GlyphFail;
            var tone = error ? "Error" : matched ? "Success" : "Caution";
            result.Style = ctx.Style($"Editor{tone}TextStyle");
            icon.Style = ctx.Style($"Editor{tone}IconStyle");
            result.Text = text;
        }
    }

    private static T WithBottom<T>(T e) where T : FrameworkElement
    {
        e.VerticalAlignment = VerticalAlignment.Bottom;
        e.Margin = new Thickness(0, 0, 0, 4);
        return e;
    }
}
