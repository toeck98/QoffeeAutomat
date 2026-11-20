using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Api.OpenApi;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Fleet.Actions;

/// <summary>
/// The fleet-action endpoints of the hub API (PLAN.md S20 "Fleet actions", T917). Everyone who is signed in may read the actions and the versions of the agents; an operator starts a
/// bulk run and cancels it; only an administrator starts the update of agents or cancels one, because an update replaces software on every PC it reaches. A start answers 202 with the
/// action, which then runs in the background and is followed through <c>GET /fleet-actions/{id}</c>. Starting and cancelling are audited by the audit middleware with the agents that
/// are affected; the end of an action is recorded by the service.
/// </summary>
public static class FleetActionEndpoints
{
    /// <summary>The largest request body that is read.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>Maps the endpoints below <c>/api/v1/fleet-actions</c> and <c>/api/v1/fleet-versions</c>.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        const string tag = "Fleet actions";
        api.MapGet("/fleet-versions", GetVersionsAsync)
            .Describe(tag, "listFleetVersions", "Shows the version of every enrolled agent against the newest release that an agent reports. The hub makes no request to the internet itself.")
            .Returns<FleetVersionOverview>();
        api.MapGet("/fleet-actions", List)
            .Describe(tag, "listFleetActions", "Lists the bulk runs and updates that the hub keeps in memory, the newest first, with the state of every agent. The audit log keeps the permanent record.")
            .Returns<FleetActionList>();
        api.MapGet("/fleet-actions/{id}", Get)
            .Describe(tag, "getFleetAction", "Shows one bulk run or update with the state of every agent.")
            .Returns<FleetActionInfo>()
            .Fails(StatusCodes.Status404NotFound);
        api.MapPost("/fleet-actions/bulk-runs", StartBulkRunAsync).RequireAuthorization(HubPolicies.Operator)
            .Describe(tag, "startBulkRun", "Runs one command with variables on the agents that are named or carry one of the tags, with bounded parallelism. The answer is the action, which runs in the background: every agent ends as success, failure or offline.")
            .Takes<StartBulkRunRequest>()
            .Returns<FleetActionInfo>(StatusCodes.Status202Accepted)
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict, HubApiResults.UnprocessableContent);
        api.MapPost("/fleet-actions/updates", StartUpdateAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "startFleetUpdate", "Updates the named or tagged agents one after another with the built-in agent update command and waits for each to come back with the target version. The first agent that does not come back in time stops the update.")
            .Takes<StartFleetUpdateRequest>()
            .Returns<FleetActionInfo>(StatusCodes.Status202Accepted)
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict, HubApiResults.UnprocessableContent);
        api.MapPost("/fleet-actions/{id}/cancel", Cancel).RequireAuthorization(HubPolicies.Operator)
            .Describe(tag, "cancelFleetAction", "Stops a running action: agents that were not handled yet stay untouched. A command that runs on an agent is not taken back. Cancelling an update needs an administrator.")
            .Returns<FleetActionInfo>()
            .Fails(StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> GetVersionsAsync(FleetActionService actions, CancellationToken cancellationToken)
    {
        FleetVersionOverview overview = await actions.GetVersionsAsync(cancellationToken).ConfigureAwait(false);
        return HubApiResults.Json(overview, HubApiJsonContext.Default.FleetVersionOverview);
    }

    private static IResult List(FleetActionService actions)
    {
        return HubApiResults.Json(new FleetActionList(actions.List()), HubApiJsonContext.Default.FleetActionList);
    }

    private static IResult Get(string id, FleetActionService actions)
    {
        FleetActionInfo? action = actions.Find(id);
        return action is null
            ? HubApiResults.NotFound("unknown_action", "There is no such fleet action.")
            : HubApiResults.Json(action, HubApiJsonContext.Default.FleetActionInfo);
    }

    private static async Task<IResult> StartBulkRunAsync(HttpContext context, FleetActionService actions, CancellationToken cancellationToken)
    {
        AuditContext audit = context.Audit();
        audit.Target = "fleet-action:bulk-run";
        (StartBulkRunRequest? request, IResult? failure) = await HubApiResults.ReadBodyAsync(
            context.Request, HubApiJsonContext.Default.StartBulkRunRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        FleetActionStartResult result = await actions.StartBulkRunAsync(ActorOf(context), request, cancellationToken).ConfigureAwait(false);
        return Answer(result, audit, "command:" + result.Action?.Command);
    }

    private static async Task<IResult> StartUpdateAsync(HttpContext context, FleetActionService actions, CancellationToken cancellationToken)
    {
        AuditContext audit = context.Audit();
        audit.Target = "fleet-action:update";
        (StartFleetUpdateRequest? request, IResult? failure) = await HubApiResults.ReadBodyAsync(
            context.Request, HubApiJsonContext.Default.StartFleetUpdateRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        FleetActionStartResult result = await actions.StartUpdateAsync(ActorOf(context), request, cancellationToken).ConfigureAwait(false);
        return Answer(result, audit, "update:" + result.Action?.TargetVersion);
    }

    private static IResult Cancel(string id, HttpContext context, FleetActionService actions)
    {
        AuditContext audit = context.Audit();
        audit.Target = "fleet-action:" + id;
        FleetActionInfo? action = actions.Find(id);
        if (action is null)
        {
            return HubApiResults.NotFound("unknown_action", "There is no such fleet action.");
        }

        foreach (FleetActionItem item in action.Items)
        {
            _ = audit.Agents.Add(item.AgentId);
        }

        // The route lets an operator in for the sake of bulk runs; the update of agents is the administrator's.
        if (action.Kind == FleetActionKinds.Update && HubRoles.Rank(HubPrincipal.Role(context.User)) < HubRoles.Rank(HubRoles.Admin))
        {
            return HubApiResults.Problem(StatusCodes.Status403Forbidden, "admin_required", "Only an administrator may cancel an update of agents.");
        }

        return actions.Cancel(id) switch
        {
            FleetActionCancelResult.NotFound => HubApiResults.NotFound("unknown_action", "There is no such fleet action."),
            FleetActionCancelResult.NotRunning => HubApiResults.Problem(StatusCodes.Status409Conflict, "not_running", "The action has ended already."),
            _ => HubApiResults.Json(actions.Find(id) ?? action, HubApiJsonContext.Default.FleetActionInfo),
        };
    }

    private static IResult Answer(FleetActionStartResult result, AuditContext audit, string detail)
    {
        if (result.Action is not { } action)
        {
            return result.Path is not null
                ? HubApiResults.Invalid(result.Path, result.Code ?? "invalid", result.Message ?? "The request is not valid.")
                : HubApiResults.Problem(result.Status, result.Code ?? "refused", result.Message ?? "The action was refused.");
        }

        audit.Target = "fleet-action:" + action.Id + ":" + detail;
        foreach (FleetActionItem item in action.Items)
        {
            _ = audit.Agents.Add(item.AgentId);
        }

        return HubApiResults.Json(action, HubApiJsonContext.Default.FleetActionInfo, StatusCodes.Status202Accepted);
    }

    private static FleetActionActor ActorOf(HttpContext context)
    {
        return new FleetActionActor(
            HubPrincipal.Name(context.User) ?? "unknown",
            HubPrincipal.Role(context.User),
            HubPrincipal.Method(context.User),
            context.Connection.RemoteIpAddress?.ToString());
    }
}
