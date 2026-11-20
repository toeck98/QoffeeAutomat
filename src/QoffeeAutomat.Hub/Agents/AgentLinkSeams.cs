namespace QoffeeAutomat.Hub.Agents;

/// <summary>
/// What the registry needs from the agent gateway (T906) to reach a connected agent. Until the gateway is registered, the default
/// <see cref="OfflineAgentLinkNotifier"/> reports every agent as not connected. A later task replaces the registration.
/// </summary>
public interface IAgentLinkNotifier
{
    /// <summary>Sends <c>credential_rotate</c> to a connected agent and waits for its answer.</summary>
    /// <param name="agentId">The agent.</param>
    /// <param name="credential">The new credential.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>True when the agent was connected and answered with an ok result, that is when it stored the new credential.</returns>
    Task<bool> SendCredentialRotateAsync(string agentId, string credential, CancellationToken cancellationToken);

    /// <summary>Sends <c>unenroll</c> to a connected agent, if it is connected, and closes its link. Best effort.</summary>
    /// <param name="agentId">The agent.</param>
    /// <param name="reason">The reason, for example <c>removed</c>.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that ends when the frame is sent or the agent is known to be offline.</returns>
    Task SendUnenrollAsync(string agentId, string reason, CancellationToken cancellationToken);
}

/// <summary>The notifier of a hub without a gateway: no agent is ever connected.</summary>
public sealed class OfflineAgentLinkNotifier : IAgentLinkNotifier
{
    /// <inheritdoc />
    public Task<bool> SendCredentialRotateAsync(string agentId, string credential, CancellationToken cancellationToken) => Task.FromResult(false);

    /// <inheritdoc />
    public Task SendUnenrollAsync(string agentId, string reason, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Takes over the request of <c>GET /hub/v1/agent</c> after the credential was checked (T904): the gateway of T906 accepts the WebSocket
/// there. The default <see cref="UnavailableAgentLinkHandler"/> answers 503.
/// </summary>
public interface IAgentLinkHandler
{
    /// <summary>Serves the link of an authenticated agent.</summary>
    /// <param name="context">The request.</param>
    /// <param name="agent">The agent whose credential was accepted.</param>
    /// <returns>A task that ends when the link ends.</returns>
    Task HandleAsync(HttpContext context, AgentRecord agent);
}

/// <summary>The handler of a hub without a gateway.</summary>
public sealed class UnavailableAgentLinkHandler : IAgentLinkHandler
{
    /// <inheritdoc />
    public Task HandleAsync(HttpContext context, AgentRecord agent)
    {
        ArgumentNullException.ThrowIfNull(context);

        return HubLinkErrors.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "unavailable", "The agent gateway is not available.");
    }
}
