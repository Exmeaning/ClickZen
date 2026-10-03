using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClickZen.App.Controls.TaskEditing;

/// <summary>Text fields with syntax checking: plain expressions and <c>{expr}</c>-interpolated text.</summary>
internal static class ExpressionFields
{
    /// <summary>An expression box; validated on focus loss (and while an error is shown, on every edit).</summary>
    public static FrameworkElement Expression(EditorContext ctx, string header, string value, Action<string> set, Action touch)
    {
        var loc = ctx.Loc;
        return Build(ctx, header, value, set, touch, multiline: false, validate: text =>
        {
            var error = TaskEditorLogic.ValidateExpression(text);
            if (error is not null)
            {
                return ([string.IsNullOrWhiteSpace(text) ? loc["Editor_Err_EmptyExpression"] : loc.Format("Editor_Err_Expression", error)], true);
            }

            var undeclared = TaskEditorLogic.UndeclaredVariables(text, ctx.Scheme);
            return undeclared.Count == 0
                ? ([], false)
                : ([loc.Format("Editor_Warn_Undeclared", string.Join(", ", undeclared))], false);
        });
    }

    /// <summary>Free text whose <c>{expr}</c> placeholders are validated on focus loss.</summary>
    public static FrameworkElement Interpolated(EditorContext ctx, string header, string value, Action<string> set, Action touch, bool multiline = false)
    {
        var loc = ctx.Loc;
        var root = (StackPanel)Build(ctx, header, value, set, touch, multiline, text =>
        {
            var problems = TaskEditorLogic.ValidateInterpolated(loc, text);
            return (problems, problems.Count > 0);
        });
        root.Children.Add(Ui.Hint(loc["Editor_PlaceholderHint"]));
        return root;
    }

    private static FrameworkElement Build(EditorContext ctx, string header, string value, Action<string> set, Action touch,
        bool multiline, Func<string, (IReadOnlyList<string> Messages, bool IsError)> validate)
    {
        var box = new TextBox
        {
            Header = header,
            Text = value,
            FontFamily = (FontFamily)Application.Current.Resources["MonoFontFamily"],
            MinWidth = 0,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        if (multiline)
        {
            box.MinHeight = 64;
        }

        var message = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        var showing = false;

        box.TextChanged += (_, _) =>
        {
            if (box.Text != value)
            {
                value = box.Text;
                set(value);
                touch();
            }

            if (showing)
            {
                Validate();
            }
        };
        box.LostFocus += (_, _) => Validate();

        var root = new StackPanel { Spacing = 4 };
        root.Children.Add(box);
        root.Children.Add(message);

        // Existing content is checked right away so broken expressions are visible when the card opens.
        if (!string.IsNullOrEmpty(value))
        {
            Validate();
        }

        return root;

        void Validate()
        {
            var (messages, isError) = validate(box.Text);
            showing = messages.Count > 0;
            message.Text = string.Join("\n", messages);
            message.Style = ctx.Style(isError ? "EditorErrorTextStyle" : "EditorCautionTextStyle");
            message.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
