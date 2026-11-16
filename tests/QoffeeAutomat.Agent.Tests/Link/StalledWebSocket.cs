using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace QoffeeAutomat.Agent.Tests.Link;

/// <summary>
/// A WebSocket of a client that has stopped reading: the frames that the test hands in arrive at the agent,
/// but every frame that the agent sends stays in the send call until the test releases it or the agent aborts
/// the socket. It is what a dead Wi-Fi link looks like to the link server.
/// </summary>
internal sealed class StalledWebSocket : WebSocket
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _sendAttempts;
    private int _state = (int)WebSocketState.Open;

    /// <summary>The number of frames that the agent has tried to send.</summary>
    public int SendAttempts => Volatile.Read(ref _sendAttempts);

    /// <summary>True once the agent has aborted the socket.</summary>
    public bool WasAborted => State == WebSocketState.Aborted;

    public override WebSocketCloseStatus? CloseStatus => null;

    public override string? CloseStatusDescription => null;

    public override WebSocketState State => (WebSocketState)Volatile.Read(ref _state);

    public override string? SubProtocol => "qoffeelink.v1";

    /// <summary>Lets the agent receive a text frame.</summary>
    public void Receive(string json)
    {
        Assert.True(_incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>Lets the stalled sends complete, as if the client had started to read again.</summary>
    public void Resume()
    {
        _release.TrySetResult();
    }

    public override void Abort()
    {
        Volatile.Write(ref _state, (int)WebSocketState.Aborted);
        _incoming.Writer.TryComplete();
        _release.TrySetCanceled();
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _state, (int)WebSocketState.Closed);
        _incoming.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _state, (int)WebSocketState.CloseSent);
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _incoming.Writer.TryComplete();
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            byte[] frame = await _incoming.Reader.ReadAsync(cancellationToken);
            Assert.True(frame.Length <= buffer.Count, "The test frame is larger than the receive buffer.");
            frame.CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(frame.Length, WebSocketMessageType.Text, true);
        }
        catch (ChannelClosedException)
        {
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
        }
    }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _sendAttempts);
        await _release.Task.WaitAsync(cancellationToken);
    }
}
