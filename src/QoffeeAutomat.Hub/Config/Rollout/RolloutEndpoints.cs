using System.Globalization;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Api.OpenApi;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Config.Rollout;

/// <summary>
/// The roll-out endpoints of the hub API (PLAN.md S20, T909): how the desired state has reached the agents. Everyone who is signed in may read: the status of one
/// agent (desired, pushed and applied generation, the state of every document and the errors the agent reported), the status of all agents, and how a published
/// revision of a profile has reached the agents it targets. The role operator may ask for a new push to one agent, which sends nothing the published profiles do not
/// already say.
/// </summary>
public static class RolloutEndpoints
{
    /// <summary>Maps the endpoints.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        const string tag = "Rollout";
        api.MapGet("/agents/{id}/config-status", GetAgentStatusAsync)
            .Describe(tag, "getAgentConfigStatus", "Shows how the desired configuration has reached an agent: the generation the hub wants, the one it pushed, the one the agent applied, the state of every document and the errors the agent reported.")
            .Returns<AgentConfigStatusInfo>()
            .Fails(StatusCodes.Status404NotFound);
        api.MapGet("/rollouts", GetOverviewAsync)
            .Describe(tag, "listRolloutStatus", "Shows the configuration status of every enrolled agent and how many are up to date, pending, failed, without a desired state or offline.")
            .Returns<RolloutOverview>();
        api.MapGet("/profiles/{id}/rollout", GetProfileRolloutAsync)
            .Describe(tag, "getProfileRollout", "Shows how a published revision of a profile has reached the agents it targets, with the state of its documents on each agent and the errors the agents reported. Defaults to the revision in effect.")
            .Query("revision", "integer", "A published revision other than the one in effect.")
            .Returns<ProfileRolloutInfo>()
            .Fails(StatusCodes.Status404NotFound, HubApiResults.UnprocessableContent);
        api.MapPost("/agents/{id}/config-push", PushAsync).RequireAuthorization(HubPolicies.Operator)
            .Describe(tag, "pushAgentConfig", "Recomputes the desired state of an agent and sends it again, also when it was sent before; for an agent that is online. The answer is the status before the push completes.")
            .Returns<AgentConfigStatusInfo>(StatusCodes.Status202Accepted)
            .Fails(StatusCodes.Status404NotFound);
    }

    private static IResult UnknownAgent() => HubApiResults.NotFound(HubErrorCodes.UnknownAgent, "There is no such enrolled agent.");

    private static async Task<IResult> GetAgentStatusAsync(string id, ConfigRolloutService rollout, CancellationToken cancellationToken)
    {
        AgentConfigStatusInfo? status = await rollout.GetAgentStatusAsync(id, cancellationToken).ConfigureAwait(false);
        return status is null ? UnknownAgent() : HubApiResults.Json(status, HubApiJsonContext.Default.AgentConfigStatusInfo);
    }

    private static async Task<IResult> GetOverviewAsync(ConfigRolloutService rollout, CancellationToken cancellationToken)
    {
        RolloutOverview overview = await rollout.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
        return HubApiResults.Json(overview, HubApiJsonContext.Default.RolloutOverview);
    }

    private static async Task<IResult> GetProfileRolloutAsync(string id, HttpContext context, ConfigRolloutService rollout, CancellationToken cancellationToken)
    {
        int? revision = null;
        string text = context.Request.Query["revision"].ToString();
        if (text.Length > 0)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < 1)
            {
                return HubApiResults.Invalid("/revision", "invalid", "The revision must be a positive whole number.");
            }

            revision = parsed;
        }

        ConfigResult<ProfileRolloutInfo> result = await rollout.GetProfileRolloutAsync(id, revision, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? HubApiResults.Json(result.Value!, HubApiJsonContext.Default.ProfileRolloutInfo)
            : HubApiResults.NotFound(result.Code ?? "not_found", result.Message ?? "The profile or revision does not exist.");
    }

    private static async Task<IResult> PushAsync(string id, HttpContext context, ConfigRolloutService rollout, CancellationToken cancellationToken)
    {
        AuditContext audit = context.Audit();
        audit.Target = "agent:" + id;
        audit.Agents.Add(id);

        AgentConfigStatusInfo? status = await rollout.GetAgentStatusAsync(id, cancellationToken).ConfigureAwait(false);
        if (status is null)
        {
            return UnknownAgent();
        }

        rollout.RequestSync(id);
        return HubApiResults.Json(status, HubApiJsonContext.Default.AgentConfigStatusInfo, StatusCodes.Status202Accepted);
    }
}
