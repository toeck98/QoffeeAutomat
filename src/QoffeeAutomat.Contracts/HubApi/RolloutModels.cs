using QoffeeAutomat.Contracts.Hub;

namespace QoffeeAutomat.Hub.Config.Rollout;

/// <summary>The roll-out state of an agent: how far the generation that the hub wants it to have has come (PLAN.md S20, T909).</summary>
public static class RolloutStates
{
    /// <summary>No profile reaches the agent and none ever did: the hub has no desired state for it (generation 0) and sends nothing.</summary>
    public const string NoDesiredState = "no_desired_state";

    /// <summary>The agent reported that it applied the desired generation, or a later one.</summary>
    public const string UpToDate = "up_to_date";

    /// <summary>The agent has not applied the desired generation yet: it is offline, or the generation is on its way.</summary>
    public const string Pending = "pending";

    /// <summary>The agent reported that it rejected the desired generation; the previous generation stays active on the agent.</summary>
    public const string Failed = "failed";
}

/// <summary>The state of one document on one agent.</summary>
/// <param name="Kind">The document kind.</param>
/// <param name="Id">The document id.</param>
/// <param name="State">What the agent reported (<see cref="ConfigItemStates"/>), or <c>pending</c> while the agent has not reported on the desired generation.</param>
/// <param name="Errors">The validation errors or the reason for skipping that the agent reported; empty when there are none.</param>
public sealed record DocumentRollout(string Kind, string Id, string State, IReadOnlyList<string> Errors);

/// <summary>The configuration status of one agent: the generation the hub wants, the one it pushed, the one the agent applied, and the per-document results the agent reported.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="AgentName">The display name of the agent.</param>
/// <param name="Online">True while the agent link is open.</param>
/// <param name="State">One of <see cref="RolloutStates"/>.</param>
/// <param name="DesiredGeneration">The generation the hub last created for the agent; 0 when none.</param>
/// <param name="PushedGeneration">The generation the hub last tried to send; 0 when none.</param>
/// <param name="PushedAt">When the hub tried; absent when it never did.</param>
/// <param name="DeliveryError">Why the last push did not complete (the agent refused, did not answer or the hub lacked a document); absent when it completed.</param>
/// <param name="AppliedGeneration">The generation the agent last reported as applied; 0 when none.</param>
/// <param name="AppliedAt">When the hub received that report; absent when there is none.</param>
/// <param name="ReportedGeneration">The generation of the last report of the agent.</param>
/// <param name="ReportedState">The state of the last report: <c>applied</c> or <c>failed</c>; absent when the agent never reported.</param>
/// <param name="ReportedAt">When the hub received the last report; absent when there is none.</param>
/// <param name="Errors">The errors that the agent reported for the desired generation, as <c>kind/id: text</c>.</param>
/// <param name="Documents">The documents of the desired generation with the state the agent reported; empty while the agent has not reported on that generation.</param>
public sealed record AgentConfigStatusInfo(
    string AgentId,
    string AgentName,
    bool Online,
    string State,
    long DesiredGeneration,
    long PushedGeneration,
    DateTimeOffset? PushedAt,
    string? DeliveryError,
    long AppliedGeneration,
    DateTimeOffset? AppliedAt,
    long ReportedGeneration,
    string? ReportedState,
    DateTimeOffset? ReportedAt,
    IReadOnlyList<string> Errors,
    IReadOnlyList<DocumentRollout> Documents);

/// <summary>How many agents are in which state.</summary>
/// <param name="Total">The number of agents.</param>
/// <param name="UpToDate">Agents in the state <see cref="RolloutStates.UpToDate"/>.</param>
/// <param name="Pending">Agents in the state <see cref="RolloutStates.Pending"/>.</param>
/// <param name="Failed">Agents in the state <see cref="RolloutStates.Failed"/>.</param>
/// <param name="NoDesiredState">Agents in the state <see cref="RolloutStates.NoDesiredState"/>.</param>
/// <param name="Offline">Agents that are offline, whatever their state.</param>
public sealed record RolloutCounts(int Total, int UpToDate, int Pending, int Failed, int NoDesiredState, int Offline);

/// <summary>The answer of <c>GET /api/v1/rollouts</c>: the configuration status of every enrolled agent.</summary>
/// <param name="Counts">The agents per state.</param>
/// <param name="Agents">The agents, ordered by id.</param>
public sealed record RolloutOverview(RolloutCounts Counts, IReadOnlyList<AgentConfigStatusInfo> Agents);

/// <summary>An agent in the roll-out of one profile revision.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="AgentName">The display name.</param>
/// <param name="Online">True while the agent link is open.</param>
/// <param name="State">One of <see cref="RolloutStates"/>, for the whole desired generation of the agent.</param>
/// <param name="DesiredGeneration">The generation the hub wants the agent to have.</param>
/// <param name="AppliedGeneration">The generation the agent last reported as applied.</param>
/// <param name="Documents">The documents that this profile revision contributes to the agent, with their state.</param>
/// <param name="Errors">The errors that the agent reported for these documents, as <c>kind/id: text</c>.</param>
public sealed record ProfileRolloutAgent(
    string AgentId,
    string AgentName,
    bool Online,
    string State,
    long DesiredGeneration,
    long AppliedGeneration,
    IReadOnlyList<DocumentRollout> Documents,
    IReadOnlyList<string> Errors);

/// <summary>The answer of <c>GET /api/v1/profiles/{id}/rollout</c>: how a published profile revision has reached the agents.</summary>
/// <param name="ProfileId">The profile.</param>
/// <param name="Revision">The revision.</param>
/// <param name="Counts">The agents that the revision reaches, per state.</param>
/// <param name="Agents">The agents whose desired state contains documents of this revision, ordered by id.</param>
public sealed record ProfileRolloutInfo(string ProfileId, int Revision, RolloutCounts Counts, IReadOnlyList<ProfileRolloutAgent> Agents);

/// <summary>What the hub keeps about the configuration of one agent between the desired state and the reports of the agent (table <c>agent_config_status</c>).</summary>
public sealed record AgentConfigRow(
    string AgentId,
    long PushedGeneration,
    DateTimeOffset? PushedAt,
    string? DeliveryError,
    long AppliedGeneration,
    DateTimeOffset? AppliedAt,
    IReadOnlyList<ConfigItemResult> AppliedResults,
    long ReportedGeneration,
    string? ReportedState,
    DateTimeOffset? ReportedAt,
    IReadOnlyList<ConfigItemResult> ReportedResults)
{
    /// <summary>The row of an agent that has not been pushed to and has not reported.</summary>
    /// <param name="agentId">The agent.</param>
    /// <returns>The empty row.</returns>
    public static AgentConfigRow Empty(string agentId) => new(agentId, 0, null, null, 0, null, [], 0, null, null, []);
}
