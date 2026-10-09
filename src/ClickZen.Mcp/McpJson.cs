using System.Text.Json;
using System.Text.Json.Serialization;
using ClickZen.Core.Geometry;

namespace ClickZen.Mcp;

/// <summary>JSON for tool results: camelCase, nulls omitted, enums as strings, compact geometry.</summary>
public static class McpJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        o.Converters.Add(new PointConverter());
        o.Converters.Add(new SizeConverter());
        o.Converters.Add(new RectConverter());
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    private sealed class PointConverter : JsonConverter<PointI>
    {
        public override PointI Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, PointI value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", value.X);
            writer.WriteNumber("y", value.Y);
            writer.WriteEndObject();
        }
    }

    private sealed class SizeConverter : JsonConverter<SizeI>
    {
        public override SizeI Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, SizeI value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("width", value.Width);
            writer.WriteNumber("height", value.Height);
            writer.WriteEndObject();
        }
    }

    private sealed class RectConverter : JsonConverter<RectI>
    {
        public override RectI Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, RectI value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", value.X);
            writer.WriteNumber("y", value.Y);
            writer.WriteNumber("width", value.Width);
            writer.WriteNumber("height", value.Height);
            writer.WriteEndObject();
        }
    }
}
