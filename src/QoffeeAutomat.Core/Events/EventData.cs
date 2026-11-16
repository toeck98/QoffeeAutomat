using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QoffeeAutomat.Core.Events;

/// <summary>Builds the small JSON objects that events carry, without reflection.</summary>
public static class EventData
{
    /// <summary>Creates a JSON object from members.</summary>
    /// <param name="members">
    /// Name and value pairs. A value is null, a string, a bool, an int, a long, a double or a
    /// <see cref="JsonElement"/>; other types are written with their invariant string form. Null
    /// members are omitted (PLAN.md D-039: optional fields are omitted rather than sent as null).
    /// </param>
    /// <returns>The object.</returns>
    public static JsonElement Create(params (string Name, object? Value)[] members)
    {
        ArgumentNullException.ThrowIfNull(members);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach ((string name, object? value) in members)
            {
                if (value is null)
                {
                    continue;
                }

                writer.WritePropertyName(name);
                WriteValue(writer, value);
            }

            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteValue(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}

/// <summary>The outcome of <see cref="CustomEvents.Create"/>.</summary>
/// <param name="Payload">The data of the <c>custom</c> event; null when the input is invalid.</param>
/// <param name="Error">What is wrong with the input; null when it is valid.</param>
public sealed record CustomEventResult(JsonElement? Payload, string? Error)
{
    /// <summary>True when the input was accepted.</summary>
    public bool IsValid => Error is null;
}

/// <summary>
/// The custom events that scripts raise with <c>qoffeeautomat emit &lt;name&gt; [--data &lt;json&gt;]</c>
/// (PLAN.md S07, S10). On the wire they are <c>event</c> frames of type <see cref="EventTypes.Custom"/>
/// whose data is <c>{"name": "...", "data": {...}}</c>, so Home Assistant sees one event type with the
/// script's own name inside.
/// </summary>
public static class CustomEvents
{
    /// <summary>The longest accepted event name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The most bytes of user data (UTF-8 JSON) that an event may carry.</summary>
    public const int MaxDataBytes = 16 * 1024;

    private static readonly Regex NamePattern = new(
        @"^[A-Za-z][A-Za-z0-9_.-]{0,63}\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>Tells whether a name is acceptable: a letter followed by letters, digits, <c>_</c>, <c>.</c> or <c>-</c>, at most 64 characters.</summary>
    /// <param name="name">The name.</param>
    /// <returns>True when the name is valid.</returns>
    public static bool IsValidName(string? name)
    {
        return !string.IsNullOrEmpty(name) && NamePattern.IsMatch(name);
    }

    /// <summary>Validates the input and builds the data of the <c>custom</c> event.</summary>
    /// <param name="name">The event name chosen by the script.</param>
    /// <param name="data">Optional user data; it must be a JSON object.</param>
    /// <returns>The payload or the reason it was rejected.</returns>
    public static CustomEventResult Create(string? name, JsonElement? data)
    {
        if (!IsValidName(name))
        {
            return new CustomEventResult(null, "The event name must start with a letter and use only letters, digits, '_', '.' and '-' (at most 64 characters).");
        }

        JsonElement? userData = data is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } element ? element : null;
        if (userData is { ValueKind: not JsonValueKind.Object })
        {
            return new CustomEventResult(null, "The event data must be a JSON object.");
        }

        if (userData is { } checkedData && System.Text.Encoding.UTF8.GetByteCount(checkedData.GetRawText()) > MaxDataBytes)
        {
            return new CustomEventResult(null, $"The event data must not exceed {MaxDataBytes} bytes.");
        }

        JsonElement payload = userData is { } value
            ? EventData.Create(("name", name), ("data", value))
            : EventData.Create(("name", name));
        return new CustomEventResult(payload, null);
    }
}
