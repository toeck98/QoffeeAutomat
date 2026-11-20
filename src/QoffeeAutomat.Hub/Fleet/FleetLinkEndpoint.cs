using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Agents;

namespace QoffeeAutomat.Hub.Fleet;

/// <summary>
/// <c>GET /hub/v1/fleet</c> (PLAN.md S20 "Fleet link", T913): the fleet token is checked before anything else happens. A request without an acceptable token is answered with HTTP 401
/// and no upgrade: code <c>unauthorized</c> for an unknown token and <c>revoked</c> for one that was revoked, which tells the client to delete the token and to pair again. Only an
/// accepted request reaches <see cref="FleetService.HandleAsync"/>, which accepts the WebSocket.
/// </summary>
public static class FleetLinkEndpoint
{
    /// <summary>Maps the endpoint.</summary>
    /// <param name="routes">The route builder.</param>
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // Outside /api/v1: the fleet token authenticates the call, not a session.
        routes.MapGet(HubProtocol.FleetLinkPath, HandleAsync).WithName("openFleetLink");
    }

    private static async Task HandleAsync(HttpContext context, FleetTokenService tokens, FleetService fleet)
    {
        context.Response.Headers.CacheControl = "no-store";
        FleetTokenResult result = await tokens.AuthenticateAsync(AgentLinkEndpoint.ReadBearer(context.Request), context.RequestAborted).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case FleetTokenOutcome.Accepted when result.Token is { } token:
                await fleet.HandleAsync(context, token).ConfigureAwait(false);
                return;
            case FleetTokenOutcome.Revoked:
                await HubLinkErrors.WriteAsync(context, StatusCodes.Status401Unauthorized, HubErrorCodes.Revoked, "The fleet token was revoked.").ConfigureAwait(false);
                return;
            default:
                await HubLinkErrors.WriteAsync(context, StatusCodes.Status401Unauthorized, "unauthorized", "The fleet token is not valid.").ConfigureAwait(false);
                return;
        }
    }
}
