using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Security;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Fleet;

/// <summary>
/// The fleet link of the hub (PLAN.md S20 "Fleet link", T913): the one connection over which Home Assistant reaches all agents. The service keeps the directory of the
/// enrolled agents with their tags, observes the <see cref="IAgentGateway"/> (presence, manifest, state and events of every agent), and passes each change to the
/// <see cref="FleetSession"/> of every connected client, which decides from its token's scope whether the client may see it. It is also where the pieces that the sessions
/// share live: the audit entries of runs, the closing of the connections of a revoked token and the shutdown. The hub's side of an agent never changes for the fleet:
/// manifests come from the gateway's cache, subscriptions are consumers at the gateway, and a <c>run</c> goes through <see cref="IAgentGateway.RunAsync"/>.
/// </summary>
public sealed class FleetService : IAgentGatewayObserver, IDisposable
{
    private const string SubProtocolHeader = "Sec-WebSocket-Protocol";

    private readonly AgentRegistry _registry;
    private readonly IAgentGateway _gateway;
    private readonly FleetOptions _options;
    private readonly HubIdentity _identity;
    private readonly AuditLog _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<FleetService> _logger;
    private readonly ConcurrentDictionary<string, AgentRecord> _directory = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<FleetSession, byte> _sessions = new();
    private readonly ConcurrentDictionary<string, byte> _refreshing = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly IDisposable _observation;
    private volatile bool _stopping;

    /// <summary>Creates the service and starts to observe the gateway.</summary>
    /// <param name="registry">The agent registry: which agents are enrolled and what their tags are.</param>
    /// <param name="gateway">The agent gateway.</param>
    /// <param name="options">Limits and timings.</param>
    /// <param name="identity">The identity of the hub, for the fleet <c>hello</c>.</param>
    /// <param name="audit">The audit log, for runs.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public FleetService(
        AgentRegistry registry,
        IAgentGateway gateway,
        FleetOptions options,
        HubIdentity identity,
        AuditLog audit,
        TimeProvider time,
        ILogger<FleetService> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();

        _registry = registry;
        _gateway = gateway;
        _options = options;
        _identity = identity;
        _audit = audit;
        _time = time;
        _logger = logger;
        _observation = gateway.Observe(this);
    }

    /// <summary>True once the hub is stopping; the endpoint then refuses new links.</summary>
    public bool IsStopping => _stopping;

    /// <summary>The limits and timings in effect.</summary>
    public FleetOptions Options => _options;

    internal HubIdentity Identity => _identity;

    internal IAgentGateway Gateway => _gateway;

    /// <summary>The enrolled agents that the service knows, in no particular order.</summary>
    internal IReadOnlyCollection<AgentRecord> Agents => [.. _directory.Values];

    /// <summary>The number of fleet connections that are open now.</summary>
    public int ConnectionCount => _sessions.Count;

    // ------------------------------------------------------------------
    // Serving clients
    // ------------------------------------------------------------------

    /// <summary>
    /// Takes over <c>GET /hub/v1/fleet</c> after the token was checked: upgrades the request to a WebSocket with the sub-protocol <c>qoffeelink.v1</c> and serves it. A request that is
    /// not a WebSocket upgrade is answered with HTTP 400, one that comes while the hub stops or has too many fleet connections with 503.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="token">The token whose secret was accepted.</param>
    /// <returns>A task that ends when the link ends.</returns>
    public async Task HandleAsync(HttpContext context, FleetTokenInfo token)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(token);

        if (_stopping)
        {
            await HubLinkErrors.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "stopping", "The hub is stopping.").ConfigureAwait(false);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            await HubLinkErrors.WriteAsync(context, StatusCodes.Status400BadRequest, "bad_request", "This endpoint needs a WebSocket upgrade request.").ConfigureAwait(false);
            return;
        }

        if (!OffersSubProtocol(context.Request))
        {
            await HubLinkErrors.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "bad_request",
                "The WebSocket sub-protocol " + ProtocolInfo.SubProtocol + " is required.").ConfigureAwait(false);
            return;
        }

        if (_sessions.Count >= _options.MaxConnections)
        {
            await HubLinkErrors.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, ErrorCodes.Busy, "The hub serves as many fleet connections as it allows.").ConfigureAwait(false);
            return;
        }

        try
        {
            // The directory is as fresh as the database when a client arrives; changes after that reach it through the gateway and the management API.
            await ReloadAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            FleetLog.DirectoryFailed(_logger, "*", exception.GetType().Name);
            await HubLinkErrors.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "unavailable", "The hub could not read its agents.").ConfigureAwait(false);
            return;
        }

        var accept = new WebSocketAcceptContext { SubProtocol = ProtocolInfo.SubProtocol };
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(accept).ConfigureAwait(false);
        await ServeAsync(socket, token, context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Serves an accepted WebSocket of an authenticated client until the connection ends.</summary>
    /// <param name="socket">The accepted WebSocket with the sub-protocol <c>qoffeelink.v1</c>; the caller disposes it.</param>
    /// <param name="token">The token whose secret opened the link.</param>
    /// <param name="remoteAddress">The address of the client, for the audit log; null when unknown.</param>
    /// <param name="requestAborted">Fires when the transport under the socket is gone.</param>
    /// <returns>A task that completes when the connection has ended.</returns>
    public async Task ServeAsync(WebSocket socket, FleetTokenInfo token, string? remoteAddress, CancellationToken requestAborted)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(token);

        var session = new FleetSession(this, token, remoteAddress, socket, _options, _time, _logger);
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

    // ------------------------------------------------------------------
    // The directory of agents
    // ------------------------------------------------------------------

    internal bool TryGetAgent(string agentId, [NotNullWhen(true)] out AgentRecord? agent) => _directory.TryGetValue(agentId, out agent);

    internal AgentPresence GetPresence(string agentId) => _gateway.GetPresence(agentId);

    /// <summary>Finds an enrolled agent: in the directory, or, for an agent that enrolled after the directory was read, in the registry.</summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agent, or null when there is no enrolled agent with this id.</returns>
    internal async Task<AgentRecord?> ResolveAsync(string agentId, CancellationToken cancellationToken)
    {
        if (_directory.TryGetValue(agentId, out AgentRecord? known))
        {
            return known;
        }

        AgentRecord? found = await _registry.FindAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (found is not { Status: AgentStatuses.Enrolled })
        {
            return null;
        }

        _directory[agentId] = found;
        ReconcileSessions(agentId);
        return found;
    }

    /// <summary>Reads all enrolled agents from the registry, replaces the directory with them and tells the sessions what changed.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that ends when the directory is current and the sessions are told.</returns>
    internal async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<AgentRecord> agents = await _registry.ListAsync(AgentStatuses.Enrolled, cancellationToken).ConfigureAwait(false);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (AgentRecord agent in agents)
            {
                _directory[agent.Id] = agent;
                _ = ids.Add(agent.Id);
            }

            foreach (string id in _directory.Keys)
            {
                if (!ids.Contains(id))
                {
                    _ = _directory.TryRemove(id, out _);
                }
            }
        }
        finally
        {
            _ = _reloadGate.Release();
        }

        foreach (FleetSession session in _sessions.Keys)
        {
            session.ReconcileAll();
        }
    }

    private void ReconcileSessions(string agentId)
    {
        foreach (FleetSession session in _sessions.Keys)
        {
            session.Reconcile(agentId);
        }
    }

    private void RefreshInBackground(string agentId)
    {
        if (_stopping || !_refreshing.TryAdd(agentId, 0))
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    _ = await ResolveAsync(agentId, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is SqliteException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
                {
                    FleetLog.DirectoryFailed(_logger, agentId, exception.GetType().Name);
                }
                finally
                {
                    _ = _refreshing.TryRemove(agentId, out _);
                }
            },
            CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // Changes that come from the rest of the hub
    // ------------------------------------------------------------------

    /// <summary>
    /// The tags of an agent changed (or the agent changed in another way that the registry records): what each connected client sees is brought in line at once. An agent that
    /// enters the scope of a client is announced with <c>agent_added</c>; one that leaves it is announced with <c>agent_removed</c> and its subscription is dropped.
    /// </summary>
    /// <param name="agent">The agent after the change.</param>
    public void OnAgentTagsChanged(AgentRecord agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (agent.Status == AgentStatuses.Enrolled)
        {
            _directory[agent.Id] = agent;
        }
        else
        {
            _ = _directory.TryRemove(agent.Id, out _);
        }

        ReconcileSessions(agent.Id);
    }

    /// <summary>A fleet token was revoked: every connection that it opened is closed with the code 4401.</summary>
    /// <param name="tokenId">The id of the token.</param>
    public void OnTokenRevoked(string tokenId)
    {
        ArgumentNullException.ThrowIfNull(tokenId);

        foreach (FleetSession session in _sessions.Keys)
        {
            if (string.Equals(session.Token.Id, tokenId, StringComparison.Ordinal))
            {
                session.CloseRevoked();
            }
        }

        FleetLog.TokenRevoked(_logger, tokenId);
    }

    /// <summary>The number of open fleet connections of a token.</summary>
    /// <param name="tokenId">The id of the token.</param>
    /// <returns>The number of connections.</returns>
    public int CountConnections(string tokenId)
    {
        ArgumentNullException.ThrowIfNull(tokenId);

        return _sessions.Keys.Count(session => string.Equals(session.Token.Id, tokenId, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public void OnPresenceChanged(AgentPresence presence)
    {
        ArgumentNullException.ThrowIfNull(presence);

        if (_sessions.IsEmpty)
        {
            return;
        }

        if (_directory.ContainsKey(presence.AgentId))
        {
            ReconcileSessions(presence.AgentId);
        }
        else
        {
            // An agent that enrolled since the directory was read: find out whether the clients may see it.
            RefreshInBackground(presence.AgentId);
        }
    }

    /// <inheritdoc />
    public void OnManifestChanged(string agentId, CachedManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        foreach (FleetSession session in _sessions.Keys)
        {
            session.OnManifestChanged(agentId, manifest.Rev);
        }
    }

    /// <inheritdoc />
    public void OnStateChanged(AgentStateUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        foreach (FleetSession session in _sessions.Keys)
        {
            session.OnState(update);
        }
    }

    /// <inheritdoc />
    public void OnEvent(StoredAgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);

        foreach (FleetSession session in _sessions.Keys)
        {
            session.OnEvent(agentEvent);
        }
    }

    /// <inheritdoc />
    public void OnAgentRemoved(string agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId);

        _ = _directory.TryRemove(agentId, out _);
        ReconcileSessions(agentId);
    }

    // ------------------------------------------------------------------
    // Audit
    // ------------------------------------------------------------------

    /// <summary>Writes the audit entry of a <c>run</c> of a fleet client: who asked (the Home Assistant user), which command, on which agent, and how it ended.</summary>
    /// <param name="user">The Home Assistant user id, or the label of the token when the client gave none.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="command">The command id.</param>
    /// <param name="result">One of <see cref="AuditResults"/>.</param>
    /// <param name="status">The HTTP status that corresponds to the outcome.</param>
    /// <param name="remoteAddress">The address of the client.</param>
    /// <returns>A task that ends when the entry is written; a failure to write is logged.</returns>
    internal async Task AuditRunAsync(string user, string agentId, string command, string result, int status, string? remoteAddress)
    {
        var entry = new NewAuditEntry(
            user,
            null,
            AuthMethods.FleetToken,
            "fleetRun",
            "WS",
            "agent:" + agentId + ":command:" + command,
            [agentId],
            result,
            status,
            remoteAddress);
        try
        {
            await _audit.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            FleetLog.AuditWriteFailed(_logger, "fleetRun", exception.GetType().Name);
        }
    }

    // ------------------------------------------------------------------
    // Shutdown
    // ------------------------------------------------------------------

    /// <summary>Starts to stop: new links are refused and every connection is closed with the code 1001 after its queued frames.</summary>
    public void BeginStop()
    {
        _stopping = true;
        foreach (FleetSession session in _sessions.Keys)
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

        FleetSession[] open = [.. _sessions.Keys];
        try
        {
            await Task.WhenAll(open.Select(session => session.Ended)).WaitAsync(_options.CloseTimeout + _options.SendTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            foreach (FleetSession session in open)
            {
                session.Abort();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _observation.Dispose();
        _stop.Dispose();
        _reloadGate.Dispose();
    }

    private static bool OffersSubProtocol(HttpRequest request)
    {
        foreach (string? line in request.Headers[SubProtocolHeader])
        {
            if (line is null)
            {
                continue;
            }

            foreach (string offered in line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(offered, ProtocolInfo.SubProtocol, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>Ties the fleet link to the host: when the host starts to stop, clients are told to go away with the close code 1001 and the connections are given time to end.</summary>
public sealed class FleetLifecycleService : IHostedService
{
    private readonly FleetService _fleet;
    private readonly IHostApplicationLifetime _lifetime;
    private CancellationTokenRegistration _stopping;

    /// <summary>Creates the service; resolving the fleet service here also makes it observe the gateway from the start of the hub.</summary>
    /// <param name="fleet">The fleet service.</param>
    /// <param name="lifetime">The application lifetime.</param>
    public FleetLifecycleService(FleetService fleet, IHostApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(lifetime);

        _fleet = fleet;
        _lifetime = lifetime;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Kestrel waits for open connections once the host stops; closing the fleet links when the stop begins lets it finish at once.
        _stopping = _lifetime.ApplicationStopping.Register(static state => ((FleetService)state!).BeginStop(), _fleet);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.DisposeAsync().ConfigureAwait(false);
        await _fleet.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
