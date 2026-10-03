using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Serilog.Events;

namespace ClickZen.App.ViewModels;

/// <summary>x:Bind helpers for log and terminal rows.</summary>
public static class LogFormat
{
    public static string Time(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public static string Level(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        _ => "FTL",
    };

    public static Brush LevelBrush(LogEventLevel level) => Resource(level switch
    {
        LogEventLevel.Warning => "SystemFillColorCautionBrush",
        LogEventLevel.Error or LogEventLevel.Fatal => "SystemFillColorCriticalBrush",
        LogEventLevel.Information => "AccentTextFillColorPrimaryBrush",
        _ => "TextFillColorTertiaryBrush",
    });

    public static string Message(string message, string? exception)
    {
        if (string.IsNullOrEmpty(exception))
        {
            return message;
        }

        var firstLine = exception.Split('\n', 2)[0].Trim();
        return $"{message}  —  {firstLine}";
    }

    public static Brush TerminalBrush(bool isCommand, bool isError) => Resource(
        isError ? "SystemFillColorCriticalBrush" : isCommand ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
}
