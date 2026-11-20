using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Api.OpenApi;
using QoffeeAutomat.Hub.Config.Rollout;
using QoffeeAutomat.Hub.Fleet;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Agents;

/// <summary>
/// The agent registry endpoints of the hub API (PLAN.md S20, T904): list and read agents (every signed-in user), and, for administrators, edit tags,
/// approve or reject a pending agent, rotate a credential and remove an agent. Removing an agent revokes its credential: a later connection with it
/// is answered with HTTP 401 and the code <c>revoked</c>. Every change names the agent in the audit entry.
/// </summary>
public static class AgentEndpoints
{
    /// <summary>The largest request body that is read.</summary>
    public const int MaxBodyBytes = 8 * 1024;

    /// <summary>The reason that goes into <c>unenroll</c> when an administrator removes an agent.</summary>
    public const string RemovedReason = "removed";

    /// <summary>Maps the endpoints below <c>/api/v1/agents</c>.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        const string tag = "Agents";
        api.MapGet("/agents", ListAsync)
            .Describe(tag, "listAgents", "Lists the agents of the registry, the most recently enrolled first.")
            .Query("status", "string", "Only agents with this status: pending, approved, enrolled or rejected.")
            .Returns<AgentList>()
            .Fails(HubApiResults.UnprocessableContent);
        api.MapGet("/agents/{id}", GetAsync)
            .Describe(tag, "getAgent", "Returns one agent of the registry.")
            .Returns<AgentSummary>()
            .Fails(StatusCodes.Status404NotFound);
        api.MapPatch("/agents/{id}", UpdateAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "updateAgent", "Replaces the tags of an agent.")
            .Takes<UpdateAgentRequest>()
            .Returns<AgentSummary>()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, HubApiResults.UnprocessableContent);
        api.MapPost("/agents/{id}/approve", ApproveAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "approveAgent", "Accepts an agent that waits for approval. It receives its credential with its next enroll request.")
            .Returns<AgentSummary>()
            .Fails(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
        api.MapPost("/agents/{id}/reject", RejectAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "rejectAgent", "Rejects an agent that waits for approval. The agent stays in the list until it is removed.")
            .Returns<AgentSummary>()
            .Fails(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
        api.MapPost("/agents/{id}/rotate-credential", RotateAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "rotateAgentCredential", "Replaces the credential of a connected agent. The old credential stays valid until the agent has used the new one.")
            .Returns<AgentSummary>()
            .Fails(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
        api.MapDelete("/agents/{id}", RemoveAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "removeAgent", "Removes an agent and revokes its credential. A connected agent is told to leave the hub.")
            .ReturnsNothing()
            .Fails(StatusCodes.Status404NotFound);
    }

    /// <summary>Converts a registry record to its API form.</summary>
    /// <param name="agent">The record.</param>
    /// <returns>The summary.</returns>
    public static AgentSummary ToSummary(AgentRecord agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return new AgentSummary(
            agent.Id,
            agent.Name,
            agent.Hostname,
            agent.Os,
            agent.OsVersion,
            agent.Arch,
            agent.Version,
            agent.Macs,
            agent.UiUrl,
            agent.Tags,
            agent.Status,
            agent.EnrolledAt,
            agent.ApprovedAt,
            agent.LastSeenAt,
            agent.TokenId,
            agent.CredentialIssuedAt,
            agent.RotationPending);
    }

    private static IResult UnknownAgent() => HubApiResults.NotFound(HubErrorCodes.UnknownAgent, "There is no such agent.");

    private static void Audit(HttpContext context, string id)
    {
        AuditContext audit = context.Audit();
        audit.Target = "agent:" + id;
        audit.Agents.Add(id);
    }

    private static async Task<IResult> ListAsync(HttpContext context, AgentRegistry registry, CancellationToken cancellationToken)
    {
        string? status = context.Request.Query["status"].ToString();
        if (string.IsNullOrEmpty(status))
        {
            status = null;
        }
        else if (!AgentStatuses.IsValid(status))
        {
            return HubApiResults.Invalid("/status", "invalid", "The status must be pending, approved, enrolled or rejected.");
        }

        IReadOnlyList<AgentRecord> agents = await registry.ListAsync(status, cancellationToken).ConfigureAwait(false);
        return HubApiResults.Json(new AgentList([.. agents.Select(ToSummary)]), HubApiJsonContext.Default.AgentList);
    }

    private static async Task<IResult> GetAsync(string id, AgentRegistry registry, CancellationToken cancellationToken)
    {
        AgentRecord? agent = await registry.FindAsync(id, cancellationToken).ConfigureAwait(false);
        return agent is null ? UnknownAgent() : HubApiResults.Json(ToSummary(agent), HubApiJsonContext.Default.AgentSummary);
    }

    private static async Task<IResult> UpdateAsync(
        string id,
        HttpContext context,
        AgentRegistry registry,
        ConfigRolloutService rollout,
        FleetService fleet,
        CancellationToken cancellationToken)
    {
        Audit(context, id);
        (UpdateAgentRequest? request, IResult? failure) = await HubApiResults.ReadBodyAsync(
            context.Request, HubApiJsonContext.Default.UpdateAgentRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        if (request.Tags is null)
        {
            return HubApiResults.Invalid("/tags", "required", "The list of tags is required; send an empty list to remove all tags.");
        }

        if (!AgentTags.TryNormalize(request.Tags, out IReadOnlyList<string> tags, out string? error))
        {
            return HubApiResults.Invalid("/tags", "invalid", error ?? "The tags are not valid.");
        }

        AgentRecord? agent = await registry.SetTagsAsync(id, tags, cancellationToken).ConfigureAwait(false);
        if (agent is null)
        {
            return UnknownAgent();
        }

        // The tags decide which agents a fleet token sees: every connected Home Assistant is brought in line at once (T913).
        fleet.OnAgentTagsChanged(agent);

        // The tags decide which profiles reach the agent: its desired state is recomputed and a new generation is pushed when the agent is online (T909).
        _ = await rollout.OnAgentTagsChangedAsync(id, cancellationToken).ConfigureAwait(false);
        return HubApiResults.Json(ToSummary(agent), HubApiJsonContext.Default.AgentSummary);
    }

    private static Task<IResult> ApproveAsync(string id, HttpContext context, AgentRegistry registry, CancellationToken cancellationToken)
    {
        return DecideAsync(id, context, registry, registry.ApproveAsync, cancellationToken);
    }

    private static Task<IResult> RejectAsync(string id, HttpContext context, AgentRegistry registry, CancellationToken cancellationToken)
    {
        return DecideAsync(id, context, registry, registry.RejectAsync, cancellationToken);
    }

    private static async Task<IResult> DecideAsync(
        string id,
        HttpContext context,
        AgentRegistry registry,
        Func<string, CancellationToken, Task<AgentDecision>> decide,
        CancellationToken cancellationToken)
    {
        Audit(context, id);
        AgentDecision decision = await decide(id, cancellationToken).ConfigureAwait(false);
        if (decision == AgentDecision.NotFound)
        {
            return UnknownAgent();
        }

        if (decision == AgentDecision.WrongState)
        {
            return HubApiResults.Problem(StatusCodes.Status409Conflict, "invalid_state", "Only an agent that waits for approval can be approved or rejected.");
        }

        AgentRecord? agent = await registry.FindAsync(id, cancellationToken).ConfigureAwait(false);
        return agent is null ? UnknownAgent() : HubApiResults.Json(ToSummary(agent), HubApiJsonContext.Default.AgentSummary);
    }

    private static async Task<IResult> RotateAsync(
        string id,
        HttpContext context,
        AgentRegistry registry,
        IAgentLinkNotifier notifier,
        CancellationToken cancellationToken)
    {
        Audit(context, id);
        RotationStart start = await registry.BeginRotationAsync(id, cancellationToken).ConfigureAwait(false);
        if (start.Decision == AgentDecision.NotFound)
        {
            return UnknownAgent();
        }

        if (start.Decision == AgentDecision.WrongState || start.Credential is null)
        {
            return HubApiResults.Problem(StatusCodes.Status409Conflict, "invalid_state", "Only an enrolled agent has a credential that can be rotated.");
        }

        bool delivered;
        try
        {
            delivered = await notifier.SendCredentialRotateAsync(id, start.Credential, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await registry.CancelRotationAsync(id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (!delivered)
        {
            // Nobody has the new credential: forget it, the old one stays.
            await registry.CancelRotationAsync(id, CancellationToken.None).ConfigureAwait(false);
            return HubApiResults.Problem(StatusCodes.Status409Conflict, HubErrorCodes.AgentOffline, "The agent is not connected or did not accept the new credential.");
        }

        await registry.CompleteRotationAsync(id, CancellationToken.None).ConfigureAwait(false);
        AgentRecord? agent = await registry.FindAsync(id, cancellationToken).ConfigureAwait(false);
        return agent is null ? UnknownAgent() : HubApiResults.Json(ToSummary(agent), HubApiJsonContext.Default.AgentSummary);
    }

    private static async Task<IResult> RemoveAsync(
        string id,
        HttpContext context,
        AgentRegistry registry,
        IAgentLinkNotifier notifier,
        CancellationToken cancellationToken)
    {
        Audit(context, id);

        // The credential is revoked first: even when the agent cannot be told, a later connection is answered with 401 revoked.
        AgentRecord? removed = await registry.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
        if (removed is null)
        {
            return UnknownAgent();
        }

        await notifier.SendUnenrollAsync(id, RemovedReason, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }
}
