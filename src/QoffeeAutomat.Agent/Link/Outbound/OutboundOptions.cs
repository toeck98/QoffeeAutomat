using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Agent.Link.Outbound;

/// <summary>
/// Timings of the outbound links (ADR 0003, PLAN.md S07 and S20). The defaults are the values of the specification: a
/// reconnect delay that grows from one second to five minutes with jitter, and the same keep-alive as the inbound link.
/// Tests shorten them through the service collection.
/// </summary>
public sealed class OutboundOptions
{
    /// <summary>How long a dial attempt (TCP, TLS and the WebSocket upgrade) may take.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The first reconnect delay, before jitter (S20: one second).</summary>
    public TimeSpan BackoffMin { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest reconnect delay, before jitter (S20: five minutes).</summary>
    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The interval of the WebSocket keep-alive ping that the agent sends (S07: every 30 s).</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The time without any traffic after which the connection is dropped (S07: 75 s).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(75);

    /// <summary>The largest frame that is accepted; the same limit as the inbound link.</summary>
    public int MaxFrameBytes { get; init; } = ProtocolInfo.MaxFrameBytes;

    /// <summary>
    /// A source of random numbers in [0, 1) for the jitter; null uses <see cref="Random.Shared"/>. Tests pass a fixed value so
    /// that the delays are exact.
    /// </summary>
    public Func<double>? Random { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ConnectTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(BackoffMin, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(BackoffMax, BackoffMin);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PingInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(IdleTimeout, PingInterval);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFrameBytes, 1024);
    }
}
