using ClickZen.Core.Settings;

namespace ClickZen.Mcp;

/// <summary>
/// Groups of MCP tools the user can switch on and off independently (AI page). A disabled group's tools
/// are hidden from <c>tools/list</c> and refused by <c>tools/call</c>.
/// </summary>
public enum McpCapability
{
    /// <summary>Status, devices, screenshots, UI tree, image search (read only).</summary>
    Observe,
    /// <summary>Taps, swipes, keys, text, launching / stopping apps.</summary>
    Input,
    /// <summary>Schemes and automation runs.</summary>
    Automation,
    /// <summary>Recording playback.</summary>
    Recording,
    /// <summary>Automation variables.</summary>
    Variables,
    /// <summary>adb shell.</summary>
    Shell,
}

/// <summary>Marks the capability group of a tool method in <see cref="ClickZenTools"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class McpCapabilityAttribute(McpCapability capability) : Attribute
{
    public McpCapability Capability { get; } = capability;
}

public static class McpCapabilities
{
    public static IReadOnlyList<McpCapability> All { get; } = Enum.GetValues<McpCapability>();

    /// <summary>Whether the settings allow a capability group.</summary>
    public static bool Allows(this McpSettings settings, McpCapability capability)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return capability switch
        {
            McpCapability.Observe => settings.AllowObserve,
            McpCapability.Input => settings.AllowInput,
            McpCapability.Automation => settings.AllowAutomation,
            McpCapability.Recording => settings.AllowRecording,
            McpCapability.Variables => settings.AllowVariables,
            McpCapability.Shell => settings.AllowShell,
            _ => false,
        };
    }

    /// <summary>Sets a capability group in the settings.</summary>
    public static void Set(this McpSettings settings, McpCapability capability, bool allowed)
    {
        ArgumentNullException.ThrowIfNull(settings);
        switch (capability)
        {
            case McpCapability.Observe: settings.AllowObserve = allowed; break;
            case McpCapability.Input: settings.AllowInput = allowed; break;
            case McpCapability.Automation: settings.AllowAutomation = allowed; break;
            case McpCapability.Recording: settings.AllowRecording = allowed; break;
            case McpCapability.Variables: settings.AllowVariables = allowed; break;
            case McpCapability.Shell: settings.AllowShell = allowed; break;
        }
    }
}
