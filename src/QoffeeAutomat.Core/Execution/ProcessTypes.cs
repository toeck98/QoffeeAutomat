using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Execution;

/// <summary>
/// Starts executables without a shell and supervises them (PLAN.md S06 <c>process</c> action, S09, D-012):
/// arguments are separate argv entries, output is captured with a cap, a timeout or cancellation
/// kills the whole process tree.
/// </summary>
public interface IProcessRunner
{
    /// <summary>Starts a run in the background.</summary>
    /// <param name="request">What to start. Invalid requests (no file name, no timeout) throw; a process that cannot be started is reported in the result.</param>
    /// <param name="cancellationToken">Cancelling it kills the process tree and ends the run with <see cref="ProcessRunResult.Cancelled"/>.</param>
    /// <returns>The run: live output and the eventual result.</returns>
    IProcessRun Start(ProcessLaunchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Runs a process to its end.</summary>
    /// <param name="request">What to start, see <see cref="Start"/>.</param>
    /// <param name="cancellationToken">Cancelling it kills the process tree and ends the run with <see cref="ProcessRunResult.Cancelled"/>.</param>
    /// <returns>How the run ended; never throws for a failing, timed-out or cancelled process.</returns>
    Task<ProcessRunResult> RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A running (or finished) process started by <see cref="IProcessRunner"/>.</summary>
public interface IProcessRun : IAsyncDisposable
{
    /// <summary>
    /// The output as it arrives, for the UI. Only what is also captured appears here, so the 64 KiB cap
    /// per stream applies. The sequence ends when the run ends; it has a single consumer.
    /// </summary>
    IAsyncEnumerable<ProcessOutputChunk> Output { get; }

    /// <summary>Completes with the outcome when the process has ended (or could not be started).</summary>
    Task<ProcessRunResult> Completion { get; }

    /// <summary>Kills the process tree and ends the run with <see cref="ProcessRunResult.Cancelled"/>; nothing happens after the run ended.</summary>
    void Cancel();
}

/// <summary>Which output stream of the process a chunk came from.</summary>
public enum ProcessOutputSource
{
    /// <summary>Standard output.</summary>
    StandardOutput,

    /// <summary>Standard error.</summary>
    StandardError,
}

/// <summary>A piece of output of a running process.</summary>
/// <param name="Source">The stream.</param>
/// <param name="Text">The text, decoded as UTF-8. A chunk is not necessarily a whole line.</param>
/// <param name="Timestamp">When the chunk was read.</param>
public sealed record ProcessOutputChunk(ProcessOutputSource Source, string Text, DateTimeOffset Timestamp);

/// <summary>How a process run ended (PLAN.md T206).</summary>
/// <param name="ExitCode">
/// The exit code of the process. A killed process reports what the operating system reports (for
/// example 137 on Linux); <c>-1</c> when the process could not be started or its code is unknown. A
/// run with <c>WaitForExit = false</c> reports <c>0</c>: the process was started.
/// </param>
/// <param name="StandardOutput">Captured standard output, at most 64 KiB; empty when output is not captured.</param>
/// <param name="StandardError">Captured standard error, at most 64 KiB; empty when output is not captured.</param>
/// <param name="Duration">How long the run took.</param>
/// <param name="TimedOut">The timeout expired and the process tree was killed.</param>
/// <param name="OutputTruncated">More than 64 KiB were written to standard output or error; the excess was dropped.</param>
/// <param name="Cancelled">The run was cancelled and the process tree was killed.</param>
/// <param name="StartError">Why the process could not be started; null when it was started.</param>
/// <param name="ProcessId">The id of the process, when it was started.</param>
public sealed record ProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool TimedOut,
    bool OutputTruncated,
    bool Cancelled = false,
    string? StartError = null,
    int? ProcessId = null)
{
    /// <summary>True when the process was started, ran to its end within the timeout and exited with zero.</summary>
    public bool Succeeded => StartError is null && !TimedOut && !Cancelled && ExitCode == 0;

    /// <summary>Converts the result for the command engine.</summary>
    /// <returns>
    /// A result whose error code is <see cref="ErrorCodes.Timeout"/>, <see cref="ErrorCodes.Cancelled"/>
    /// or, when the process could not be started, <see cref="ErrorCodes.Internal"/>; a process that ran
    /// and exited non-zero has no error code.
    /// </returns>
    public ExecutionResult ToExecutionResult()
    {
        if (StartError is not null)
        {
            return ExecutionResult.Failure(ErrorCodes.Internal, StartError);
        }

        string? errorCode = TimedOut ? ErrorCodes.Timeout : Cancelled ? ErrorCodes.Cancelled : null;
        return new ExecutionResult(ExitCode, StandardOutput, StandardError, errorCode, OutputTruncated);
    }
}
