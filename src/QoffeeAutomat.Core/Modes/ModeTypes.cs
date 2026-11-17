using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Commands;

namespace QoffeeAutomat.Core.Modes;

/// <summary>
/// Runs one step of a mode switch: an ordinary command, subject to every rule of the <see cref="CommandEngine"/> (disabled, paused,
/// conditions, concurrency and so on). The command engine hands the mode engine a runner that records the step as a child of the
/// switch, so the history shows the steps below the run of the generated command.
/// </summary>
/// <param name="request">What to run.</param>
/// <param name="cancellationToken">Cancels the step.</param>
/// <returns>The outcome; the runner never throws for a failing or rejected step.</returns>
public delegate Task<RunResult> ModeStepRunner(RunRequest request, CancellationToken cancellationToken);

/// <summary>
/// The outcome of a switch (<see cref="IModeSwitcher.SwitchAsync"/>). A request that was rejected before anything ran has
/// <see cref="Started"/> false and one of the codes <c>unknown_command</c>, <c>invalid_vars</c> or <c>busy</c>.
/// </summary>
/// <param name="GroupId">The group.</param>
/// <param name="Mode">The mode that was requested.</param>
/// <param name="Started">False when the request was rejected.</param>
/// <param name="Completed">True when the current mode is now <paramref name="Mode"/> (all steps ran, or failed under <c>onError: continue</c>).</param>
/// <param name="From">The mode that was current when the switch began; null when the state was unknown.</param>
/// <param name="FailedSteps">How many steps failed; with <c>onError: stop</c> at most one.</param>
/// <param name="StandardOutput">The output of the steps.</param>
/// <param name="StandardError">One line per failed step.</param>
/// <param name="ErrorCode">For a rejected request its code; for a switch that did not complete the error code of the first failed step, when it has one.</param>
/// <param name="ErrorMessage">A human-readable explanation of a rejection.</param>
public sealed record ModeSwitchResult(
    string GroupId,
    string Mode,
    bool Started,
    bool Completed,
    string? From,
    int FailedSteps,
    string StandardOutput,
    string StandardError,
    string? ErrorCode,
    string? ErrorMessage)
{
    /// <summary>True when the switch completed and no step failed.</summary>
    public bool Succeeded => Completed && FailedSteps == 0;

    /// <summary>Creates the result of a request that was rejected.</summary>
    /// <param name="groupId">The group.</param>
    /// <param name="mode">The requested mode.</param>
    /// <param name="errorCode">The rejection code.</param>
    /// <param name="message">The explanation.</param>
    /// <returns>The result.</returns>
    public static ModeSwitchResult Rejected(string groupId, string mode, string errorCode, string message)
    {
        return new ModeSwitchResult(groupId, mode, false, false, null, 0, string.Empty, message, errorCode, message);
    }
}

/// <summary>What the <see cref="CommandEngine"/> needs from the mode engine to run the generated command <c>mode_&lt;groupId&gt;</c>.</summary>
public interface IModeSwitcher
{
    /// <summary>Switches a group to a mode: the exit steps of the current mode, then the enter steps of the target.</summary>
    /// <param name="groupId">The group id.</param>
    /// <param name="modeId">The id of the mode to enter.</param>
    /// <param name="runStep">Runs the steps.</param>
    /// <param name="cancellationToken">Cancels the switch; the state is then unknown and the exception propagates.</param>
    /// <returns>The outcome.</returns>
    Task<ModeSwitchResult> SwitchAsync(string groupId, string modeId, ModeStepRunner runStep, CancellationToken cancellationToken);
}

/// <summary>Arguments of <see cref="ModeEngine.StateChanged"/>.</summary>
public sealed class ModeStateChangedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="groupId">The group whose state changed.</param>
    public ModeStateChangedEventArgs(string groupId)
    {
        GroupId = groupId;
    }

    /// <summary>The group whose state changed.</summary>
    public string GroupId { get; }

    /// <summary>The id of the state metric of the group.</summary>
    public string MetricId => ModeNames.MetricId(GroupId);
}

/// <summary>Raised when the mode engine could not complete a bookkeeping step; the switch itself is unaffected.</summary>
public sealed class ModeEngineFaultEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="groupId">The group concerned.</param>
    /// <param name="stage">What failed: <c>state</c> (the state file) or <c>event</c> (the <c>mode_changed</c> event).</param>
    /// <param name="exception">What went wrong.</param>
    public ModeEngineFaultEventArgs(string groupId, string stage, Exception exception)
    {
        GroupId = groupId;
        Stage = stage;
        Exception = exception;
    }

    /// <summary>The group concerned.</summary>
    public string GroupId { get; }

    /// <summary>What failed: <c>state</c> or <c>event</c>.</summary>
    public string Stage { get; }

    /// <summary>What went wrong.</summary>
    public Exception Exception { get; }
}
