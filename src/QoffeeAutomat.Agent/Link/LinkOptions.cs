using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Agent.Link;

/// <summary>
/// Limits and timings of the QoffeeLink server (PLAN.md S07). The defaults are the values of the
/// specification; tests shorten the timings through the service collection.
/// </summary>
public sealed class LinkOptions
{
    /// <summary>The largest frame that is accepted and sent. A larger incoming frame closes the connection with code 1009.</summary>
    public int MaxFrameBytes { get; init; } = ProtocolInfo.MaxFrameBytes;

    /// <summary>The most frames that wait to be sent to one client. A client that falls further behind is disconnected.</summary>
    public int SendQueueCapacity { get; init; } = 256;

    /// <summary>The most bytes that wait to be sent to one client; a single frame that is larger than this is still sent when the queue is empty.</summary>
    public long SendQueueMaxBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>How long a client has to answer <c>hello</c> with <c>client_hello</c>.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The interval of the WebSocket keep-alive ping that the agent sends (S07: every 30 s).</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The time without any traffic after which a connection is dropped (S07: 75 s). The wait for the answer to a ping is this value minus <see cref="PingInterval"/>.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(75);

    /// <summary>The longest a single frame may take to be written to the socket before the client counts as stalled and is disconnected.</summary>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the agent waits for the close frame of the peer before it drops the connection.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a power event waits for the <c>bye</c> frames to be written; the computer may be about to sleep.</summary>
    public TimeSpan ByeFlushTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a stopping agent waits for the connections to end after <c>bye</c>.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The size of the buffer that frames are read into; frames that are larger arrive in several reads.</summary>
    public int ReceiveBufferBytes { get; init; } = 8 * 1024;

    /// <summary>The most requests (<c>run</c>, <c>subscribe</c>, <c>get_manifest</c>) of one connection that are in progress at the same time.</summary>
    public int MaxPendingRequests { get; init; } = 64;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFrameBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(SendQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(SendQueueMaxBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(HandshakeTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PingInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(IdleTimeout, PingInterval);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SendTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CloseTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(ByeFlushTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(ShutdownTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(ReceiveBufferBytes, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPendingRequests, 1);
    }
}
