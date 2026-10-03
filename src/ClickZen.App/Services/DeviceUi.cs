using ClickZen.Core.Devices;
using ClickZen.Device.Scrcpy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ClickZen.App.Services;

/// <summary>Pure helpers used by x:Bind function bindings in device views.</summary>
public static class DeviceUi
{
    private static ILocalizer Loc => App.Current.Services.GetRequiredService<ILocalizer>();

    public static string KindGlyph(ConnectionKind kind) => kind switch
    {
        ConnectionKind.Wireless => "\uE701", // Wifi
        ConnectionKind.Emulator => "\uE7F4", // TVMonitor
        _ => "\uE8EA",                       // CellPhone
    };

    public static string Details(DeviceInfo info)
    {
        var parts = new List<string>();
        parts.Add(info.Kind switch
        {
            ConnectionKind.Wireless => Loc["DeviceKind_Wireless"],
            ConnectionKind.Emulator => Loc["DeviceKind_Emulator"],
            _ => Loc["DeviceKind_Usb"],
        });
        if (!info.PhysicalSize.IsEmpty)
        {
            parts.Add($"{info.PhysicalSize.Width}×{info.PhysicalSize.Height}");
        }

        if (info.Density > 0)
        {
            parts.Add($"{info.Density} dpi");
        }

        return string.Join("  ·  ", parts);
    }

    public static string StateText(DeviceAdbState adb, SessionState session, string? error)
    {
        var text = adb switch
        {
            DeviceAdbState.Unauthorized => Loc["DeviceState_Unauthorized"],
            DeviceAdbState.Offline => Loc["DeviceState_Offline"],
            DeviceAdbState.Other => Loc["DeviceState_Other"],
            _ => session switch
            {
                SessionState.Streaming => Loc["SessionState_Streaming"],
                SessionState.Connected => Loc["SessionState_Connected"],
                SessionState.Connecting => Loc["SessionState_Connecting"],
                SessionState.Reconnecting => Loc["SessionState_Reconnecting"],
                SessionState.Faulted => Loc["SessionState_Faulted"],
                _ => Loc["DeviceState_Ready"],
            },
        };

        return string.IsNullOrEmpty(error) || session is SessionState.Streaming or SessionState.Connected
            ? text
            : $"{text}: {error}";
    }

    public static Brush StateBrush(DeviceAdbState adb, SessionState session)
    {
        var key = adb != DeviceAdbState.Online
            ? "SystemFillColorCriticalBrush"
            : session switch
            {
                SessionState.Streaming or SessionState.Connected => "SystemFillColorSuccessBrush",
                SessionState.Connecting or SessionState.Reconnecting => "SystemFillColorCautionBrush",
                SessionState.Faulted => "SystemFillColorCriticalBrush",
                _ => "SystemFillColorNeutralBrush",
            };
        return (Brush)Application.Current.Resources[key];
    }

    public static Visibility CanStart(DeviceAdbState adb, ScrcpySession? session) =>
        adb == DeviceAdbState.Online && session is null ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility HasSession(ScrcpySession? session) => session is null ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility IsNetwork(ConnectionKind kind) => kind == ConnectionKind.Usb ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility NotTrue(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;
}
