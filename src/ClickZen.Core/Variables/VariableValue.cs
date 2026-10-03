using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClickZen.Core.Variables;

public enum VariableType
{
    Int,
    Double,
    Bool,
    String,
}

/// <summary>
/// A strongly typed variable value. Arithmetic follows simple rules:
/// int op int → int (integer division truncates), anything with a double → double,
/// string + anything → concatenation. Comparisons between numbers are numeric,
/// otherwise ordinal string comparison.
/// </summary>
[JsonConverter(typeof(VariableValueJsonConverter))]
public readonly struct VariableValue : IEquatable<VariableValue>
{
    private readonly long _int;
    private readonly double _double;
    private readonly string? _string;

    private VariableValue(VariableType type, long i, double d, string? s)
    {
        Type = type;
        _int = i;
        _double = d;
        _string = s;
    }

    public VariableType Type { get; }

    public static VariableValue FromInt(long v) => new(VariableType.Int, v, 0, null);
    public static VariableValue FromDouble(double v) => new(VariableType.Double, 0, v, null);
    public static VariableValue FromBool(bool v) => new(VariableType.Bool, v ? 1 : 0, 0, null);
    public static VariableValue FromString(string v) => new(VariableType.String, 0, 0, v ?? "");

    public static VariableValue Zero => FromInt(0);
    public static VariableValue True => FromBool(true);
    public static VariableValue False => FromBool(false);

    public static implicit operator VariableValue(long v) => FromInt(v);
    public static implicit operator VariableValue(int v) => FromInt(v);
    public static implicit operator VariableValue(double v) => FromDouble(v);
    public static implicit operator VariableValue(bool v) => FromBool(v);
    public static implicit operator VariableValue(string v) => FromString(v);

    public bool IsNumeric => Type is VariableType.Int or VariableType.Double or VariableType.Bool;

    public long AsInt => Type switch
    {
        VariableType.Int or VariableType.Bool => _int,
        VariableType.Double => (long)Math.Truncate(_double),
        _ => long.TryParse(_string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l
            : double.TryParse(_string, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)Math.Truncate(d) : 0,
    };

    public double AsDouble => Type switch
    {
        VariableType.Int or VariableType.Bool => _int,
        VariableType.Double => _double,
        _ => double.TryParse(_string, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0,
    };

    public bool AsBool => Type switch
    {
        VariableType.Int or VariableType.Bool => _int != 0,
        VariableType.Double => _double != 0,
        _ => !string.IsNullOrEmpty(_string) && !string.Equals(_string, "false", StringComparison.OrdinalIgnoreCase) && _string != "0",
    };

    public string AsString => Type switch
    {
        VariableType.Int => _int.ToString(CultureInfo.InvariantCulture),
        VariableType.Double => _double.ToString("R", CultureInfo.InvariantCulture),
        VariableType.Bool => _int != 0 ? "true" : "false",
        _ => _string ?? "",
    };

    /// <summary>Converts to <paramref name="target"/> (used when a declared variable receives a value).</summary>
    public VariableValue ConvertTo(VariableType target) => target switch
    {
        VariableType.Int => FromInt(AsInt),
        VariableType.Double => FromDouble(AsDouble),
        VariableType.Bool => FromBool(AsBool),
        _ => FromString(AsString),
    };

    /// <summary>Best-effort parse: integer, then double, then true/false, else string.</summary>
    public static VariableValue Parse(string text)
    {
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return FromInt(l);
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return FromDouble(d);
        }

        if (bool.TryParse(text, out var b))
        {
            return FromBool(b);
        }

        return FromString(text);
    }

    public static VariableValue Default(VariableType type) => type switch
    {
        VariableType.Int => FromInt(0),
        VariableType.Double => FromDouble(0),
        VariableType.Bool => False,
        _ => FromString(""),
    };

    /// <summary>
    /// Numbers compare numerically. A string that parses as a number compares numerically against a
    /// number (so a value "3" received over the network equals 3). Otherwise ordinal string comparison.
    /// </summary>
    public int CompareTo(VariableValue other)
    {
        if (TryNumeric(out var a, out var aIsDouble) && other.TryNumeric(out var b, out var bIsDouble)
            && (IsNumeric || other.IsNumeric))
        {
            return aIsDouble || bIsDouble ? a.CompareTo(b) : AsInt.CompareTo(other.AsInt);
        }

        return string.CompareOrdinal(AsString, other.AsString);
    }

    public bool Equals(VariableValue other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is VariableValue v && Equals(v);

    public override int GetHashCode() =>
        TryNumeric(out var d, out _) ? d.GetHashCode() : StringComparer.Ordinal.GetHashCode(AsString);

    private bool TryNumeric(out double value, out bool isDouble)
    {
        switch (Type)
        {
            case VariableType.Int:
            case VariableType.Bool:
                value = _int;
                isDouble = false;
                return true;
            case VariableType.Double:
                value = _double;
                isDouble = true;
                return true;
            default:
                if (long.TryParse(_string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                {
                    value = l;
                    isDouble = false;
                    return true;
                }

                isDouble = true;
                return double.TryParse(_string, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }

    public static bool operator ==(VariableValue a, VariableValue b) => a.Equals(b);

    public static bool operator !=(VariableValue a, VariableValue b) => !a.Equals(b);

    public override string ToString() => AsString;

    /// <summary>From an arbitrary JSON value (network sync, files).</summary>
    public static VariableValue FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt64(out var l) => FromInt(l),
        JsonValueKind.Number => FromDouble(e.GetDouble()),
        JsonValueKind.True => True,
        JsonValueKind.False => False,
        JsonValueKind.String => FromString(e.GetString() ?? ""),
        JsonValueKind.Null or JsonValueKind.Undefined => FromString(""),
        _ => FromString(e.GetRawText()),
    };

    public void WriteJson(Utf8JsonWriter w)
    {
        switch (Type)
        {
            case VariableType.Int: w.WriteNumberValue(_int); break;
            case VariableType.Double: w.WriteNumberValue(_double); break;
            case VariableType.Bool: w.WriteBooleanValue(_int != 0); break;
            default: w.WriteStringValue(_string); break;
        }
    }
}

public sealed class VariableValueJsonConverter : JsonConverter<VariableValue>
{
    public override VariableValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return VariableValue.FromJson(doc.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, VariableValue value, JsonSerializerOptions options) => value.WriteJson(writer);
}
