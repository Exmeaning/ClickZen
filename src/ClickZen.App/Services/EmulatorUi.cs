using System.Globalization;
using ClickZen.Core.Emulators;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ClickZen.App.Services;

/// <summary>Pure helpers for x:Bind function bindings in the "Detected emulators" section.</summary>
public static class EmulatorUi
{
    private static ILocalizer Loc => App.Current.Services.GetRequiredService<ILocalizer>();

    /// <summary>Localized emulator family name ("MuMu 模拟器", "雷电模拟器"…).</summary>
    public static string KindName(EmulatorKind kind) => Loc["Emu_Kind_" + kind];

    /// <summary>"MuMu 模拟器 · Android Device".</summary>
    public static string Title(EmulatorInstance instance)
    {
        var kind = KindName(instance.Kind);
        return instance.Name.Equals(kind, StringComparison.OrdinalIgnoreCase) ? kind : $"{kind}  ·  {instance.Name}";
    }

    /// <summary>"127.0.0.1:16384 · port from config · PID 1234 · not verified on hardware".</summary>
    public static string Details(EmulatorInstance instance)
    {
        var parts = new List<string>
        {
            instance.AdbAddress ?? Loc["Emu_PortNone"],
            Loc["Emu_Source_" + instance.PortSource],
        };
        if (instance.Index is int index)
        {
            parts.Add(Loc.Format("Emu_Index", index));
        }

        if (instance.ProcessId != 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"PID {instance.ProcessId}"));
        }

        if (!instance.HasWindow)
        {
            parts.Add(Loc["Emu_NoWindow"]);
        }
        else if (instance.WindowMinimized)
        {
            parts.Add(Loc["Emu_Minimized"]);
        }

        if (!EmulatorCatalog.Get(instance.Kind).HardwareVerified)
        {
            parts.Add(Loc["Emu_Unverified"]);
        }

        return string.Join("  ·  ", parts);
    }

    public static string StateText(EmulatorConnectionState state, string? serial) => state switch
    {
        EmulatorConnectionState.Connected => Loc.Format("Emu_State_Connected", serial ?? ""),
        _ => Loc["Emu_State_" + state],
    };

    public static Brush StateBrush(EmulatorConnectionState state) => (Brush)Application.Current.Resources[state switch
    {
        EmulatorConnectionState.Connected => "SystemFillColorSuccessBrush",
        EmulatorConnectionState.NotConnected => "SystemFillColorNeutralBrush",
        EmulatorConnectionState.Offline => "SystemFillColorCautionBrush",
        _ => "SystemFillColorCriticalBrush",
    }];

    /// <summary>The connect button is offered while the instance is not connected and connecting can work.</summary>
    public static bool CanConnect(EmulatorConnectionState state, bool busy) =>
        !busy && state is EmulatorConnectionState.NotConnected or EmulatorConnectionState.Offline;

    public static string ConnectText(EmulatorConnectionState state) =>
        state == EmulatorConnectionState.Connected ? Loc["Emu_ConnectedButton"] : Loc["Emu_Connect"];

    /// <summary>Binding needs a window that is not minimized.</summary>
    public static bool CanBind(EmulatorInstance instance, bool busy) => !busy && instance.HasWindow;

    public static string BindText(bool bound) => bound ? Loc["Emu_Rebind"] : Loc["Emu_Bind"];

    public static Visibility BoundBadge(bool bound) => bound ? Visibility.Visible : Visibility.Collapsed;

    public static string KindGlyph(EmulatorKind kind) => kind == EmulatorKind.AndroidStudio ? "\uE8EA" : "\uE7F4";
}
