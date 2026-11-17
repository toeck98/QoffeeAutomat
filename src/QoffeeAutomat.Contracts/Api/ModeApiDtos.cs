using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Contracts.Api;

/// <summary>The state of a mode group as the agent knows it (<c>GET /api/v1/modes/{id}/switch</c>, PLAN.md S06, S08, T717).</summary>
/// <param name="GroupId">The group id.</param>
/// <param name="Current">The id of the current mode; absent while the state is unknown (after a failed switch with <c>onError: stop</c>, or before the first switch of a group without a state).</param>
/// <param name="Switching">True while a switch is running.</param>
/// <param name="Target">The id of the mode that the running switch enters; absent when none is running.</param>
/// <param name="ChangedAt">When <paramref name="Current"/> was set; absent when it never was.</param>
public sealed record ModeState(
    string GroupId,
    string? Current = null,
    bool Switching = false,
    string? Target = null,
    DateTimeOffset? ChangedAt = null);

/// <summary>A mode group with its state, as listed by <c>GET /api/v1/modes</c>.</summary>
/// <param name="Group">The document.</param>
/// <param name="State">The state of the group.</param>
public sealed record ModeGroupItem(ModeGroupDocument Group, ModeState State);

/// <summary>Response of <c>GET /api/v1/modes</c>.</summary>
/// <param name="Groups">All mode groups, ordered by id.</param>
public sealed record ModeGroupList(IReadOnlyList<ModeGroupItem> Groups);

/// <summary>Body of <c>POST /api/v1/modes/{id}/switch</c>.</summary>
/// <param name="Mode">The id of the mode to switch to. Selecting the current mode runs its enter steps again.</param>
public sealed record ModeSwitchRequest(string Mode);

/// <summary>Response of a switch that was accepted (HTTP 202); follow it with <c>/runs/{runId}</c> or by reading the state.</summary>
/// <param name="RunId">The run of the generated command <c>mode_&lt;id&gt;</c>.</param>
/// <param name="GroupId">The group id.</param>
/// <param name="Mode">The mode that is being entered.</param>
public sealed record ModeSwitchStarted(string RunId, string GroupId, string Mode);
