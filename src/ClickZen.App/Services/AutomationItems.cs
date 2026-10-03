using System.Globalization;
using ClickZen.Core.Automation;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ClickZen.App.Services;

/// <summary>One row of the task list: the task plus live run badges.</summary>
public sealed partial class TaskItem : ObservableObject
{
    private readonly AutomationService _owner;

    internal TaskItem(AutomationTask task, AutomationService owner)
    {
        Task = task;
        _owner = owner;
    }

    public AutomationTask Task { get; }

    public string Name => string.IsNullOrWhiteSpace(Task.Name) ? _owner.Loc["Auto_UnnamedTask"] : Task.Name;

    public bool IsEnabled
    {
        get => Task.Enabled;
        set
        {
            if (Task.Enabled == value)
            {
                return;
            }

            Task.Enabled = value;
            OnPropertyChanged();
            _owner.OnTaskEnabledChanged(this);
        }
    }

    public string Summary => _owner.Loc.Format("Auto_TaskSummary", Task.Rules.Count,
        (Task.CooldownMs / 1000.0).ToString("0.##", CultureInfo.CurrentCulture));

    /// <summary>Times the task fired in the current (or last) run, all devices together.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeVisibility))]
    public partial int FireCount { get; set; }

    [ObservableProperty]
    public partial string LastFiredText { get; set; } = "";

    public Visibility BadgeVisibility => FireCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Re-reads every displayed property from the task.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>One template of the scheme for the template list.</summary>
public sealed partial class TemplateItem : ObservableObject
{
    internal TemplateItem(TemplateAsset template, int usage, ILocalizer loc)
    {
        Template = template;
        Usage = usage;
        UsageText = usage == 0 ? loc["Auto_TemplateUnused"] : loc.Format("Auto_TemplateUsedBy", usage);
    }

    public TemplateAsset Template { get; }

    public string Name => string.IsNullOrWhiteSpace(Template.Name) ? Template.Id : Template.Name;

    public string SizeText => Template.Size.IsEmpty ? "" : $"{Template.Size.Width}x{Template.Size.Height}";

    public int Usage { get; }

    public string UsageText { get; }

    /// <summary>Thumbnail, created on first use (UI thread).</summary>
    public ImageSource? Thumbnail => _thumbnail ??= AutomationUi.LoadPng(Template.Png);

    private ImageSource? _thumbnail;
}

/// <summary>One device the scheme is running on.</summary>
public sealed partial class DeviceRun : ObservableObject
{
    internal DeviceRun(DeviceEntry entry, string label)
    {
        Entry = entry;
        Label = label;
    }

    public DeviceEntry Entry { get; }

    public string Label { get; }

    [ObservableProperty]
    public partial EngineState State { get; set; }

    [ObservableProperty]
    public partial long Rounds { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    internal AutomationEngine? Engine { get; set; }

    internal Scheme? Scheme { get; set; }

    internal CancellationTokenSource? Cts { get; set; }

    internal Task? Completion { get; set; }
}

/// <summary>One line of the run log.</summary>
public sealed class RunLogItem
{
    public RunLogItem(DateTime time, LogLevel level, string device, string message)
    {
        Time = time;
        Level = level;
        Device = device;
        Message = message;
    }

    public DateTime Time { get; }

    public LogLevel Level { get; }

    public string Device { get; }

    public string Message { get; }

    public string TimeText => Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string Glyph => Level switch
    {
        >= LogLevel.Error => "\uEA39",
        LogLevel.Warning => "\uE7BA",
        LogLevel.Debug or LogLevel.Trace => "\uE9D9",
        _ => "\uE946",
    };

    public Brush? LevelBrush => AutomationUi.LevelBrush(Level);

    public override string ToString() => $"{TimeText} [{Device}] {Message}";
}

/// <summary>Helpers for x:Bind on the automation page.</summary>
public static class AutomationUi
{
    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    public static Brush? Resource(string key) =>
        Application.Current.Resources.TryGetValue(key, out var b) ? b as Brush : null;

    public static Brush? LevelBrush(LogLevel level) => Resource(level switch
    {
        >= LogLevel.Error => "SystemFillColorCriticalBrush",
        LogLevel.Warning => "SystemFillColorCautionBrush",
        LogLevel.Debug or LogLevel.Trace => "TextFillColorTertiaryBrush",
        _ => "TextFillColorSecondaryBrush",
    });

    /// <summary>PNG bytes → BitmapImage (decoded asynchronously by XAML).</summary>
    public static ImageSource? LoadPng(byte[] png)
    {
        if (png.Length == 0)
        {
            return null;
        }

        try
        {
            var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(png);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.FlushAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream();
            }

            stream.Seek(0);
            _ = image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
