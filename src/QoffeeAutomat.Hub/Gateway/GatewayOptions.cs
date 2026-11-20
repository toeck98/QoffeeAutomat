using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Hub.Gateway;

/// <summary>
/// Limits and timings of the agent gateway (PLAN.md S07, S20, T906). The defaults are the values of the specification; tests shorten the timings
/// through the service collection. Every queue and every table of the gateway has a bound here, and an agent connection that exceeds one is closed.
/// </summary>
public sealed class GatewayOptions
{
    /// <summary>The largest frame that is accepted and sent. A larger incoming frame closes the connection with code 1009.</summary>
    public int MaxFrameBytes { get; init; } = ProtocolInfo.MaxFrameBytes;

    /// <summary>The most frames that wait to be sent to one agent. An agent that falls further behind is disconnected.</summary>
    public int SendQueueCapacity { get; init; } = 128;

    /// <summary>How long an agent has to send <c>hello</c> after the upgrade.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The interval of the WebSocket keep-alive ping of the hub (S07: every 30 s).</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The time without any traffic after which a connection is dropped (S07: 75 s). The wait for the answer to a ping is this value minus <see cref="PingInterval"/>.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(75);

    /// <summary>The longest a single frame may take to be written to the socket before the agent counts as stalled and is disconnected.</summary>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the hub waits for the close frame of the agent before it drops the connection.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a request that returns quickly (<c>get_manifest</c>, <c>subscribe</c>, <c>credential_rotate</c>, a <c>run</c> that does not wait) waits for its result.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a <c>run</c> that waits for the end of the command waits for its result.</summary>
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The most requests to one agent that are in progress at the same time; further requests fail with <c>busy</c>.</summary>
    public int MaxPendingRequests { get; init; } = 64;

    /// <summary>The most frames that an agent may send per second, averaged over one second; an agent that sends more is disconnected.</summary>
    public int MaxFramesPerSecond { get; init; } = 200;

    /// <summary>The most results that an agent may send for requests that the hub did not make before it is disconnected.</summary>
    public int MaxUnsolicitedResults { get; init; } = 16;

    /// <summary>The size of the buffer that frames are read into; larger frames arrive in several reads.</summary>
    public int ReceiveBufferBytes { get; init; } = 8 * 1024;

    /// <summary>The most events that are kept per agent; older ones are removed when a new one is stored.</summary>
    public int MaxEventsPerAgent { get; init; } = 500;

    /// <summary>The largest <c>data</c> of an event that is stored, as UTF-8 JSON; an event with more data is stored without it.</summary>
    public int MaxEventDataBytes { get; init; } = 16 * 1024;

    /// <summary>The most metrics in the live state of one agent. An agent that reports more is disconnected.</summary>
    public int MaxStateMetrics { get; init; } = 5000;

    /// <summary>The most metric subscriptions of internal consumers per agent.</summary>
    public int MaxSubscriptionsPerAgent { get; init; } = 256;

    /// <summary>The most metrics in one demand and in the union that is sent to an agent.</summary>
    public int MaxMetricsPerDemand { get; init; } = 2000;

    /// <summary>The most event streams of the management tunnel that are open to one agent at the same time (T912).</summary>
    public int MaxStreamsPerAgent { get; init; } = 16;

    /// <summary>The most events of one tunnelled stream that wait for the browser to read them.</summary>
    public int StreamQueueCapacity { get; init; } = 256;

    /// <summary>
    /// How long the receive loop of an agent link waits for room in a full stream queue before it closes the stream. The wait holds back everything that the agent sends
    /// on this link, which is the back-pressure: the agent's send queue fills and its stream pump waits. It is short because every other message of the agent waits too.
    /// </summary>
    public TimeSpan StreamBackpressureTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The optional features that the hub announces in <c>client_hello</c> (<see cref="QoffeeAutomat.Contracts.Hub.HubFeatures"/>). A feature is used with an agent only
    /// when both sides announced it. The default of the hub (<c>AddHubGateway</c>) announces <c>config_sync</c>, which the rollout service serves (T909), and <c>mgmt_tunnel</c>, which
    /// the tunnel proxy serves (T912).
    /// </summary>
    public IReadOnlyList<string> Features { get; init; } = [];

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFrameBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(SendQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(HandshakeTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PingInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(IdleTimeout, PingInterval);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SendTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CloseTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RequestTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RunTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPendingRequests, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFramesPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxUnsolicitedResults, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(ReceiveBufferBytes, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEventsPerAgent, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEventDataBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxStateMetrics, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSubscriptionsPerAgent, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxMetricsPerDemand, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxStreamsPerAgent, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(StreamQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StreamBackpressureTimeout, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(Features);
    }
}
