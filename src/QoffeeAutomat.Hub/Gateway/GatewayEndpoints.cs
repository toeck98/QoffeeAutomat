using System.Globalization;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Api.OpenApi;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Gateway;

/// <summary>
/// The endpoints of the agent gateway in the hub API (PLAN.md S20, T906): <c>GET /api/v1/inventory</c> and <c>GET /api/v1/inventory/{id}</c> show the enrolled agents with
/// their presence, manifest, live state and events (every signed-in user), and <c>POST /api/v1/agents/{id}/run</c> runs a command on a connected agent (role operator or
/// administrator). A run is audited with the user, the agent and the command; the user also goes to the audit log of the agent as the user of the run.
/// </summary>
public static class GatewayEndpoints
{
    /// <summary>The largest request body that is read.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>The number of events that the detail shows unless the request asks for another number.</summary>
    public const int DefaultEvents = 20;

    /// <summary>The longest command id that is accepted.</summary>
    public const int MaxCommandLength = 256;

    /// <summary>Maps the endpoints.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/inventory", ListAsync)
            .Describe("Inventory", "listInventory", "Lists the enrolled agents with their presence: online, power state, last seen and the revision of the cached manifest.")
            .Returns<InventoryList>();
        api.MapGet("/inventory/{id}", GetAsync)
            .Describe("Inventory", "getInventoryAgent", "Shows one agent with its presence, the cached manifest, the live state of the subscribed metrics and its newest events.")
            .Query("events", "integer", "The number of events to return, 0 to 200; 20 by default.")
            .Returns<InventoryDetail>()
            .Fails(StatusCodes.Status404NotFound, HubApiResults.UnprocessableContent);
        api.MapPost("/agents/{id}/run", RunAsync).RequireAuthorization(HubPolicies.Operator)
            .Describe("Agents", "runAgentCommand", "Runs a command on a connected agent and, unless wait is false, returns its result. The user is recorded in the audit log of the hub and of the agent.")
            .Takes<RunCommandRequest>()
            .Returns<RunCommandResult>()
            .Fails(
                StatusCodes.Status400BadRequest,
                StatusCodes.Status404NotFound,
                StatusCodes.Status409Conflict,
                HubApiResults.UnprocessableContent,
                StatusCodes.Status429TooManyRequests,
                StatusCodes.Status502BadGateway,
                StatusCodes.Status504GatewayTimeout);
    }

    private static AgentPresenceInfo ToInfo(AgentPresence presence, AgentRecord agent)
    {
        return new AgentPresenceInfo(
            presence.Online,
            presence.PowerState,
            presence.ConnectedAt,
            presence.LastSeenAt ?? agent.LastSeenAt,
            presence.ManifestRev,
            presence.BootId,
            presence.Features);
    }

    private static async Task<IResult> ListAsync(AgentRegistry registry, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentRecord> agents = await registry.ListAsync(AgentStatuses.Enrolled, cancellationToken).ConfigureAwait(false);
        InventoryAgent[] entries =
        [
            .. agents.Select(agent => new InventoryAgent(AgentEndpoints.ToSummary(agent), ToInfo(gateway.GetPresence(agent.Id), agent))),
        ];
        return HubApiResults.Json(new InventoryList(entries), HubApiJsonContext.Default.InventoryList);
    }

    private static async Task<IResult> GetAsync(string id, HttpContext context, AgentRegistry registry, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        int limit = DefaultEvents;
        string? text = context.Request.Query["events"].ToString();
        if (!string.IsNullOrEmpty(text)
            && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit > 200))
        {
            return HubApiResults.Invalid("/events", "invalid", "events must be a number from 0 to 200.");
        }

        AgentRecord? agent = await registry.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (agent is null)
        {
            return HubApiResults.NotFound(HubErrorCodes.UnknownAgent, "There is no such agent.");
        }

        CachedManifest? manifest = gateway.GetManifest(id);
        AgentStateSnapshot? state = gateway.GetState(id);
        IReadOnlyList<StoredAgentEvent> events = limit == 0
            ? []
            : await gateway.GetEventsAsync(id, limit, cancellationToken).ConfigureAwait(false);
        var detail = new InventoryDetail(
            AgentEndpoints.ToSummary(agent),
            ToInfo(gateway.GetPresence(id), agent),
            manifest?.Manifest,
            state is null ? null : new AgentLiveState(state.Ts, state.UpdatedAt, state.Values),
            [.. events.Select(stored => new AgentEventInfo(stored.Id, stored.At, stored.ReceivedAt, stored.Type, stored.Data))]);
        return HubApiResults.Json(detail, HubApiJsonContext.Default.InventoryDetail);
    }

    private static async Task<IResult> RunAsync(
        string id,
        HttpContext context,
        AgentRegistry registry,
        IAgentGateway gateway,
        CancellationToken cancellationToken)
    {
        AuditContext audit = context.Audit();
        audit.Target = "agent:" + id;
        audit.Agents.Add(id);

        AgentRecord? agent = await registry.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (agent is null)
        {
            return HubApiResults.NotFound(HubErrorCodes.UnknownAgent, "There is no such agent.");
        }

        (RunCommandRequest? request, IResult? failure) = await HubApiResults.ReadBodyAsync(
            context.Request, HubApiJsonContext.Default.RunCommandRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        if (string.IsNullOrWhiteSpace(request.Command) || request.Command.Length > MaxCommandLength)
        {
            return HubApiResults.Invalid("/command", "invalid", "command must be the id of a command, at most " + MaxCommandLength.ToString(CultureInfo.InvariantCulture) + " characters.");
        }

        audit.Target = "agent:" + id + ":command:" + request.Command;
        string user = HubPrincipal.Name(context.User) ?? "unknown";
        AgentRunOutcome outcome = await gateway.RunAsync(
            new AgentRunRequest(id, request.Command, request.Vars, request.Wait ?? true, user),
            cancellationToken).ConfigureAwait(false);
        if (outcome.Result is { } result)
        {
            return HubApiResults.Json(
                new RunCommandResult(result.RunId, result.ExitCode, result.Stdout, result.Stderr, result.DurationMs, result.TimedOut),
                HubApiJsonContext.Default.RunCommandResult);
        }

        string code = outcome.ErrorCode ?? ErrorCodes.Internal;
        return HubApiResults.Problem(StatusOf(code), code, outcome.ErrorMessage ?? "The command could not be run.");
    }

    /// <summary>Maps an error code of the gateway or of the agent to the HTTP status of the answer.</summary>
    /// <param name="code">The error code.</param>
    /// <returns>The status.</returns>
    public static int StatusOf(string code)
    {
        return code switch
        {
            HubErrorCodes.UnknownAgent or ErrorCodes.UnknownCommand => StatusCodes.Status404NotFound,
            ErrorCodes.BadRequest => StatusCodes.Status400BadRequest,
            ErrorCodes.InvalidVars => HubApiResults.UnprocessableContent,
            ErrorCodes.Cooldown => StatusCodes.Status429TooManyRequests,
            ErrorCodes.Timeout => StatusCodes.Status504GatewayTimeout,
            HubErrorCodes.AgentOffline
                or ErrorCodes.CommandDisabled
                or ErrorCodes.Busy
                or ErrorCodes.NoSession
                or ErrorCodes.ElevationUnavailable
                or ErrorCodes.Paused
                or ErrorCodes.ConditionNotMet
                or ErrorCodes.Cancelled => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status502BadGateway,
        };
    }
}
