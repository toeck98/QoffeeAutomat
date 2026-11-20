using System.Text.Json;
using System.Text.Json.Serialization;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Contracts.Hub;

/// <summary>Identity of a hub, in the fleet hello.</summary>
public sealed record HubInfo(string Id, string Name, string Version);

/// <summary>
/// Hub to client. First frame of the fleet link: the S07 <c>hello</c> with <c>hub</c> in place of <c>agent</c>.
/// <see cref="Features"/> contains <see cref="HubFeatures.Fleet"/>.
/// </summary>
/// <param name="Protocol">The range of protocol versions the hub supports.</param>
/// <param name="Hub">The hub.</param>
/// <param name="Features">The features of the hub.</param>
public sealed record FleetHelloMessage(
    ProtocolRange Protocol,
    HubInfo Hub,
    IReadOnlyList<string> Features) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => MessageTypes.Hello;
}

/// <summary>The scope of a fleet token in the response of the pairing endpoint: every agent, or the agents that carry at least one of the tags.</summary>
/// <param name="Type"><c>all</c> or <c>tags</c>.</param>
/// <param name="Tags">The tags of a tag scope; absent for <c>all</c>.</param>
public sealed record FleetScopeInfo(string Type, IReadOnlyList<string>? Tags = null);

/// <summary>Response of a successful <c>POST /hub/v1/pair</c> (PLAN.md S20 "Fleet link").</summary>
/// <param name="Token">The fleet token: 32 random bytes as base64url without padding. The hub shows it once and stores only a hash.</param>
/// <param name="Hub">The hub; its id is the unique id of the Home Assistant config entry.</param>
/// <param name="Scope">What the token may see and control.</param>
/// <param name="Fingerprint">The SHA-256 fingerprint of the hub certificate for pinning; absent when the hub is served without TLS.</param>
public sealed record FleetPairResponse(
    string Token,
    EnrollHubInfo Hub,
    FleetScopeInfo Scope,
    string? Fingerprint = null)
{
    /// <summary>Hides the token from the generated <c>ToString</c> so that it does not end up in logs.</summary>
    /// <returns>A text without the token.</returns>
    public override string ToString() => nameof(FleetPairResponse) + " { Hub = " + Hub.Id + " }";
}

/// <summary>An agent as listed by <c>get_agents</c>.</summary>
/// <param name="Id">The agent id.</param>
/// <param name="Name">The agent name.</param>
/// <param name="Online">Whether the agent is connected to the hub.</param>
/// <param name="PowerState">The last <c>bye</c> reason (<see cref="ByeReasons"/>) or <see cref="PowerStates.Unknown"/>.</param>
/// <param name="ManifestRev">The revision of the cached manifest; absent for an agent that never connected.</param>
/// <param name="Agent">The agent identity; absent for an agent that never connected.</param>
public sealed record FleetAgentEntry(
    string Id,
    string Name,
    bool Online,
    string PowerState,
    string? ManifestRev = null,
    AgentInfo? Agent = null);

/// <summary>Client to hub request (fleet link): list the agents in the scope of the token.</summary>
/// <param name="Id">Request id chosen by the client.</param>
public sealed record GetAgentsMessage(long Id) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => HubMessageTypes.GetAgents;
}

/// <summary>The data of the result of <c>get_agents</c>.</summary>
/// <param name="Agents">The agents.</param>
public sealed record GetAgentsResultData(IReadOnlyList<FleetAgentEntry> Agents);

/// <summary>
/// Hub to client (fleet link): an agent entered the scope of the token. <c>A</c> is the agent id; the other members are
/// those of <see cref="FleetAgentEntry"/> without the id.
/// </summary>
/// <param name="A">The agent id.</param>
/// <param name="Name">The agent name.</param>
/// <param name="Online">Whether the agent is connected.</param>
/// <param name="PowerState">The last <c>bye</c> reason or <see cref="PowerStates.Unknown"/>.</param>
/// <param name="ManifestRev">The revision of the cached manifest; absent for an agent that never connected.</param>
/// <param name="Agent">The agent identity; absent for an agent that never connected.</param>
public sealed record AgentAddedMessage(
    string A,
    string Name,
    bool Online,
    string PowerState,
    string? ManifestRev = null,
    AgentInfo? Agent = null) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => HubMessageTypes.AgentAdded;
}

/// <summary>Hub to client (fleet link): an agent left the scope of the token or was removed from the hub.</summary>
/// <param name="A">The agent id.</param>
public sealed record AgentRemovedMessage(string A) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => HubMessageTypes.AgentRemoved;
}

/// <summary>Hub to client (fleet link): an agent went online or offline.</summary>
/// <param name="A">The agent id.</param>
/// <param name="Online">Whether the agent is connected.</param>
/// <param name="PowerState">The last <c>bye</c> reason or <see cref="PowerStates.Unknown"/>.</param>
public sealed record AgentStatusMessage(string A, bool Online, string PowerState) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => HubMessageTypes.AgentStatus;
}

/// <summary>Client to hub request (fleet link): <c>get_manifest</c> of S07 plus the agent id.</summary>
/// <param name="A">The agent id.</param>
/// <param name="Id">Request id chosen by the client.</param>
public sealed record FleetGetManifestMessage(string A, long Id) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => MessageTypes.GetManifest;
}

/// <summary>Client to hub request (fleet link): <c>subscribe</c> of S07 plus the agent id.</summary>
/// <param name="A">The agent id.</param>
/// <param name="Id">Request id chosen by the client.</param>
/// <param name="Metrics">The metric ids or all metrics.</param>
/// <param name="Intervals">Optional metric id to seconds.</param>
public sealed record FleetSubscribeMessage(
    string A,
    long Id,
    MetricSelection Metrics,
    IReadOnlyDictionary<string, double>? Intervals = null) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => MessageTypes.Subscribe;
}

/// <summary>Client to hub request (fleet link): <c>run</c> of S07 plus the agent id.</summary>
/// <param name="A">The agent id.</param>
/// <param name="Id">Request id chosen by the client.</param>
/// <param name="Command">The command id.</param>
/// <param name="Vars">Variable values; absent means none.</param>
/// <param name="Wait">Wait for the end of the run; absent means <c>true</c>.</param>
/// <param name="Context">Optional run context; the hub forwards it with source <c>ha</c>.</param>
public sealed record FleetRunMessage(
    string A,
    long Id,
    string Command,
    IReadOnlyDictionary<string, JsonElement>? Vars = null,
    bool? Wait = null,
    RunContext? Context = null) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => MessageTypes.Run;

    /// <summary>Whether the request waits for the end of the run (default <c>true</c>).</summary>
    [JsonIgnore]
    public bool ShouldWait => Wait ?? true;
}

/// <summary>Hub to client (fleet link): <c>manifest_changed</c> of S07 plus the agent id.</summary>
/// <param name="A">The agent id.</param>
/// <param name="Rev">The new manifest revision.</param>
public sealed record FleetManifestChangedMessage(string A, string Rev) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => MessageTypes.ManifestChanged;
}

/// <summary>Hub to client (fleet link): <c>state</c> of S07 plus the agent id.</summary>
/// <param name="A">The agent id.</param>
/// <param name="Ts">Unix time in milliseconds.</param>
/// <param name="V">Metric id to value; a JSON null means unavailable.</param>
public sealed record FleetStateMessage(string A, long Ts, IReadOnlyDictionary<string, JsonElement> V) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => MessageTypes.State;
}

/// <summary>Hub to client (fleet link): <c>event</c> of S07 plus the agent id.</summary>
/// <param name="A">The agent id.</param>
/// <param name="Type">The event type.</param>
/// <param name="Ts">Unix time in milliseconds.</param>
/// <param name="Data">Optional event data (an object).</param>
public sealed record FleetEventMessage(string A, string Type, long Ts, JsonElement? Data = null) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => MessageTypes.Event;
}
