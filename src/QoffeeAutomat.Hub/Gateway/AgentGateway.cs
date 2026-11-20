using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Hosting;
using QoffeeAutomat.Hub.Storage;
using QoffeeAutomat.Hub.Tunnel;

namespace QoffeeAutomat.Hub.Gateway;

/// <summary>Everything the gateway keeps about one agent, across connections. All fields are read and written under <see cref="Gate"/>.</summary>
internal sealed class AgentSlot(string agentId)
{
    public string AgentId { get; } = agentId;

    public object Gate { get; } = new();

    public Dictionary<long, MetricDemand> Demands { get; } = [];

    public AgentSession? Session { get; set; }

    public DateTimeOffset? ConnectedAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public string PowerState { get; set; } = PowerStates.Unknown;

    public string? BootId { get; set; }

    public IReadOnlyList<string> Features { get; set; } = [];

    public long ConnectionId { get; set; }

    /// <summary>How many links of this agent the gateway accepted since the hub started.</summary>
    public long Connections { get; set; }

    public AgentInfo? Info { get; set; }

    public CachedManifest? Manifest { get; set; }

    public AgentStateSnapshot? State { get; set; }

    /// <summary>True between the result of an accepted <c>subscribe</c> and the full <c>state</c> snapshot that follows it.</summary>
    public bool AwaitingSnapshot { get; set; }

    /// <summary>The key of the union that the current connection is subscribed to (<see cref="MetricUnion.Key"/>); empty for none.</summary>
    public string SentKey { get; set; } = string.Empty;

    public bool SyncRunning { get; set; }

    public bool SyncDirty { get; set; }

    public bool ManifestRunning { get; set; }

    public string? ManifestWanted { get; set; }

    public long LastSubscriptionId { get; set; }
}

/// <summary>
/// The agent gateway (PLAN.md S07, S20, T906): the hub's side of the agent link. It accepts the link of an authenticated agent (<see cref="AgentLinkHandler"/>), runs the
/// handshake as a client of the kind <c>hub</c>, and keeps for every agent the presence, the manifest (cached by revision), the live state and the stored events. Other
/// hub services use it through <see cref="IAgentGateway"/>: they declare which metrics they want (<see cref="MetricDemand"/>), and the gateway holds each agent at the
/// union of the demands (<see cref="MetricUnion"/>) and sends a new <c>subscribe</c> whenever a consumer comes or goes; they run commands, and they observe presence,
/// manifest, state and events. The gateway is also the <see cref="IAgentLinkNotifier"/> through which <c>credential_rotate</c> and <c>unenroll</c> reach a connected agent.
/// One session exists per agent: a new connection of the same agent replaces the old one. All queues are bounded (<see cref="GatewayOptions"/>).
/// </summary>
public sealed class AgentGateway : IAgentGateway, IAgentLinkNotifier, IAgentRequestChannel, IAgentStreamChannel, IGatewayStatistics, IDisposable
{
    private const int MaxManifestFetches = 3;
    private const int MaxErrorMessageLength = 512;

    private readonly AgentRegistry _registry;
    private readonly AgentEventStore _events;
    private readonly GatewayOptions _options;
    private readonly HubIdentity _identity;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentGateway> _logger;
    private readonly ConcurrentDictionary<string, AgentSlot> _slots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<AgentSession, byte> _sessions = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _observerGate = new();
    private IAgentGatewayObserver[] _observers = [];
    private long _lastConnectionId;
    private long _linkReconnects;
    private volatile bool _stopping;

    /// <summary>Creates the gateway.</summary>
    /// <param name="registry">The agent registry, for the last-seen time.</param>
    /// <param name="events">Stores the events of the agents.</param>
    /// <param name="options">Limits and timings.</param>
    /// <param name="identity">The identity of the hub, for <c>client_hello</c>.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public AgentGateway(
        AgentRegistry registry,
        AgentEventStore events,
        GatewayOptions options,
        HubIdentity identity,
        TimeProvider time,
        ILogger<AgentGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();

        _registry = registry;
        _events = events;
        _options = options;
        _identity = identity;
        _time = time;
        _logger = logger;
    }

    /// <summary>True once the hub is stopping; the endpoint then refuses new links.</summary>
    public bool IsStopping => _stopping;

    /// <summary>The limits and timings in effect.</summary>
    public GatewayOptions Options => _options;

    /// <inheritdoc />
    public long LinkReconnects => Interlocked.Read(ref _linkReconnects);

    // ------------------------------------------------------------------
    // Serving agents
    // ------------------------------------------------------------------

    /// <summary>Serves an accepted WebSocket of an authenticated agent until the connection ends.</summary>
    /// <param name="socket">The accepted WebSocket with the sub-protocol <c>qoffeelink.v1</c>; the caller disposes it.</param>
    /// <param name="agent">The agent whose credential opened the link.</param>
    /// <param name="requestAborted">Fires when the transport under the socket is gone.</param>
    /// <returns>A task that completes when the connection has ended.</returns>
    public async Task ServeAsync(System.Net.WebSockets.WebSocket socket, AgentRecord agent, CancellationToken requestAborted)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(agent);

        var session = new AgentSession(this, agent.Id, socket, _options, _time, _logger);
        _ = _sessions.TryAdd(session, 0);
        try
        {
            if (_stopping)
            {
                session.Close(CloseCodes.GoingAway, "The hub is stopping.");
            }

            await session.RunAsync(requestAborted).ConfigureAwait(false);
        }
        finally
        {
            _ = _sessions.TryRemove(session, out _);
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal ClientHelloMessage CreateClientHello()
    {
        return new ClientHelloMessage(
            ProtocolInfo.Version,
            new ClientInfo("hub", _identity.Name, HubVersion.Current),
            _options.Features.Count > 0 ? _options.Features : null);
    }

    internal void Attach(AgentSession session, HelloMessage hello)
    {
        AgentSlot slot = GetSlot(session.AgentId);
        DateTimeOffset now = _time.GetUtcNow();
        AgentSession? previous;
        AgentPresence presence;
        lock (slot.Gate)
        {
            previous = slot.Session;
            slot.Session = session;
            slot.ConnectedAt = now;
            slot.LastSeenAt = now;
            slot.PowerState = PowerStates.Unknown;
            slot.BootId = hello.BootId;
            slot.Info = hello.Agent;
            slot.Features = Negotiate(hello.Features);
            slot.ConnectionId = Interlocked.Increment(ref _lastConnectionId);
            if (slot.Connections++ > 0)
            {
                _ = Interlocked.Increment(ref _linkReconnects);
            }

            // A new connection starts without a subscription; the next synchronisation sends the union.
            slot.SentKey = string.Empty;
            slot.AwaitingSnapshot = false;
            presence = BuildPresence(slot);
        }

        if (previous is not null)
        {
            GatewayLog.AgentReplaced(_logger, session.AgentId);
            previous.Close(CloseCodes.Normal, "Replaced by a new connection of the same agent.");
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            string features = string.Join(',', presence.Features);
            GatewayLog.AgentConnected(_logger, session.AgentId, features);
        }
        Notify(observer => observer.OnPresenceChanged(presence));
    }

    internal async Task DetachAsync(AgentSession session)
    {
        if (!_slots.TryGetValue(session.AgentId, out AgentSlot? slot))
        {
            return;
        }

        AgentPresence presence;
        lock (slot.Gate)
        {
            if (!ReferenceEquals(slot.Session, session))
            {
                // A newer connection of the agent replaced this one; the agent is still online.
                return;
            }

            slot.Session = null;
            slot.ConnectedAt = null;
            slot.LastSeenAt = session.LastFrameAt;
            slot.SentKey = string.Empty;
            slot.AwaitingSnapshot = false;
            presence = BuildPresence(slot);
        }

        GatewayLog.AgentDisconnected(_logger, session.AgentId, presence.PowerState);
        Notify(observer => observer.OnPresenceChanged(presence));
        try
        {
            await _registry.MarkSeenAsync(session.AgentId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            GatewayLog.LastSeenNotStored(_logger, session.AgentId, exception.GetType().Name);
        }
    }

    internal void StartSynchronisation(AgentSession session, string announcedRev)
    {
        AgentSlot slot = GetSlot(session.AgentId);
        ScheduleManifest(slot, session, announcedRev);
        ScheduleSubscription(slot);
    }

    internal bool OnState(AgentSession session, StateMessage message)
    {
        if (!_slots.TryGetValue(session.AgentId, out AgentSlot? slot))
        {
            return true;
        }

        AgentStateUpdate update;
        lock (slot.Gate)
        {
            if (!ReferenceEquals(slot.Session, session))
            {
                return true;
            }

            // The first frame after an accepted subscribe is a full snapshot and replaces what the hub knew; later frames carry changes.
            bool snapshot = slot.AwaitingSnapshot;
            slot.AwaitingSnapshot = false;
            var values = snapshot || slot.State is null
                ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>(slot.State.Values, StringComparer.Ordinal);
            foreach ((string id, JsonElement value) in message.V)
            {
                values[id] = value;
            }

            if (values.Count > _options.MaxStateMetrics)
            {
                return false;
            }

            slot.State = new AgentStateSnapshot(message.Ts, values, _time.GetUtcNow());
            update = new AgentStateUpdate(session.AgentId, message.Ts, message.V, snapshot);
        }

        Notify(observer => observer.OnStateChanged(update));
        return true;
    }

    internal async Task OnEventAsync(AgentSession session, EventMessage message, CancellationToken cancellationToken)
    {
        if (!IsCurrent(session))
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        StoredAgentEvent stored;
        try
        {
            stored = await _events.AppendAsync(session.AgentId, message.Type, ReportedTime(message.Ts, now), now, message.Data, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            GatewayLog.EventNotStored(_logger, session.AgentId, exception.GetType().Name);
            return;
        }

        Notify(observer => observer.OnEvent(stored));
    }

    internal void OnManifestChanged(AgentSession session, string rev)
    {
        if (_slots.TryGetValue(session.AgentId, out AgentSlot? slot))
        {
            ScheduleManifest(slot, session, rev);
        }
    }

    internal void OnBye(AgentSession session, string reason)
    {
        if (!_slots.TryGetValue(session.AgentId, out AgentSlot? slot))
        {
            return;
        }

        lock (slot.Gate)
        {
            if (ReferenceEquals(slot.Session, session))
            {
                slot.PowerState = reason is ByeReasons.Sleep or ByeReasons.Hibernate or ByeReasons.Shutdown or ByeReasons.Restart or ByeReasons.ServiceStop or ByeReasons.Update
                    ? reason
                    : PowerStates.Unknown;
            }
        }
    }

    internal void OnConfigStatus(AgentSession session, JsonElement raw)
    {
        if (!_slots.TryGetValue(session.AgentId, out AgentSlot? slot))
        {
            return;
        }

        long connection;
        lock (slot.Gate)
        {
            // A message of a feature that was not negotiated, or of a connection that has been replaced, is ignored (S20).
            if (!ReferenceEquals(slot.Session, session) || !slot.Features.Contains(HubFeatures.ConfigSync, StringComparer.Ordinal))
            {
                return;
            }

            connection = slot.ConnectionId;
        }

        ConfigStatusMessage? status;
        try
        {
            status = raw.Deserialize(HubJsonContext.Default.ConfigStatusMessage);
        }
        catch (JsonException)
        {
            GatewayLog.ConfigStatusIgnored(_logger, session.AgentId);
            return;
        }

        if (status is not null)
        {
            Notify(observer => observer.OnConfigStatus(session.AgentId, connection, status));
        }
    }

    // ------------------------------------------------------------------
    // Caches and presence
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public IReadOnlyList<AgentPresence> GetAllPresence()
    {
        var all = new List<AgentPresence>();
        foreach (AgentSlot slot in _slots.Values)
        {
            lock (slot.Gate)
            {
                // Slots that only exist because a consumer subscribed to an agent that never connected are not agents the hub has seen.
                if (slot.LastSeenAt is not null || slot.Session is not null)
                {
                    all.Add(BuildPresence(slot));
                }
            }
        }

        all.Sort(static (left, right) => string.CompareOrdinal(left.AgentId, right.AgentId));
        return all;
    }

    /// <inheritdoc />
    public AgentPresence GetPresence(string agentId)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        if (!_slots.TryGetValue(agentId, out AgentSlot? slot))
        {
            return Offline(agentId);
        }

        lock (slot.Gate)
        {
            return BuildPresence(slot);
        }
    }

    /// <inheritdoc />
    public CachedManifest? GetManifest(string agentId)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        if (!_slots.TryGetValue(agentId, out AgentSlot? slot))
        {
            return null;
        }

        lock (slot.Gate)
        {
            return slot.Manifest;
        }
    }

    /// <inheritdoc />
    public AgentStateSnapshot? GetState(string agentId)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        if (!_slots.TryGetValue(agentId, out AgentSlot? slot))
        {
            return null;
        }

        lock (slot.Gate)
        {
            return slot.State;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredAgentEvent>> GetEventsAsync(string agentId, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        return _events.RecentAsync(agentId, limit, cancellationToken);
    }

    private static AgentPresence BuildPresence(AgentSlot slot)
    {
        AgentSession? session = slot.Session;
        DateTimeOffset? lastSeen = slot.LastSeenAt;
        if (session is not null && (lastSeen is null || session.LastFrameAt > lastSeen))
        {
            lastSeen = session.LastFrameAt;
        }

        return new AgentPresence(
            slot.AgentId,
            session is not null,
            slot.ConnectedAt,
            lastSeen,
            slot.PowerState,
            slot.Manifest?.Rev,
            slot.BootId,
            slot.Features,
            slot.Info,
            session is not null ? slot.ConnectionId : 0);
    }

    private static AgentPresence Offline(string agentId)
    {
        return new AgentPresence(agentId, false, null, null, PowerStates.Unknown, null, null, [], null);
    }

    private IReadOnlyList<string> Negotiate(IReadOnlyList<string>? announced)
    {
        if (announced is null || announced.Count == 0 || _options.Features.Count == 0)
        {
            return [];
        }

        return [.. _options.Features.Where(feature => announced.Contains(feature, StringComparer.Ordinal))];
    }

    private static DateTimeOffset ReportedTime(long unixMilliseconds, DateTimeOffset fallback)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return fallback;
        }
    }

    // ------------------------------------------------------------------
    // Manifest
    // ------------------------------------------------------------------

    private void ScheduleManifest(AgentSlot slot, AgentSession session, string rev)
    {
        lock (slot.Gate)
        {
            if (!ReferenceEquals(slot.Session, session) || (slot.Manifest is { } cached && string.Equals(cached.Rev, rev, StringComparison.Ordinal)))
            {
                return;
            }

            slot.ManifestWanted = rev;
            if (slot.ManifestRunning)
            {
                return;
            }

            slot.ManifestRunning = true;
        }

        _ = Task.Run(() => FetchManifestAsync(slot, session), CancellationToken.None);
    }

    private async Task FetchManifestAsync(AgentSlot slot, AgentSession session)
    {
        bool released = false;
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                lock (slot.Gate)
                {
                    string? wanted = slot.ManifestWanted;
                    bool needed = attempt < MaxManifestFetches
                        && wanted is not null
                        && ReferenceEquals(slot.Session, session)
                        && !string.Equals(slot.Manifest?.Rev, wanted, StringComparison.Ordinal);
                    if (!needed)
                    {
                        slot.ManifestWanted = null;
                        slot.ManifestRunning = false;
                        released = true;
                        return;
                    }
                }

                ResultMessage result = await session.RequestAsync(
                    static id => LinkCodec.SerializeToUtf8(new GetManifestMessage(id)),
                    _options.RequestTimeout,
                    _stop.Token).ConfigureAwait(false);
                if (!result.Ok || result.Data is not { } data)
                {
                    GatewayLog.ManifestFailed(_logger, slot.AgentId, result.Error?.Code ?? "no data");
                    return;
                }

                Manifest manifest = LinkCodec.ParseManifest(data);
                CacheManifest(slot, session, new CachedManifest(manifest.Rev, data, _time.GetUtcNow()));
            }
        }
        catch (Exception exception) when (exception is AgentOfflineException or GatewayBusyException or TimeoutException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            GatewayLog.ManifestFailed(_logger, slot.AgentId, exception.GetType().Name);
        }
        finally
        {
            if (!released)
            {
                lock (slot.Gate)
                {
                    slot.ManifestWanted = null;
                    slot.ManifestRunning = false;
                }
            }
        }
    }

    private void CacheManifest(AgentSlot slot, AgentSession session, CachedManifest manifest)
    {
        AgentPresence? presence = null;
        bool changed;
        lock (slot.Gate)
        {
            if (!ReferenceEquals(slot.Session, session))
            {
                return;
            }

            changed = !string.Equals(slot.Manifest?.Rev, manifest.Rev, StringComparison.Ordinal);
            slot.Manifest = manifest;
            if (changed)
            {
                presence = BuildPresence(slot);
            }
        }

        if (changed)
        {
            GatewayLog.ManifestCached(_logger, slot.AgentId, manifest.Rev);
            Notify(observer => observer.OnManifestChanged(slot.AgentId, manifest));
            if (presence is not null)
            {
                Notify(observer => observer.OnPresenceChanged(presence));
            }
        }
    }

    // ------------------------------------------------------------------
    // Subscriptions
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public IMetricSubscription SubscribeMetrics(string agentId, MetricDemand demand)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentNullException.ThrowIfNull(demand);

        AgentSlot slot = GetSlot(agentId);
        long id;
        lock (slot.Gate)
        {
            if (slot.Demands.Count >= _options.MaxSubscriptionsPerAgent)
            {
                throw new InvalidOperationException("The agent has too many metric subscriptions.");
            }

            CheckUnionSize(slot, demand, replacing: null);
            id = ++slot.LastSubscriptionId;
            slot.Demands[id] = demand;
        }

        ScheduleSubscription(slot);
        return new MetricSubscription(this, slot, id);
    }

    private void CheckUnionSize(AgentSlot slot, MetricDemand demand, long? replacing)
    {
        IEnumerable<MetricDemand> others = slot.Demands.Where(pair => pair.Key != replacing).Select(pair => pair.Value);
        if (MetricUnion.Compute(others.Append(demand)).Count > _options.MaxMetricsPerDemand)
        {
            throw new InvalidOperationException("The subscriptions of the agent name too many metrics.");
        }
    }

    private void UpdateDemand(AgentSlot slot, long id, MetricDemand demand)
    {
        lock (slot.Gate)
        {
            if (!slot.Demands.ContainsKey(id))
            {
                return;
            }

            CheckUnionSize(slot, demand, id);
            slot.Demands[id] = demand;
        }

        ScheduleSubscription(slot);
    }

    private void RemoveDemand(AgentSlot slot, long id)
    {
        bool removed;
        lock (slot.Gate)
        {
            removed = slot.Demands.Remove(id);
        }

        if (removed)
        {
            ScheduleSubscription(slot);
        }
    }

    private void ScheduleSubscription(AgentSlot slot)
    {
        lock (slot.Gate)
        {
            slot.SyncDirty = true;
            if (slot.SyncRunning || slot.Session is null || _stopping)
            {
                return;
            }

            slot.SyncRunning = true;
        }

        _ = Task.Run(() => SubscriptionPumpAsync(slot), CancellationToken.None);
    }

    /// <summary>
    /// Brings the agent to the union of the demands of its consumers. One request is in flight at a time; a change that comes meanwhile marks the slot dirty and the
    /// loop computes the union again, so the agent ends up at the latest union and never receives more than one subscribe for several changes that arrive together.
    /// </summary>
    private async Task SubscriptionPumpAsync(AgentSlot slot)
    {
        bool released = false;
        try
        {
            while (true)
            {
                AgentSession session;
                MetricUnion union;
                lock (slot.Gate)
                {
                    if (!slot.SyncDirty || slot.Session is not { } current || _stopping)
                    {
                        slot.SyncDirty = false;
                        slot.SyncRunning = false;
                        released = true;
                        return;
                    }

                    slot.SyncDirty = false;
                    session = current;
                    union = MetricUnion.Compute(slot.Demands.Values);
                    if (string.Equals(union.Key, slot.SentKey, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                await SendSubscribeAsync(slot, session, union).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!released)
            {
                lock (slot.Gate)
                {
                    slot.SyncRunning = false;
                }
            }
        }
    }

    private async Task SendSubscribeAsync(AgentSlot slot, AgentSession session, MetricUnion union)
    {
        try
        {
            ResultMessage result = await session.RequestAsync(
                id => LinkCodec.SerializeToUtf8(union.ToMessage(id)),
                _options.RequestTimeout,
                _stop.Token,
                onResult: answer =>
                {
                    // On the receive loop, before the snapshot that follows the result is read.
                    if (answer.Ok)
                    {
                        OnSubscribeAccepted(slot, session, union);
                    }
                }).ConfigureAwait(false);
            if (!result.Ok)
            {
                GatewayLog.SubscribeFailed(_logger, slot.AgentId, result.Error?.Code ?? "no error code");
                return;
            }

            GatewayLog.Subscribed(_logger, slot.AgentId, union.Count, union.All ? " (all)" : string.Empty);
        }
        catch (Exception exception) when (exception is AgentOfflineException or GatewayBusyException or TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            GatewayLog.SubscribeFailed(_logger, slot.AgentId, exception.GetType().Name);
        }
    }

    private static void OnSubscribeAccepted(AgentSlot slot, AgentSession session, MetricUnion union)
    {
        lock (slot.Gate)
        {
            if (!ReferenceEquals(slot.Session, session))
            {
                return;
            }

            slot.SentKey = union.Key;
            slot.AwaitingSnapshot = !union.IsEmpty;
            if (union.IsEmpty)
            {
                slot.State = null;
            }
        }
    }

    // ------------------------------------------------------------------
    // Observers
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public IDisposable Observe(IAgentGatewayObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_observerGate)
        {
            _observers = [.. _observers, observer];
        }

        return new ObserverRegistration(this, observer);
    }

    private void RemoveObserver(IAgentGatewayObserver observer)
    {
        lock (_observerGate)
        {
            _observers = [.. _observers.Where(existing => !ReferenceEquals(existing, observer))];
        }
    }

    private void Notify(Action<IAgentGatewayObserver> call)
    {
        foreach (IAgentGatewayObserver observer in Volatile.Read(ref _observers))
        {
            try
            {
                call(observer);
            }
#pragma warning disable CA1031 // An observer belongs to another service; its failure must not end the agent connection.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                GatewayLog.ObserverFailed(_logger, exception.GetType().Name);
            }
        }
    }

    // ------------------------------------------------------------------
    // Requests to an agent
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        AgentSession? session = CurrentSession(request.AgentId);
        if (session is null)
        {
            return AgentRunOutcome.Failure(HubErrorCodes.AgentOffline, "The agent is not connected.");
        }

        TimeSpan timeout = request.Wait ? _options.RunTimeout : _options.RequestTimeout;
        try
        {
            ResultMessage result = await session.RequestAsync(
                id => LinkCodec.SerializeToUtf8(new RunMessage(
                    id,
                    request.Command,
                    request.Vars,
                    request.Wait ? null : false,
                    request.UserId is null ? null : new RunContext(request.UserId))),
                timeout,
                cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                string message = result.Error?.Message ?? "The agent refused the command.";
                return AgentRunOutcome.Failure(
                    result.Error?.Code ?? ErrorCodes.Internal,
                    message.Length > MaxErrorMessageLength ? message[..MaxErrorMessageLength] : message);
            }

            return result.Data is { } data
                ? AgentRunOutcome.Success(LinkCodec.ParseRunResult(data))
                : AgentRunOutcome.Failure(ErrorCodes.Internal, "The agent answered without data.");
        }
        catch (AgentOfflineException)
        {
            return AgentRunOutcome.Failure(HubErrorCodes.AgentOffline, "The agent is not connected.");
        }
        catch (TimeoutException)
        {
            return AgentRunOutcome.Failure(ErrorCodes.Timeout, "The agent did not answer in time.");
        }
        catch (GatewayBusyException)
        {
            return AgentRunOutcome.Failure(ErrorCodes.Busy, "Too many requests to this agent are in progress.");
        }
        catch (JsonException)
        {
            return AgentRunOutcome.Failure(ErrorCodes.Internal, "The agent answered with a result that is not valid.");
        }
        catch (InvalidOperationException)
        {
            return AgentRunOutcome.Failure(ErrorCodes.BadRequest, "The request is too large to send to the agent.");
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendCredentialRotateAsync(string agentId, string credential, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentException.ThrowIfNullOrEmpty(credential);

        AgentSession? session = CurrentSession(agentId);
        if (session is null)
        {
            return false;
        }

        try
        {
            ResultMessage result = await session.RequestAsync(
                id => JsonSerializer.SerializeToUtf8Bytes(new CredentialRotateMessage(id, credential), HubJsonContext.Default.CredentialRotateMessage),
                _options.RequestTimeout,
                cancellationToken).ConfigureAwait(false);
            if (result.Ok)
            {
                GatewayLog.CredentialRotated(_logger, agentId);
            }

            return result.Ok;
        }
        catch (Exception exception) when (exception is AgentOfflineException or GatewayBusyException or TimeoutException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task SendUnenrollAsync(string agentId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentNullException.ThrowIfNull(reason);

        _ = _slots.TryRemove(agentId, out AgentSlot? slot);
        AgentSession? session = null;
        if (slot is not null)
        {
            lock (slot.Gate)
            {
                session = slot.Session;
            }
        }

        if (session is not null)
        {
            _ = session.Send(JsonSerializer.SerializeToUtf8Bytes(new UnenrollMessage(reason), HubJsonContext.Default.UnenrollMessage));
            session.Close(CloseCodes.Normal, "The agent was removed from the hub.");
            try
            {
                await session.Ended.WaitAsync(_options.CloseTimeout + _options.SendTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                session.Abort();
            }
        }

        GatewayLog.AgentUnenrolled(_logger, agentId, reason);
        Notify(observer => observer.OnAgentRemoved(agentId));
    }

    /// <inheritdoc />
    public async Task<AgentRequestOutcome> RequestAsync(string agentId, long connectionId, Func<long, byte[]> build, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentNullException.ThrowIfNull(build);

        AgentSession? session = CurrentSession(agentId, connectionId);
        if (session is null)
        {
            return AgentRequestOutcome.Failure(HubErrorCodes.AgentOffline, "The agent is not connected.");
        }

        try
        {
            ResultMessage result = await session.RequestAsync(build, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                string message = result.Error?.Message ?? "The agent refused the request.";
                return AgentRequestOutcome.Failure(
                    result.Error?.Code ?? ErrorCodes.Internal,
                    message.Length > MaxErrorMessageLength ? message[..MaxErrorMessageLength] : message);
            }

            return AgentRequestOutcome.Success(result.Data);
        }
        catch (AgentOfflineException)
        {
            return AgentRequestOutcome.Failure(HubErrorCodes.AgentOffline, "The agent is not connected.");
        }
        catch (TimeoutException)
        {
            return AgentRequestOutcome.Failure(ErrorCodes.Timeout, "The agent did not answer in time.");
        }
        catch (GatewayBusyException)
        {
            return AgentRequestOutcome.Failure(ErrorCodes.Busy, "Too many requests to this agent are in progress.");
        }
        catch (InvalidOperationException)
        {
            return AgentRequestOutcome.Failure(ErrorCodes.BadRequest, "The request is too large to send to the agent.");
        }
    }

    // ------------------------------------------------------------------
    // Management tunnel streams (T912)
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<AgentStreamOutcome> OpenStreamAsync(
        string agentId,
        long connectionId,
        string path,
        IReadOnlyDictionary<string, string>? query,
        TunnelPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(principal);

        AgentSession? session = CurrentSession(agentId, connectionId);
        if (session is null)
        {
            return AgentStreamOutcome.Failure(HubErrorCodes.AgentOffline, "The agent is not connected.");
        }

        if (session.StreamCount >= _options.MaxStreamsPerAgent)
        {
            return AgentStreamOutcome.Failure(ErrorCodes.Busy, "Too many event streams are open to this agent.");
        }

        AgentStreamRelay? opened = null;
        try
        {
            ResultMessage result = await session.RequestAsync(
                id => JsonSerializer.SerializeToUtf8Bytes(new MgmtStreamOpenMessage(id, path, principal, query), HubJsonContext.Default.MgmtStreamOpenMessage),
                _options.RequestTimeout,
                cancellationToken,
                onResult: answer =>
                {
                    // Runs on the receive loop before the next frame is read, so the first event cannot arrive before the stream is registered.
                    if (answer.Ok
                        && answer.Data is { } data
                        && data.Deserialize(HubJsonContext.Default.MgmtStreamOpenResultData) is { Sid.Length: > 0 } sid)
                    {
                        var created = new AgentStreamRelay(agentId, sid.Sid, _options.StreamQueueCapacity, stream => ReleaseStream(session, stream));
                        if (session.TryAddStream(created))
                        {
                            opened = created;
                        }
                    }
                }).ConfigureAwait(false);
            if (!result.Ok)
            {
                string message = result.Error?.Message ?? "The agent refused the request.";
                return AgentStreamOutcome.Failure(
                    result.Error?.Code ?? ErrorCodes.Internal,
                    message.Length > MaxErrorMessageLength ? message[..MaxErrorMessageLength] : message);
            }

            return opened is { } stream
                ? AgentStreamOutcome.Success(stream)
                : AgentStreamOutcome.Failure(ErrorCodes.Internal, "The agent answered with a stream id that is not valid.");
        }
        catch (OperationCanceledException)
        {
            // The caller gave up; the agent may have opened the stream anyway: close it.
            if (opened is not null)
            {
                await opened.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception exception) when (exception is AgentOfflineException or TimeoutException or GatewayBusyException or InvalidOperationException or JsonException)
        {
            if (opened is not null)
            {
                await opened.DisposeAsync().ConfigureAwait(false);
            }

            return exception switch
            {
                AgentOfflineException => AgentStreamOutcome.Failure(HubErrorCodes.AgentOffline, "The agent is not connected."),
                TimeoutException => AgentStreamOutcome.Failure(ErrorCodes.Timeout, "The agent did not answer in time."),
                GatewayBusyException => AgentStreamOutcome.Failure(ErrorCodes.Busy, "Too many requests to this agent are in progress."),
                JsonException => AgentStreamOutcome.Failure(ErrorCodes.Internal, "The agent answered with a result that is not valid."),
                _ => AgentStreamOutcome.Failure(ErrorCodes.BadRequest, "The request is too large to send to the agent."),
            };
        }
    }

    /// <summary>Called when a consumer lets go of a stream: forgets it and, unless the agent knows that it ended, tells the agent to close it.</summary>
    private static void ReleaseStream(AgentSession session, AgentStreamRelay stream)
    {
        session.RemoveStream(stream);
        if (!stream.AgentKnowsItEnded)
        {
            _ = session.Send(JsonSerializer.SerializeToUtf8Bytes(new MgmtStreamCloseMessage(stream.Sid), HubJsonContext.Default.MgmtStreamCloseMessage));
        }
    }

    internal bool IsNegotiated(AgentSession session, string feature)
    {
        if (!_slots.TryGetValue(session.AgentId, out AgentSlot? slot))
        {
            return false;
        }

        lock (slot.Gate)
        {
            return ReferenceEquals(slot.Session, session) && slot.Features.Contains(feature, StringComparer.Ordinal);
        }
    }

    // ------------------------------------------------------------------
    // Shutdown
    // ------------------------------------------------------------------

    /// <summary>Starts to stop: new links are refused and every connection is closed with the code 1001 after its queued frames.</summary>
    public void BeginStop()
    {
        _stopping = true;
        foreach (AgentSession session in _sessions.Keys)
        {
            session.Close(CloseCodes.GoingAway, "The hub is stopping.");
        }
    }

    /// <summary>Waits until the connections have ended, at most the close timeout; the rest is dropped.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the connections are gone.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        BeginStop();
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Dispose raced with StopAsync while the host was tearing down.
        }
        AgentSession[] open = [.. _sessions.Keys];
        try
        {
            await Task.WhenAll(open.Select(session => session.Ended)).WaitAsync(_options.CloseTimeout + _options.SendTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            foreach (AgentSession session in open)
            {
                session.Abort();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Dispose();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private AgentSlot GetSlot(string agentId) => _slots.GetOrAdd(agentId, static id => new AgentSlot(id));

    private AgentSession? CurrentSession(string agentId, long connectionId = 0)
    {
        if (!_slots.TryGetValue(agentId, out AgentSlot? slot))
        {
            return null;
        }

        lock (slot.Gate)
        {
            if (connectionId != 0 && slot.ConnectionId != connectionId)
            {
                return null;
            }

            return slot.Session is { IsOpen: true } session ? session : null;
        }
    }

    private bool IsCurrent(AgentSession session)
    {
        if (!_slots.TryGetValue(session.AgentId, out AgentSlot? slot))
        {
            return false;
        }

        lock (slot.Gate)
        {
            return ReferenceEquals(slot.Session, session);
        }
    }

    private sealed class MetricSubscription(AgentGateway gateway, AgentSlot slot, long id) : IMetricSubscription
    {
        private int _disposed;

        public string AgentId => slot.AgentId;

        public void Update(MetricDemand demand)
        {
            ArgumentNullException.ThrowIfNull(demand);

            if (Volatile.Read(ref _disposed) == 0)
            {
                gateway.UpdateDemand(slot, id, demand);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gateway.RemoveDemand(slot, id);
            }
        }
    }

    private sealed class ObserverRegistration(AgentGateway gateway, IAgentGatewayObserver observer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gateway.RemoveObserver(observer);
            }
        }
    }
}

/// <summary>Ties the gateway to the host: when the host starts to stop, agents are told to go away with the close code 1001 and the connections are given time to end.</summary>
public sealed class GatewayLifecycleService : IHostedService
{
    private readonly AgentGateway _gateway;
    private readonly IHostApplicationLifetime _lifetime;
    private CancellationTokenRegistration _stopping;

    /// <summary>Creates the service.</summary>
    /// <param name="gateway">The gateway.</param>
    /// <param name="lifetime">The application lifetime.</param>
    public GatewayLifecycleService(AgentGateway gateway, IHostApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(lifetime);

        _gateway = gateway;
        _lifetime = lifetime;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Kestrel waits for open connections once the host stops; closing the agent links when the stop begins lets it finish at once.
        _stopping = _lifetime.ApplicationStopping.Register(static state => ((AgentGateway)state!).BeginStop(), _gateway);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.DisposeAsync().ConfigureAwait(false);
        await _gateway.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
