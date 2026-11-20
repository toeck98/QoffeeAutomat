using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Discovery;
using QoffeeAutomat.Agent.Hub.ConfigSync;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Agent.Link.Outbound;

/// <summary>
/// Runs the reverse link (ADR 0003, T710): while the settings turn it on and a token is stored, the agent dials the endpoint of the
/// Home Assistant integration and, once the WebSocket is open, serves it exactly like an accepted connection (<see cref="LinkServer.ServeOutboundAsync"/>:
/// <c>hello</c> first, then <c>client_hello</c> and the standard messages). It dials again after every end of the connection, at once when the
/// computer resumes from sleep or its network changes, and when the reverse link is joined, left or changed in the settings. A peer that
/// rejects the token ends the dialling until the link is enrolled again. The service is registered before the QoffeeLink lifecycle service,
/// which therefore stops first and tells the peer <c>bye</c> before this service ends the connector.
/// </summary>
public sealed class ReverseLinkService : IHostedService, IDisposable
{
    /// <summary>The id that the peer of the reverse link has in the audit log and the run history.</summary>
    public const string PeerId = "reverse-link";

    private const string PeerName = "Home Assistant (reverse link)";
    private const string PeerKind = "homeassistant";

    private readonly ReverseLinkManager _manager;
    private readonly LinkServer _server;
    private readonly IOutboundWebSocketConnector _transport;
    private readonly OutboundOptions _options;
    private readonly IConfigStore _config;
    private readonly IPowerEvents _power;
    private readonly INetworkChangeSource? _network;
    private readonly TimeProvider _time;
    private readonly ILogger<ReverseLinkService> _logger;
    private readonly IHubPolicy? _hubPolicy;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _settingsGate = new();

    private OutboundConnector? _connector;
    private IDisposable? _networkWatch;
    private ConfigSubscription? _settings;
    private Task _settingsPump = Task.CompletedTask;
    private ReverseLinkSettings? _lastSettings;
    private int _stopped;
    private int _disposed;

    /// <summary>Creates the service.</summary>
    /// <param name="manager">Resolves the endpoint and tells when the link was joined or left.</param>
    /// <param name="server">The QoffeeLink server that serves the open socket.</param>
    /// <param name="transport">Opens the WebSocket.</param>
    /// <param name="options">Timings.</param>
    /// <param name="config">The settings, for changes made through the API or by hand.</param>
    /// <param name="power">Power events; a resume dials at once.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="network">Tells when the network changed, so that a laptop that joined another network dials at once; null disables that.</param>
    /// <param name="hubPolicy">The policy of the hub; the reverse link is a direct link to Home Assistant, so it is not dialled while the hub denies direct links (PLAN.md S20, T908). Null means no hub policy.</param>
    public ReverseLinkService(
        ReverseLinkManager manager,
        LinkServer server,
        IOutboundWebSocketConnector transport,
        OutboundOptions options,
        IConfigStore config,
        IPowerEvents power,
        TimeProvider time,
        ILogger<ReverseLinkService> logger,
        INetworkChangeSource? network = null,
        IHubPolicy? hubPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(power);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _manager = manager;
        _server = server;
        _transport = transport;
        _options = options;
        _config = config;
        _power = power;
        _network = network;
        _time = time;
        _logger = logger;
        _hubPolicy = hubPolicy;
    }

    /// <summary>The connector, or null while the service is not started; tests and the status API read its state.</summary>
    public OutboundConnector? Connector => _connector;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var connector = new OutboundConnector(ResolveAsync, _transport, ServeAsync, _options, _time, _logger);
        _connector = connector;
        _lastSettings = ReadSettings();

        _manager.SettingsSaving += OnManagerSettingsSaving;
        _manager.Changed += OnManagerChanged;
        _power.PowerChanged += OnPowerChanged;
        if (_hubPolicy is not null)
        {
            _hubPolicy.Changed += OnHubPolicyChanged;
        }

        _networkWatch = _network?.Watch(OnNetworkChanged);
        _settings = _config.Subscribe();
        ConfigSubscription settings = _settings;
        _settingsPump = Task.Run(() => PumpSettingsAsync(settings, _stop.Token), CancellationToken.None);

        connector.Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0 || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _manager.SettingsSaving -= OnManagerSettingsSaving;
        _manager.Changed -= OnManagerChanged;
        _power.PowerChanged -= OnPowerChanged;
        if (_hubPolicy is not null)
        {
            _hubPolicy.Changed -= OnHubPolicyChanged;
        }

        _networkWatch?.Dispose();
        _networkWatch = null;

        await _stop.CancelAsync().ConfigureAwait(false);
        _settings?.Dispose();
        await _settingsPump.ConfigureAwait(false);

        if (_connector is { } connector)
        {
            await connector.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<OutboundEndpoint?> ResolveAsync(CancellationToken cancellationToken)
    {
        // The agent is stopping: the lifecycle service has said bye already and the next dial would only be refused.
        if (_server.IsStopping || _hubPolicy is { DirectLinkDenied: true })
        {
            return null;
        }

        return await _manager.ResolveAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnHubPolicyChanged(object? sender, EventArgs e)
    {
        // A denial ends the connection (the connector drops it when there is nothing to dial); the end of a denial dials again.
        _connector?.Reconfigure();
    }

    private Task<int?> ServeAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var peer = new LinkClient(PeerId, PeerName, PeerKind, _time.GetUtcNow(), null);
        return _server.ServeOutboundAsync(socket, peer, cancellationToken);
    }

    private ReverseLinkSettings? ReadSettings()
    {
        return _config.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings)?.ReverseLink;
    }

    private void OnManagerSettingsSaving(object? sender, ReverseLinkSettings? next)
    {
        lock (_settingsGate)
        {
            _lastSettings = next;
        }
    }

    private void OnManagerChanged(object? sender, EventArgs e)
    {
        lock (_settingsGate)
        {
            _lastSettings = ReadSettings();
        }

        _connector?.Reconfigure();
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

    private async Task PumpSettingsAsync(ConfigSubscription subscription, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ConfigChange change in subscription.Changes.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (change.Kind != ConfigKind.Settings)
                {
                    continue;
                }

                // Only a change of the reverse link itself matters; the name or a port does not need a new connection.
                bool changed;
                lock (_settingsGate)
                {
                    ReverseLinkSettings? current = ReadSettings();
                    changed = !Equals(current, _lastSettings);
                    if (changed)
                    {
                        _lastSettings = current;
                    }
                }

                if (changed)
                {
                    _connector?.Reconfigure();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The service is stopping.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stop.Dispose();
        }
    }
}

/// <summary>Registers the reverse link.</summary>
public static class OutboundLinkServices
{
    /// <summary>
    /// Adds the reverse link: its options, the WebSocket connector, the manager and the hosted service. The configuration store, the secret
    /// store, the QoffeeLink server and the power events must be registered. Call it before <see cref="LinkServices.AddQoffeeLink"/> registers
    /// the lifecycle service, so that this service stops after it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddReverseLink(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new OutboundOptions());
        services.TryAddSingleton<IOutboundWebSocketConnector>(provider => new ClientWebSocketConnector(provider.GetRequiredService<OutboundOptions>()));
        services.TryAddSingleton<ReverseLinkManager>();
        services.AddSingleton<ReverseLinkService>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ReverseLinkService>());
        return services;
    }
}
