using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QoffeeAutomat.Contracts.Config;

/// <summary>
/// Writes <see cref="DateTimeOffset"/> values as UTC ISO 8601 text with a trailing <c>Z</c>, as
/// PLAN.md S06 requires for <c>updatedAt</c>. Reading accepts any ISO 8601 offset.
/// </summary>
public sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

    /// <inheritdoc />
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetDateTimeOffset();
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
    }
}
