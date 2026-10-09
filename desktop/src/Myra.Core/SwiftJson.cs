using System.Text.Json;
using System.Text.Json.Serialization;

namespace Myra.Core;

/// JSON settings that read and write the same documents as the macOS app's Swift JSONEncoder/Decoder:
/// camelCase keys, enums as lowercase strings, UUIDs in upper case, sets as arrays, and dates as
/// seconds since 2001-01-01 UTC (Swift's default date strategy).
public static class SwiftJson
{
    public static JsonSerializerOptions Options { get; } = Create(indented: false);
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        WriteIndented = indented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new SwiftDateConverter(),
            new UpperGuidConverter(),
        },
    };

    /// Swift's Date reference: 2001-01-01T00:00:00Z.
    public static readonly DateTimeOffset ReferenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static double ToReferenceSeconds(DateTimeOffset value) => (value - ReferenceDate).TotalSeconds;

    public static DateTimeOffset FromReferenceSeconds(double seconds) => ReferenceDate.AddSeconds(seconds).ToLocalTime();

    private sealed class SwiftDateConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var seconds = reader.GetDouble();
            if (!double.IsFinite(seconds) || Math.Abs(seconds) > 1e11) throw new JsonException("Invalid date.");
            return FromReferenceSeconds(seconds);
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(ToReferenceSeconds(value));
    }

    private sealed class UpperGuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Guid.TryParse(reader.GetString(), out var value) ? value : throw new JsonException("Invalid UUID.");

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString().ToUpperInvariant());
    }
}
