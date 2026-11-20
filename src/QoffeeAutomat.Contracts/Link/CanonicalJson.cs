using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace QoffeeAutomat.Contracts.Link;

/// <summary>
/// Canonical JSON as defined for the manifest revision (PLAN.md S07, D-039): UTF-8 without
/// insignificant whitespace, object members sorted by name in ordinal order of the UTF-16
/// code units, members whose value is null omitted, strings escaped only where JSON requires
/// it, numbers written as they appear in the (shortest-form) source.
/// </summary>
public static class CanonicalJson
{
    /// <summary>Returns the canonical JSON text of an element.</summary>
    /// <param name="element">The element.</param>
    /// <param name="omittedRootMember">A member of the root object to leave out, for example <c>rev</c>.</param>
    /// <returns>The canonical JSON text.</returns>
    public static string Serialize(JsonElement element, string? omittedRootMember = null)
    {
        var builder = new StringBuilder();
        Append(builder, element, omittedRootMember);
        return builder.ToString();
    }

    /// <summary>Returns the canonical JSON as UTF-8 bytes.</summary>
    /// <param name="element">The element.</param>
    /// <param name="omittedRootMember">A member of the root object to leave out.</param>
    /// <returns>The UTF-8 bytes.</returns>
    public static byte[] SerializeToUtf8(JsonElement element, string? omittedRootMember = null)
    {
        return Encoding.UTF8.GetBytes(Serialize(element, omittedRootMember));
    }

    /// <summary>
    /// Serializes a value with source-generated metadata and returns its canonical JSON as UTF-8 bytes. The hub uses it
    /// for the signed desired state; the manifest revision uses the same canonical form (PLAN.md S07, S20).
    /// </summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">The source-generated metadata of <typeparamref name="T"/>.</param>
    /// <returns>The canonical UTF-8 bytes.</returns>
    public static byte[] SerializeToUtf8<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return SerializeToUtf8(JsonSerializer.SerializeToElement(value, typeInfo));
    }

    private static void Append(StringBuilder builder, JsonElement element, string? omittedMember)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                AppendObject(builder, element, omittedMember);
                break;
            case JsonValueKind.Array:
                AppendArray(builder, element);
                break;
            case JsonValueKind.String:
                AppendString(builder, element.GetString()!);
                break;
            case JsonValueKind.Number:
                builder.Append(element.GetRawText());
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            default:
                builder.Append("null");
                break;
        }
    }

    private static void AppendObject(StringBuilder builder, JsonElement element, string? omittedMember)
    {
        var members = new List<JsonProperty>();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (omittedMember is not null && string.Equals(property.Name, omittedMember, StringComparison.Ordinal))
            {
                continue;
            }

            members.Add(property);
        }

        members.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));

        builder.Append('{');
        for (int index = 0; index < members.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            AppendString(builder, members[index].Name);
            builder.Append(':');
            Append(builder, members[index].Value, null);
        }

        builder.Append('}');
    }

    private static void AppendArray(StringBuilder builder, JsonElement element)
    {
        builder.Append('[');
        bool first = true;
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            Append(builder, item, null);
        }

        builder.Append(']');
    }

    private static void AppendString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (char character in text)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < 0x20)
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
