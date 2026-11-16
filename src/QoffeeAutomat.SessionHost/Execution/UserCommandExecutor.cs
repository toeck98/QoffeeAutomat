using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.SessionHost.Execution;

/// <summary>
/// Executes the <c>run</c> messages of the agent in the session of the user (PLAN.md S06, S11, T303): a <c>process</c> through
/// the <see cref="IProcessRunner"/>, a <c>powershell</c> script through the <see cref="PowerShellRunner"/> with the same
/// wrapper code that the agent uses (variables bound as named parameters, never spliced into the code, D-012), and a
/// <c>builtin</c> action through the <see cref="IBuiltinActionProvider"/> of the SessionHost. The run ends with a
/// <c>run_result</c> in the common shape: exit code, captured output, an error code for work that could not be started,
/// timed out or was cancelled. Cancelling the token kills the whole process tree.
/// </summary>
public sealed class UserCommandExecutor
{
    private readonly IProcessRunner _runner;
    private readonly PowerShellRunner _powerShell;
    private readonly IBuiltinActionProvider _builtins;
    private readonly TimeProvider _time;

    /// <summary>Creates the executor.</summary>
    /// <param name="runner">Runs processes.</param>
    /// <param name="powerShell">Runs PowerShell through the wrapper; its options name the scripts directory announced by the agent.</param>
    /// <param name="builtins">The built-in actions of the session.</param>
    /// <param name="time">The clock for the duration.</param>
    public UserCommandExecutor(IProcessRunner runner, PowerShellRunner powerShell, IBuiltinActionProvider builtins, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(powerShell);
        ArgumentNullException.ThrowIfNull(builtins);

        _runner = runner;
        _powerShell = powerShell;
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
#pragma warning disable CA1031 // A run that fails in an unexpected way is reported to the agent; it must not end the SessionHost.
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
            case SessionRunKinds.PowerShell when run.PowerShell is { } powerShell:
                return RunPowerShellAsync(run, powerShell, output, cancellationToken);
            case SessionRunKinds.Builtin when run.Builtin is { } builtin:
                return RunBuiltinAsync(run, builtin, cancellationToken);
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
        return new LocalProcessLauncher(_runner, output).LaunchAsync(RunAsValues.User, request, cancellationToken);
    }

    private ValueTask<ExecutionResult> RunPowerShellAsync(
        SessionRunMessage run,
        SessionPowerShellSpec spec,
        Func<ProcessOutputChunk, ValueTask>? output,
        CancellationToken cancellationToken)
    {
        TimeSpan timeout = ToTimeout(run.TimeoutMs);
        if (timeout <= TimeSpan.Zero)
        {
            return ValueTask.FromResult(ExecutionResult.Failure(ErrorCodes.BadRequest, "A PowerShell run needs a positive timeout."));
        }

        var invocation = new PowerShellInvocation(
            spec.Source,
            spec.Code,
            spec.Script,
            spec.Engine,
            SessionVariable.ToAll(spec.Variables),
            spec.EnvironmentVariables,
            timeout,
            RunAsValues.User,
            run.CaptureOutput);
        return _powerShell.RunAsync(invocation, new LocalProcessLauncher(_runner, output), cancellationToken);
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

/// <summary>
/// The built-in actions of the SessionHost until T306 adds the Windows actions (audio, desktop, launching programs): it
/// offers none, so every <c>builtin</c> run ends with <see cref="ErrorCodes.BadRequest"/>.
/// </summary>
public sealed class NoBuiltinActions : IBuiltinActionProvider
{
    /// <inheritdoc />
    public IReadOnlyList<BuiltinActionDescriptor> Actions => [];

    /// <inheritdoc />
    public ValueTask<ExecutionResult> ExecuteAsync(
        string name,
        IReadOnlyDictionary<string, object> variables,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(ExecutionResult.Failure(ErrorCodes.BadRequest, $"Unknown built-in action '{name}'."));
    }
}
