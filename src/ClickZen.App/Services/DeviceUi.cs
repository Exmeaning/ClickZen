using ClickZen.Core.Automation;
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
        ConnectionKind.Window => "\uE737",   // Window
        _ => "\uE8EA",                       // CellPhone
    };

    public static string Details(DeviceInfo info)
    {
        var parts = new List<string>();
        parts.Add(info.Kind switch
        {
            ConnectionKind.Wireless => Loc["DeviceKind_Wireless"],
            ConnectionKind.Emulator => Loc["DeviceKind_Emulator"],
            ConnectionKind.Window => Loc["Window_Kind"],
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

    public static Visibility CanStart(DeviceAdbState adb, ILiveFrameSource? frames) =>
        adb == DeviceAdbState.Online && frames is null ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility HasSession(ILiveFrameSource? frames) => frames is null ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility IsNetwork(ConnectionKind kind) => kind is ConnectionKind.Wireless or ConnectionKind.Emulator ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsWindowKind(ConnectionKind kind) => kind == ConnectionKind.Window ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>"Start mirroring" for adb devices, "Start capture" / "Reconnect" for window devices.</summary>
    public static string StartText(ConnectionKind kind, SessionState state) =>
        kind != ConnectionKind.Window ? Loc["Window_StartMirror"]
        : state == SessionState.Faulted ? Loc["Window_Reconnect"]
        : Loc["Window_StartCapture"];

    public static string StopText(ConnectionKind kind) =>
        kind == ConnectionKind.Window ? Loc["Window_StopCapture"] : Loc["Window_StopMirror"];

    /// <summary>What a picture view shows while a device has no picture (no device / not started / connecting / failed).</summary>
    public static string PlaceholderText(DeviceEntry? entry)
    {
        if (entry is null)
        {
            return Loc["Mirror_NoDevice"];
        }

        if (entry.Frames is null)
        {
            return entry.IsWindow
                ? entry.SessionState switch
                {
                    SessionState.Connecting => Loc["SessionState_Connecting"],
                    SessionState.Faulted => Loc.Format("Window_Failed", entry.SessionError ?? ""),
                    _ => Loc["Window_NotStarted"],
                }
                : entry.SessionState == SessionState.Connecting ? Loc["SessionState_Connecting"] : Loc["Mirror_NotStarted"];
        }

        return entry.SessionState switch
        {
            SessionState.Connecting => Loc["SessionState_Connecting"],
            SessionState.Reconnecting => Loc.Format("Mirror_Reconnecting", entry.SessionError ?? ""),
            SessionState.Faulted => Loc.Format("Mirror_Failed", entry.SessionError ?? ""),
            _ => Loc["Mirror_WaitingForVideo"],
        };
    }

    public static Visibility NotTrue(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;
}
