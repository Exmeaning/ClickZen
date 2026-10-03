using System.ComponentModel;
using ClickZen.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using WinUIEx;

namespace ClickZen.App.Shell;

/// <summary>Stand-alone, resizable mirror of one device (so the scheme editor can stay visible).</summary>
public sealed partial class MirrorWindow : Window
{
    private readonly DeviceEntry _entry;

    public MirrorWindow(DeviceEntry entry)
    {
        _entry = entry;
        InitializeComponent();
        Title = entry.Info.DisplayName;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ClickZen.ico"));

        var manager = WindowManager.Get(this);
        manager.MinWidth = 240;
        manager.MinHeight = 320;
        manager.PersistenceId = "ClickZenMirrorWindow";

        var services = App.Current.Services;
        ThemeService.ApplyBackdrop(this);
        services.GetRequiredService<ThemeService>().Register(this);

        var size = entry.Session?.DeviceSize ?? default;
        if (!size.IsEmpty)
        {
            // Open at a sensible size with the device's aspect ratio.
            var h = 900.0;
            var w = h * size.Width / size.Height;
            if (w > 1400)
            {
                w = 1400;
                h = w * size.Height / size.Width;
            }

            this.SetWindowSize(w, h);
        }

        Mirror.Session = entry.Session;
        Mirror.PlaceholderMessage = services.GetRequiredService<ILocalizer>()["Mirror_WaitingForVideo"];
        entry.PropertyChanged += OnEntryChanged;
        Closed += (_, _) =>
        {
            entry.PropertyChanged -= OnEntryChanged;
            Mirror.Session = null;
        };
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceEntry.Session))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_entry.Session is null)
                {
                    Close();
                }
                else
                {
                    Mirror.Session = _entry.Session;
                }
            });
        }
    }
}
