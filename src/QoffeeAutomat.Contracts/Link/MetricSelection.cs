using System.Text.Json;
using System.Text.Json.Serialization;

namespace QoffeeAutomat.Contracts.Link;

/// <summary>
/// The <c>metrics</c> member of <c>subscribe</c>: an array of metric ids or the string
/// <c>"*"</c> for all metrics.
/// </summary>
[JsonConverter(typeof(MetricSelectionConverter))]
public sealed record MetricSelection
{
    /// <summary>The wire representation of "all metrics".</summary>
    public const string WildcardText = "*";

    private MetricSelection(bool all, IReadOnlyList<string> ids)
    {
        All = all;
        Ids = ids;
    }

    /// <summary>Selects all metrics.</summary>
    public static MetricSelection Everything { get; } = new(true, []);

    /// <summary>True when all metrics are selected; <see cref="Ids"/> is empty then.</summary>
    public bool All { get; }

    /// <summary>The selected metric ids when <see cref="All"/> is false.</summary>
    public IReadOnlyList<string> Ids { get; }

    /// <summary>Selects the given metric ids.</summary>
    /// <param name="ids">The metric ids.</param>
    /// <returns>The selection.</returns>
    public static MetricSelection From(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return new MetricSelection(false, ids.ToArray());
    }
}

/// <summary>Reads and writes <see cref="MetricSelection"/> as an array or the string "*".</summary>
public sealed class MetricSelectionConverter : JsonConverter<MetricSelection>
{
    /// <inheritdoc />
    public override MetricSelection? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            if (reader.ValueTextEquals(MetricSelection.WildcardText))
            {
                return MetricSelection.Everything;
            }

            throw new JsonException("metrics must be an array of metric ids or \"*\".");
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("metrics must be an array of metric ids or \"*\".");
        }

        var ids = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("metrics must contain only strings.");
            }

            ids.Add(reader.GetString()!);
        }

        return MetricSelection.From(ids);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, MetricSelection value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        if (value.All)
        {
            writer.WriteStringValue(MetricSelection.WildcardText);
            return;
        }

        writer.WriteStartArray();
        foreach (string id in value.Ids)
        {
            writer.WriteStringValue(id);
        }

        writer.WriteEndArray();
    }
}
