using QoffeeAutomat.Agent.Api.OpenApi;
using QoffeeAutomat.Agent.Hosting;

namespace QoffeeAutomat.Agent.Api;

/// <summary>The <c>/agent</c> endpoints of the management API (PLAN.md S08): the restart that the settings page offers.</summary>
public static class AgentEndpoints
{
    /// <summary>Maps the endpoints.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapPost("/agent/restart", Restart)
            .Describe("Agent", "restartAgent", "Restarts the agent a moment after the response, so that changed ports and remote UI take effect.")
            .ReturnsNothing(StatusCodes.Status202Accepted);
    }

    private static IResult Restart(IAgentRestarter restarter)
    {
        restarter.RequestRestart();
        return Results.StatusCode(StatusCodes.Status202Accepted);
    }
}
