using System.Text.Json;
using QoffeeAutomat.Hub.Api;

namespace QoffeeAutomat.Hub.Gateway;

/// <summary>Whether and how an agent is connected to the hub, as the inventory shows it.</summary>
/// <param name="Online">True while the agent link is open.</param>
/// <param name="PowerState">The reason of the last <c>bye</c> of the agent while it is offline (<c>sleep</c>, <c>hibernate</c>, <c>shutdown</c>, <c>restart</c>, <c>service_stop</c>, <c>update</c>), otherwise <c>unknown</c>.</param>
/// <param name="ConnectedAt">When the current connection was established; absent while offline.</param>
/// <param name="LastSeenAt">When the hub last heard from the agent.</param>
/// <param name="ManifestRev">The revision of the manifest that the hub holds, if it holds one.</param>
/// <param name="BootId">The <c>boot_id</c> that the agent announced; it changes with every start of the computer.</param>
/// <param name="Features">The link features that the agent and the hub both announced.</param>
public sealed record AgentPresenceInfo(
    bool Online,
    string PowerState,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? LastSeenAt,
    string? ManifestRev,
    string? BootId,
    IReadOnlyList<string> Features);

/// <summary>An enrolled agent with its presence.</summary>
/// <param name="Agent">The registry record.</param>
/// <param name="Presence">Whether it is connected.</param>
public sealed record InventoryAgent(AgentSummary Agent, AgentPresenceInfo Presence);

/// <summary>The enrolled agents with their presence: the answer of <c>GET /api/v1/inventory</c>.</summary>
/// <param name="Agents">The agents, the most recently enrolled first.</param>
public sealed record InventoryList(IReadOnlyList<InventoryAgent> Agents);

/// <summary>An event that an agent reported.</summary>
/// <param name="Id">The sequence number.</param>
/// <param name="At">The time that the agent reported.</param>
/// <param name="ReceivedAt">When the hub received it.</param>
/// <param name="Type">The event type.</param>
/// <param name="Data">The event data, if there was any.</param>
public sealed record AgentEventInfo(long Id, DateTimeOffset At, DateTimeOffset ReceivedAt, string Type, JsonElement? Data);

/// <summary>The latest values of the metrics that the hub has subscribed from an agent.</summary>
/// <param name="Ts">The unix time in milliseconds of the last <c>state</c> frame of the agent.</param>
/// <param name="UpdatedAt">When the hub received it.</param>
/// <param name="Values">Metric id to value; a null means unavailable. An offline agent shows the last values it reported.</param>
public sealed record AgentLiveState(long Ts, DateTimeOffset UpdatedAt, IReadOnlyDictionary<string, JsonElement> Values);

/// <summary>One agent in detail: the answer of <c>GET /api/v1/inventory/{id}</c>.</summary>
/// <param name="Agent">The registry record.</param>
/// <param name="Presence">Whether it is connected.</param>
/// <param name="Manifest">The cached manifest of the agent (PLAN.md S07), if the hub holds one.</param>
/// <param name="State">The live state, if the hub has received any.</param>
/// <param name="Events">The newest events of the agent, the newest first.</param>
public sealed record InventoryDetail(
    AgentSummary Agent,
    AgentPresenceInfo Presence,
    JsonElement? Manifest,
    AgentLiveState? State,
    IReadOnlyList<AgentEventInfo> Events);

/// <summary>Body of <c>POST /api/v1/agents/{id}/run</c>.</summary>
/// <param name="Command">The id of the command to run.</param>
/// <param name="Vars">The values of the variables of the command.</param>
/// <param name="Wait">True (the default) to wait for the end of the run; false to return when the agent has started it.</param>
public sealed record RunCommandRequest(string Command, IReadOnlyDictionary<string, JsonElement>? Vars = null, bool? Wait = null);

/// <summary>The answer of <c>POST /api/v1/agents/{id}/run</c>.</summary>
/// <param name="RunId">The id of the run on the agent.</param>
/// <param name="ExitCode">The exit code, when the request waited for the end of the run.</param>
/// <param name="Stdout">The captured standard output, if the command captures it.</param>
/// <param name="Stderr">The captured standard error, if the command captures it.</param>
/// <param name="DurationMs">How long the run took in milliseconds.</param>
/// <param name="TimedOut">True when the run exceeded its timeout.</param>
public sealed record RunCommandResult(string RunId, int? ExitCode, string? Stdout, string? Stderr, long? DurationMs, bool? TimedOut);
