using System.Text.Json;
using System.Text.Json.Serialization;
using QoffeeAutomat.Contracts.Hub;

namespace QoffeeAutomat.Hub.Config;

/// <summary>
/// System.Text.Json source-generation context for what the hub keeps in its database about the configuration: the content of a profile,
/// the policy and the items of a generation. No reflection-based serialization is used (PLAN.md S14).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(ProfileContent))]
[JsonSerializable(typeof(PolicyInfo))]
[JsonSerializable(typeof(DesiredItem[]))]
internal sealed partial class ConfigStorageJsonContext : JsonSerializerContext
{
}

/// <summary>Small helpers for the JSON bodies of hub documents.</summary>
public static class ConfigJson
{
    /// <summary>Parses JSON text into a standalone element.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The root element; it does not depend on a disposed document.</returns>
    public static JsonElement Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Creates a JSON object whose members are all strings, in the order given.</summary>
    /// <param name="members">The members.</param>
    /// <returns>The object.</returns>
    public static JsonElement StringObject(IEnumerable<KeyValuePair<string, string>> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, string> member in members)
            {
                writer.WriteString(member.Key, member.Value);
            }

            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>Reads a string member of an object.</summary>
    /// <param name="element">The element; anything but an object has no members.</param>
    /// <param name="name">The member name.</param>
    /// <returns>The string, or null when the member is missing or not a string.</returns>
    public static string? GetString(JsonElement element, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
