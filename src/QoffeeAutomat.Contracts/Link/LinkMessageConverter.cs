using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace QoffeeAutomat.Contracts.Link;

/// <summary>
/// Reads and writes <see cref="LinkMessage"/> polymorphically on the envelope field
/// <c>t</c>. A frame with an unknown <c>t</c> becomes an <see cref="UnknownMessage"/>
/// (forward compatibility, PLAN.md S07). It uses only the metadata of
/// <see cref="LinkJsonContext"/>, so no reflection is involved.
/// </summary>
public sealed class LinkMessageConverter : JsonConverter<LinkMessage>
{
    /// <inheritdoc />
    public override LinkMessage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A QoffeeLink frame must be a JSON object.");
        }

        if (!root.TryGetProperty("t", out JsonElement typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("A QoffeeLink frame must have a string member \"t\".");
        }

        string type = typeElement.GetString()!;
        LinkJsonContext context = LinkJsonContext.Default;

        LinkMessage message = type switch
        {
            MessageTypes.Hello => Parse(root, context.HelloMessage),
            MessageTypes.ClientHello => Parse(root, context.ClientHelloMessage),
            MessageTypes.GetManifest => Parse(root, context.GetManifestMessage),
            MessageTypes.ManifestChanged => Parse(root, context.ManifestChangedMessage),
            MessageTypes.Subscribe => Parse(root, context.SubscribeMessage),
            MessageTypes.State => Parse(root, context.StateMessage),
            MessageTypes.Run => Parse(root, context.RunMessage),
            MessageTypes.Event => Parse(root, context.EventMessage),
            MessageTypes.Bye => Parse(root, context.ByeMessage),
            MessageTypes.Result => Parse(root, context.ResultMessage),
            _ => new UnknownMessage(type, root.Clone()),
        };

        return message;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, LinkMessage value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        LinkJsonContext context = LinkJsonContext.Default;

        switch (value)
        {
            case HelloMessage hello:
                JsonSerializer.Serialize(writer, hello, context.HelloMessage);
                break;
            case ClientHelloMessage clientHello:
                JsonSerializer.Serialize(writer, clientHello, context.ClientHelloMessage);
                break;
            case GetManifestMessage getManifest:
                JsonSerializer.Serialize(writer, getManifest, context.GetManifestMessage);
                break;
            case ManifestChangedMessage manifestChanged:
                JsonSerializer.Serialize(writer, manifestChanged, context.ManifestChangedMessage);
                break;
            case SubscribeMessage subscribe:
                JsonSerializer.Serialize(writer, subscribe, context.SubscribeMessage);
                break;
            case StateMessage state:
                JsonSerializer.Serialize(writer, state, context.StateMessage);
                break;
            case RunMessage run:
                JsonSerializer.Serialize(writer, run, context.RunMessage);
                break;
            case EventMessage eventMessage:
                JsonSerializer.Serialize(writer, eventMessage, context.EventMessage);
                break;
            case ByeMessage bye:
                JsonSerializer.Serialize(writer, bye, context.ByeMessage);
                break;
            case ResultMessage result:
                JsonSerializer.Serialize(writer, result, context.ResultMessage);
                break;
            case UnknownMessage unknown:
                unknown.Raw.WriteTo(writer);
                break;
            default:
                throw new NotSupportedException($"The message type {value.GetType().Name} is not supported.");
        }
    }

    private static T Parse<T>(JsonElement root, JsonTypeInfo<T> typeInfo)
        where T : LinkMessage
    {
        return root.Deserialize(typeInfo) ?? throw new JsonException("The frame is not valid.");
    }
}
