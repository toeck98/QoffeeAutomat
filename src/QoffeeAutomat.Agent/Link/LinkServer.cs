using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Manifest;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Core.Platform;
using LinkManifest = QoffeeAutomat.Contracts.Link.Manifest;

namespace QoffeeAutomat.Agent.Link;

/// <summary>
/// The QoffeeLink server (PLAN.md S07, T210): keeps the open connections, builds <c>hello</c> and tells the
/// clients when the agent goes away. The WebSocket endpoint (<see cref="LinkEndpoint"/>) authenticates the
/// request and hands the accepted socket to <see cref="ServeAsync"/>. Several clients are served at the same
/// time; each has its own subscriptions, queue and writer, so a slow client affects nobody else.
/// </summary>
public sealed class LinkServer
{
    private readonly object _gate = new();
    private readonly HashSet<LinkConnection> _connections = [];
    private bool _stopping;

    /// <summary>Creates the server.</summary>
    /// <param name="pairing">Authenticates link tokens and registers connections, so that revoking a token closes them.</param>
    /// <param name="manifest">The manifest and its change notification.</param>
    /// <param name="metrics">The metric engine; every connection subscribes to it on demand.</param>
    /// <param name="commands">The command engine that runs <c>run</c> requests.</param>
    /// <param name="events">The event bus whose events are forwarded.</param>
    /// <param name="platform">The platform facts, for the boot id.</param>
    /// <param name="time">The clock for every timeout.</param>
    /// <param name="options">Limits and timings.</param>
    /// <param name="logger">The logger.</param>
    public LinkServer(
        PairingService pairing,
        IManifestSource manifest,
        MetricEngine metrics,
        CommandEngine commands,
        IEventBus events,
        IPlatformInfo platform,
        TimeProvider time,
        LinkOptions options,
        ILogger<LinkServer> logger)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();

        Pairing = pairing;
        Manifest = manifest;
        Metrics = metrics;
        Commands = commands;
        Events = events;
        Platform = platform;
        Time = time;
        Options = options;
        Logger = logger;
    }

    /// <summary>The number of open connections.</summary>
    public int ConnectionCount
    {
        get
        {
            lock (_gate)
            {
                return _connections.Count;
            }
        }
    }

    /// <summary>True after <see cref="StopAsync"/> was called; the endpoint then refuses new connections.</summary>
    public bool IsStopping
    {
        get
        {
            lock (_gate)
            {
                return _stopping;
            }
        }
    }

    /// <summary>The limits and timings in effect.</summary>
    public LinkOptions Options { get; }

    internal PairingService Pairing { get; }

    internal IManifestSource Manifest { get; }

    internal MetricEngine Metrics { get; }

    internal CommandEngine Commands { get; }

    internal IEventBus Events { get; }

    internal IPlatformInfo Platform { get; }

    internal TimeProvider Time { get; }

    internal ILogger Logger { get; }

    /// <summary>
    /// Serves an accepted socket until the connection ends. The caller has authenticated the bearer token
    /// and negotiated the sub-protocol; the first frame that this method sends is <c>hello</c>.
    /// </summary>
    /// <param name="socket">The accepted WebSocket; it is disposed when the connection ends.</param>
    /// <param name="client">The paired client that owns the token.</param>
    /// <param name="cancellationToken">Fires when the transport is gone.</param>
    /// <returns>A task that completes when the connection has ended.</returns>
    public async Task ServeAsync(WebSocket socket, LinkClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(client);

        _ = await ServeCoreAsync(socket, client, isPaired: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Serves a socket that the agent opened itself (the outbound mode of ADR 0003, T710; later the hub link, T905). The
    /// peer authenticated the agent's credential during the upgrade, so there is no link token to check: the connection is
    /// not registered with the pairing service (nothing there can revoke it) and ends when the peer closes it or the agent
    /// stops. Everything after that is the same as for an accepted socket: the agent sends <c>hello</c> first, the peer
    /// answers with <c>client_hello</c> and then uses the standard messages.
    /// </summary>
    /// <param name="socket">The connected WebSocket; it is disposed when the connection ends.</param>
    /// <param name="peer">Describes the peer for the audit log and for the source of runs; its kind is <c>homeassistant</c> or <c>hub</c>.</param>
    /// <param name="cancellationToken">Cancels the connection.</param>
    /// <returns>The WebSocket close code that the peer sent, or null when it did not send one (it went away, or the agent closed first).</returns>
    public Task<int?> ServeOutboundAsync(WebSocket socket, LinkClient peer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(peer);

        return ServeCoreAsync(socket, peer, isPaired: false, cancellationToken);
    }

    private async Task<int?> ServeCoreAsync(WebSocket socket, LinkClient client, bool isPaired, CancellationToken cancellationToken)
    {
        var connection = new LinkConnection(this, socket, client, isPaired);
        bool refused;
        lock (_gate)
        {
            refused = _stopping;
            if (!refused)
            {
                _connections.Add(connection);
            }
        }

        try
        {
            if (refused)
            {
                // The agent is going away: say so instead of opening a connection that is closed a moment later.
                connection.Abort();
                socket.Dispose();
                return null;
            }

            await connection.RunAsync(cancellationToken).ConfigureAwait(false);
            return connection.PeerCloseStatus;
        }
        finally
        {
            lock (_gate)
            {
                _connections.Remove(connection);
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tells every client that the agent goes away and closes the connections with code 1001. Returns when
    /// the frames are written or <see cref="LinkOptions.ByeFlushTimeout"/> has passed, so the computer can go to
    /// sleep after it.
    /// </summary>
    /// <param name="reason">One of <see cref="ByeReasons"/>.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The number of clients that were told.</returns>
    public async Task<int> SayByeAsync(string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);

        LinkConnection[] connections = Snapshot();
        foreach (LinkConnection connection in connections)
        {
            connection.SayBye(reason);
        }

        LinkLog.ByeSent(Logger, connections.Length, reason);
        if (connections.Length > 0 && Options.ByeFlushTimeout > TimeSpan.Zero)
        {
            try
            {
                await Task.WhenAll(connections.Select(connection => connection.Flushed))
                    .WaitAsync(Options.ByeFlushTimeout, Time, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A client that does not take the frame in time is dropped by its own send timeout.
            }
        }

        return connections.Length;
    }

    /// <summary>
    /// Stops the server: no new connection is accepted, every client is told that the service stops and the
    /// connections are given <see cref="LinkOptions.ShutdownTimeout"/> to end; the rest is dropped.
    /// </summary>
    /// <param name="reason">One of <see cref="ByeReasons"/>, normally <see cref="ByeReasons.ServiceStop"/>.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when no connection is left.</returns>
    public async Task StopAsync(string reason, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _stopping = true;
        }

        await SayByeAsync(reason, cancellationToken).ConfigureAwait(false);

        LinkConnection[] remaining = Snapshot();
        if (remaining.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(remaining.Select(connection => connection.Ended))
                .WaitAsync(Options.ShutdownTimeout, Time, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            foreach (LinkConnection connection in remaining)
            {
                connection.Abort();
            }
        }
    }

    internal HelloMessage CreateHello()
    {
        LinkManifest manifest = Manifest.Current;
        return new HelloMessage(
            new ProtocolRange(ProtocolInfo.Version, ProtocolInfo.Version),
            manifest.Agent,
            manifest.Rev,
            Platform.BootId,
            manifest.Features ?? []);
    }

    private LinkConnection[] Snapshot()
    {
        lock (_gate)
        {
            return [.. _connections];
        }
    }
}
