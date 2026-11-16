using System.Buffers.Binary;
using System.Text.Json;

namespace QoffeeAutomat.Contracts.Session;

/// <summary>A frame on the session pipe is malformed: too large, cut off, not JSON or of an unknown type.</summary>
public sealed class SessionProtocolException : Exception
{
    /// <summary>Creates an exception without a message.</summary>
    public SessionProtocolException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What is wrong with the frame.</param>
    public SessionProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    /// <param name="message">What is wrong with the frame.</param>
    /// <param name="innerException">The cause.</param>
    public SessionProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The framing of the session pipe (PLAN.md S11): every message is a UTF-8 JSON object preceded by its length as an
/// unsigned 32-bit little-endian integer. A frame is written with a single write call, so callers that serialise their
/// writes never interleave frames. Reading is strict: a frame above <see cref="MaxPayloadBytes"/>, an empty or cut-off
/// frame, a payload that is not valid JSON of a known message type all end in a <see cref="SessionProtocolException"/>.
/// </summary>
public static class SessionFrameCodec
{
    /// <summary>The size of the length prefix in bytes.</summary>
    public const int HeaderSize = 4;

    /// <summary>The largest payload that is accepted: 4 MiB (a result carries two output streams of at most 64 KiB).</summary>
    public const int MaxPayloadBytes = 4 * 1024 * 1024;

    /// <summary>Serialises a message into one frame: the length prefix followed by the JSON payload.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The frame.</returns>
    /// <exception cref="SessionProtocolException">The payload is larger than <see cref="MaxPayloadBytes"/>.</exception>
    public static byte[] Encode(SessionMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, SessionJsonContext.Default.SessionMessage);
        if (payload.Length > MaxPayloadBytes)
        {
            throw new SessionProtocolException($"The message is {payload.Length} bytes, more than the {MaxPayloadBytes} bytes of a frame.");
        }

        byte[] frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>Parses the payload of a frame.</summary>
    /// <param name="payload">The JSON bytes, without the length prefix.</param>
    /// <returns>The message.</returns>
    /// <exception cref="SessionProtocolException">The payload is not a valid message.</exception>
    public static SessionMessage Decode(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize(payload, SessionJsonContext.Default.SessionMessage)
                ?? throw new SessionProtocolException("The frame does not contain a message.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new SessionProtocolException("The frame is not a valid message: " + exception.Message, exception);
        }
    }

    /// <summary>Writes a message as one frame.</summary>
    /// <param name="stream">The stream; concurrent callers must serialise their calls.</param>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the frame was handed to the stream.</returns>
    public static async ValueTask WriteAsync(Stream stream, SessionMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] frame = Encode(message);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the next frame.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The message, or null when the stream ended cleanly before the first byte of a frame.</returns>
    /// <exception cref="SessionProtocolException">The stream ended inside a frame, or the frame is not valid.</exception>
    public static async ValueTask<SessionMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = new byte[HeaderSize];
        int read = await ReadFullyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < HeaderSize)
        {
            throw new SessionProtocolException("The stream ended inside the length prefix of a frame.");
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0)
        {
            throw new SessionProtocolException("The frame is empty.");
        }

        if (length > MaxPayloadBytes)
        {
            throw new SessionProtocolException($"The frame announces {length} bytes, more than the {MaxPayloadBytes} bytes that are accepted.");
        }

        byte[] payload = new byte[length];
        read = await ReadFullyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (read < payload.Length)
        {
            throw new SessionProtocolException("The stream ended inside a frame.");
        }

        return Decode(payload);
    }

    private static async ValueTask<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
