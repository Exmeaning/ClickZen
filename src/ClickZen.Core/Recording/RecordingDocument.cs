using System.Text.Json;
using System.Text.Json.Serialization;
using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Core.Persistence;

namespace ClickZen.Core.Recording;

/// <summary>
/// A .czrec file: recorded gestures plus the screen they were recorded on.
/// Points are stored compactly as [x, y, offsetMs] triples.
/// </summary>
public sealed class RecordingDocument
{
    public const int CurrentFormatVersion = 1;
    public const string FileExtension = ".czrec";

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    /// <summary>Screen size (current orientation) the coordinates refer to.</summary>
    public SizeI ScreenSize { get; set; }
    public string? DeviceModel { get; set; }
    public List<Gesture> Gestures { get; set; } = [];

    public long DurationMs => Gestures.Count == 0 ? 0 : Gestures.Max(g => g.EndMs);

    public static RecordingDocument Load(string path)
    {
        var doc = JsonSerializer.Deserialize<RecordingDocument>(File.ReadAllBytes(path), Options)
                  ?? throw new InvalidDataException("Empty recording file.");
        if (doc.FormatVersion > CurrentFormatVersion)
        {
            throw new InvalidDataException($"Recording format {doc.FormatVersion} is newer than supported ({CurrentFormatVersion}).");
        }

        return Validate(doc);
    }

    public static RecordingDocument FromJson(string json) =>
        Validate(JsonSerializer.Deserialize<RecordingDocument>(json, Options) ?? throw new InvalidDataException("Empty recording."));

    private static RecordingDocument Validate(RecordingDocument doc)
    {
        if (doc.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException($"Unsupported recording format {doc.FormatVersion}.");
        }

        if (doc.Gestures is null || doc.ScreenSize.Width < 0 || doc.ScreenSize.Height < 0)
        {
            throw new InvalidDataException("Invalid recording metadata.");
        }

        foreach (var g in doc.Gestures)
        {
            if (g is null || g.StartMs < 0 || !Enum.IsDefined(g.Kind) || g.Fingers is null)
            {
                throw new InvalidDataException("Invalid recorded gesture.");
            }

            if (g.Kind is GestureKind.Tap or GestureKind.LongPress or GestureKind.Swipe or GestureKind.MultiTouch && g.Fingers.Count == 0)
            {
                throw new InvalidDataException("Touch gesture has no fingers.");
            }

            foreach (var finger in g.Fingers)
            {
                if (finger is null || finger.Points is null || finger.Points.Count == 0)
                {
                    throw new InvalidDataException("Finger stroke has no points.");
                }

                var last = -1;
                foreach (var p in finger.Points)
                {
                    if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.OffsetMs < last || p.OffsetMs < 0)
                    {
                        throw new InvalidDataException("Invalid stroke coordinates or timing.");
                    }

                    last = p.OffsetMs;
                }
            }
        }

        doc.Gestures = doc.Gestures.OrderBy(g => g.StartMs).ToList();
        return doc;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public void Save(string path) => AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, Options));

    /// <summary>Serializer options with compact point encoding.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions(JsonDefaults.Options);
        o.Converters.Add(new TimedPointArrayConverter());
        o.Converters.Add(new SizeIConverter());
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }
}

/// <summary>Encodes <see cref="TimedPoint"/> as [x, y, t] with x/y rounded to 0.1 px.</summary>
internal sealed class TimedPointArrayConverter : JsonConverter<TimedPoint>
{
    public override TimedPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected [x, y, t].");
        }

        reader.Read();
        var x = reader.GetDouble();
        reader.Read();
        var y = reader.GetDouble();
        reader.Read();
        var t = reader.GetInt32();
        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("Expected end of [x, y, t].");
        }

        return new TimedPoint(x, y, t);
    }

    public override void Write(Utf8JsonWriter writer, TimedPoint value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(Math.Round(value.X, 1));
        writer.WriteNumberValue(Math.Round(value.Y, 1));
        writer.WriteNumberValue(value.OffsetMs);
        writer.WriteEndArray();
    }
}

/// <summary>Encodes <see cref="SizeI"/> as [width, height].</summary>
public sealed class SizeIConverter : JsonConverter<SizeI>
{
    public override SizeI Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return default;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected [width, height].");
        }

        reader.Read();
        var w = reader.GetInt32();
        reader.Read();
        var h = reader.GetInt32();
        reader.Read();
        return new SizeI(w, h);
    }

    public override void Write(Utf8JsonWriter writer, SizeI value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Width);
        writer.WriteNumberValue(value.Height);
        writer.WriteEndArray();
    }
}
