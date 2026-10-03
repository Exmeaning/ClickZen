using System.Runtime.CompilerServices;
using ClickZen.App.Controls.TaskEditing;
using ClickZen.App.Services;
using ClickZen.Core.Automation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClickZen.App.Controls;

/// <summary>
/// Editor for one <see cref="AutomationTask"/>: basic info, the precondition tree and the rule list
/// with inline condition / action editors. Edits the model in place; every change raises
/// <see cref="TaskChanged"/> and calls <see cref="IAutomationEditorHost.MarkDirty"/>.
/// Workbench interactions (pick point / area, capture template, test, highlight) go through <see cref="Host"/>.
/// </summary>
public sealed partial class TaskEditor : UserControl
{
    // Expanded cards are remembered per task so switching tasks back and forth keeps the layout.
    private static readonly ConditionalWeakTable<AutomationTask, HashSet<object>> ExpandedByTask = new();

    private readonly ILocalizer _loc;
    private IAutomationEditorHost? _host;
    private AutomationTask? _task;
    private bool _loading;

    public TaskEditor()
    {
        _loc = App.Current.Services.GetRequiredService<ILocalizer>();
        InitializeComponent();
        Ui.SetTip(ExpandAllButton, _loc["Editor_ExpandAll"]);
        Ui.SetTip(CollapseAllButton, _loc["Editor_CollapseAll"]);
        Unloaded += (_, _) => _host?.Highlight(null);
        Rebuild();
    }

    /// <summary>Raised after any edit made through the editor.</summary>
    public event EventHandler? TaskChanged;

    /// <summary>The page providing scheme data and workbench interactions. Setting it rebuilds the editor.</summary>
    public IAutomationEditorHost? Host
    {
        get => _host;
        set
        {
            if (ReferenceEquals(_host, value))
            {
                return;
            }

            _host?.Highlight(null);
            _host = value;
            Rebuild();
        }
    }

    /// <summary>The task being edited (edited in place). Null shows a placeholder. Setting it rebuilds the editor.</summary>
    public AutomationTask? Task
    {
        get => _task;
        set
        {
            _host?.Highlight(null);
            _task = value;
            Rebuild();
        }
    }

    /// <summary>Rebuilds the whole editor from the model (e.g. after the host changed the task or templates externally).</summary>
    public void Refresh() => Rebuild();

    private void Rebuild()
    {
        PreconditionHost.Content = null;
        RulesHost.Content = null;
        if (_task is null)
        {
            EmptyText.Visibility = Visibility.Visible;
            Scroller.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;

        _loading = true;
        try
        {
            NameBox.Text = _task.Name;
            EnabledSwitch.IsOn = _task.Enabled;
            CooldownBox.Value = _task.CooldownMs;
            PriorityBox.Value = _task.Priority;
            RunLimitBox.Value = _task.RunLimit;
        }
        finally
        {
            _loading = false;
        }

        var ctx = new EditorContext(_loc, _host, Resources, ExpandedByTask.GetValue(_task, _ => []), OnChanged);
        PreconditionHost.Content = ConditionEditors.Group(ctx, _task.Precondition, OnChanged);
        RulesHost.Content = RuleEditors.List(ctx, _task.Rules, OnChanged);
    }

    private void OnChanged()
    {
        if (_loading)
        {
            return;
        }

        _host?.MarkDirty();
        TaskChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ basic info

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_task is null || _loading || _task.Name == NameBox.Text)
        {
            return;
        }

        _task.Name = NameBox.Text;
        OnChanged();
    }

    private void OnEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_task is null || _loading || _task.Enabled == EnabledSwitch.IsOn)
        {
            return;
        }

        _task.Enabled = EnabledSwitch.IsOn;
        OnChanged();
    }

    private void OnCooldownChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        SetInt(sender, args, () => _task!.CooldownMs, v => _task!.CooldownMs = v);

    private void OnPriorityChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        SetInt(sender, args, () => _task!.Priority, v => _task!.Priority = v);

    private void OnRunLimitChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        SetInt(sender, args, () => _task!.RunLimit, v => _task!.RunLimit = v);

    private void SetInt(NumberBox box, NumberBoxValueChangedEventArgs args, Func<int> get, Action<int> set)
    {
        if (_task is null || _loading)
        {
            return;
        }

        if (double.IsNaN(args.NewValue))
        {
            box.Value = get();
            return;
        }

        var v = (int)Math.Round(Math.Clamp(args.NewValue, box.Minimum, box.Maximum));
        if (v == get())
        {
            return;
        }

        set(v);
        OnChanged();
    }

    // ------------------------------------------------------------------ expand / collapse

    private void OnExpandAllClick(object sender, RoutedEventArgs e)
    {
        if (_task is null)
        {
            return;
        }

        var set = ExpandedByTask.GetValue(_task, _ => []);
        foreach (var rule in _task.Rules)
        {
            set.Add(rule);
        }

        Rebuild();
    }

    private void OnCollapseAllClick(object sender, RoutedEventArgs e)
    {
        if (_task is null)
        {
            return;
        }

        ExpandedByTask.GetValue(_task, _ => []).Clear();
        _host?.Highlight(null);
        Rebuild();
    }
}
