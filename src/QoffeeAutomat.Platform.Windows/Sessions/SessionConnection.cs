using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Windows.Sessions;

/// <summary>Raised when the SessionHost reports metric values.</summary>
public sealed class SessionMetricsEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="message">The <c>metrics_state</c> message.</param>
    public SessionMetricsEventArgs(SessionMetricsStateMessage message)
    {
        Message = message;
    }

    /// <summary>The <c>metrics_state</c> message.</summary>
    public SessionMetricsStateMessage Message { get; }
}

/// <summary>Raised when the SessionHost reports that something happened in the session.</summary>
public sealed class SessionHostEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="message">The <c>event</c> message.</param>
    public SessionHostEventArgs(SessionEventMessage message)
    {
        Message = message;
    }

    /// <summary>The <c>event</c> message.</summary>
    public SessionEventMessage Message { get; }
}

/// <summary>Raised when the SessionHost asks to pause or resume the commands of the agent.</summary>
public sealed class SessionPauseRequestedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="paused">The wanted value of the global pause flag.</param>
    public SessionPauseRequestedEventArgs(bool paused)
    {
        Paused = paused;
    }

    /// <summary>The wanted value of the global pause flag.</summary>
    public bool Paused { get; }
}

/// <summary>
/// The agent's end of an established session pipe: the SessionHost of one session after its handshake. It sends
/// <c>run</c> messages and delivers the <c>run_output</c> and <c>run_result</c> messages to the waiting caller,
/// forwards cancellation (<c>cancel</c>) and ends all waiting runs with <see cref="ErrorCodes.NoSession"/> when the
/// pipe breaks. Writes are serialised so frames never interleave.
/// </summary>
public sealed class SessionConnection : IDisposable
{
    /// <summary>The default for how long a cancelled run waits for the session helper to report that the process tree is gone.</summary>
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
    /// <param name="stream">The connected pipe, after the handshake.</param>
    /// <param name="hello">The <c>hello</c> of the SessionHost.</param>
    /// <param name="peer">The verified client process.</param>
    /// <param name="time">The clock for the cancellation grace period.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancelGrace">How long to wait for the result of a cancelled run; null uses <see cref="DefaultCancelGrace"/>.</param>
    public SessionConnection(
        Stream stream,
        SessionHelloMessage hello,
        PipePeer peer,
        TimeProvider time,
        ILogger logger,
        TimeSpan? cancelGrace = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(hello);
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _stream = stream;
        _time = time;
        _logger = logger;
        _cancelGrace = cancelGrace ?? DefaultCancelGrace;
        _stopToken = _stop.Token;
        Hello = hello;
        SessionId = peer.SessionId;
        ProcessId = peer.ProcessId;
    }

    /// <summary>Raised when the SessionHost reports metric values. Handlers must return quickly.</summary>
    public event EventHandler<SessionMetricsEventArgs>? MetricsStateReceived;

    /// <summary>Raised when the SessionHost reports an event of the session. Handlers must return quickly.</summary>
    public event EventHandler<SessionHostEventArgs>? EventReceived;

    /// <summary>Raised when the user chose Pause commands or Resume commands in the tray menu (T310). Handlers must return quickly.</summary>
    public event EventHandler<SessionPauseRequestedEventArgs>? PauseRequested;

    /// <summary>The <c>hello</c> of the SessionHost.</summary>
    public SessionHelloMessage Hello { get; }

    /// <summary>The Windows session, as reported by the operating system for the client process.</summary>
    public int SessionId { get; }

    /// <summary>The process id of the SessionHost, as reported by the operating system.</summary>
    public int ProcessId { get; }

    /// <summary>The user of the session, as the SessionHost reports it.</summary>
    public string? UserName => Hello.UserName;

    /// <summary>The user is an administrator.</summary>
    public bool IsAdministrator => Hello.IsAdministrator;

    /// <summary>Completes when the connection ended, whatever the reason.</summary>
    public Task Closed => _closed.Task;

    /// <summary>True until the connection ended.</summary>
    public bool IsOpen => !_closed.Task.IsCompleted;

    /// <summary>Starts reading messages of the SessionHost.</summary>
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

    /// <summary>Runs PowerShell in the user's context; the SessionHost finds the engine and prepares the wrapper.</summary>
    /// <param name="invocation">What to run.</param>
    /// <param name="cancellationToken">Cancelling it kills the engine's process tree in the session.</param>
    /// <returns>The outcome; <see cref="ErrorCodes.NoSession"/> when the connection is gone.</returns>
    public ValueTask<ExecutionResult> RunPowerShellAsync(PowerShellInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        SessionPowerShellSpec spec;
        try
        {
            spec = new SessionPowerShellSpec(
                invocation.Source,
                invocation.Engine,
                SessionVariable.FromAll(invocation.Variables),
                invocation.Environment,
                invocation.Code,
                invocation.Script);
        }
        catch (ArgumentException exception)
        {
            return ValueTask.FromResult(ExecutionResult.Failure(ErrorCodes.InvalidVars, exception.Message));
        }

        var message = new SessionRunMessage(
            string.Empty,
            SessionRunKinds.PowerShell,
            ToMilliseconds(invocation.Timeout),
            invocation.CaptureOutput,
            PowerShell: spec);
        return ExecuteAsync(message, onOutput: null, cancellationToken);
    }

    /// <summary>Runs a built-in action of the SessionHost.</summary>
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

    /// <summary>Tells the SessionHost which user metrics to sample (replaces the previous subscription).</summary>
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

    /// <summary>Tells the SessionHost what its tray icon shows (T310).</summary>
    /// <param name="state">The state.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the message was sent.</returns>
    public Task SendTrayStateAsync(SessionTrayStateMessage state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        return SendAsync(state, cancellationToken);
    }

    /// <summary>Asks the SessionHost to end and closes the connection.</summary>
    /// <param name="reason">One of <see cref="SessionShutdownReasons"/>.</param>
    /// <returns>A task that completes when the connection is closed.</returns>
    public async Task ShutdownAsync(string reason)
    {
        try
        {
            await SendAsync(new SessionShutdownMessage(reason), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            // The peer is gone anyway.
        }

        await DrainAsync().ConfigureAwait(false);
        Close();
    }

    /// <summary>
    /// Closing a pipe can throw away what the peer has not read yet. On Windows the server waits (briefly) until the
    /// peer has read the last message, so that <c>shutdown</c> is not lost.
    /// </summary>
    private async Task DrainAsync()
    {
        if (_stream is not PipeStream pipe || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            await Task.Run(pipe.WaitForPipeDrain, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2), _time).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The peer does not read, or is gone: close anyway.
        }
    }

    /// <summary>Closes the connection; waiting runs end with <see cref="ErrorCodes.NoSession"/>.</summary>
    public void Dispose()
    {
        Close();
        _writeGate.Dispose();
        _stop.Dispose();
    }

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
                if (message is null)
                {
                    return;
                }

                if (!Dispatch(message))
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
            SessionLog.ConnectionFault(_logger, SessionId, exception.Message);
        }
        finally
        {
            Close();
        }
    }

    /// <summary>Handles one message of the SessionHost; false ends the connection.</summary>
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
                MetricsStateReceived?.Invoke(this, new SessionMetricsEventArgs(metrics));
                return true;
            case SessionEventMessage sessionEvent:
                EventReceived?.Invoke(this, new SessionHostEventArgs(sessionEvent));
                return true;
            case SessionSetPauseMessage setPause:
                PauseRequested?.Invoke(this, new SessionPauseRequestedEventArgs(setPause.Paused));
                return true;
            case SessionShutdownMessage:
                return false;
            default:
                SessionLog.UnexpectedMessage(_logger, SessionId, message.GetType().Name);
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
