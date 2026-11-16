using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.SessionHost.Execution;

/// <summary>
/// The <see cref="IProcessLauncher"/> inside the SessionHost: every process starts right here, in the session of the
/// user and with the token of the SessionHost, whatever <c>runAs</c> says (the agent chose this session by sending the run
/// to it). It is the counterpart of the routing launcher of the agent and shares the <see cref="IProcessRunner"/> with it, so
/// arguments stay separate entries, output is capped, and a timeout or cancellation kills the whole process tree.
/// </summary>
public sealed class LocalProcessLauncher : IProcessLauncher
{
    private readonly IProcessRunner _runner;
    private readonly Func<ProcessOutputChunk, ValueTask>? _output;

    /// <summary>Creates the launcher.</summary>
    /// <param name="runner">Runs the processes.</param>
    /// <param name="output">Receives the output while the process runs, in order; null captures it for the result only.</param>
    public LocalProcessLauncher(IProcessRunner runner, Func<ProcessOutputChunk, ValueTask>? output = null)
    {
        ArgumentNullException.ThrowIfNull(runner);

        _runner = runner;
        _output = output;
    }

    /// <inheritdoc />
    public async ValueTask<ExecutionResult> LaunchAsync(string runAs, ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_output is null || !request.WaitForExit || !request.CaptureOutput)
        {
            ProcessRunResult plain = await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            return plain.ToExecutionResult();
        }

        await using IProcessRun run = _runner.Start(request, cancellationToken);
        Task pump = PumpAsync(run, _output);
        ProcessRunResult result = await run.Completion.ConfigureAwait(false);
        await pump.ConfigureAwait(false);
        return result.ToExecutionResult();
    }

    private static async Task PumpAsync(IProcessRun run, Func<ProcessOutputChunk, ValueTask> output)
    {
        bool deliver = true;
        await foreach (ProcessOutputChunk chunk in run.Output.ConfigureAwait(false))
        {
            if (!deliver)
            {
                continue;
            }

            try
            {
                await output(chunk).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
                // The agent is gone. The process is supervised on, and the result goes nowhere either; keep draining.
                deliver = false;
            }
        }
    }
}
