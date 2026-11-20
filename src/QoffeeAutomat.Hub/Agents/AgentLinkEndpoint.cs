using Microsoft.Net.Http.Headers;
using QoffeeAutomat.Contracts.Hub;

namespace QoffeeAutomat.Hub.Agents;

/// <summary>
/// <c>GET /hub/v1/agent</c> (PLAN.md S20, T904): the credential is checked before anything else happens. A request without an acceptable credential
/// is answered with HTTP 401 and no upgrade: code <c>unauthorized</c> for an unknown credential and <c>revoked</c> for one that was revoked (the agent
/// was removed or the credential replaced), which tells the agent to delete its credential and to stop connecting. An agent that is not (or no longer)
/// enrolled gets 403 <c>pending_approval</c>; a pending agent has no credential, so it cannot even present one. Only an accepted request reaches the
/// <see cref="IAgentLinkHandler"/>, which accepts the WebSocket (T906).
/// </summary>
public static class AgentLinkEndpoint
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>Maps the endpoint.</summary>
    /// <param name="routes">The route builder.</param>
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // Outside /api/v1: the agent credential authenticates the call, not a session.
        routes.MapGet(HubProtocol.AgentLinkPath, HandleAsync).WithName("openAgentLink");
    }

    /// <summary>Reads the bearer token of a request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The token, or null when the request has no <c>Authorization: Bearer</c> header.</returns>
    public static string? ReadBearer(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string header = request.Headers[HeaderNames.Authorization].ToString();
        return header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) ? header[BearerPrefix.Length..].Trim() : null;
    }

    private static async Task HandleAsync(
        HttpContext context,
        AgentRegistry registry,
        IAgentLinkHandler handler)
    {
        context.Response.Headers.CacheControl = "no-store";
        AgentCredentialResult result = await registry.AuthenticateAsync(ReadBearer(context.Request), context.RequestAborted).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case AgentCredentialOutcome.Accepted when result.Agent is { } agent:
                await handler.HandleAsync(context, agent).ConfigureAwait(false);
                return;
            case AgentCredentialOutcome.Revoked:
                await HubLinkErrors.WriteAsync(context, StatusCodes.Status401Unauthorized, HubErrorCodes.Revoked, "The credential was revoked.").ConfigureAwait(false);
                return;
            case AgentCredentialOutcome.NotApproved:
                await HubLinkErrors.WriteAsync(context, StatusCodes.Status403Forbidden, "pending_approval", "The agent is not enrolled yet.").ConfigureAwait(false);
                return;
            default:
                await HubLinkErrors.WriteAsync(context, StatusCodes.Status401Unauthorized, "unauthorized", "The credential is not valid.").ConfigureAwait(false);
                return;
        }
    }
}
