using System.Text.Json;

namespace QoffeeAutomat.Contracts.Link;

/// <summary>Convenience methods to read and write QoffeeLink frames and payloads.</summary>
public static class LinkCodec
{
    /// <summary>Parses one frame. Unknown message types become <see cref="UnknownMessage"/>.</summary>
    /// <param name="json">The frame as UTF-16 text.</param>
    /// <returns>The message.</returns>
    /// <exception cref="JsonException">The frame is not valid JSON, not an object, has no <c>t</c> or violates the schema of its type.</exception>
    public static LinkMessage Parse(string json)
    {
        return JsonSerializer.Deserialize(json, LinkJsonContext.Default.LinkMessage)
            ?? throw new JsonException("The frame is empty.");
    }

    /// <summary>Parses one frame from UTF-8 bytes.</summary>
    /// <param name="utf8Json">The frame.</param>
    /// <returns>The message.</returns>
    /// <exception cref="JsonException">See <see cref="Parse(string)"/>.</exception>
    public static LinkMessage Parse(ReadOnlySpan<byte> utf8Json)
    {
        return JsonSerializer.Deserialize(utf8Json, LinkJsonContext.Default.LinkMessage)
            ?? throw new JsonException("The frame is empty.");
    }

    /// <summary>Serializes a message to the text of one frame.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The JSON text.</returns>
    public static string Serialize(LinkMessage message)
    {
        return JsonSerializer.Serialize(message, LinkJsonContext.Default.LinkMessage);
    }

    /// <summary>Serializes a message to UTF-8 bytes.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The UTF-8 JSON.</returns>
    public static byte[] SerializeToUtf8(LinkMessage message)
    {
        return JsonSerializer.SerializeToUtf8Bytes(message, LinkJsonContext.Default.LinkMessage);
    }

    /// <summary>Parses a manifest, for example the data of the result of <c>get_manifest</c>.</summary>
    /// <param name="json">The manifest as JSON text.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="JsonException">The manifest violates the schema.</exception>
    public static Manifest ParseManifest(string json)
    {
        return JsonSerializer.Deserialize(json, LinkJsonContext.Default.Manifest)
            ?? throw new JsonException("The manifest is empty.");
    }

    /// <summary>Converts the data of a result to a manifest.</summary>
    /// <param name="data">The result data.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="JsonException">The data is not a valid manifest.</exception>
    public static Manifest ParseManifest(JsonElement data)
    {
        return data.Deserialize(LinkJsonContext.Default.Manifest)
            ?? throw new JsonException("The manifest is empty.");
    }

    /// <summary>Serializes a manifest to a JSON element, for example as result data.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The JSON element.</returns>
    public static JsonElement ToElement(Manifest manifest)
    {
        return JsonSerializer.SerializeToElement(manifest, LinkJsonContext.Default.Manifest);
    }

    /// <summary>Converts the data of a <c>run</c> result.</summary>
    /// <param name="data">The result data.</param>
    /// <returns>The typed run result.</returns>
    /// <exception cref="JsonException">The data is not a valid run result.</exception>
    public static RunResultData ParseRunResult(JsonElement data)
    {
        return data.Deserialize(LinkJsonContext.Default.RunResultData)
            ?? throw new JsonException("The run result is empty.");
    }

    /// <summary>Serializes the data of a <c>run</c> result.</summary>
    /// <param name="data">The run result data.</param>
    /// <returns>The JSON element.</returns>
    public static JsonElement ToElement(RunResultData data)
    {
        return JsonSerializer.SerializeToElement(data, LinkJsonContext.Default.RunResultData);
    }
}
