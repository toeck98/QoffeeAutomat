using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Http.Features;

namespace QoffeeAutomat.Agent.Hub.Tunnel;

/// <summary>A stream that can only be written to; the members that make no sense for the body of a response throw.</summary>
internal abstract class WriteOnlyStream : Stream
{
    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        Write(buffer.AsSpan(offset, count));
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Collects the body of a response in memory, up to a limit. A write beyond the limit throws <see cref="IOException"/> and sets <see cref="Overflowed"/>.</summary>
/// <param name="limit">The most bytes to collect.</param>
internal sealed class BoundedBufferStream(int limit) : WriteOnlyStream
{
    private readonly MemoryStream _buffer = new();

    /// <summary>True once a write was refused because the body is larger than the limit.</summary>
    public bool Overflowed { get; private set; }

    /// <summary>What was written.</summary>
    public ReadOnlyMemory<byte> Written => _buffer.GetBuffer().AsMemory(0, (int)_buffer.Length);

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_buffer.Length + buffer.Length > limit)
        {
            Overflowed = true;
            throw new IOException("The response is larger than the management tunnel carries.");
        }

        _buffer.Write(buffer);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _buffer.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Passes writes to another stream and tells when the first one happens, which is how the tunnel learns that an endpoint has begun to answer.</summary>
/// <param name="inner">The stream that receives the bytes.</param>
/// <param name="activity">Called before every write and flush.</param>
internal sealed class NotifyingStream(Stream inner, Action activity) : WriteOnlyStream
{
    public override void Flush()
    {
        activity();
        inner.Flush();
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        activity();
        await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        activity();
        inner.Write(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        activity();
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The response body of an in-process request: bytes go to a stream, and <see cref="Started"/> completes when the endpoint starts or ends its answer.</summary>
internal sealed class TunnelResponseBodyFeature : IHttpResponseBodyFeature, IDisposable
{
    private readonly Stream _stream;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PipeWriter? _writer;

    public TunnelResponseBodyFeature(Stream body)
    {
        _stream = new NotifyingStream(body, Signal);
    }

    /// <summary>Completes when the endpoint wrote, flushed, started or completed its response.</summary>
    public Task Started => _started.Task;

    /// <inheritdoc />
    public Stream Stream => _stream;

    /// <inheritdoc />
    public PipeWriter Writer => _writer ??= PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: true));

    /// <inheritdoc />
    public void DisableBuffering()
    {
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Signal();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
    {
        Signal();
        return SendFileFallback.SendFileAsync(_stream, path, offset, count, cancellationToken);
    }

    /// <inheritdoc />
    public async Task CompleteAsync()
    {
        if (_writer is not null)
        {
            _ = await _writer.FlushAsync().ConfigureAwait(false);
        }

        Signal();
    }

    private void Signal() => _ = _started.TrySetResult();

    public void Dispose() => _stream.Dispose();
}

/// <summary>The abort token of an in-process request: it fires when the tunnel stops the request (the hub went away, the stream was closed, the time ran out).</summary>
internal sealed class TunnelLifetimeFeature(Action abort, CancellationToken aborted) : IHttpRequestLifetimeFeature
{
    /// <inheritdoc />
    public CancellationToken RequestAborted { get; set; } = aborted;

    /// <inheritdoc />
    public void Abort() => abort();
}

/// <summary>One event of a Server-Sent-Events stream.</summary>
/// <param name="Name">The event name; <c>message</c> when the stream names none.</param>
/// <param name="Data">The data lines joined with a line feed.</param>
internal readonly record struct SseEvent(string Name, string Data);

/// <summary>Reads Server-Sent Events (the <c>text/event-stream</c> format) from the body that an endpoint of the management API writes.</summary>
internal static class SseReader
{
    private const string DefaultName = "message";

    /// <summary>Reads events until the writer completes. An incomplete event at the end is dropped, as the format says.</summary>
    /// <param name="reader">The reader of the response body.</param>
    /// <param name="maxEventBytes">The largest event; a larger one throws <see cref="InvalidDataException"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The events.</returns>
    public static async IAsyncEnumerable<SseEvent> ReadAsync(
        PipeReader reader,
        int maxEventBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);

        string? name = null;
        StringBuilder? data = null;
        int size = 0;
        var ready = new List<SseEvent>();
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;
            SequencePosition consumed = buffer.Start;
            ready.Clear();
            while (buffer.PositionOf((byte)'\n') is { } end)
            {
                ReadOnlySequence<byte> line = buffer.Slice(0, end);
                buffer = buffer.Slice(buffer.GetPosition(1, end));
                consumed = buffer.Start;
                size += (int)Math.Min(line.Length + 1, int.MaxValue);
                if (size > maxEventBytes)
                {
                    throw new InvalidDataException("An event of the stream is larger than the tunnel carries.");
                }

                string text = Encoding.UTF8.GetString(line);
                if (text.EndsWith('\r'))
                {
                    text = text[..^1];
                }

                if (text.Length == 0)
                {
                    if (name is not null || data is not null)
                    {
                        ready.Add(new SseEvent(name ?? DefaultName, data?.ToString() ?? string.Empty));
                    }

                    name = null;
                    data = null;
                    size = 0;
                    continue;
                }

                if (text[0] == ':')
                {
                    continue;
                }

                int colon = text.IndexOf(':', StringComparison.Ordinal);
                string field = colon < 0 ? text : text[..colon];
                string value = colon < 0 ? string.Empty : text[(colon + 1)..];
                if (value.StartsWith(' '))
                {
                    value = value[1..];
                }

                switch (field)
                {
                    case "event":
                        name = value.Length == 0 ? null : value;
                        break;
                    case "data":
                        if (data is null)
                        {
                            data = new StringBuilder();
                        }
                        else
                        {
                            _ = data.Append('\n');
                        }

                        _ = data.Append(value);
                        break;
                }
            }

            // What is left has no line feed yet: it counts against the limit, too.
            if (size + buffer.Length > maxEventBytes)
            {
                throw new InvalidDataException("An event of the stream is larger than the tunnel carries.");
            }

            reader.AdvanceTo(consumed, buffer.End);
            foreach (SseEvent item in ready)
            {
                yield return item;
            }

            if (result.IsCompleted)
            {
                yield break;
            }
        }
    }
}
