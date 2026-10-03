using ClickZen.App.Controls;
using ClickZen.Core.Automation;
using Microsoft.UI.Xaml;

namespace ClickZen.App.Views;

// Bridge to the centre-column task editor (ClickZen.App.Controls.TaskEditor, developed separately).
public sealed partial class AutomationPage
{
    private TaskEditor? _editor;

    private void ShowInEditor(AutomationTask? task)
    {
        if (_editor is null)
        {
            if (task is null)
            {
                return;
            }

            _editor = new TaskEditor { Host = this };
            _editor.TaskChanged += (_, _) => _summariesStale = true;
            EditorHost.Content = _editor;
        }

        _editor.Visibility = task is null ? Visibility.Collapsed : Visibility.Visible;
        if (!ReferenceEquals(_editor.Task, task))
        {
            _editor.Task = task;
        }
    }

    /// <summary>Rebuilds the editor from the model (after templates were renamed or deleted).</summary>
    private void ReloadEditor() => _editor?.Refresh();
}
