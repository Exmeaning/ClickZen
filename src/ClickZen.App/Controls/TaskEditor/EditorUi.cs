using System.Globalization;
using ClickZen.App.Services;
using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ClickZen.App.Controls.TaskEditing;

/// <summary>State shared by every builder of one task editor instance.</summary>
internal sealed class EditorContext
{
    private readonly Action _changed;
    private readonly ResourceDictionary _resources;
    private bool _busy;

    public EditorContext(ILocalizer loc, IAutomationEditorHost? host, ResourceDictionary resources, HashSet<object> expanded, Action changed)
    {
        Loc = loc;
        Host = host;
        _resources = resources;
        Expanded = expanded;
        _changed = changed;
    }

    public ILocalizer Loc { get; }

    public IAutomationEditorHost? Host { get; }

    public Scheme? Scheme => Host?.Scheme;

    public bool HasHost => Host is not null;

    /// <summary>Model objects (rules, conditions, actions, branches) whose cards are expanded.</summary>
    public HashSet<object> Expanded { get; }

    public int DefaultDelayMs => Scheme?.Settings.DefaultDelayAfterMs ?? new SchemeSettings().DefaultDelayAfterMs;

    /// <summary>Reports an edit (marks the document dirty and raises TaskChanged).</summary>
    public void Changed() => _changed();

    public Style Style(string key) =>
        _resources.TryGetValue(key, out var s) ? (Style)s : (Style)Application.Current.Resources[key];

    public void Highlight(RectI? area, PointI? point = null) => Host?.Highlight(area, point);

    public void ClearHighlight() => Host?.Highlight(null);

    public Task<PointI?> PickPointAsync() => RunAsync(h => h.PickPointAsync(), null);

    public Task<RectI?> PickAreaAsync() => RunAsync(h => h.PickAreaAsync(), null);

    public Task<(TemplateAsset Template, RectI Area)?> CaptureTemplateAsync() => RunAsync(h => h.CaptureTemplateAsync(), null);

    public Task<ConditionTestResult?> TestAsync(Condition condition) =>
        RunAsync<ConditionTestResult?>(async h => await h.TestConditionAsync(condition), null);

    /// <summary>Runs one host interaction at a time; failures go to the page InfoBar.</summary>
    private async Task<T> RunAsync<T>(Func<IAutomationEditorHost, Task<T>> op, T fallback)
    {
        if (Host is null || _busy)
        {
            return fallback;
        }

        _busy = true;
        try
        {
            return await op(Host);
        }
        catch (OperationCanceledException)
        {
            return fallback;
        }
        catch (Exception ex)
        {
            Host.ShowMessage(Loc.Format("Editor_Err_Host", ex.Message), isError: true);
            return fallback;
        }
        finally
        {
            _busy = false;
        }
    }
}

/// <summary>Small factory helpers so the code-built editors stay readable.</summary>
internal static class Ui
{
    public const string GlyphAdd = "\uE710";
    public const string GlyphUp = "\uE74A";
    public const string GlyphDown = "\uE74B";
    public const string GlyphChevronDown = "\uE70D";
    public const string GlyphRight = "\uE76C";
    public const string GlyphDelete = "\uE74D";
    public const string GlyphMore = "\uE712";
    public const string GlyphCopy = "\uE8C8";
    public const string GlyphPickPoint = "\uEF3C";
    public const string GlyphPickArea = "\uEF20";
    public const string GlyphClear = "\uE894";
    public const string GlyphCapture = "\uE7A8";
    public const string GlyphTest = "\uE9D9";
    public const string GlyphGroup = "\uE71D";
    public const string GlyphRule = "\uE8FD";
    public const string GlyphBranch = "\uE8B1";
    public const string GlyphOk = "\uE73E";
    public const string GlyphFail = "\uE711";

    public static FontIcon Icon(string glyph, double size = 14) => new() { Glyph = glyph, FontSize = size };

    public static TextBlock Text(string text, Style? style = null)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (style is not null)
        {
            tb.Style = style;
        }

        return tb;
    }

    public static TextBlock Hint(string text) => Text(text, AppStyle("SecondaryCaptionTextBlockStyle"));

    public static TextBlock Label(string text) => Text(text, AppStyle("BodyStrongTextBlockStyle"));

    public static Style AppStyle(string key) => (Style)Application.Current.Resources[key];

    public static void SetTip(FrameworkElement e, string tip)
    {
        ToolTipService.SetToolTip(e, tip);
        AutomationProperties.SetName(e, tip);
    }

    public static Button IconButton(string glyph, string tip, RoutedEventHandler click)
    {
        var b = new Button
        {
            Content = Icon(glyph),
            Padding = new Thickness(7, 6, 7, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out var s) && s is Style style)
        {
            b.Style = style;
        }

        SetTip(b, tip);
        b.Click += click;
        return b;
    }

    public static Button LabeledButton(string glyph, string text, RoutedEventHandler click)
    {
        var b = new Button { Content = HStack(8, Icon(glyph), new TextBlock { Text = text }), VerticalAlignment = VerticalAlignment.Bottom };
        b.Click += click;
        return b;
    }

    public static DropDownButton AddMenuButton(string text, MenuFlyout flyout) =>
        new() { Content = HStack(8, Icon(GlyphAdd), new TextBlock { Text = text }), Flyout = flyout };

    public static MenuFlyoutItem MenuItem(string glyph, string text, RoutedEventHandler click)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = Icon(glyph) };
        item.Click += click;
        return item;
    }

    public static StackPanel HStack(double spacing, params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children)
        {
            p.Children.Add(c);
        }

        return p;
    }

    public static StackPanel VStack(double spacing, params UIElement[] children)
    {
        var p = new StackPanel { Spacing = spacing };
        foreach (var c in children)
        {
            p.Children.Add(c);
        }

        return p;
    }

    /// <summary>A one-row grid. <paramref name="spec"/> is a comma list of "*", "2*", "Auto" or pixel widths.</summary>
    public static Grid Columns(string spec, params UIElement?[] children)
    {
        var g = new Grid { ColumnSpacing = 8 };
        foreach (var part in spec.Split(','))
        {
            var p = part.Trim();
            GridLength len;
            if (p.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                len = GridLength.Auto;
            }
            else if (p.EndsWith('*'))
            {
                var f = p.Length == 1 ? 1 : double.Parse(p[..^1], CultureInfo.InvariantCulture);
                len = new GridLength(f, GridUnitType.Star);
            }
            else
            {
                len = new GridLength(double.Parse(p, CultureInfo.InvariantCulture));
            }

            g.ColumnDefinitions.Add(new ColumnDefinition { Width = len });
        }

        for (var i = 0; i < children.Length; i++)
        {
            if (children[i] is { } c)
            {
                Grid.SetColumn((FrameworkElement)c, i);
                g.Children.Add(c);
            }
        }

        return g;
    }

    // ------------------------------------------------------------------ fields

    public static NumberBox IntBox(string header, int value, double min, double max, Action<int> set, Action touch, double step = 1)
    {
        var current = value;
        var box = new NumberBox
        {
            Header = header,
            Minimum = min,
            Maximum = max,
            Value = value,
            SmallChange = step,
            LargeChange = step * 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            MinWidth = 0,
        };
        box.ValueChanged += (s, e) =>
        {
            if (double.IsNaN(e.NewValue))
            {
                s.Value = current;
                return;
            }

            var v = (int)Math.Round(Math.Clamp(e.NewValue, min, max));
            if (v == current)
            {
                return;
            }

            current = v;
            set(v);
            touch();
        };
        return box;
    }

    /// <summary>Integer box where empty means null (e.g. "use the scheme default").</summary>
    public static NumberBox NullableIntBox(string header, int? value, double min, double max, string placeholder, Action<int?> set, Action touch, double step = 10)
    {
        var current = value;
        var box = new NumberBox
        {
            Header = header,
            Minimum = min,
            Maximum = max,
            Value = value ?? double.NaN,
            PlaceholderText = placeholder,
            SmallChange = step,
            LargeChange = step * 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            MinWidth = 0,
        };
        box.ValueChanged += (_, e) =>
        {
            int? v = double.IsNaN(e.NewValue) ? null : (int)Math.Round(Math.Clamp(e.NewValue, min, max));
            if (v == current)
            {
                return;
            }

            current = v;
            set(v);
            touch();
        };
        return box;
    }

    public static NumberBox DoubleBox(string header, double value, double min, double max, double step, Action<double> set, Action touch)
    {
        var current = value;
        var box = new NumberBox
        {
            Header = header,
            Minimum = min,
            Maximum = max,
            Value = value,
            SmallChange = step,
            LargeChange = step * 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            MinWidth = 0,
            NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter
            {
                FractionDigits = 0,
                IntegerDigits = 1,
                NumberRounder = new Windows.Globalization.NumberFormatting.IncrementNumberRounder { Increment = 0.01 },
            },
        };
        box.ValueChanged += (s, e) =>
        {
            if (double.IsNaN(e.NewValue))
            {
                s.Value = current;
                return;
            }

            var v = Math.Round(Math.Clamp(e.NewValue, min, max), 2);
            if (v.Equals(current))
            {
                return;
            }

            current = v;
            set(v);
            touch();
        };
        return box;
    }

    public static TextBox TextField(string header, string value, Action<string> set, Action touch, bool mono = false, string? placeholder = null)
    {
        var tb = new TextBox { Header = header, Text = value, PlaceholderText = placeholder ?? "", MinWidth = 0 };
        if (mono)
        {
            tb.FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["MonoFontFamily"];
        }

        tb.TextChanged += (_, _) =>
        {
            if (tb.Text == value)
            {
                return;
            }

            value = tb.Text;
            set(value);
            touch();
        };
        return tb;
    }

    public static CheckBox Check(string text, bool value, Action<bool> set, Action touch)
    {
        var cb = new CheckBox { Content = text, IsChecked = value, MinWidth = 0 };
        cb.Click += (_, _) =>
        {
            set(cb.IsChecked == true);
            touch();
        };
        return cb;
    }

    public static ComboBox Combo(string header, IEnumerable<string> items, int index, Action<int> selected)
    {
        var cb = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
        foreach (var i in items)
        {
            cb.Items.Add(i);
        }

        cb.SelectedIndex = index;
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedIndex >= 0 && cb.SelectedIndex != index)
            {
                index = cb.SelectedIndex;
                selected(index);
            }
        };
        return cb;
    }

    /// <summary>Editable combo: pick a suggestion or type free text; commits on selection, Enter or focus loss.</summary>
    public static ComboBox EditableCombo(string header, IEnumerable<string> suggestions, string text, string placeholder, Action<string> commit)
    {
        var cb = new ComboBox
        {
            Header = header,
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0,
            PlaceholderText = placeholder,
        };
        var list = suggestions.ToList();
        foreach (var s in list)
        {
            cb.Items.Add(s);
        }

        var idx = list.IndexOf(text);
        if (idx >= 0)
        {
            cb.SelectedIndex = idx;
        }
        else
        {
            cb.Text = text;
        }

        var last = text;
        void Commit(string? v)
        {
            v = (v ?? "").Trim();
            if (v == last)
            {
                return;
            }

            last = v;
            commit(v);
        }

        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is string s)
            {
                Commit(s);
            }
        };
        cb.TextSubmitted += (_, e) => Commit(e.Text);
        cb.LostFocus += (_, _) => Commit(cb.Text);
        return cb;
    }

    /// <summary>Enabled switch for a card header (compact, no on/off text).</summary>
    public static ToggleSwitch EnabledSwitch(ILocalizer loc, bool value, Action<bool> set)
    {
        var ts = new ToggleSwitch
        {
            IsOn = value,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
            Width = 44,
            VerticalAlignment = VerticalAlignment.Center,
        };
        SetTip(ts, loc["Editor_EnabledTip"]);
        ts.Toggled += (_, _) => set(ts.IsOn);
        return ts;
    }
}

/// <summary>
/// A collapsible card: a header button (chevron, icon, one-line summary) plus header commands; the
/// body is built the first time the card is expanded.
/// </summary>
internal sealed class EditorCard
{
    private readonly EditorContext _ctx;
    private readonly object _key;
    private readonly Func<FrameworkElement> _buildBody;
    private readonly Grid _header;
    private readonly Button _toggle;
    private readonly Grid _toggleContent;
    private readonly FontIcon _chevron;
    private readonly Border _bodyHost;
    private bool _expanded;

    public EditorCard(EditorContext ctx, object key, string glyph, string styleKey, Func<FrameworkElement> buildBody)
    {
        _ctx = ctx;
        _key = key;
        _buildBody = buildBody;

        _chevron = Ui.Icon(Ui.GlyphRight, 10);
        Title = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var icon = Ui.Icon(glyph, 16);
        _toggleContent = Ui.Columns("Auto,Auto,*", _chevron, icon, Title);
        _toggleContent.ColumnSpacing = 10;
        _toggle = new Button
        {
            Content = _toggleContent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 6, 8, 6),
        };
        if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out var s) && s is Style style)
        {
            _toggle.Style = style;
        }

        _toggle.Click += (_, _) => SetExpanded(!_expanded);

        _header = Ui.Columns("*", _toggle);
        _header.ColumnSpacing = 2;
        _bodyHost = new Border { Visibility = Visibility.Collapsed, Padding = new Thickness(12, 4, 8, 8) };
        Root = new Border { Style = ctx.Style(styleKey), Child = Ui.VStack(0, _header, _bodyHost) };

        if (ctx.Expanded.Contains(key))
        {
            SetExpanded(true);
        }
    }

    public Border Root { get; }

    public TextBlock Title { get; }

    public void SetTitle(string text)
    {
        Title.Text = text;
        ToolTipService.SetToolTip(_toggle, text);
        AutomationProperties.SetName(_toggle, text);
    }

    /// <summary>Dims the header for disabled items.</summary>
    public void SetDimmed(bool dimmed) => _toggleContent.Opacity = dimmed ? 0.5 : 1;

    public void AddHeader(FrameworkElement e)
    {
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(e, _header.ColumnDefinitions.Count - 1);
        _header.Children.Add(e);
    }

    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        if (expanded)
        {
            _ctx.Expanded.Add(_key);
            if (_bodyHost.Child is null)
            {
                _bodyHost.Child = _buildBody();
            }
        }
        else
        {
            _ctx.Expanded.Remove(_key);
        }

        _bodyHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        _chevron.Glyph = expanded ? Ui.GlyphChevronDown : Ui.GlyphRight;
    }

    /// <summary>Adds enable switch, move up/down and a "more" menu (duplicate, delete) to the header.</summary>
    public void AddItemCommands(bool enabled, Action<bool> setEnabled, int index, int count,
        Action<int> move, Action duplicate, Action delete)
    {
        var loc = _ctx.Loc;
        AddHeader(Ui.EnabledSwitch(loc, enabled, setEnabled));

        var up = Ui.IconButton(Ui.GlyphUp, loc["Editor_MoveUp"], (_, _) => move(-1));
        up.IsEnabled = index > 0;
        AddHeader(up);
        var down = Ui.IconButton(Ui.GlyphDown, loc["Editor_MoveDown"], (_, _) => move(1));
        down.IsEnabled = index < count - 1;
        AddHeader(down);

        var flyout = new MenuFlyout();
        flyout.Items.Add(Ui.MenuItem(Ui.GlyphCopy, loc["Editor_Duplicate"], (_, _) => duplicate()));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(Ui.MenuItem(Ui.GlyphDelete, loc["Editor_Delete"], (_, _) => delete()));
        var more = Ui.IconButton(Ui.GlyphMore, loc["Editor_More"], (_, _) => { });
        more.Flyout = flyout;
        AddHeader(more);
    }
}
