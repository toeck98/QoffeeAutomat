using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Execution.Bash;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Linux.Sessions;

/// <summary>
/// The <see cref="IProcessLauncher"/> inside the session helper: every process starts right here, in the session of the user and with the
/// rights of the helper, whatever <c>runAs</c> says (the agent chose this session by sending the run to it). It is the counterpart of the
/// routing launcher of the agent and shares the <see cref="IProcessRunner"/> with it, so arguments stay separate entries, output is capped,
/// and a timeout or cancellation kills the whole process tree.
/// </summary>
public sealed class LinuxLocalProcessLauncher : IProcessLauncher
{
    private readonly IProcessRunner _runner;
    private readonly Func<ProcessOutputChunk, ValueTask>? _output;

    /// <summary>Creates the launcher.</summary>
    /// <param name="runner">Runs the processes.</param>
    /// <param name="output">Receives the output while the process runs, in order; null captures it for the result only.</param>
    public LinuxLocalProcessLauncher(IProcessRunner runner, Func<ProcessOutputChunk, ValueTask>? output = null)
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

/// <summary>
/// Executes the <c>run</c> messages of the agent in the session of the user (PLAN.md S06, S11, S17, T803): a <c>process</c> through the
/// <see cref="IProcessRunner"/>, a <c>bash</c> script through the <see cref="BashRunner"/> (the values arrive as environment variables and
/// positional arguments, never in the script, D-012), and a <c>builtin</c> action through the <see cref="IBuiltinActionProvider"/> of the
/// helper. The run ends with a <c>run_result</c> in the common shape: exit code, captured output, an error code for work that could not be
/// started, timed out or was cancelled. Cancelling the token kills the whole process tree. A <c>powershell</c> run is refused: it is a
/// Windows action type.
/// </summary>
public sealed class LinuxUserCommandExecutor
{
    private readonly IProcessRunner _runner;
    private readonly BashRunner _bash;
    private readonly IBuiltinActionProvider _builtins;
    private readonly TimeProvider _time;

    /// <summary>Creates the executor.</summary>
    /// <param name="runner">Runs processes.</param>
    /// <param name="bash">Runs bash; it has no user-session seam of its own, because it already is in the session.</param>
    /// <param name="builtins">The built-in actions of the session.</param>
    /// <param name="time">The clock for the duration.</param>
    public LinuxUserCommandExecutor(IProcessRunner runner, BashRunner bash, IBuiltinActionProvider builtins, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(bash);
        ArgumentNullException.ThrowIfNull(builtins);

        _runner = runner;
        _bash = bash;
        _builtins = builtins;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Executes a run.</summary>
    /// <param name="run">The <c>run</c> message.</param>
    /// <param name="output">Receives the output of processes while they run, in order; null delivers it with the result only.</param>
    /// <param name="cancellationToken">Cancelling it stops the run and kills the process tree; the result then carries the error <c>cancelled</c>.</param>
    /// <returns>The <c>run_result</c> message; never throws for a failing, timed-out or cancelled run.</returns>
    public async Task<SessionRunResultMessage> ExecuteAsync(
        SessionRunMessage run,
        Func<ProcessOutputChunk, ValueTask>? output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        DateTimeOffset started = _time.GetUtcNow();
        ExecutionResult result;
#pragma warning disable CA1031 // A run that fails in an unexpected way is reported to the agent; it must not end the helper.
        try
        {
            result = await DispatchAsync(run, output, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new ExecutionResult(-1, string.Empty, string.Empty, ErrorCodes.Cancelled);
        }
        catch (ArgumentException exception)
        {
            result = ExecutionResult.Failure(ErrorCodes.BadRequest, exception.Message);
        }
        catch (FormatException exception)
        {
            result = ExecutionResult.Failure(ErrorCodes.InvalidVars, exception.Message);
        }
        catch (Exception exception)
        {
            result = ExecutionResult.Failure(ErrorCodes.Internal, exception.Message);
        }
#pragma warning restore CA1031

        TimeSpan elapsed = _time.GetUtcNow() - started;
        return new SessionRunResultMessage(
            run.RunId,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            elapsed < TimeSpan.Zero ? 0 : (long)elapsed.TotalMilliseconds,
            result.ErrorCode,
            result.OutputTruncated);
    }

    private static TimeSpan ToTimeout(long milliseconds)
    {
        return TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
    }

    private ValueTask<ExecutionResult> DispatchAsync(
        SessionRunMessage run,
        Func<ProcessOutputChunk, ValueTask>? output,
        CancellationToken cancellationToken)
    {
        switch (run.Kind)
        {
            case SessionRunKinds.Process when run.Process is { } process:
                return RunProcessAsync(run, process, output, cancellationToken);
            case SessionRunKinds.Bash when run.Bash is { } bash:
                return RunBashAsync(run, bash, output, cancellationToken);
            case SessionRunKinds.Builtin when run.Builtin is { } builtin:
                return RunBuiltinAsync(run, builtin, cancellationToken);
            case SessionRunKinds.PowerShell:
                return ValueTask.FromResult(ExecutionResult.Failure(
                    ErrorCodes.BadRequest,
                    PlatformActionSupport.Explain(PlatformActionSupport.LinuxOs, ActionTypes.PowerShell)));
            default:
                return ValueTask.FromResult(ExecutionResult.Failure(
                    ErrorCodes.BadRequest,
                    $"The run kind '{run.Kind}' is unknown or its description is missing."));
        }
    }

    private ValueTask<ExecutionResult> RunProcessAsync(
        SessionRunMessage run,
        SessionProcessSpec spec,
        Func<ProcessOutputChunk, ValueTask>? output,
        CancellationToken cancellationToken)
    {
        TimeSpan timeout = ToTimeout(run.TimeoutMs);
        if (spec.WaitForExit && timeout <= TimeSpan.Zero)
        {
            return ValueTask.FromResult(ExecutionResult.Failure(ErrorCodes.BadRequest, "A process that is awaited needs a positive timeout."));
        }

        var request = new ProcessLaunchRequest(
            spec.FileName,
            spec.Arguments,
            spec.WorkingDirectory,
            spec.EnvironmentVariables,
            timeout,
            spec.WaitForExit,
            run.CaptureOutput);
        return new LinuxLocalProcessLauncher(_runner, output).LaunchAsync(RunAsValues.User, request, cancellationToken);
    }

    private ValueTask<ExecutionResult> RunBashAsync(
        SessionRunMessage run,
        SessionBashSpec spec,
        Func<ProcessOutputChunk, ValueTask>? output,
        CancellationToken cancellationToken)
    {
        TimeSpan timeout = ToTimeout(run.TimeoutMs);
        if (timeout <= TimeSpan.Zero)
        {
            return ValueTask.FromResult(ExecutionResult.Failure(ErrorCodes.BadRequest, "A bash run needs a positive timeout."));
        }

        var invocation = new BashInvocation(
            spec.Source,
            spec.Code,
            spec.Script,
            spec.Arguments,
            spec.EnvironmentVariables,
            timeout,
            RunAsValues.User,
            run.CaptureOutput);
        return _bash.RunAsync(invocation, new LinuxLocalProcessLauncher(_runner, output), cancellationToken);
    }

    private async ValueTask<ExecutionResult> RunBuiltinAsync(SessionRunMessage run, SessionBuiltinSpec spec, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, object> variables = SessionVariable.ToAll(spec.Variables);
        TimeSpan timeout = ToTimeout(run.TimeoutMs);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero)
        {
            limit.CancelAfter(timeout);
        }

        try
        {
            return await _builtins.ExecuteAsync(spec.Name, variables, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ExecutionResult.Failure(ErrorCodes.Timeout, $"The action '{spec.Name}' did not finish in time.");
        }
    }
}
