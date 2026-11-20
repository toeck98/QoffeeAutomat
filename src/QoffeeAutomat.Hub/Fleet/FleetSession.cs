using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Hosting;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Fleet;

/// <summary>
/// One fleet connection as the hub sees it (PLAN.md S07, S20 "Fleet link", T913): the accepted WebSocket of a client whose fleet token was checked. The hub speaks first (the fleet
/// <c>hello</c>); the client then sends requests (<c>get_agents</c>, and <c>get_manifest</c>, <c>subscribe</c> and <c>run</c> with the agent id in <c>a</c>) and the hub sends
/// results and, unasked, <c>agent_added</c>, <c>agent_removed</c>, <c>agent_status</c>, <c>manifest_changed</c>, <c>state</c> and <c>event</c>.
/// <para>
/// The session enforces the scope of the token on every message, in both directions. A request for an agent that does not exist is answered with <c>unknown_agent</c>, one for an
/// agent outside the scope with <c>forbidden</c>; no unsolicited frame is ever sent for an agent outside the scope. What the client has been told about an agent is kept in
/// <see cref="_announced"/>; every change of an agent (presence, tags, removal) runs through <see cref="ReconcileLocked"/>, which compares what the client should know now with
/// what it knows and sends exactly the difference. Because the comparison is against the current state, a burst of changes converges on the right frames whatever order the
/// notifications arrive in.
/// </para>
/// The session owns the bounded send queue and the rules for a client that misbehaves: a frame that is not JSON, too large, binary or too frequent, a queue that overflows. Such a
/// client is disconnected with a close code and the reason is logged. Requests run concurrently (a <c>run</c> may wait for minutes), up to
/// <see cref="FleetOptions.MaxPendingRequests"/>; the receive loop never waits for one.
/// </summary>
internal sealed class FleetSession : IAsyncDisposable
{
    private const int PolicyViolation = (int)WebSocketCloseStatus.PolicyViolation;
    private const int MaxUserIdLength = 128;

    private static readonly JsonElement NullValue = ParseNull();

    private readonly FleetService _service;
    private readonly string? _remoteAddress;
    private readonly WebSocket _socket;
    private readonly FleetOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<byte[]> _outbound;
    private readonly byte[] _receiveBuffer;
    private readonly object _gate = new();
    private readonly Dictionary<string, AnnouncedAgent> _announced = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentSubscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> _requests = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task _writer = Task.CompletedTask;
    private ITimer? _closeTimer;
    private long _requestSequence;
    private long _windowStart;
    private int _windowCount;
    private int _closeRequested;
    private int _aborted;
    private int _closeCode = CloseCodes.Normal;
    private string _closeReason = string.Empty;
    private bool _ready;
    private bool _cleanedUp;

    public FleetSession(
        FleetService service,
        FleetTokenInfo token,
        string? remoteAddress,
        WebSocket socket,
        FleetOptions options,
        TimeProvider time,
        ILogger logger)
    {
        _service = service;
        Token = token;
        _remoteAddress = remoteAddress;
        _socket = socket;
        _options = options;
        _time = time;
        _logger = logger;
        _receiveBuffer = new byte[options.ReceiveBufferBytes];
        _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.SendQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _windowStart = time.GetTimestamp();
    }

    /// <summary>The token whose secret opened the link.</summary>
    public FleetTokenInfo Token { get; }

    /// <summary>Completes when the connection has ended.</summary>
    public Task Ended => _ended.Task;

    private FleetScope Scope => Token.Scope;

    private bool IsClosing => Volatile.Read(ref _closeRequested) != 0;

    /// <summary>Runs the session until the connection ends.</summary>
    /// <param name="requestAborted">Fires when the transport under the socket is gone.</param>
    /// <returns>A task that completes when the connection has ended and the session has cleaned up.</returns>
    public async Task RunAsync(CancellationToken requestAborted)
    {
        using CancellationTokenRegistration aborted = requestAborted.Register(static state => ((FleetSession)state!).Abort(), this);
        _writer = Task.Run(WriteLoopAsync, CancellationToken.None);
        try
        {
            FleetLog.ClientConnected(_logger, Token.Id, Scope.Type);
            Start();
            await ReceiveLoopAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException)
        {
            // The client went away or the hub is stopping; cleanup does the rest.
        }
#pragma warning disable CA1031 // A failing connection must not take the hub down; it is closed with 1011 and logged.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            FleetLog.Fault(_logger, Token.Id, exception.GetType().Name);
            Close(CloseCodes.InternalError, "Internal error.");
        }
        finally
        {
            await CleanupAsync().ConfigureAwait(false);
            FleetLog.ClientDisconnected(_logger, Token.Id);
        }
    }

    /// <summary>Closes the connection with a close code after the queued frames.</summary>
    /// <param name="closeCode">The WebSocket close code.</param>
    /// <param name="reason">A short reason for the client (at most 123 bytes of UTF-8).</param>
    public void Close(int closeCode, string reason)
    {
        if (Interlocked.Exchange(ref _closeRequested, 1) != 0)
        {
            return;
        }

        _closeCode = closeCode;
        _closeReason = reason;
        _outbound.Writer.TryComplete();

        // A peer that does not answer the close frame is dropped.
        _closeTimer = _time.CreateTimer(static state => ((FleetSession)state!).Abort(), this, _options.CloseTimeout, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The token of this connection was revoked: the client is told with the close code 4401.</summary>
    public void CloseRevoked() => Close(CloseCodes.Unauthorized, "The fleet token was revoked.");

    /// <summary>Drops the connection at once, without a close handshake.</summary>
    public void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
        {
            return;
        }

        _ = Interlocked.Exchange(ref _closeRequested, 1);
        _outbound.Writer.TryComplete();
        try
        {
            _socket.Abort();
        }
        catch (ObjectDisposedException)
        {
            // The socket is already gone.
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The session has ended already.
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _closeTimer?.Dispose();
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Start
    // ------------------------------------------------------------------

    /// <summary>
    /// Sends the fleet <c>hello</c> and records which agents of the scope the client knows from now on: all that exist now. A client learns about them with <c>get_agents</c>;
    /// what changes afterwards reaches it as <c>agent_added</c>, <c>agent_removed</c> and <c>agent_status</c>.
    /// </summary>
    private void Start()
    {
        var hello = new FleetHelloMessage(
            new ProtocolRange(ProtocolInfo.Version, ProtocolInfo.Version),
            new HubInfo(_service.Identity.Id, _service.Identity.Name, HubVersion.Current),
            [HubFeatures.Fleet]);

        lock (_gate)
        {
            _ = Send(Serialize(hello, HubJsonContext.Default.FleetHelloMessage));
            foreach (AgentRecord record in _service.Agents)
            {
                if (Scope.Allows(record.Tags))
                {
                    _announced[record.Id] = AnnouncedAgent.Of(_service.GetPresence(record.Id));
                }
            }

            _ready = true;
        }
    }

    // ------------------------------------------------------------------
    // What the client knows about the agents
    // ------------------------------------------------------------------

    /// <summary>Brings the client in line with the current state of every agent that it knows or may know.</summary>
    public void ReconcileAll()
    {
        lock (_gate)
        {
            if (!_ready || _cleanedUp)
            {
                return;
            }

            var ids = new HashSet<string>(_announced.Keys, StringComparer.Ordinal);
            foreach (AgentRecord record in _service.Agents)
            {
                _ = ids.Add(record.Id);
            }

            foreach (string id in ids)
            {
                ReconcileLocked(id);
            }
        }
    }

    /// <summary>Brings the client in line with the current state of one agent: its presence, and whether it is in the scope at all.</summary>
    /// <param name="agentId">The agent id.</param>
    public void Reconcile(string agentId)
    {
        lock (_gate)
        {
            if (_ready && !_cleanedUp)
            {
                ReconcileLocked(agentId);
            }
        }
    }

    /// <summary>Compares what the client should know about an agent with what it knows and sends the difference. The caller holds <see cref="_gate"/>.</summary>
    private void ReconcileLocked(string agentId)
    {
        bool inScope = _service.TryGetAgent(agentId, out AgentRecord? record) && Scope.Allows(record.Tags);
        if (!inScope)
        {
            if (_announced.Remove(agentId))
            {
                // The subscription goes with the agent: no data of an agent outside the scope is delivered, and the gateway stops collecting for this client.
                DropSubscriptionLocked(agentId);
                _ = Send(Serialize(new AgentRemovedMessage(agentId), HubJsonContext.Default.AgentRemovedMessage));
            }

            return;
        }

        AgentPresence presence = _service.GetPresence(agentId);
        if (!_announced.TryGetValue(agentId, out AnnouncedAgent? known))
        {
            _announced[agentId] = AnnouncedAgent.Of(presence);
            _ = Send(Serialize(
                new AgentAddedMessage(agentId, record!.Name, presence.Online, presence.PowerState, presence.ManifestRev, presence.Agent),
                HubJsonContext.Default.AgentAddedMessage));
            return;
        }

        if (known.Online == presence.Online && string.Equals(known.PowerState, presence.PowerState, StringComparison.Ordinal))
        {
            return;
        }

        bool wentOffline = known.Online && !presence.Online;
        known.Online = presence.Online;
        known.PowerState = presence.PowerState;
        _ = Send(Serialize(new AgentStatusMessage(agentId, presence.Online, presence.PowerState), HubJsonContext.Default.AgentStatusMessage));
        if (wentOffline)
        {
            // The entities of an offline agent have no value: the client is told so for every metric that it follows (PLAN.md S20).
            SendUnavailableLocked(agentId);
        }
    }

    public void OnManifestChanged(string agentId, string rev)
    {
        lock (_gate)
        {
            if (!_ready || _cleanedUp || !_announced.TryGetValue(agentId, out AnnouncedAgent? known)
                || string.Equals(known.ManifestRev, rev, StringComparison.Ordinal))
            {
                return;
            }

            known.ManifestRev = rev;
            _ = Send(Serialize(new FleetManifestChangedMessage(agentId, rev), HubJsonContext.Default.FleetManifestChangedMessage));
        }
    }

    public void OnState(AgentStateUpdate update)
    {
        lock (_gate)
        {
            if (!_ready || _cleanedUp || !_announced.ContainsKey(update.AgentId) || !_subscriptions.TryGetValue(update.AgentId, out AgentSubscription? subscription))
            {
                return;
            }

            Dictionary<string, JsonElement> wanted = subscription.Select(update.Values);
            if (wanted.Count > 0)
            {
                _ = Send(Serialize(new FleetStateMessage(update.AgentId, update.Ts, wanted), HubJsonContext.Default.FleetStateMessage));
            }
        }
    }

    public void OnEvent(StoredAgentEvent agentEvent)
    {
        lock (_gate)
        {
            if (!_ready || _cleanedUp || !_announced.ContainsKey(agentEvent.AgentId))
            {
                return;
            }

            _ = Send(Serialize(
                new FleetEventMessage(agentEvent.AgentId, agentEvent.Type, agentEvent.At.ToUnixTimeMilliseconds(), agentEvent.Data),
                HubJsonContext.Default.FleetEventMessage));
        }
    }

    /// <summary>Sends a <c>state</c> frame that reports every metric that the client follows for the agent as unavailable (<c>null</c>). The caller holds <see cref="_gate"/>.</summary>
    private void SendUnavailableLocked(string agentId)
    {
        if (!_subscriptions.TryGetValue(agentId, out AgentSubscription? subscription))
        {
            return;
        }

        IEnumerable<string> ids = subscription.All ? KnownMetricIds(agentId) : subscription.Ids;
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            values[id] = NullValue;
        }

        if (values.Count > 0)
        {
            _ = Send(Serialize(
                new FleetStateMessage(agentId, _time.GetUtcNow().ToUnixTimeMilliseconds(), values),
                HubJsonContext.Default.FleetStateMessage));
        }
    }

    /// <summary>Sends the values that the hub holds for the metrics that the client follows, as the snapshot that follows a <c>subscribe</c>. The caller holds <see cref="_gate"/>.</summary>
    private void SendSnapshotLocked(string agentId, AgentSubscription subscription)
    {
        if (!_service.GetPresence(agentId).Online)
        {
            SendUnavailableLocked(agentId);
            return;
        }

        AgentStateSnapshot? state = _service.Gateway.GetState(agentId);
        if (state is null)
        {
            // Nothing is known yet; the agent's own snapshot follows the subscription the hub sent it.
            return;
        }

        Dictionary<string, JsonElement> wanted = subscription.Select(state.Values);
        if (wanted.Count > 0)
        {
            _ = Send(Serialize(new FleetStateMessage(agentId, state.Ts, wanted), HubJsonContext.Default.FleetStateMessage));
        }
    }

    /// <summary>The ids of the metrics that the hub knows for an agent: those in its cached state and those in its cached manifest.</summary>
    private HashSet<string> KnownMetricIds(string agentId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (_service.Gateway.GetState(agentId) is { } state)
        {
            ids.UnionWith(state.Values.Keys);
        }

        if (_service.Gateway.GetManifest(agentId) is { } manifest
            && manifest.Manifest.ValueKind == JsonValueKind.Object
            && manifest.Manifest.TryGetProperty("metrics", out JsonElement metrics)
            && metrics.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement metric in metrics.EnumerateArray())
            {
                if (metric.ValueKind == JsonValueKind.Object
                    && metric.TryGetProperty("id", out JsonElement id)
                    && id.ValueKind == JsonValueKind.String
                    && id.GetString() is { Length: > 0 } text)
                {
                    _ = ids.Add(text);
                }
            }
        }

        return ids;
    }

    private void DropSubscriptionLocked(string agentId)
    {
        if (_subscriptions.Remove(agentId, out AgentSubscription? subscription))
        {
            subscription.Handle.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Receiving
    // ------------------------------------------------------------------

    private async Task ReceiveLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            Frame frame = await ReadFrameAsync().ConfigureAwait(false);
            if (frame.Kind == FrameKind.Closed)
            {
                return;
            }

            if (IsClosing)
            {
                // The close handshake is under way; wait for the close frame of the client and ignore the rest.
                continue;
            }

            if (!CountFrame())
            {
                Violation("it sends more than " + _options.MaxFramesPerSecond.ToString(CultureInfo.InvariantCulture) + " frames per second", PolicyViolation, "Too many frames.");
                continue;
            }

            switch (frame.Kind)
            {
                case FrameKind.TooLarge:
                    Violation("a frame is larger than the limit", CloseCodes.MessageTooBig, "Frame too large.");
                    break;
                case FrameKind.Binary:
                    Violation("it sent a binary frame", CloseCodes.BadRequest, "Only text frames are allowed.");
                    break;
                default:
                    HandleTextFrame(frame.Data);
                    break;
            }
        }
    }

    private bool CountFrame()
    {
        long now = _time.GetTimestamp();
        if (now - _windowStart >= _time.TimestampFrequency)
        {
            _windowStart = now;
            _windowCount = 0;
        }

        _windowCount++;
        return _windowCount <= _options.MaxFramesPerSecond;
    }

    private async Task<Frame> ReadFrameAsync()
    {
        var message = new ArrayBufferWriter<byte>();
        while (true)
        {
            ValueWebSocketReceiveResult result = await _socket.ReceiveAsync(_receiveBuffer.AsMemory(), _lifetime.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Answer the close frame of the client so that the close handshake completes.
                Close(CloseCodes.Normal, string.Empty);
                return new Frame(FrameKind.Closed, ReadOnlyMemory<byte>.Empty);
            }

            if ((long)message.WrittenCount + result.Count > _options.MaxFrameBytes)
            {
                return new Frame(FrameKind.TooLarge, ReadOnlyMemory<byte>.Empty);
            }

            message.Write(_receiveBuffer.AsSpan(0, result.Count));
            if (result.EndOfMessage)
            {
                return new Frame(
                    result.MessageType == WebSocketMessageType.Binary ? FrameKind.Binary : FrameKind.Text,
                    message.WrittenMemory.ToArray());
            }
        }
    }

    private void HandleTextFrame(ReadOnlyMemory<byte> data)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            Violation("it sent a frame that is not valid JSON", CloseCodes.BadRequest, "Invalid frame.");
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("t", out JsonElement typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                Violation("it sent a frame without a message type", CloseCodes.BadRequest, "Invalid frame.");
                return;
            }

            string type = typeElement.GetString()!;
            if (type is HubMessageTypes.GetAgents or MessageTypes.GetManifest or MessageTypes.Subscribe or MessageTypes.Run)
            {
                StartRequest(type, root.Clone());
            }

            // Everything else (a client_hello, results, message types of a later protocol version) is ignored (S07).
        }
    }

    private void StartRequest(string type, JsonElement frame)
    {
        // A request without an integer id cannot be answered.
        if (!frame.TryGetProperty("id", out JsonElement idElement)
            || idElement.ValueKind != JsonValueKind.Number
            || !idElement.TryGetInt64(out long id)
            || id < 0)
        {
            return;
        }

        if (_requests.Count >= _options.MaxPendingRequests)
        {
            _ = Send(Failure(id, ErrorCodes.Busy, "Too many requests are in progress."));
            return;
        }

        long key = Interlocked.Increment(ref _requestSequence);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[key] = done.Task;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await ExecuteRequestAsync(type, id, frame).ConfigureAwait(false);
                }
                finally
                {
                    _ = _requests.TryRemove(key, out _);
                    _ = done.TrySetResult();
                }
            },
            CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // Requests
    // ------------------------------------------------------------------

    private async Task ExecuteRequestAsync(string type, long id, JsonElement frame)
    {
        try
        {
            // The handlers that must put frames in a fixed order (a result before the snapshot that follows it) send their result themselves and return null.
            ResultMessage? result = type switch
            {
                HubMessageTypes.GetAgents => await GetAgentsAsync(id).ConfigureAwait(false),
                MessageTypes.GetManifest => await GetManifestAsync(id, frame).ConfigureAwait(false),
                MessageTypes.Subscribe => await SubscribeAsync(id, frame).ConfigureAwait(false),
                _ => await RunCommandAsync(id, frame).ConfigureAwait(false),
            };
            if (result is not null)
            {
                _ = Send(result);
            }
        }
        catch (OperationCanceledException)
        {
            // The connection ended; nobody waits for the result.
        }
        catch (JsonException)
        {
            _ = Send(Failure(id, ErrorCodes.BadRequest, "The request is not valid: a member is missing or has the wrong type."));
        }
        catch (Exception exception) when (exception is SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            FleetLog.Fault(_logger, Token.Id, exception.GetType().Name);
            _ = Send(Failure(id, ErrorCodes.Internal, "The hub could not handle the request."));
        }
    }

    /// <summary>Checks that an agent exists and is in the scope of the token; the answer to a request for any other agent is a failed result.</summary>
    private async Task<(AgentRecord? Agent, ResultMessage? Failure)> AuthorizeAsync(long id, string agentId)
    {
        AgentRecord? agent = await _service.ResolveAsync(agentId, _lifetime.Token).ConfigureAwait(false);
        if (agent is null)
        {
            return (null, Failure(id, HubErrorCodes.UnknownAgent, "There is no such agent."));
        }

        return Scope.Allows(agent.Tags)
            ? (agent, null)
            : (null, Failure(id, HubErrorCodes.Forbidden, "The fleet token does not cover this agent."));
    }

    private async Task<ResultMessage?> GetAgentsAsync(long id)
    {
        // The list is read fresh: an agent that enrolled since the last change is in it, and the other clients learn of it too.
        await _service.ReloadAsync(_lifetime.Token).ConfigureAwait(false);

        lock (_gate)
        {
            // The list and what the client is told afterwards stay consistent: the agents of the list are the ones it knows from now on.
            _announced.Clear();
            var entries = new List<FleetAgentEntry>();
            foreach (AgentRecord record in _service.Agents.OrderBy(agent => agent.Id, StringComparer.Ordinal))
            {
                if (!Scope.Allows(record.Tags))
                {
                    continue;
                }

                AgentPresence presence = _service.GetPresence(record.Id);
                _announced[record.Id] = AnnouncedAgent.Of(presence);
                entries.Add(new FleetAgentEntry(record.Id, record.Name, presence.Online, presence.PowerState, presence.ManifestRev, presence.Agent));
            }

            foreach (string gone in _subscriptions.Keys.Where(agentId => !_announced.ContainsKey(agentId)).ToList())
            {
                DropSubscriptionLocked(gone);
            }

            JsonElement data = JsonSerializer.SerializeToElement(new GetAgentsResultData(entries), HubJsonContext.Default.GetAgentsResultData);
            _ = Send(ResultMessage.Success(id, data));
        }

        return null;
    }

    private async Task<ResultMessage?> GetManifestAsync(long id, JsonElement frame)
    {
        FleetGetManifestMessage? request = frame.Deserialize(HubJsonContext.Default.FleetGetManifestMessage);
        if (request is null)
        {
            return Failure(id, ErrorCodes.BadRequest, "The request is empty.");
        }

        (AgentRecord? agent, ResultMessage? failure) = await AuthorizeAsync(id, request.A).ConfigureAwait(false);
        if (agent is null)
        {
            return failure;
        }

        // Served from the cache of the gateway; the agent is not asked.
        if (_service.Gateway.GetManifest(agent.Id) is { } manifest)
        {
            return ResultMessage.Success(id, manifest.Manifest);
        }

        return _service.GetPresence(agent.Id).Online
            ? Failure(id, ErrorCodes.Busy, "The hub has not received the manifest of the agent yet.")
            : Failure(id, HubErrorCodes.AgentOffline, "The agent has not connected to the hub yet, so the hub has no manifest for it.");
    }

    private async Task<ResultMessage?> SubscribeAsync(long id, JsonElement frame)
    {
        FleetSubscribeMessage? request = frame.Deserialize(HubJsonContext.Default.FleetSubscribeMessage);
        if (request is null)
        {
            return Failure(id, ErrorCodes.BadRequest, "The request is empty.");
        }

        (AgentRecord? agent, ResultMessage? failure) = await AuthorizeAsync(id, request.A).ConfigureAwait(false);
        if (agent is null)
        {
            return failure;
        }

        MetricDemand demand;
        try
        {
            demand = BuildDemand(request);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return Failure(id, ErrorCodes.BadRequest, "The metric ids or intervals are not valid.");
        }

        if (demand.IsEmpty)
        {
            // An empty list ends the subscription of this client for this agent.
            lock (_gate)
            {
                DropSubscriptionLocked(agent.Id);
                _ = Send(ResultMessage.Success(id, EmptyObject()));
            }

            return null;
        }

        AgentSubscription? existing;
        int count;
        lock (_gate)
        {
            _ = _subscriptions.TryGetValue(agent.Id, out existing);
            count = _subscriptions.Count;
        }

        if (existing is null && count >= _options.MaxSubscriptionsPerConnection)
        {
            return Failure(id, ErrorCodes.Busy, "The connection follows as many agents as it may.");
        }

        IMetricSubscription handle;
        try
        {
            if (existing is not null)
            {
                existing.Handle.Update(demand);
                handle = existing.Handle;
            }
            else
            {
                handle = _service.Gateway.SubscribeMetrics(agent.Id, demand);
            }
        }
        catch (InvalidOperationException exception)
        {
            return Failure(id, ErrorCodes.Busy, exception.Message);
        }

        lock (_gate)
        {
            // The tags may have changed while the demand was being registered: the scope is checked once more, where nothing can change it.
            bool inScope = !_cleanedUp && _service.TryGetAgent(agent.Id, out AgentRecord? current) && Scope.Allows(current.Tags);
            if (!inScope)
            {
                // Tells the client that the agent left, if it has not been told, and drops whatever was subscribed for it.
                ReconcileLocked(agent.Id);
                DropSubscriptionLocked(agent.Id);
                handle.Dispose();
                return Failure(id, HubErrorCodes.Forbidden, "The fleet token does not cover this agent.");
            }

            ReconcileLocked(agent.Id);
            var subscription = new AgentSubscription(handle, request.Metrics.All, [.. request.Metrics.Ids]);
            _subscriptions[agent.Id] = subscription;
            _ = Send(ResultMessage.Success(id, EmptyObject()));

            // The result is followed by a full snapshot of what the client follows (S07).
            SendSnapshotLocked(agent.Id, subscription);
        }

        return null;
    }

    private async Task<ResultMessage?> RunCommandAsync(long id, JsonElement frame)
    {
        FleetRunMessage? request = frame.Deserialize(HubJsonContext.Default.FleetRunMessage);
        if (request is null)
        {
            return Failure(id, ErrorCodes.BadRequest, "The request is empty.");
        }

        string? requestedUser = request.Context?.UserId?.Trim();
        string user = string.IsNullOrEmpty(requestedUser) ? "fleet:" + Token.Id : requestedUser;
        (AgentRecord? agent, ResultMessage? failure) = await AuthorizeAsync(id, request.A).ConfigureAwait(false);
        if (agent is null)
        {
            if (failure?.Error?.Code == HubErrorCodes.Forbidden)
            {
                await _service.AuditRunAsync(Clip(user), request.A, Clip(request.Command), AuditResults.Denied, StatusCodes.Status403Forbidden, _remoteAddress).ConfigureAwait(false);
            }

            return failure;
        }

        if (string.IsNullOrWhiteSpace(request.Command)
            || request.Command.Length > _options.MaxCommandLength
            || user.Length > MaxUserIdLength
            || user.Any(char.IsControl))
        {
            return Failure(id, ErrorCodes.BadRequest, "command must be the id of a command and context.user_id must be a short text without control characters.");
        }

        FleetLog.RunRequested(_logger, Token.Id, request.Command, agent.Id);

        // Forwarded with the Home Assistant user, which the agent writes to its audit log; the hub writes its own entry with the outcome.
        AgentRunOutcome outcome = await _service.Gateway.RunAsync(
            new AgentRunRequest(agent.Id, request.Command, request.Vars, request.ShouldWait, user),
            _lifetime.Token).ConfigureAwait(false);

        if (outcome.Result is { } result)
        {
            await _service.AuditRunAsync(user, agent.Id, request.Command, AuditResults.Success, StatusCodes.Status200OK, _remoteAddress).ConfigureAwait(false);
            return ResultMessage.Success(id, LinkCodec.ToElement(result));
        }

        string code = outcome.ErrorCode ?? ErrorCodes.Internal;
        await _service.AuditRunAsync(user, agent.Id, request.Command, AuditResults.Failure, GatewayEndpoints.StatusOf(code), _remoteAddress).ConfigureAwait(false);
        return Failure(id, code, outcome.ErrorMessage ?? "The command could not be run.");
    }

    /// <summary>Turns the <c>subscribe</c> of the client into the demand that the hub holds at the gateway: the metrics, each live or with the interval that the client gave.</summary>
    private static MetricDemand BuildDemand(FleetSubscribeMessage request)
    {
        if (request.Metrics.All)
        {
            return MetricDemand.Everything;
        }

        return MetricDemand.Of(request.Metrics.Ids.Select(metric => new KeyValuePair<string, TimeSpan?>(
            metric,
            request.Intervals is not null && request.Intervals.TryGetValue(metric, out double seconds) ? TimeSpan.FromSeconds(seconds) : null)));
    }

    // ------------------------------------------------------------------
    // Sending
    // ------------------------------------------------------------------

    private static byte[] Serialize<T>(T message, JsonTypeInfo<T> typeInfo) => JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);

    private static ResultMessage Failure(long id, string code, string message) => ResultMessage.Failure(id, code, message);

    private static string Clip(string text) => text.Length <= MaxUserIdLength ? text : text[..MaxUserIdLength];

    private static JsonElement EmptyObject()
    {
        using JsonDocument document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static JsonElement ParseNull()
    {
        using JsonDocument document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }

    private SendOutcome Send(ResultMessage message) => Send(LinkCodec.SerializeToUtf8(message));

    /// <summary>Queues a frame. A client whose queue is full is disconnected.</summary>
    private SendOutcome Send(byte[] frame)
    {
        if (frame.Length > _options.MaxFrameBytes)
        {
            return SendOutcome.TooLarge;
        }

        if (IsClosing)
        {
            return SendOutcome.Closed;
        }

        if (_outbound.Writer.TryWrite(frame))
        {
            return SendOutcome.Sent;
        }

        if (IsClosing)
        {
            return SendOutcome.Closed;
        }

        Violation("its send queue is full: the client does not read its frames", PolicyViolation, "The client does not read its frames.");
        return SendOutcome.Overflow;
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (byte[] frame in _outbound.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Volatile.Read(ref _aborted) != 0)
                {
                    return;
                }

                using var timeout = new CancellationTokenSource(_options.SendTimeout);
                await _socket.SendAsync(frame.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, timeout.Token).ConfigureAwait(false);
            }

            if (Volatile.Read(ref _aborted) == 0 && _socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(_options.SendTimeout);
                await _socket.CloseOutputAsync((WebSocketCloseStatus)_closeCode, _closeReason, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The client does not take its frames in time: it is stalled.
            if (Volatile.Read(ref _aborted) == 0)
            {
                FleetLog.Violation(_logger, Token.Id, "it does not accept frames in time");
            }

            Abort();
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or IOException or InvalidOperationException)
        {
            Abort();
        }
    }

    private void Violation(string reason, int closeCode, string closeText)
    {
        if (IsClosing)
        {
            return;
        }

        FleetLog.Violation(_logger, Token.Id, reason);
        Close(closeCode, closeText);
    }

    private async Task CleanupAsync()
    {
        // Whatever ended the connection, the writer must send the close frame if the socket is still open.
        Close(CloseCodes.Normal, string.Empty);

        // Requests that are still waiting (a run that waits for the end of a command) are given up; the command on the agent is not cancelled.
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The session has been disposed already.
        }

        try
        {
            await Task.WhenAll(_requests.Values).WaitAsync(_options.CloseTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A request that does not end is left to finish on its own; its frames go nowhere.
        }

        // The client's demand for metrics goes with the connection, so the gateway stops collecting what only this client wanted.
        lock (_gate)
        {
            _cleanedUp = true;
            _ready = false;
            foreach (AgentSubscription subscription in _subscriptions.Values)
            {
                subscription.Handle.Dispose();
            }

            _subscriptions.Clear();
            _announced.Clear();
        }

        try
        {
            await _writer.WaitAsync(_options.CloseTimeout + _options.SendTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Abort();
        }

        Abort();
        _ = _ended.TrySetResult();
    }

    /// <summary>What the client was last told about an agent.</summary>
    private sealed class AnnouncedAgent
    {
        public bool Online { get; set; }

        public string PowerState { get; set; } = PowerStates.Unknown;

        public string? ManifestRev { get; set; }

        public static AnnouncedAgent Of(AgentPresence presence)
        {
            return new AnnouncedAgent { Online = presence.Online, PowerState = presence.PowerState, ManifestRev = presence.ManifestRev };
        }
    }

    /// <summary>The metrics that a client follows for one agent, and the demand that the hub holds for it at the gateway.</summary>
    private sealed class AgentSubscription(IMetricSubscription handle, bool all, HashSet<string> ids)
    {
        public IMetricSubscription Handle { get; } = handle;

        public bool All { get; } = all;

        public HashSet<string> Ids { get; } = ids;

        /// <summary>The values of the metrics that the client follows.</summary>
        public Dictionary<string, JsonElement> Select(IReadOnlyDictionary<string, JsonElement> values)
        {
            var wanted = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach ((string metric, JsonElement value) in values)
            {
                if (All || Ids.Contains(metric))
                {
                    wanted[metric] = value;
                }
            }

            return wanted;
        }
    }

    private enum FrameKind
    {
        Text,
        Binary,
        TooLarge,
        Closed,
    }

    private readonly record struct Frame(FrameKind Kind, ReadOnlyMemory<byte> Data);
}
