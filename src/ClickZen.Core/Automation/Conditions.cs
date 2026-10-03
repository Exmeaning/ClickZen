using System.Text.Json.Serialization;
using ClickZen.Core.Geometry;

namespace ClickZen.Core.Automation;

public enum GroupLogic
{
    /// <summary>Every child must be true.</summary>
    All,
    /// <summary>At least one child must be true.</summary>
    Any,
    /// <summary>No child may be true.</summary>
    None,
}

/// <summary>A boolean tree of conditions. An empty group is true for All/None and false for Any.</summary>
public sealed class ConditionGroup
{
    public GroupLogic Logic { get; set; } = GroupLogic.All;
    public List<Condition> Conditions { get; set; } = [];
    public List<ConditionGroup> Groups { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty => Conditions.Count == 0 && Groups.Count == 0;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ImageCondition), "image")]
[JsonDerivedType(typeof(ColorCondition), "color")]
[JsonDerivedType(typeof(ExpressionCondition), "expression")]
[JsonDerivedType(typeof(ElapsedCondition), "elapsed")]
public abstract class Condition
{
    public bool Enabled { get; set; } = true;
    /// <summary>Optional label shown in the editor.</summary>
    public string Label { get; set; } = "";
}

/// <summary>Template matching inside a search area.</summary>
public sealed class ImageCondition : Condition
{
    public string TemplateId { get; set; } = "";
    /// <summary>Search area in device pixels at <see cref="Scheme.RefSize"/>; empty = whole screen.</summary>
    public RectI Area { get; set; }
    /// <summary>Normalised correlation threshold (0..1).</summary>
    public double Threshold { get; set; } = 0.85;
    /// <summary>Match on grayscale (faster, ignores tint) instead of colour.</summary>
    public bool Grayscale { get; set; }
    /// <summary>Also try template scales 0.8–1.2 (slower; for UIs that resize).</summary>
    public bool MultiScale { get; set; }
    /// <summary>When false the condition is true if the image is NOT found.</summary>
    public bool ExpectFound { get; set; } = true;
}

/// <summary>Colour of a single pixel within a tolerance.</summary>
public sealed class ColorCondition : Condition
{
    public PointI Point { get; set; }
    /// <summary>#RRGGBB.</summary>
    public string Color { get; set; } = "#000000";
    /// <summary>Maximum per-channel difference (0..255).</summary>
    public int Tolerance { get; set; } = 16;
    public bool ExpectMatch { get; set; } = true;
}

/// <summary>A boolean expression over variables, e.g. <c>count &gt;= 3 &amp;&amp; state == "battle"</c>.</summary>
public sealed class ExpressionCondition : Condition
{
    public string Expression { get; set; } = "true";
}

public enum ElapsedSince
{
    /// <summary>Since this task last ran (true when it never ran).</summary>
    TaskLastRun,
    /// <summary>Since the scheme started.</summary>
    SchemeStart,
}

/// <summary>Time-based gate, e.g. "at least 30 s since this task last ran".</summary>
public sealed class ElapsedCondition : Condition
{
    public ElapsedSince Since { get; set; } = ElapsedSince.TaskLastRun;
    public int AtLeastMs { get; set; } = 1000;
}
