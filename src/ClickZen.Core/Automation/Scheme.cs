using System.Text.Json.Serialization;
using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Core.Variables;

namespace ClickZen.Core.Automation;

// =====================================================================================
//  Scheme  →  Task  →  Rule (When: ConditionGroup, Then: actions)
//  All coordinates are device pixels relative to Scheme.RefSize (the screen they were
//  authored on) and are rescaled at run time.
// =====================================================================================

/// <summary>An automation scheme (saved as a .czscheme package).</summary>
public sealed class Scheme
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Screen size (current orientation) all coordinates were authored against.</summary>
    public SizeI RefSize { get; set; }
    public SchemeSettings Settings { get; set; } = new();
    public List<VariableDefinition> Variables { get; set; } = [];
    public List<AutomationTask> Tasks { get; set; } = [];
    /// <summary>Template images by id; bytes live in the package's assets/ folder.</summary>
    public List<TemplateAsset> Templates { get; set; } = [];
}

public sealed class SchemeSettings
{
    /// <summary>Pause between evaluation rounds.</summary>
    public int CheckIntervalMs { get; set; } = 300;
    public HumanizeOptions Humanize { get; set; } = new();
    /// <summary>Pause after every action unless the action overrides it.</summary>
    public int DefaultDelayAfterMs { get; set; } = 100;
    /// <summary>Clear scheme-scope variables when the scheme stops.</summary>
    public bool ClearVariablesOnStop { get; set; } = true;
    /// <summary>Stop the scheme after this long (0 = never).</summary>
    public int MaxRunSeconds { get; set; }
}

public enum VariableScope
{
    /// <summary>Per device run (each device running the scheme has its own copy).</summary>
    Scheme,
    /// <summary>Shared by every device in this ClickZen instance.</summary>
    Global,
}

public enum SyncDirection
{
    None,
    Send,
    Receive,
    Both,
}

public sealed class VariableDefinition
{
    public string Name { get; set; } = "";
    public VariableType Type { get; set; } = VariableType.Int;
    public VariableValue Initial { get; set; } = VariableValue.Zero;
    public VariableScope Scope { get; set; } = VariableScope.Scheme;
    public SyncDirection Sync { get; set; } = SyncDirection.None;
    public string Description { get; set; } = "";
}

public sealed class TemplateAsset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Size of the template in device pixels at <see cref="Scheme.RefSize"/>.</summary>
    public SizeI Size { get; set; }
    /// <summary>Where it was cut from, for display ("re-capture" support).</summary>
    public RectI SourceRect { get; set; }

    /// <summary>PNG bytes. Not serialized into scheme.json – stored as assets/{Id}.png.</summary>
    [JsonIgnore]
    public byte[] Png { get; set; } = [];
}

public sealed class AutomationTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Minimum time between two executions of this task, measured from the end of the previous one.</summary>
    public int CooldownMs { get; set; } = 1000;
    /// <summary>Higher runs first within a round; ties keep list order.</summary>
    public int Priority { get; set; }
    /// <summary>Stop considering this task after it ran this many times (0 = unlimited).</summary>
    public int RunLimit { get; set; }
    /// <summary>Gate evaluated before the rules; empty = always.</summary>
    public ConditionGroup Precondition { get; set; } = new();
    public List<Rule> Rules { get; set; } = [];
}

public enum RulePick
{
    /// <summary>Run <see cref="Rule.Then"/> in order.</summary>
    Sequential,
    /// <summary>Run exactly one of <see cref="Rule.Branches"/>, chosen by weight.</summary>
    RandomOne,
}

public sealed class Rule
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public ConditionGroup When { get; set; } = new();
    public RulePick Pick { get; set; } = RulePick.Sequential;
    public List<AutomationAction> Then { get; set; } = [];
    public List<ActionBranch> Branches { get; set; } = [];
    /// <summary>After this rule fires, skip the remaining rules of the task this round.</summary>
    public bool StopAfterMatch { get; set; }
}

public sealed class ActionBranch
{
    public string Name { get; set; } = "";
    public double Weight { get; set; } = 1;
    public List<AutomationAction> Actions { get; set; } = [];
}
