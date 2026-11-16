using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace QoffeeAutomat.Agent.Link;

/// <summary>Converts metric values to the JSON values of <c>state.v</c> without reflection.</summary>
internal static class LinkValues
{
    private static readonly JsonElement NullValue = Build(static writer => writer.WriteNullValue());
    private static readonly JsonElement TrueValue = Build(static writer => writer.WriteBooleanValue(true));
    private static readonly JsonElement FalseValue = Build(static writer => writer.WriteBooleanValue(false));

    /// <summary>An empty JSON object, the data of a result that has nothing to report.</summary>
    public static JsonElement EmptyObject { get; } = Build(static writer =>
    {
        writer.WriteStartObject();
        writer.WriteEndObject();
    });

    /// <summary>
    /// Converts a metric value. Numbers become JSON numbers (a number that is not finite is unavailable),
    /// booleans and strings stay what they are and <c>null</c> stays <c>null</c> (unavailable).
    /// </summary>
    /// <param name="value">The value as a provider returned it.</param>
    /// <returns>The JSON value.</returns>
    public static JsonElement ToElement(object? value)
    {
        switch (value)
        {
            case null:
                return NullValue;
            case bool flag:
                return flag ? TrueValue : FalseValue;
            case string text:
                return Build(writer => writer.WriteStringValue(text));
            case double number:
                return Number(number);
            case float single:
                return Number(single);
            case int integer:
                return Number(integer);
            case long wide:
                return Number(wide);
            case decimal money:
                return Number((double)money);
            default:
                return Build(writer => writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)));
        }
    }

    private static JsonElement Number(double number)
    {
        return double.IsFinite(number) ? Build(writer => writer.WriteNumberValue(number)) : NullValue;
    }

    private static JsonElement Build(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
