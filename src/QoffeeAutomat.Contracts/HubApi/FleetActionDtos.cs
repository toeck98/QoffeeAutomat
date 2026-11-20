using System.Text.Json;

namespace QoffeeAutomat.Hub.Fleet.Actions;

/// <summary>The kinds of a fleet action (PLAN.md S20 "Fleet actions", T917).</summary>
public static class FleetActionKinds
{
    /// <summary>One command with variables, run on many agents with bounded parallelism.</summary>
    public const string BulkRun = "bulk_run";

    /// <summary>The agent update action, run on the agents one after another.</summary>
    public const string Update = "update";
}

/// <summary>The states of a fleet action.</summary>
public static class FleetActionStates
{
    /// <summary>The action still has agents to handle.</summary>
    public const string Running = "running";

    /// <summary>Every targeted agent was handled; for a bulk run some agents may have failed or been offline, and for an update none failed.</summary>
    public const string Completed = "completed";

    /// <summary>An update stopped after an agent that did not come back with the new version; the agents after it were not touched.</summary>
    public const string Stopped = "stopped";

    /// <summary>A user cancelled the action; agents that had not started stay untouched.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>The states of one agent within a fleet action.</summary>
public static class FleetActionItemStates
{
    /// <summary>Not handled yet.</summary>
    public const string Pending = "pending";

    /// <summary>The command runs, or the hub waits for the agent to come back.</summary>
    public const string Running = "running";

    /// <summary>A bulk run: the command ended with exit code 0 (or was started when the run does not wait). An update: the agent came back with the target version.</summary>
    public const string Success = "success";

    /// <summary>The agent refused or failed the command, or did not come back with the new version in time.</summary>
    public const string Failure = "failure";

    /// <summary>The agent was not connected, so nothing was sent to it.</summary>
    public const string Offline = "offline";

    /// <summary>An update that was not needed (the agent has the target version already) or that was not reached because an earlier agent failed.</summary>
    public const string Skipped = "skipped";

    /// <summary>The action was cancelled before this agent was handled.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>Body of <c>POST /api/v1/fleet-actions/bulk-runs</c>. The targets are the union of <paramref name="Agents"/> and the agents that carry one of <paramref name="Tags"/>.</summary>
/// <param name="Command">The id of the command to run.</param>
/// <param name="Vars">The values of the variables, bound by the agent as for any run; never spliced into a script.</param>
/// <param name="Agents">Ids of enrolled agents.</param>
/// <param name="Tags">Tags; every enrolled agent with at least one of them is a target.</param>
/// <param name="Parallelism">How many agents run at the same time, 1 to the limit of the hub; the default of the hub when absent.</param>
/// <param name="TimeoutSeconds">How long the hub waits for the run on one agent, 1 to the limit of the hub; the default of the hub when absent.</param>
/// <param name="Wait">True (the default) to wait for the end of the command on each agent; false to count it a success when the agent started it.</param>
public sealed record StartBulkRunRequest(
    string Command,
    IReadOnlyDictionary<string, JsonElement>? Vars = null,
    IReadOnlyList<string>? Agents = null,
    IReadOnlyList<string>? Tags = null,
    int? Parallelism = null,
    int? TimeoutSeconds = null,
    bool? Wait = null);

/// <summary>Body of <c>POST /api/v1/fleet-actions/updates</c>. The targets are chosen as for a bulk run.</summary>
/// <param name="Agents">Ids of enrolled agents.</param>
/// <param name="Tags">Tags; every enrolled agent with at least one of them is a target.</param>
/// <param name="TargetVersion">The version that every agent must report after its update; the newest version that an agent reports as <c>agent.latest_version</c> when absent.</param>
/// <param name="TimeoutSeconds">How long the hub waits for one agent to come back with the target version, 1 to the limit of the hub; the default of the hub when absent.</param>
public sealed record StartFleetUpdateRequest(
    IReadOnlyList<string>? Agents = null,
    IReadOnlyList<string>? Tags = null,
    string? TargetVersion = null,
    int? TimeoutSeconds = null);

/// <summary>One agent in a fleet action and what became of it.</summary>
/// <param name="AgentId">The agent id.</param>
/// <param name="AgentName">The name of the agent.</param>
/// <param name="State">One of <see cref="FleetActionItemStates"/>.</param>
/// <param name="ErrorCode">The error code of a failure or of an offline agent: an agent code, <c>agent_offline</c>, <c>timeout</c>, <c>nonzero_exit</c> or <c>version_not_reached</c>.</param>
/// <param name="Message">What happened, for people.</param>
/// <param name="ExitCode">The exit code of a bulk run that waited for the command.</param>
/// <param name="DurationMs">How long the command ran, or how long the update took until the agent came back.</param>
/// <param name="VersionBefore">The version of the agent when its update started (updates only).</param>
/// <param name="VersionAfter">The version the agent reported when it came back (updates only).</param>
/// <param name="StartedAt">When the agent was handled; absent while it waits.</param>
/// <param name="FinishedAt">When the agent was done.</param>
public sealed record FleetActionItem(
    string AgentId,
    string AgentName,
    string State,
    string? ErrorCode,
    string? Message,
    int? ExitCode,
    long? DurationMs,
    string? VersionBefore,
    string? VersionAfter,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>The number of agents of a fleet action per state.</summary>
/// <param name="Pending">Waiting.</param>
/// <param name="Running">Being handled.</param>
/// <param name="Success">Succeeded.</param>
/// <param name="Failure">Failed.</param>
/// <param name="Offline">Not connected.</param>
/// <param name="Skipped">Not needed or not reached.</param>
/// <param name="Cancelled">Cancelled before they were handled.</param>
public sealed record FleetActionCounts(int Pending, int Running, int Success, int Failure, int Offline, int Skipped, int Cancelled);

/// <summary>A fleet action as the API shows it: a bulk run or an update orchestration with the state of each agent.</summary>
/// <param name="Id">The id of the action.</param>
/// <param name="Kind">One of <see cref="FleetActionKinds"/>.</param>
/// <param name="State">One of <see cref="FleetActionStates"/>.</param>
/// <param name="User">Who started it.</param>
/// <param name="CreatedAt">When it was started.</param>
/// <param name="FinishedAt">When it ended; absent while it runs.</param>
/// <param name="Command">The command of a bulk run.</param>
/// <param name="TargetVersion">The version of an update.</param>
/// <param name="Tags">The tags that were asked for.</param>
/// <param name="Counts">The agents per state.</param>
/// <param name="Items">The agents, in the order they are handled.</param>
/// <param name="Updated">The ids of the agents that an update brought to the target version, in the order they were updated.</param>
/// <param name="StoppedAt">The id of the agent that stopped an update; absent otherwise.</param>
/// <param name="StopReason">Why the update stopped.</param>
public sealed record FleetActionInfo(
    string Id,
    string Kind,
    string State,
    string User,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    string? Command,
    string? TargetVersion,
    IReadOnlyList<string> Tags,
    FleetActionCounts Counts,
    IReadOnlyList<FleetActionItem> Items,
    IReadOnlyList<string> Updated,
    string? StoppedAt,
    string? StopReason);

/// <summary>The answer of <c>GET /api/v1/fleet-actions</c>.</summary>
/// <param name="Actions">The retained actions, the newest first.</param>
public sealed record FleetActionList(IReadOnlyList<FleetActionInfo> Actions);

/// <summary>The version of an agent against the newest release.</summary>
/// <param name="AgentId">The agent id.</param>
/// <param name="AgentName">The name of the agent.</param>
/// <param name="Online">True while the agent is connected.</param>
/// <param name="Version">The version that the agent reported last.</param>
/// <param name="Tags">The tags of the agent.</param>
/// <param name="State"><c>current</c> (at the newest version or later), <c>outdated</c> or <c>unknown</c> (no newest version known, or the version is not understood).</param>
/// <param name="CanUpdate">True when the agent is online and offers the update command.</param>
public sealed record AgentVersionInfo(
    string AgentId,
    string AgentName,
    bool Online,
    string? Version,
    IReadOnlyList<string> Tags,
    string State,
    bool CanUpdate);

/// <summary>The answer of <c>GET /api/v1/fleet-versions</c>.</summary>
/// <param name="LatestVersion">The newest release that an agent reported (<c>agent.latest_version</c>); absent when no agent knows one. The hub makes no request to the internet itself.</param>
/// <param name="Agents">The enrolled agents.</param>
public sealed record FleetVersionOverview(string? LatestVersion, IReadOnlyList<AgentVersionInfo> Agents);
