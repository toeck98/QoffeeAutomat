using System.Text.Json;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Events;

namespace QoffeeAutomat.Core.Commands;

/// <summary>The values of the context variable <c>QA_SOURCE</c> (PLAN.md S06): who asked for the run.</summary>
public static class RunSources
{
    /// <summary>Home Assistant, through QoffeeLink.</summary>
    public const string HomeAssistant = "ha";

    /// <summary>The agent's UI or its management API.</summary>
    public const string Ui = "ui";

    /// <summary>A hub.</summary>
    public const string Hub = "hub";

    /// <summary>The command line.</summary>
    public const string Cli = "cli";

    /// <summary>Another command: a step of a sequence.</summary>
    public const string Sequence = "sequence";

    /// <summary>A test run of a draft command from the editor (<c>POST /commands/test</c>); it is recorded in the history like any run.</summary>
    public const string Test = "test";
}

/// <summary>A request to run a command, the same for every entry point (link, UI, command line, sequence).</summary>
/// <param name="CommandId">The id of the command.</param>
/// <param name="Variables">Variable values; validated against the command's parameters.</param>
/// <param name="Source">Where the request came from, see <see cref="RunSources"/>.</param>
/// <param name="ClientId">The link client or UI session, for the history.</param>
/// <param name="UserId">The Home Assistant user on whose behalf the command runs, for the history.</param>
public sealed record RunRequest(
    string CommandId,
    IReadOnlyDictionary<string, JsonElement>? Variables = null,
    string Source = RunSources.Ui,
    string? ClientId = null,
    string? UserId = null);

/// <summary>
/// The outcome of a run. A failure to run is described by <see cref="ErrorCode"/>, one of the codes of
/// PLAN.md S07: <c>unknown_command</c>, <c>command_disabled</c>, <c>paused</c>, <c>cooldown</c>,
/// <c>invalid_vars</c> and <c>busy</c> (the request was rejected, <see cref="Started"/> is false),
/// <c>no_session</c>, <c>elevation_unavailable</c>, <c>timeout</c>, <c>cancelled</c> and <c>internal</c>
/// (the run was admitted but did not complete normally). A command that ran and exited with a non-zero
/// code has an <see cref="ExitCode"/> and no error code.
/// </summary>
/// <param name="RunId">The id of the run; also set for rejected requests (they appear in the history).</param>
/// <param name="CommandId">The id of the command that was requested.</param>
/// <param name="Started">False when the request was rejected before the command was started.</param>
/// <param name="StartedAt">When the run started, or when the request was rejected.</param>
/// <param name="Duration">How long the run took; zero for a rejected request.</param>
/// <param name="ExitCode">The exit code; null when nothing ran.</param>
/// <param name="StandardOutput">Captured standard output (at most 64 KiB).</param>
/// <param name="StandardError">Captured standard error (at most 64 KiB).</param>
/// <param name="OutputTruncated">More output was produced than is kept.</param>
/// <param name="ErrorCode">The error code, or null.</param>
/// <param name="ErrorMessage">A human-readable explanation of the error code; never contains variable values.</param>
public sealed record RunResult(
    string RunId,
    string CommandId,
    bool Started,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool OutputTruncated,
    string? ErrorCode,
    string? ErrorMessage)
{
    /// <summary>True when the command ran and exited with zero.</summary>
    public bool Succeeded => ErrorCode is null && ExitCode == 0;

    /// <summary>True when the run exceeded its timeout.</summary>
    public bool TimedOut => ErrorCode == ErrorCodes.Timeout;

    /// <summary>The data of the successful <c>run</c> result of QoffeeLink (PLAN.md S07).</summary>
    /// <param name="includeOutput">False leaves out stdout and stderr (a command without <c>captureOutput</c> has none anyway).</param>
    /// <returns>The data object.</returns>
    public RunResultData ToLinkData(bool includeOutput = true)
    {
        return new RunResultData(
            RunId,
            ExitCode,
            includeOutput ? StandardOutput : null,
            includeOutput ? StandardError : null,
            (long)Duration.TotalMilliseconds,
            TimedOut);
    }
}

/// <summary>A run that was accepted or rejected by <see cref="CommandEngine.StartAsync"/>.</summary>
public sealed class RunHandle
{
    private readonly Action? _cancel;

    internal RunHandle(string runId, bool isAccepted, Task<RunResult> completion, Action? cancel)
    {
        RunId = runId;
        IsAccepted = isAccepted;
        Completion = completion;
        _cancel = cancel;
    }

    /// <summary>The id of the run. Rejected requests have one too.</summary>
    public string RunId { get; }

    /// <summary>False when the request was rejected at once; <see cref="Completion"/> is then already completed.</summary>
    public bool IsAccepted { get; }

    /// <summary>Completes with the outcome; never faults.</summary>
    public Task<RunResult> Completion { get; }

    /// <summary>Cancels the run: a waiting run is dropped, a running one is stopped and its process tree killed. Nothing happens after the run ended.</summary>
    public void Cancel()
    {
        _cancel?.Invoke();
    }
}

/// <summary>Whether an active run waits for its turn or executes.</summary>
public enum ActiveRunState
{
    /// <summary>Waiting for the run in front of it (concurrency <c>queue</c> or <c>restart</c>).</summary>
    Waiting,

    /// <summary>Executing.</summary>
    Running,
}

/// <summary>A run that has not finished yet.</summary>
/// <param name="RunId">The run id.</param>
/// <param name="CommandId">The command id.</param>
/// <param name="Source">Where the request came from.</param>
/// <param name="State">Waiting or running.</param>
/// <param name="AcceptedAt">When the request was accepted.</param>
/// <param name="ParentRunId">The sequence run that started this one, or null.</param>
public sealed record ActiveRunInfo(
    string RunId,
    string CommandId,
    string Source,
    ActiveRunState State,
    DateTimeOffset AcceptedAt,
    string? ParentRunId);

/// <summary>Settings of <see cref="CommandEngine"/>.</summary>
public sealed class CommandEngineOptions
{
    /// <summary>The default for <see cref="QueueLimit"/>, the same as Home Assistant's <c>max</c> of a script.</summary>
    public const int DefaultRunLimit = 10;

    /// <summary>The default for <see cref="MaxSequenceDepth"/>.</summary>
    public const int DefaultMaxSequenceDepth = 16;

    /// <summary>The most runs that may wait behind a running one in concurrency mode <c>queue</c>; further requests get <c>busy</c>.</summary>
    public int QueueLimit { get; init; } = DefaultRunLimit;

    /// <summary>The most runs of one command that may overlap in concurrency mode <c>parallel</c>; further requests get <c>busy</c>.</summary>
    public int ParallelLimit { get; init; } = DefaultRunLimit;

    /// <summary>The deepest nesting of sequences; deeper calls fail.</summary>
    public int MaxSequenceDepth { get; init; } = DefaultMaxSequenceDepth;

    /// <summary>The agent name for <c>QA_AGENT_NAME</c> and <c>{{qa.agent_name}}</c>; evaluated for every run.</summary>
    public Func<string>? AgentName { get; init; }

    /// <summary>
    /// The local channel (PLAN.md T217): its address and secret are added to the environment of every
    /// process that a command starts, so a script can call <c>qoffeeautomat emit</c>.
    /// </summary>
    public LocalChannel? LocalChannel { get; init; }

    /// <summary>
    /// Receives the live output of the runs (PLAN.md S08 <c>/runs/{id}/stream</c>); null keeps no live output. Output
    /// is streamed for processes that the agent supervises itself; a command with secret variables delivers its
    /// (redacted) output only when it ends, because a secret could be split across two pieces of output.
    /// </summary>
    public RunOutputHub? RunOutput { get; init; }

    /// <summary>How long <see cref="CommandEngine.DisposeAsync"/> waits for cancelled runs to end.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(QueueLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ParallelLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSequenceDepth);
        ArgumentOutOfRangeException.ThrowIfLessThan(ShutdownTimeout, TimeSpan.Zero);
    }
}

/// <summary>Raised when the engine could not complete a bookkeeping step; the run itself is unaffected.</summary>
public sealed class CommandEngineFaultEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="runId">The run concerned.</param>
    /// <param name="stage">What failed: <c>history</c>, <c>event</c> or <c>handler</c>.</param>
    /// <param name="exception">What went wrong.</param>
    public CommandEngineFaultEventArgs(string runId, string stage, Exception exception)
    {
        RunId = runId;
        Stage = stage;
        Exception = exception;
    }

    /// <summary>The run concerned.</summary>
    public string RunId { get; }

    /// <summary>What failed: <c>history</c>, <c>event</c> or <c>handler</c>.</summary>
    public string Stage { get; }

    /// <summary>What went wrong.</summary>
    public Exception Exception { get; }
}
