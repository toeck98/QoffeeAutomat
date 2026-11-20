using System.Net.WebSockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Discovery;
using QoffeeAutomat.Agent.Hub.ConfigSync;
using QoffeeAutomat.Agent.Hub.Tunnel;
using QoffeeAutomat.Agent.Link;
using QoffeeAutomat.Agent.Link.Outbound;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Config.HubLayer;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Secrets;

namespace QoffeeAutomat.Agent.Hub;

/// <summary>Timings of the hub link that are not part of <see cref="OutboundOptions"/>. Tests shorten them through the service collection.</summary>
public sealed class HubLinkOptions
{
    /// <summary>Multiplies the delay between two enrollment attempts of a pending agent (the hub says how long to wait). 1 is the specified behaviour.</summary>
    public double PendingDelayScale { get; init; } = 1.0;
}

/// <summary>
/// Runs the hub link (PLAN.md S20, D-025, T905): while the agent is enrolled it dials <c>wss://&lt;hub&gt;/hub/v1/agent</c> with the credential, pinned to the
/// certificate of the enrollment string, and serves the open socket exactly like an accepted link connection, with the hub as a client of the kind
/// <c>hub</c> (<see cref="LinkServer.ServeOutboundAsync"/>: <c>hello</c> first, then <c>client_hello</c> and the standard messages). It reconnects with
/// the backoff of S20 (1 s to 5 min, with jitter), at once when the computer resumes or its network changes. A hub that answers 401 <c>revoked</c> makes the
/// agent delete the credential and stop; every other refusal keeps the credential and the agent tries again. While the enrollment is pending the service
/// repeats it until an administrator decides. The service is registered before the QoffeeLink lifecycle service, which therefore stops first and tells the hub
/// <c>bye</c> before this service ends the connector.
/// </summary>
public sealed class HubLinkService : IHostedService, IDisposable
{
    /// <summary>The id that the hub has in the audit log and the run history.</summary>
    public const string PeerId = "hub-link";

    private const string PeerName = "Hub";
    private const string PeerKind = "hub";

    private readonly HubLinkManager _manager;
    private readonly HubLinkExtension _extension;
    private readonly LinkServer _server;
    private readonly HubWebSocketConnector _transport;
    private readonly OutboundOptions _options;
    private readonly HubLinkOptions _hubOptions;
    private readonly IPowerEvents _power;
    private readonly INetworkChangeSource? _network;
    private readonly TimeProvider _time;
    private readonly ILogger<HubLinkService> _logger;
    private readonly IConfigStore? _config;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _pendingGate = new();

    private OutboundConnector? _connector;
    private IDisposable? _networkWatch;
    private CancellationTokenSource? _pendingStop;
    private Task _pending = Task.CompletedTask;
    private int _stopped;
    private int _disposed;

    /// <summary>Creates the service.</summary>
    /// <param name="manager">Resolves the endpoint and tells when the agent joined, left or was revoked.</param>
    /// <param name="extension">The additions of the hub to the QoffeeLink session.</param>
    /// <param name="server">The QoffeeLink server that serves the open socket.</param>
    /// <param name="socketConnector">Opens the WebSocket; the hub link wraps it to understand the refusals of the hub.</param>
    /// <param name="http">Creates the HTTP clients for the probe of a refused link.</param>
    /// <param name="options">Timings of the outbound links.</param>
    /// <param name="hubOptions">Timings of the hub link.</param>
    /// <param name="power">Power events; a resume dials at once.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="network">Tells when the network changed, so that a laptop that joined another network dials at once; null disables that.</param>
    /// <param name="config">The configuration store, for the generation and the policy of the hub layer in the status; null leaves them out.</param>
    public HubLinkService(
        HubLinkManager manager,
        HubLinkExtension extension,
        LinkServer server,
        IOutboundWebSocketConnector socketConnector,
        IHubHttpClientFactory http,
        OutboundOptions options,
        HubLinkOptions hubOptions,
        IPowerEvents power,
        TimeProvider time,
        ILogger<HubLinkService> logger,
        INetworkChangeSource? network = null,
        IConfigStore? config = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(socketConnector);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hubOptions);
        ArgumentNullException.ThrowIfNull(power);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _manager = manager;
        _extension = extension;
        _server = server;
        _transport = new HubWebSocketConnector(socketConnector, http, manager.MarkRevokedAsync);
        _options = options;
        _hubOptions = hubOptions;
        _power = power;
        _network = network;
        _time = time;
        _logger = logger;
        _config = config;
    }

    /// <summary>The connector, or null while the service is not started; tests and the status API read its state.</summary>
    public OutboundConnector? Connector => _connector;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _manager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SecretStoreException exception)
        {
            // The switch stays on; the link itself finds the same problem and stays idle.
            HubLog.StoreUnavailable(_logger, exception.GetType().Name);
        }

        var connector = new OutboundConnector(ResolveAsync, _transport, ServeAsync, _options, _time, _logger);
        _connector = connector;

        _manager.Changed += OnManagerChanged;
        _power.PowerChanged += OnPowerChanged;
        _networkWatch = _network?.Watch(OnNetworkChanged);

        connector.Start();
        RestartPendingLoop(immediate: true);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0 || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _manager.Changed -= OnManagerChanged;
        _power.PowerChanged -= OnPowerChanged;
        _networkWatch?.Dispose();
        _networkWatch = null;

        await _stop.CancelAsync().ConfigureAwait(false);
        Task pending;
        lock (_pendingGate)
        {
            _pendingStop?.Cancel();
            pending = _pending;
        }

        await pending.ConfigureAwait(false);

        if (_connector is { } connector)
        {
            await connector.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Describes the hub link as <c>/status</c> and <c>GET /api/v1/hub</c> report it.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The state, the hub, the address and why the link is in its state; with the generation of the hub configuration and what the policy locks (T908).</returns>
    public async Task<HubLinkStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        HubLinkStatus status = await GetLinkStatusAsync(cancellationToken).ConfigureAwait(false);
        if (_config is null)
        {
            return status;
        }

        HubLayerState layer = _config.Hub.State;
        return status with
        {
            ConfigGeneration = layer.Generation,
            LocalConfigLocked = layer.LocalConfigLocked,
            DirectLinkDenied = layer.DirectLinkDenied,
        };
    }

    private async Task<HubLinkStatus> GetLinkStatusAsync(CancellationToken cancellationToken)
    {
        bool tunnel = _manager.ManagementTunnelEnabled;
        HubStoredState? state;
        try
        {
            state = await _manager.GetStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SecretStoreException)
        {
            return new HubLinkStatus(HubLinkStates.NotJoined, tunnel, Detail: "The stored hub state cannot be read.");
        }

        if (state is null)
        {
            return new HubLinkStatus(HubLinkStates.NotJoined, tunnel, Detail: _manager.Detail);
        }

        if (state.Phase == HubPhases.Pending)
        {
            return new HubLinkStatus(HubLinkStates.Pending, tunnel, state.HubId, state.HubName, state.Url, _manager.Detail);
        }

        if (state.Phase == HubPhases.Revoked)
        {
            return new HubLinkStatus(HubLinkStates.Revoked, tunnel, state.HubId, state.HubName, state.Url, _manager.Detail);
        }

        OutboundConnector? connector = _connector;
        return connector?.State == OutboundLinkState.Connected
            ? new HubLinkStatus(HubLinkStates.Connected, tunnel, state.HubId, state.HubName, state.Url)
            : new HubLinkStatus(HubLinkStates.Disconnected, tunnel, state.HubId, state.HubName, state.Url, connector?.Detail);
    }

    private async ValueTask<OutboundEndpoint?> ResolveAsync(CancellationToken cancellationToken)
    {
        // The agent is stopping: the lifecycle service has said bye already and the next dial would only be refused.
        return _server.IsStopping ? null : await _manager.ResolveAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int?> ServeAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var peer = new LinkClient(PeerId, PeerName, PeerKind, _time.GetUtcNow(), null);
        int? closeCode = await _server.ServeOutboundAsync(socket, peer, cancellationToken, _extension).ConfigureAwait(false);
        if (closeCode != CloseCodes.Unauthorized)
        {
            return closeCode;
        }

        // The hub closed the link with 4401: the credential was revoked while connected, or the hub is confused. Only a revocation deletes it.
        OutboundEndpoint? endpoint = await _manager.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (endpoint is not null
            && string.Equals(await _transport.ReadRefusalCodeAsync(endpoint, cancellationToken).ConfigureAwait(false), HubErrorCodes.Revoked, StringComparison.Ordinal))
        {
            await _manager.MarkRevokedAsync(cancellationToken).ConfigureAwait(false);
            return closeCode;
        }

        return null;
    }

    private void OnManagerChanged(object? sender, EventArgs e)
    {
        _connector?.Reconfigure();
        RestartPendingLoop(immediate: false);
    }

    private void OnPowerChanged(object? sender, PowerEventArgs e)
    {
        if (e.Kind == PowerEventKind.Resume)
        {
            _connector?.Nudge();
        }
    }

    private void OnNetworkChanged()
    {
        _connector?.Nudge();
    }

    /// <summary>
    /// Starts the loop that repeats the enrollment of a pending agent, or ends the one that runs. After a join the hub has just answered, so the loop
    /// waits for the time that the hub named first; after a start of the agent it asks at once.
    /// </summary>
    private void RestartPendingLoop(bool immediate)
    {
        lock (_pendingGate)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                return;
            }

            _pendingStop?.Cancel();
            var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _pendingStop = stop;
            Task previous = _pending;
            _pending = Task.Run(() => RunPendingAsync(previous, immediate, stop.Token), CancellationToken.None);
        }
    }

    private async Task RunPendingAsync(Task previous, bool immediate, CancellationToken cancellationToken)
    {
        try
        {
            await previous.ConfigureAwait(false);
            TimeSpan wait = TimeSpan.Zero;
            if (!immediate)
            {
                HubStoredState? state = await _manager.GetStateAsync(cancellationToken).ConfigureAwait(false);
                if (state is not { Phase: HubPhases.Pending })
                {
                    return;
                }

                wait = TimeSpan.FromSeconds(Math.Max(1, state.RetryAfterSeconds));
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(Scale(wait), _time, cancellationToken).ConfigureAwait(false);
                }

                HubPollResult result = await _manager.PollPendingAsync(cancellationToken).ConfigureAwait(false);
                if (!result.KeepPolling)
                {
                    return;
                }

                wait = result.Delay;
            }
        }
        catch (OperationCanceledException)
        {
            // The agent stops, or the state changed and a new loop took over.
        }
        catch (SecretStoreException exception)
        {
            HubLog.StoreUnavailable(_logger, exception.GetType().Name);
        }
#pragma warning disable CA1031 // A failing poll must not take the host down; the next change of the state starts a new loop.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            HubLog.EnrollmentLoopFault(_logger, exception.GetType().Name);
        }
    }

    private TimeSpan Scale(TimeSpan delay)
    {
        return _hubOptions.PendingDelayScale == 1.0 ? delay : TimeSpan.FromTicks((long)(delay.Ticks * _hubOptions.PendingDelayScale));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stop.Dispose();
            lock (_pendingGate)
            {
                _pendingStop?.Dispose();
            }
        }
    }
}

/// <summary>Registers the hub link.</summary>
public static class HubLinkServices
{
    /// <summary>
    /// Adds the hub link: its options, the stores, the manager, the hosted service and the local channel of the command-line verbs. The secret store, the
    /// QoffeeLink server, the agent identity and the power events must be registered. Call it before <see cref="LinkServices.AddQoffeeLink"/> registers the
    /// lifecycle service, so that this service stops after it (the lifecycle service tells the hub <c>bye</c> first).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddHubLink(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new OutboundOptions());
        services.TryAddSingleton<IOutboundWebSocketConnector>(provider => new ClientWebSocketConnector(provider.GetRequiredService<OutboundOptions>()));
        services.TryAddSingleton(new HubLinkOptions());
        services.TryAddSingleton<IHubHttpClientFactory, HubHttpClientFactory>();
        services.TryAddSingleton<HubStateStore>();
        services.TryAddSingleton<HubEnrollmentClient>();
        services.TryAddSingleton<HubLinkManager>();

        // The configuration that the hub distributes (T908): the sync of the desired state, and the policy that follows from it. The enforcer closes direct
        // clients and cancels pairing when the hub denies direct links; the zeroconf service, the pairing endpoint and the link endpoint read the policy.
        services.TryAddSingleton<ConfigSyncService>();
        services.TryAddSingleton<IHubPolicy, HubPolicy>();
        services.AddHostedService<HubPolicyEnforcer>();

        // The management tunnel (T912): requests of the hub run through the agent's own request pipeline, in process. The start-up filter captures the pipeline.
        services.TryAddSingleton(new TunnelOptions());
        services.TryAddSingleton<TunnelPipeline>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, TunnelPipelineStartupFilter>());
        services.TryAddSingleton<TunnelDispatcher>();
        services.TryAddSingleton<ManagementTunnelService>();
        services.TryAddSingleton<HubLinkExtension>();
        services.AddSingleton<HubLinkService>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<HubLinkService>());

        // The channel of the verbs hub join, hub leave and hub status: separate from the local channel of emit and state (T905).
        services.AddSingleton<HubAdminChannel>();
        services.AddSingleton<IHostedService, HubAdminChannelService>();
        return services;
    }
}
