using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Ui.Services;
using QoffeeAutomat.Ui;
using QoffeeAutomat.Ui.Components.Api;

namespace QoffeeAutomat.Hub.Ui.Tunnel;

/// <summary>
/// <see cref="IAgentApi"/> over the tunnel of the hub (PLAN.md S20, D-033, T915): the management API of the selected agent, reached through
/// <c>/api/v1/agents/{id}/agent-api/...</c>, so the pages of the component library (dashboard, commands with run, sensors, history, logs) work for a
/// PC that has dialled out to the hub exactly as they do on the PC itself. It is the HTTP client of the agent UI with two differences: the route,
/// and the sign-in, which is the hub's (the hub signs the user in, the agent never sees a password: the tunnel closes <c>/auth</c> for every role).
/// The selection is read at every call, so one instance serves whichever agent is open. Role rules are the hub's and the agent's: a viewer's request
/// to run a command is answered with 403 by the hub, whatever the UI shows.
/// </summary>
public sealed class TunnelAgentApi : AgentApiClient
{
    private readonly AgentSelection _selection;
    private readonly IHubApi _hub;

    /// <summary>Creates the client.</summary>
    /// <param name="http">The HTTP client; its base address is the base address of the page, including a path prefix.</param>
    /// <param name="selection">The agent whose pages are open.</param>
    /// <param name="hub">The hub, which answers the questions about the session.</param>
    public TunnelAgentApi(HttpClient http, AgentSelection selection, IHubApi hub)
        : base(http)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(hub);

        _selection = selection;
        _hub = hub;
    }

    /// <summary>The path below the base address that the calls of an agent start with: <c>api/v1/agents/{id}/agent-api/</c>.</summary>
    /// <param name="agentId">The agent id.</param>
    /// <returns>The path with its trailing slash.</returns>
    public static string RouteOf(string agentId)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        return "api/v1/agents/" + Uri.EscapeDataString(agentId) + "/agent-api/";
    }

    /// <inheritdoc />
    protected override string ApiBase => RouteOf(
        _selection.AgentId ?? throw new InvalidOperationException("No agent is selected: the page of an agent must select it first."));

    /// <summary>The tunnel has no setup of its own: the hub is set up on its own page.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Always <c>null</c>, "not available".</returns>
    public override Task<SetupState?> GetSetupStateAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<SetupState?>(null);
    }

    /// <summary>The tunnel cannot set a password: the agent's <c>/auth</c> is closed to the hub.</summary>
    /// <param name="request">Ignored.</param>
    /// <param name="cancellationToken">Ignored.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override Task<AuthMeResponse> SetUpAsync(SetupRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("An agent cannot be set up through the hub.");
    }

    /// <summary>The tunnel cannot sign in to the agent: the hub's own sign-in is used.</summary>
    /// <param name="request">Ignored.</param>
    /// <param name="cancellationToken">Ignored.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override Task<AuthMeResponse> LogInAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("The hub signs the user in, not the agent.");
    }

    /// <summary>Ends the session at the hub.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the session is ended.</returns>
    public override Task LogOutAsync(CancellationToken cancellationToken = default)
    {
        return _hub.LogOutAsync(cancellationToken);
    }

    /// <summary>Asks the hub who is signed in; the answer is what the pages show as "the account".</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The account, or <c>null</c> when the hub session has ended.</returns>
    public override async Task<AuthMeResponse?> GetMeAsync(CancellationToken cancellationToken = default)
    {
        HubSessionInfo? me = await _hub.GetMeAsync(cancellationToken);
        return me is { Authenticated: true } ? new AuthMeResponse(true, me.Name) : null;
    }
}
