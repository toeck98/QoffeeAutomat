using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Hub.Fleet;

/// <summary>
/// Limits and timings of the fleet link (PLAN.md S07, S20 "Fleet link", T913). The defaults are the values of the specification; tests shorten the timings through the
/// service collection. Every queue and every table of a fleet connection has a bound here, and a client that exceeds one is closed.
/// </summary>
public sealed class FleetOptions
{
    /// <summary>The largest frame that is accepted and sent. A larger incoming frame closes the connection with code 1009.</summary>
    public int MaxFrameBytes { get; init; } = ProtocolInfo.MaxFrameBytes;

    /// <summary>The most frames that wait to be sent to one client. A client that falls further behind is disconnected.</summary>
    public int SendQueueCapacity { get; init; } = 1024;

    /// <summary>The longest a single frame may take to be written to the socket before the client counts as stalled and is disconnected.</summary>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the hub waits for the close frame of the client before it drops the connection.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The most requests of one client that are in progress at the same time; further requests fail with <c>busy</c>.</summary>
    public int MaxPendingRequests { get; init; } = 32;

    /// <summary>The most frames that a client may send per second, averaged over one second; a client that sends more is disconnected.</summary>
    public int MaxFramesPerSecond { get; init; } = 100;

    /// <summary>The size of the buffer that frames are read into; larger frames arrive in several reads.</summary>
    public int ReceiveBufferBytes { get; init; } = 8 * 1024;

    /// <summary>The most fleet connections that the hub serves at the same time; further requests are answered with HTTP 503.</summary>
    public int MaxConnections { get; init; } = 16;

    /// <summary>The most agents that one connection subscribes to at the same time (50 agents are the design target of the hub, S20).</summary>
    public int MaxSubscriptionsPerConnection { get; init; } = 256;

    /// <summary>The longest command id that a <c>run</c> may name.</summary>
    public int MaxCommandLength { get; init; } = 256;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFrameBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(SendQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SendTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CloseTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPendingRequests, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFramesPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ReceiveBufferBytes, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConnections, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSubscriptionsPerConnection, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCommandLength, 1);
    }
}
