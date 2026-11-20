using System.Text.Json;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Hub.Gateway;

/// <summary>Whether and how an agent is connected to the hub (PLAN.md S20 "Fleet link": <c>agent_status</c>).</summary>
/// <param name="AgentId">The agent id.</param>
/// <param name="Online">True while the agent link is open and the handshake is complete.</param>
/// <param name="ConnectedAt">When the current connection was established; null while offline.</param>
/// <param name="LastSeenAt">When the hub last heard from the agent (a frame or the end of a connection); null when it never connected since the hub started.</param>
/// <param name="PowerState">The reason of the last <c>bye</c> of the agent (a <see cref="ByeReasons"/> value) while it is offline, otherwise <see cref="PowerStates.Unknown"/>.</param>
/// <param name="ManifestRev">The revision of the cached manifest, or null when the hub has none.</param>
/// <param name="BootId">The <c>boot_id</c> of the last <c>hello</c>.</param>
/// <param name="Features">The features that both sides announced (the hub's <see cref="GatewayOptions.Features"/> and those of the agent's <c>hello</c>).</param>
/// <param name="Agent">The identity from the last <c>hello</c>.</param>
/// <param name="ConnectionId">Identifies the current connection: it grows with every connection the gateway accepts, whichever agent, and is 0 while the agent is offline. Services that remember what they sent over a connection compare it.</param>
public sealed record AgentPresence(
    string AgentId,
    bool Online,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? LastSeenAt,
    string PowerState,
    string? ManifestRev,
    string? BootId,
    IReadOnlyList<string> Features,
    AgentInfo? Agent,
    long ConnectionId = 0);

/// <summary>The manifest of an agent as the hub holds it, keyed by its revision.</summary>
/// <param name="Rev">The revision (<see cref="Manifest.Rev"/>).</param>
/// <param name="Manifest">The manifest as the agent sent it (the data of the result of <c>get_manifest</c>).</param>
/// <param name="FetchedAt">When the hub fetched it.</param>
public sealed record CachedManifest(string Rev, JsonElement Manifest, DateTimeOffset FetchedAt);

/// <summary>The latest values that an agent reported for the metrics that the hub has subscribed.</summary>
/// <param name="Ts">The unix time in milliseconds of the last <c>state</c> frame.</param>
/// <param name="Values">Metric id to value; a JSON null means unavailable.</param>
/// <param name="UpdatedAt">When the hub received the last frame.</param>
public sealed record AgentStateSnapshot(long Ts, IReadOnlyDictionary<string, JsonElement> Values, DateTimeOffset UpdatedAt);

/// <summary>A <c>state</c> frame of an agent, as notification.</summary>
/// <param name="AgentId">The agent id.</param>
/// <param name="Ts">The unix time in milliseconds.</param>
/// <param name="Values">The values in the frame: all subscribed metrics for a snapshot, the changed ones otherwise.</param>
/// <param name="IsSnapshot">True for the full snapshot that follows a <c>subscribe</c>.</param>
public sealed record AgentStateUpdate(string AgentId, long Ts, IReadOnlyDictionary<string, JsonElement> Values, bool IsSnapshot);

/// <summary>An event that an agent reported and the hub stored.</summary>
/// <param name="Id">The sequence number of the stored event.</param>
/// <param name="AgentId">The agent id.</param>
/// <param name="At">The time that the agent reported.</param>
/// <param name="ReceivedAt">When the hub received it.</param>
/// <param name="Type">The event type.</param>
/// <param name="Data">The event data; null when there was none or it was too large to store.</param>
public sealed record StoredAgentEvent(long Id, string AgentId, DateTimeOffset At, DateTimeOffset ReceivedAt, string Type, JsonElement? Data);

/// <summary>A request to run a command on an agent.</summary>
/// <param name="AgentId">The agent id.</param>
/// <param name="Command">The command id.</param>
/// <param name="Vars">The values of the variables, or null for none.</param>
/// <param name="Wait">True to wait for the end of the run (the default of the protocol); false to return when the agent started it.</param>
/// <param name="UserId">The hub user (or the Home Assistant user) on whose behalf the command runs; it goes to the audit log of the agent as <c>context.user_id</c>.</param>
public sealed record AgentRunRequest(
    string AgentId,
    string Command,
    IReadOnlyDictionary<string, JsonElement>? Vars = null,
    bool Wait = true,
    string? UserId = null);

/// <summary>The outcome of <see cref="IAgentGateway.RunAsync"/>. A failure is a value, not an exception: the agent's error codes (S07) and <c>agent_offline</c> are expected.</summary>
/// <param name="Result">The data of the result when the agent ran or started the command; null on failure.</param>
/// <param name="ErrorCode">The error code of the failure: an agent code (<see cref="ErrorCodes"/>), <see cref="HubErrorCodes.AgentOffline"/>, <see cref="ErrorCodes.Timeout"/> when the agent did not answer in time or <see cref="ErrorCodes.Busy"/> when too many requests are open; null on success.</param>
/// <param name="ErrorMessage">What went wrong, for people.</param>
public sealed record AgentRunOutcome(RunResultData? Result, string? ErrorCode, string? ErrorMessage)
{
    /// <summary>True when the agent accepted the request.</summary>
    public bool Ok => ErrorCode is null;

    /// <summary>Creates a successful outcome.</summary>
    /// <param name="result">The result data.</param>
    /// <returns>The outcome.</returns>
    public static AgentRunOutcome Success(RunResultData result) => new(result, null, null);

    /// <summary>Creates a failed outcome.</summary>
    /// <param name="code">The error code.</param>
    /// <param name="message">What went wrong.</param>
    /// <returns>The outcome.</returns>
    public static AgentRunOutcome Failure(string code, string message) => new(null, code, message);
}

/// <summary>
/// Receives notifications of the gateway: presence, manifest, state and events. The methods are called on the thread that handled the change, one after another for the
/// same agent, and must return quickly and not throw; an observer that needs time copies the value and works on its own queue (which it bounds). An exception is
/// logged and does not reach the agent connection.
/// </summary>
public interface IAgentGatewayObserver
{
    /// <summary>An agent went online or offline, or the identity or the power state of an agent changed.</summary>
    /// <param name="presence">The new presence.</param>
    void OnPresenceChanged(AgentPresence presence)
    {
    }

    /// <summary>The hub cached a manifest with a revision that it did not have for the agent.</summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="manifest">The new manifest.</param>
    void OnManifestChanged(string agentId, CachedManifest manifest)
    {
    }

    /// <summary>An agent sent a <c>state</c> frame. The live state cache already contains the values.</summary>
    /// <param name="update">The frame.</param>
    void OnStateChanged(AgentStateUpdate update)
    {
    }

    /// <summary>An agent sent an event; it is stored.</summary>
    /// <param name="agentEvent">The event.</param>
    void OnEvent(StoredAgentEvent agentEvent)
    {
    }

    /// <summary>An administrator removed the agent from the hub; all that the gateway knows about it is gone.</summary>
    /// <param name="agentId">The agent id.</param>
    void OnAgentRemoved(string agentId)
    {
    }

    /// <summary>An agent sent <c>config_status</c> (feature <c>config_sync</c>, T909): the outcome of a generation, or after every handshake the last applied one.</summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="connectionId">The connection it arrived on (<see cref="AgentPresence.ConnectionId"/>).</param>
    /// <param name="status">The frame.</param>
    void OnConfigStatus(string agentId, long connectionId, ConfigStatusMessage status)
    {
    }
}

/// <summary>The outcome of <see cref="IAgentRequestChannel.RequestAsync"/>. A failure is a value, not an exception.</summary>
/// <param name="Data">The data of the result; null for a failure or a result without data.</param>
/// <param name="ErrorCode">The error code: an agent code (<see cref="ErrorCodes"/>), <see cref="HubErrorCodes.AgentOffline"/>, <see cref="ErrorCodes.Timeout"/> or <see cref="ErrorCodes.Busy"/>; null on success.</param>
/// <param name="ErrorMessage">What went wrong, for people.</param>
public sealed record AgentRequestOutcome(JsonElement? Data, string? ErrorCode, string? ErrorMessage)
{
    /// <summary>True when the agent answered with an ok result.</summary>
    public bool Ok => ErrorCode is null;

    /// <summary>Creates a successful outcome.</summary>
    /// <param name="data">The data of the result.</param>
    /// <returns>The outcome.</returns>
    public static AgentRequestOutcome Success(JsonElement? data) => new(data, null, null);

    /// <summary>Creates a failed outcome.</summary>
    /// <param name="code">The error code.</param>
    /// <param name="message">What went wrong.</param>
    /// <returns>The outcome.</returns>
    public static AgentRequestOutcome Failure(string code, string message) => new(null, code, message);
}

/// <summary>
/// Sends one request frame of a hub feature to a connected agent and waits for its result (T909: <c>config_desired</c> and <c>config_documents</c>). The gateway owns the
/// request ids, the correlation and the limits; the caller only builds the frame.
/// </summary>
public interface IAgentRequestChannel
{
    /// <summary>Sends a request to an agent.</summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="connectionId">The connection the request is meant for (<see cref="AgentPresence.ConnectionId"/>); the request fails with <see cref="HubErrorCodes.AgentOffline"/> when the agent has another one by now. 0 means any.</param>
    /// <param name="build">Creates the UTF-8 JSON of the frame for the request id that the gateway chose.</param>
    /// <param name="cancellationToken">Cancels the wait; the request cannot be taken back.</param>
    /// <returns>The outcome.</returns>
    Task<AgentRequestOutcome> RequestAsync(string agentId, long connectionId, Func<long, byte[]> build, CancellationToken cancellationToken);
}

/// <summary>The handle of one consumer's demand for the metrics of one agent. Dispose it when the consumer goes away; the gateway then drops what no other consumer wants.</summary>
public interface IMetricSubscription : IDisposable
{
    /// <summary>The agent whose metrics are subscribed.</summary>
    string AgentId { get; }

    /// <summary>Replaces the demand of this consumer, for example when a hub UI view shows other metrics.</summary>
    /// <param name="demand">The new demand.</param>
    void Update(MetricDemand demand);
}

/// <summary>
/// What the other services of the hub (the configuration service, the metric store, the tunnel proxy, the fleet link and the UI) use from the agent gateway (PLAN.md S20, T906):
/// presence, the manifest and state caches, the union subscription, running commands, and notifications. Everything works for an agent that is offline: the
/// caches keep the last values, a subscription is held and sent when the agent connects, and a run fails with <see cref="HubErrorCodes.AgentOffline"/>.
/// </summary>
public interface IAgentGateway
{
    /// <summary>The presence of every agent that connected since the hub started.</summary>
    /// <returns>The presences, ordered by agent id.</returns>
    IReadOnlyList<AgentPresence> GetAllPresence();

    /// <summary>The presence of an agent.</summary>
    /// <param name="agentId">The agent id.</param>
    /// <returns>The presence; an agent that never connected is offline with the power state <see cref="PowerStates.Unknown"/>.</returns>
    AgentPresence GetPresence(string agentId);

    /// <summary>The cached manifest of an agent.</summary>
    /// <param name="agentId">The agent id.</param>
    /// <returns>The manifest, or null when the hub has none.</returns>
    CachedManifest? GetManifest(string agentId);

    /// <summary>The live state of an agent: the last values of the subscribed metrics.</summary>
    /// <param name="agentId">The agent id.</param>
    /// <returns>The state, or null when no frame arrived yet.</returns>
    AgentStateSnapshot? GetState(string agentId);

    /// <summary>The newest stored events of an agent.</summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="limit">The most events to return.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The events, the newest first.</returns>
    Task<IReadOnlyList<StoredAgentEvent>> GetEventsAsync(string agentId, int limit, CancellationToken cancellationToken);

    /// <summary>Declares what a consumer wants to receive from an agent. The gateway keeps the agent subscribed to the union of the demands of all consumers.</summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="demand">The demand.</param>
    /// <returns>The handle; dispose it to drop the demand.</returns>
    /// <exception cref="InvalidOperationException">The agent has too many subscriptions (<see cref="GatewayOptions.MaxSubscriptionsPerAgent"/>) or the union would be too large.</exception>
    IMetricSubscription SubscribeMetrics(string agentId, MetricDemand demand);

    /// <summary>Starts to notify an observer.</summary>
    /// <param name="observer">The observer.</param>
    /// <returns>A handle; dispose it to stop the notifications.</returns>
    IDisposable Observe(IAgentGatewayObserver observer);

    /// <summary>Runs a command on an agent.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the wait; the command on the agent is not cancelled.</param>
    /// <returns>The outcome.</returns>
    Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken cancellationToken);
}
