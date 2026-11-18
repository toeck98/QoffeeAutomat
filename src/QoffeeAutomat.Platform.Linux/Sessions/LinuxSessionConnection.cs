using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Execution.Bash;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Linux.Sessions;

/// <summary>Raised when the session helper reports metric values.</summary>
public sealed class SessionMetricsReceivedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="message">The <c>metrics_state</c> message.</param>
    public SessionMetricsReceivedEventArgs(SessionMetricsStateMessage message)
    {
        Message = message;
    }

    /// <summary>The <c>metrics_state</c> message.</summary>
    public SessionMetricsStateMessage Message { get; }
}

/// <summary>
/// The agent's end of an established connection to a session helper (PLAN.md S11, S17, T803), after the handshake. It sends <c>run</c> messages
/// and hands the <c>run_output</c> and <c>run_result</c> messages to the waiting caller, forwards cancellation, and ends all waiting runs with
/// <see cref="ErrorCodes.NoSession"/> when the connection breaks. Writes are serialised, so frames never interleave. It speaks the session
/// protocol of the Windows SessionHost over a stream; only the kind of the stream differs.
/// </summary>
public sealed partial class LinuxSessionConnection : IDisposable
{
    /// <summary>The default for how long a cancelled run waits for the helper to report that the process tree is gone.</summary>
    public static readonly TimeSpan DefaultCancelGrace = TimeSpan.FromSeconds(15);

    private readonly Stream _stream;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TimeSpan _cancelGrace;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingRun> _runs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopToken;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _runCounter;
    private int _closing;

    /// <summary>Creates the connection; call <see cref="Start"/> to begin reading.</summary>
    /// <param name="stream">The connected stream, after the handshake.</param>
    /// <param name="hello">The <c>hello</c> of the session helper.</param>
    /// <param name="userId">The numeric id of the user that the kernel reported for the helper process.</param>
    /// <param name="time">The clock for the cancellation grace period.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancelGrace">How long to wait for the result of a cancelled run; null uses <see cref="DefaultCancelGrace"/>.</param>
    public LinuxSessionConnection(
        Stream stream,
        SessionHelloMessage hello,
        long userId,
        TimeProvider time,
        ILogger logger,
        TimeSpan? cancelGrace = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(hello);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _stream = stream;
        _time = time;
        _logger = logger;
        _cancelGrace = cancelGrace ?? DefaultCancelGrace;
        _stopToken = _stop.Token;
        Hello = hello;
        UserId = userId;
    }

    /// <summary>Raised when the helper reports metric values. Handlers must return quickly.</summary>
    public event EventHandler<SessionMetricsReceivedEventArgs>? MetricsStateReceived;

    /// <summary>The <c>hello</c> of the session helper.</summary>
    public SessionHelloMessage Hello { get; }

    /// <summary>The numeric user id that the kernel reported for the helper.</summary>
    public long UserId { get; }

    /// <summary>The user of the session, as the helper reports it.</summary>
    public string? UserName => Hello.UserName;

    /// <summary>Completes when the connection ended, whatever the reason.</summary>
    public Task Closed => _closed.Task;

    /// <summary>True until the connection ended.</summary>
    public bool IsOpen => !_closed.Task.IsCompleted;

    /// <summary>Starts reading messages of the helper.</summary>
    public void Start()
    {
        _ = Task.Run(ReadLoopAsync, CancellationToken.None);
    }

    /// <summary>Runs a process in the user's context.</summary>
    /// <param name="request">What to start; <see cref="ProcessLaunchRequest.OnOutput"/> receives the output while it arrives.</param>
    /// <param name="cancellationToken">Cancelling it kills the process tree in the session.</param>
    /// <returns>The outcome; <see cref="ErrorCodes.NoSession"/> when the connection is gone.</returns>
    public ValueTask<ExecutionResult> RunProcessAsync(ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var message = new SessionRunMessage(
            string.Empty,
            SessionRunKinds.Process,
            ToMilliseconds(request.Timeout),
            request.CaptureOutput,
            Process: new SessionProcessSpec(
                request.FileName,
                request.Arguments,
                request.EnvironmentVariables,
                request.WorkingDirectory,
                request.WaitForExit));
        return ExecuteAsync(message, request.OnOutput, cancellationToken);
    }

    /// <summary>Runs a bash script in the user's context; the helper writes the script into its own private directory and starts bash there.</summary>
    /// <param name="invocation">What to run; the source is inline.</param>
    /// <param name="cancellationToken">Cancelling it kills the shell's process tree in the session.</param>
    /// <returns>The outcome; <see cref="ErrorCodes.NoSession"/> when the connection is gone.</returns>
    public ValueTask<ExecutionResult> RunBashAsync(BashInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var message = new SessionRunMessage(
            string.Empty,
            SessionRunKinds.Bash,
            ToMilliseconds(invocation.Timeout),
            invocation.CaptureOutput,
            Bash: new SessionBashSpec(invocation.Source, invocation.Arguments, invocation.Environment, invocation.Code, invocation.Script));
        return ExecuteAsync(message, onOutput: null, cancellationToken);
    }

    /// <summary>Runs a built-in action of the helper.</summary>
    /// <param name="name">The action name.</param>
    /// <param name="variables">The validated variables.</param>
    /// <param name="timeout">How long the action may take.</param>
    /// <param name="cancellationToken">Cancels the action.</param>
    /// <returns>The outcome; <see cref="ErrorCodes.NoSession"/> when the connection is gone.</returns>
    public ValueTask<ExecutionResult> RunBuiltinAsync(
        string name,
        IReadOnlyDictionary<string, object> variables,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(variables);

        SessionBuiltinSpec spec;
        try
        {
            spec = new SessionBuiltinSpec(name, SessionVariable.FromAll(variables));
        }
        catch (ArgumentException exception)
        {
            return ValueTask.FromResult(ExecutionResult.Failure(ErrorCodes.InvalidVars, exception.Message));
        }

        var message = new SessionRunMessage(string.Empty, SessionRunKinds.Builtin, ToMilliseconds(timeout), Builtin: spec);
        return ExecuteAsync(message, onOutput: null, cancellationToken);
    }

    /// <summary>Tells the helper which desktop metrics to sample (replaces the previous subscription).</summary>
    /// <param name="metricIds">The ids; empty stops the sampling.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the message was sent.</returns>
    public Task SubscribeMetricsAsync(IReadOnlyList<string> metricIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metricIds);
        return SendAsync(new SessionMetricsSubscribeMessage(metricIds), cancellationToken);
    }

    /// <summary>Shows a notification in the session.</summary>
    /// <param name="title">The title.</param>
    /// <param name="message">The text.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the message was sent.</returns>
    public Task NotifyAsync(string title, string message, CancellationToken cancellationToken)
    {
        return SendAsync(new SessionNotifyMessage(title, message), cancellationToken);
    }

    /// <summary>Asks the helper to end the conversation and closes the connection; the helper waits for the next agent.</summary>
    /// <param name="reason">One of <see cref="SessionShutdownReasons"/>.</param>
    /// <returns>A task that completes when the connection is closed.</returns>
    public async Task ShutdownAsync(string reason)
    {
        try
        {
            await SendAsync(new SessionShutdownMessage(reason), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsConnectionFailure(exception) || exception is SessionProtocolException)
        {
            // The peer is gone anyway.
        }

        Close();
    }

    /// <summary>Closes the connection; waiting runs end with <see cref="ErrorCodes.NoSession"/>.</summary>
    public void Dispose()
    {
        Close();
        _writeGate.Dispose();
        _stop.Dispose();
    }

    [LoggerMessage(EventId = 3320, Level = LogLevel.Warning, Message = "The connection to the session helper of {User} failed: {Reason}")]
    private static partial void ConnectionFault(ILogger logger, string user, string reason);

    [LoggerMessage(EventId = 3321, Level = LogLevel.Warning, Message = "The session helper of {User} sent a message that the agent does not expect: {Type}")]
    private static partial void UnexpectedMessage(ILogger logger, string user, string type);

    private static long ToMilliseconds(TimeSpan timeout)
    {
        return timeout <= TimeSpan.Zero ? 0 : (long)timeout.TotalMilliseconds;
    }

    private static bool IsConnectionFailure(Exception exception)
    {
        return exception is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException;
    }

    private static ExecutionResult Gone()
    {
        return ExecutionResult.Failure(ErrorCodes.NoSession, "The session helper is not connected any more.");
    }

    private async ValueTask<ExecutionResult> ExecuteAsync(
        SessionRunMessage template,
        Action<ProcessOutputChunk>? onOutput,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new ExecutionResult(-1, string.Empty, string.Empty, ErrorCodes.Cancelled);
        }

        string runId = "r" + Interlocked.Increment(ref _runCounter).ToString(CultureInfo.InvariantCulture);
        var pending = new PendingRun(onOutput);
        _runs[runId] = pending;

        // The connection may have ended between the caller's check and the registration.
        if (!IsOpen)
        {
            _runs.TryRemove(runId, out _);
            return Gone();
        }

        try
        {
            await SendAsync(template with { RunId = runId }, _stopToken).ConfigureAwait(false);
        }
        catch (SessionProtocolException exception)
        {
            _runs.TryRemove(runId, out _);
            return ExecutionResult.Failure(ErrorCodes.Internal, exception.Message);
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            _runs.TryRemove(runId, out _);
            return Gone();
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(() => _ = CancelRunAsync(runId, pending));
        return await pending.Completion.Task.ConfigureAwait(false);
    }

    private async Task CancelRunAsync(string runId, PendingRun pending)
    {
        try
        {
            await SendAsync(new SessionCancelMessage(runId), _stopToken).ConfigureAwait(false);
            await Task.Delay(_cancelGrace, _time, _stopToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsConnectionFailure(exception) || exception is SessionProtocolException)
        {
            // The connection ended, which completes the run with no_session.
            return;
        }

        // The helper did not report the end of the run in time (it hangs, or the process tree cannot be killed).
        if (_runs.TryRemove(runId, out _))
        {
            pending.Completion.TrySetResult(new ExecutionResult(
                -1,
                string.Empty,
                "The session helper did not confirm the cancellation in time.",
                ErrorCodes.Cancelled));
        }
    }

    private async Task SendAsync(SessionMessage message, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SessionFrameCodec.WriteAsync(_stream, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _writeGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Disposed while writing; the connection is closed.
            }
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_stopToken.IsCancellationRequested)
            {
                SessionMessage? message = await SessionFrameCodec.ReadAsync(_stream, _stopToken).ConfigureAwait(false);
                if (message is null || !Dispatch(message))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Closed on purpose.
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or SessionProtocolException)
        {
            ConnectionFault(_logger, UserName ?? "?", exception.Message);
        }
        finally
        {
            Close();
        }
    }

    /// <summary>Handles one message of the helper; false ends the connection.</summary>
    private bool Dispatch(SessionMessage message)
    {
        switch (message)
        {
            case SessionRunOutputMessage output:
                if (_runs.TryGetValue(output.RunId, out PendingRun? running))
                {
                    running.Deliver(output);
                }

                return true;
            case SessionRunResultMessage result:
                if (_runs.TryRemove(result.RunId, out PendingRun? finished))
                {
                    finished.Completion.TrySetResult(new ExecutionResult(
                        result.ExitCode,
                        result.StandardOutput,
                        result.StandardError,
                        result.ErrorCode,
                        result.OutputTruncated));
                }

                return true;
            case SessionMetricsStateMessage metrics:
                MetricsStateReceived?.Invoke(this, new SessionMetricsReceivedEventArgs(metrics));
                return true;
            case SessionMetricOptionsMessage:
            case SessionEventMessage:
                // The helper has no audio devices to announce and no events of its own on Linux (yet).
                return true;
            case SessionShutdownMessage:
                return false;
            default:
                UnexpectedMessage(_logger, UserName ?? "?", message.GetType().Name);
                return true;
        }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return;
        }

        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        _stream.Dispose();
        foreach (string runId in _runs.Keys)
        {
            if (_runs.TryRemove(runId, out PendingRun? pending))
            {
                pending.Completion.TrySetResult(Gone());
            }
        }

        _closed.TrySetResult();
    }

    private sealed class PendingRun
    {
        private readonly Action<ProcessOutputChunk>? _onOutput;

        public PendingRun(Action<ProcessOutputChunk>? onOutput)
        {
            _onOutput = onOutput;
        }

        public TaskCompletionSource<ExecutionResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Deliver(SessionRunOutputMessage message)
        {
            if (_onOutput is null)
            {
                return;
            }

            ProcessOutputSource source = message.Stream == SessionOutputStreams.StandardError
                ? ProcessOutputSource.StandardError
                : ProcessOutputSource.StandardOutput;
            var chunk = new ProcessOutputChunk(source, message.Text, DateTimeOffset.FromUnixTimeMilliseconds(message.Ts));
#pragma warning disable CA1031 // The receiver is a best-effort live view; it must not break the connection.
            try
            {
                _onOutput(chunk);
            }
            catch (Exception)
            {
                // Ignored on purpose.
            }
#pragma warning restore CA1031
        }
    }
}
