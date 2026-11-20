using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Link;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Core.Config;

namespace QoffeeAutomat.Agent.Hub.ConfigSync;

/// <summary>
/// The policy that the hub set for this agent (PLAN.md S20, D-029), as the rest of the agent reads it: whether the local configuration is locked and whether
/// direct links are denied. Both are read live from the hub layer, so they hold from the moment a generation is applied and also after a restart, while the
/// hub is unreachable.
/// </summary>
public interface IHubPolicy
{
    /// <summary>True while the hub locked the local configuration: the local layer is inactive and the API refuses local writes to commands, sensors, scripts, modes and metric overrides.</summary>
    bool LocalConfigLocked { get; }

    /// <summary>True while the hub denies direct links: no zeroconf advertisement, no pairing, no direct link clients.</summary>
    bool DirectLinkDenied { get; }

    /// <summary>Raised when <see cref="LocalConfigLocked"/> or <see cref="DirectLinkDenied"/> changed. Handlers must not throw.</summary>
    event EventHandler? Changed;
}

/// <summary>The policy of the hub layer of the config store.</summary>
public sealed class HubPolicy : IHubPolicy, IDisposable
{
    private readonly IConfigStore _config;
    private readonly object _gate = new();
    private bool _locked;
    private bool _denied;

    /// <summary>Creates the policy view.</summary>
    /// <param name="config">The configuration store whose hub layer holds the policy.</param>
    public HubPolicy(IConfigStore config)
    {
        ArgumentNullException.ThrowIfNull(config);

        _config = config;
        _config.Hub.StateChanged += OnStateChanged;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public bool LocalConfigLocked => _config.Hub.State.LocalConfigLocked;

    /// <inheritdoc />
    public bool DirectLinkDenied => _config.Hub.State.DirectLinkDenied;

    /// <inheritdoc />
    public void Dispose()
    {
        _config.Hub.StateChanged -= OnStateChanged;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        bool locked = LocalConfigLocked;
        bool denied = DirectLinkDenied;
        lock (_gate)
        {
            if (locked == _locked && denied == _denied)
            {
                return;
            }

            _locked = locked;
            _denied = denied;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Carries out <c>direct_link: deny</c> (PLAN.md S20): when the hub denies direct links the agent closes the clients that dialled in, and cancels a pairing
/// code that is open. Not advertising and refusing new pairings and connections is done where those happen (the zeroconf service and the endpoints),
/// which read <see cref="IHubPolicy.DirectLinkDenied"/> at that moment.
/// </summary>
public sealed class HubPolicyEnforcer : IHostedService
{
    /// <summary>The reason in the close frame of a direct client that the policy closes.</summary>
    public const string CloseReason = "direct_link_denied";

    private readonly IHubPolicy _policy;
    private readonly LinkServer _server;
    private readonly PairingService _pairing;
    private readonly ILogger<HubPolicyEnforcer> _logger;

    /// <summary>Creates the enforcer.</summary>
    /// <param name="policy">The hub policy.</param>
    /// <param name="server">The QoffeeLink server that holds the direct clients.</param>
    /// <param name="pairing">The pairing service, whose open code is cancelled.</param>
    /// <param name="logger">The logger.</param>
    public HubPolicyEnforcer(IHubPolicy policy, LinkServer server, PairingService pairing, ILogger<HubPolicyEnforcer> logger)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(logger);

        _policy = policy;
        _server = server;
        _pairing = pairing;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _policy.Changed += OnChanged;
        if (_policy.DirectLinkDenied)
        {
            // The hub layer was loaded from disk: the denial holds from the start.
            Enforce();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _policy.Changed -= OnChanged;
        return Task.CompletedTask;
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_policy.DirectLinkDenied)
        {
            Enforce();
        }
        else
        {
            HubLog.DirectLinkPolicy(_logger, "allowed");
        }
    }

    private void Enforce()
    {
        HubLog.DirectLinkPolicy(_logger, "denied");
        _pairing.CancelPairing();
        int closed = _server.CloseDirectClients(CloseReason);
        if (closed > 0)
        {
            HubLog.DirectClientsClosed(_logger, closed);
        }
    }
}
