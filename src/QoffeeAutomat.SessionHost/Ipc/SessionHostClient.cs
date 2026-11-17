using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.SessionHost.Execution;
using QoffeeAutomat.SessionHost.Metrics;

namespace QoffeeAutomat.SessionHost.Ipc;

/// <summary>Settings of <see cref="SessionHostClient"/>.</summary>
public sealed class SessionHostClientOptions
{
    /// <summary>The Windows session of this process, announced in <c>hello</c>; the agent compares it with what the operating system says.</summary>
    public required int SessionId { get; init; }

    /// <summary>The pipe name.</summary>
    public string PipeName { get; init; } = SessionIpc.PipeName;

    /// <summary>The user of the session, announced in <c>hello</c>.</summary>
    public string? UserName { get; init; }

    /// <summary>The user is an administrator, announced in <c>hello</c>.</summary>
    public bool IsAdministrator { get; init; }

    /// <summary>The product version, announced in <c>hello</c>.</summary>
    public string Version { get; init; } = "0.0.0";

    /// <summary>How long to wait for the pipe to appear and accept the connection.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long the agent may take to answer the <c>hello</c>.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The wait before the first new attempt after the agent could not be reached or the connection was lost.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between two attempts.</summary>
    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The wait before a new attempt when the server on the pipe is not the agent.</summary>
    public TimeSpan UntrustedRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Raised when the agent reports what the tray icon shows.</summary>
public sealed class SessionTrayStateEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="state">The <c>tray_state</c> message.</param>
    public SessionTrayStateEventArgs(SessionTrayStateMessage state)
    {
        State = state;
    }

    /// <summary>The <c>tray_state</c> message.</summary>
    public SessionTrayStateMessage State { get; }
}

/// <summary>Raised when the agent sends the list of global hotkeys (<c>hotkeys</c>, T708).</summary>
public sealed class SessionHotkeysEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="message">The <c>hotkeys</c> message.</param>
    public SessionHotkeysEventArgs(SessionHotkeysMessage message)
    {
        Message = message;
    }

    /// <summary>The <c>hotkeys</c> message.</summary>
    public SessionHotkeysMessage Message { get; }
}

/// <summary>
/// The SessionHost's end of the session pipe (PLAN.md S09 item 7, S11): connects to the agent, makes sure the server on
/// the pipe really is the agent (<see cref="IPipeServerVerifier"/>), exchanges <c>hello</c> and then serves the agent's
/// messages: <c>run</c> (executed by the <see cref="UserCommandExecutor"/>, output streamed as <c>run_output</c>, the end
/// reported as <c>run_result</c>), <c>cancel</c>, <c>metrics_subscribe</c>, <c>notify</c> and <c>shutdown</c>. When the
/// connection breaks, the processes of runs in flight are killed and the client connects again with a backoff, so a
/// restart of the agent does not need a restart of the SessionHost.
/// </summary>
public sealed class SessionHostClient
{
    private readonly SessionHostClientOptions _options;
    private readonly IPipeServerVerifier _verifier;
    private readonly IProcessRunner _runner;
    private readonly IBuiltinActionProvider _builtins;
    private readonly IPowerShellEngineLocator? _locator;
    private readonly TimeProvider _time;
    private readonly ISessionHostLog _log;
    private readonly IUserMetricProbe _metrics;
    private readonly IMetricChangeSource? _changes;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<SessionLoginTokenMessage>> _tokenRequests = new(StringComparer.Ordinal);
    private volatile Func<SessionMessage, Task>? _send;

    /// <summary>Creates the client.</summary>
    /// <param name="options">The settings.</param>
    /// <param name="verifier">Checks that the server is the agent.</param>
    /// <param name="runner">Runs processes.</param>
    /// <param name="builtins">The built-in actions of the session.</param>
    /// <param name="locator">Finds the PowerShell engine; null searches this computer.</param>
    /// <param name="time">The clock.</param>
    /// <param name="log">The diagnostics log.</param>
    /// <param name="metrics">Reads the user metrics that the agent subscribes to; null makes every user metric unavailable.</param>
    /// <param name="changes">The source of the metrics with dynamic options (the audio devices, T713): its options are sent to the agent after the handshake and whenever they change, and the changed metrics are sampled at once; null sends no options.</param>
    public SessionHostClient(
        SessionHostClientOptions options,
        IPipeServerVerifier verifier,
        IProcessRunner runner,
        IBuiltinActionProvider builtins,
        IPowerShellEngineLocator? locator = null,
        TimeProvider? time = null,
        ISessionHostLog? log = null,
        IUserMetricProbe? metrics = null,
        IMetricChangeSource? changes = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(builtins);

        _options = options;
        _verifier = verifier;
        _runner = runner;
        _builtins = builtins;
        _locator = locator;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullSessionHostLog.Instance;
        _metrics = metrics ?? NoUserMetricProbe.Instance;
        _changes = changes;
    }

    private enum Outcome
    {
        /// <summary>The agent asked for the end.</summary>
        Shutdown,

        /// <summary>The connection was established and then lost.</summary>
        Lost,

        /// <summary>The agent could not be reached or did not answer.</summary>
        Unreachable,

        /// <summary>The server on the pipe is not the agent.</summary>
        Untrusted,

        /// <summary>The agent refused this SessionHost or speaks another protocol version.</summary>
        Incompatible,
    }

    /// <summary>Raised after the handshake is complete; handlers must return quickly.</summary>
    public event EventHandler? Connected;

    /// <summary>Raised when an established connection ended, whatever the reason; handlers must return quickly.</summary>
    public event EventHandler? Disconnected;

    /// <summary>Raised when the agent reports what the tray icon shows (<c>tray_state</c>); handlers must return quickly.</summary>
    public event EventHandler<SessionTrayStateEventArgs>? TrayStateReceived;

    /// <summary>Raised when the agent sends the complete list of global hotkeys to register (T708); handlers must return quickly.</summary>
    public event EventHandler<SessionHotkeysEventArgs>? HotkeysReceived;

    /// <summary>Tells the agent which hotkeys could be registered and which conflict (<c>hotkey_state</c>, T708).</summary>
    /// <param name="status">One entry per hotkey of the last list.</param>
    /// <returns>False when there is no connection to the agent, so nothing was sent.</returns>
    public Task<bool> ReportHotkeyStateAsync(IReadOnlyList<SessionHotkeyStatus> status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return TrySendAsync(new SessionHotkeyStateMessage(status));
    }

    /// <summary>Tells the agent that a hotkey was pressed: the <c>event</c> <c>hotkey_pressed</c> with the data <c>name</c> (T708).</summary>
    /// <param name="name">The configured name of the hotkey.</param>
    /// <returns>False when there is no connection to the agent, so nothing was sent.</returns>
    public Task<bool> ReportHotkeyPressedAsync(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteEndObject();
        }

        using JsonDocument data = JsonDocument.Parse(stream.ToArray());
        return TrySendAsync(new SessionEventMessage(SessionHotkeyEvents.Pressed, _time.GetUtcNow().ToUnixTimeMilliseconds(), data.RootElement.Clone()));
    }

    /// <summary>
    /// Tells the agent that the user pressed a button of a notification: the <c>event</c> <c>notification_action</c> with the data
    /// <c>action</c> (the id of the button) and, when the notification had one, <c>tag</c> (T702).
    /// </summary>
    /// <param name="action">The id of the button.</param>
    /// <param name="tag">The tag of the notification, or null.</param>
    /// <returns>False when there is no connection to the agent, so nothing was sent.</returns>
    public Task<bool> ReportNotificationActionAsync(string action, string? tag)
    {
        ArgumentException.ThrowIfNullOrEmpty(action);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("action", action);
            if (!string.IsNullOrEmpty(tag))
            {
                writer.WriteString("tag", tag);
            }

            writer.WriteEndObject();
        }

        using JsonDocument data = JsonDocument.Parse(stream.ToArray());
        return TrySendAsync(new SessionEventMessage(SessionNotificationEvents.Action, _time.GetUtcNow().ToUnixTimeMilliseconds(), data.RootElement.Clone()));
    }

    private async Task<bool> TrySendAsync(SessionMessage message)
    {
        Func<SessionMessage, Task>? send = _send;
        if (send is null)
        {
            return false;
        }

        try
        {
            await send(message).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException or SessionProtocolException)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks the agent to pause or resume the commands (the menu entries of the tray icon). The agent decides and answers with
    /// a <c>tray_state</c>.
    /// </summary>
    /// <param name="paused">The wanted value of the global pause flag.</param>
    /// <returns>False when there is no connection to the agent, so nothing was sent.</returns>
    public async Task<bool> RequestPauseAsync(bool paused)
    {
        Func<SessionMessage, Task>? send = _send;
        if (send is null)
        {
            return false;
        }

        try
        {
            await send(new SessionSetPauseMessage(paused)).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException or SessionProtocolException)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks the agent for a single-use login token for the UI (the tray entry Open UI, T711). The agent decides who gets one: a
    /// standard user never does, whatever this client sends.
    /// </summary>
    /// <param name="timeout">How long to wait for the answer.</param>
    /// <returns>The token; null when there is no connection, the agent refused, or it did not answer in time.</returns>
    public async Task<string?> RequestLoginTokenAsync(TimeSpan timeout)
    {
        Func<SessionMessage, Task>? send = _send;
        if (send is null)
        {
            return null;
        }

        string requestId = Guid.NewGuid().ToString("N");
        var pending = new TaskCompletionSource<SessionLoginTokenMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tokenRequests[requestId] = pending;
        try
        {
            await send(new SessionLoginTokenRequestMessage(requestId)).ConfigureAwait(false);
            using var expiry = new CancellationTokenSource(timeout, _time);
            SessionLoginTokenMessage answer = await pending.Task.WaitAsync(expiry.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(answer.Token))
            {
                _log.Info("No login token was issued (" + (answer.Reason ?? "no reason given") + "); opening the login page.");
                return null;
            }

            return answer.Token;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException or SessionProtocolException)
        {
            return null;
        }
        finally
        {
            _tokenRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>Connects and serves until the agent asks for the end, the token is cancelled or the peers are incompatible.</summary>
    /// <param name="cancellationToken">Ends the client; processes of runs in flight are killed.</param>
    /// <returns>A <see cref="SessionHostExitCodes"/> value.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay = _options.ReconnectDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            Outcome outcome;
            try
            {
                outcome = await ConnectAndServeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return SessionHostExitCodes.Ok;
            }

            switch (outcome)
            {
                case Outcome.Shutdown:
                    return SessionHostExitCodes.Ok;
                case Outcome.Incompatible:
                    return SessionHostExitCodes.Rejected;
                case Outcome.Lost:
                    delay = _options.ReconnectDelay;
                    break;
                case Outcome.Untrusted:
                    delay = _options.UntrustedRetryDelay;
                    break;
                default:
                    break;
            }

            try
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return SessionHostExitCodes.Ok;
            }

            if (outcome == Outcome.Unreachable)
            {
                delay = TimeSpan.FromMilliseconds(Math.Min(_options.MaxReconnectDelay.TotalMilliseconds, delay.TotalMilliseconds * 2));
            }
        }

        return SessionHostExitCodes.Ok;
    }

    private async Task<Outcome> ConnectAndServeAsync(CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            _options.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync((int)_options.ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return Outcome.Unreachable;
        }

        string? untrusted = _verifier.Verify(pipe);
        if (untrusted is not null)
        {
            _log.Warning("Not connecting: " + untrusted);
            return Outcome.Untrusted;
        }

        SessionHelloMessage? agent;
        try
        {
            agent = await HandshakeAsync(pipe, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SessionProtocolException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _log.Warning("The handshake with the agent failed: " + exception.Message);
            return Outcome.Unreachable;
        }

        if (agent is null)
        {
            return Outcome.Incompatible;
        }

        _log.Info("Connected to the agent.");
        Connected?.Invoke(this, EventArgs.Empty);
        Outcome result = await ServeAsync(pipe, agent, cancellationToken).ConfigureAwait(false);
        _log.Info("The connection to the agent ended.");
        Disconnected?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>Sends <c>hello</c> and reads the agent's; null when the agent refuses or speaks another protocol.</summary>
    private async Task<SessionHelloMessage?> HandshakeAsync(Stream pipe, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.HandshakeTimeout);

        var hello = new SessionHelloMessage(
            SessionIpc.ProtocolVersion,
            SessionRoles.SessionHost,
            Environment.ProcessId,
            _options.SessionId,
            _options.Version,
            _options.UserName,
            _options.IsAdministrator);
        await SessionFrameCodec.WriteAsync(pipe, hello, timeout.Token).ConfigureAwait(false);

        SessionMessage? answer = await SessionFrameCodec.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
        switch (answer)
        {
            case null:
                // The pipe was closed under us, for example because the agent stops: try again later.
                throw new IOException("The agent closed the pipe during the handshake.");
            case SessionHelloMessage agent when agent.Role == SessionRoles.Agent && agent.ProtocolVersion == SessionIpc.ProtocolVersion:
                return agent;
            case SessionHelloMessage agent:
                _log.Warning($"The agent is '{agent.Role}' and speaks protocol {agent.ProtocolVersion}; this session helper speaks {SessionIpc.ProtocolVersion}.");
                return null;
            case SessionShutdownMessage shutdown:
                _log.Warning("The agent refused this session helper: " + shutdown.Reason);
                return null;
            default:
                _log.Warning("The agent did not answer with hello.");
                return null;
        }
    }

    private async Task<Outcome> ServeAsync(Stream pipe, SessionHelloMessage agent, CancellationToken cancellationToken)
    {
        using var writeGate = new SemaphoreSlim(1, 1);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var active = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var runs = new List<Task>();

        var powerShell = new PowerShellRunner(_locator, new PowerShellOptions { ScriptsDirectory = agent.ScriptsDirectory }, _time);
        var executor = new UserCommandExecutor(_runner, powerShell, _builtins, _time);

        // The user metrics are sampled only while the agent is subscribed (D-007); every connection starts without subscription.
        using var sampler = new UserMetricSampler(_metrics, _time);

        async Task SendAsync(SessionMessage message)
        {
            await writeGate.WaitAsync(stop.Token).ConfigureAwait(false);
            try
            {
                await SessionFrameCodec.WriteAsync(pipe, message, stop.Token).ConfigureAwait(false);
            }
            finally
            {
                writeGate.Release();
            }
        }

        _send = SendAsync;

        async Task ExecuteRunAsync(SessionRunMessage run, CancellationTokenSource runCancellation)
        {
            try
            {
                SessionRunResultMessage result = await executor.ExecuteAsync(
                    run,
                    chunk => new ValueTask(SendAsync(new SessionRunOutputMessage(
                        run.RunId,
                        chunk.Source == ProcessOutputSource.StandardError ? SessionOutputStreams.StandardError : SessionOutputStreams.StandardOutput,
                        chunk.Text,
                        chunk.Timestamp.ToUnixTimeMilliseconds()))),
                    runCancellation.Token).ConfigureAwait(false);
                await SendAsync(result).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException or SessionProtocolException)
            {
                // The connection is gone; the read loop ends the connection and the runs.
            }
            finally
            {
                active.TryRemove(run.RunId, out _);
                runCancellation.Dispose();
            }
        }

        async Task PublishMetricsAsync()
        {
            try
            {
                await sampler.RunAsync(state => SendAsync(state), stop.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException or SessionProtocolException)
            {
                // The connection is gone; the read loop ends the connection.
            }
        }

        Task metricsLoop = Task.Run(PublishMetricsAsync, CancellationToken.None);

        // The metrics with dynamic options (the audio devices, T713). The options go to the agent right after the handshake and
        // again whenever they change; the metrics that changed are sampled at once, so that the value of a select in Home Assistant
        // follows a plugged-in device without waiting for the interval. The gate serialises the changes, and every message is
        // built from what is current when its turn comes, so a slow one cannot deliver an older list after a newer one.
        using var changeGate = new SemaphoreSlim(1, 1);

        async Task PublishChangeAsync(MetricChangeNotice notice)
        {
            try
            {
                await changeGate.WaitAsync(stop.Token).ConfigureAwait(false);
                try
                {
                    if (notice.OptionsChanged && _changes is not null)
                    {
                        await SendAsync(new SessionMetricOptionsMessage(_changes.CurrentOptions())).ConfigureAwait(false);
                    }

                    SessionMetricsStateMessage? values = sampler.Resample(notice.MetricIds);
                    if (values is not null)
                    {
                        await SendAsync(values).ConfigureAwait(false);
                    }
                }
                finally
                {
                    changeGate.Release();
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException or SessionProtocolException)
            {
                // The connection is gone; the read loop ends the connection.
            }
        }

        IDisposable? watching = null;
        Task? initialOptions = null;
        if (_changes is not null)
        {
            watching = _changes.Watch(notice => _ = Task.Run(() => PublishChangeAsync(notice), CancellationToken.None));
            initialOptions = Task.Run(() => PublishChangeAsync(new MetricChangeNotice([], OptionsChanged: true)), CancellationToken.None);
        }

        try
        {
            while (true)
            {
                SessionMessage? message;
                try
                {
                    message = await SessionFrameCodec.ReadAsync(pipe, stop.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or SessionProtocolException or ObjectDisposedException)
                {
                    _log.Warning("The connection to the agent broke: " + exception.Message);
                    return Outcome.Lost;
                }

                switch (message)
                {
                    case null:
                        return Outcome.Lost;
                    case SessionRunMessage request:
                        var runSource = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        active[request.RunId] = runSource;
                        runs.RemoveAll(task => task.IsCompleted);
                        runs.Add(ExecuteRunAsync(request, runSource));
                        break;
                    case SessionCancelMessage cancel:
                        if (active.TryGetValue(cancel.RunId, out CancellationTokenSource? target))
                        {
                            Cancel(target);
                        }

                        break;
                    case SessionMetricsSubscribeMessage subscription:
                        // Replaces the subscription: the first values follow at once, the sampler delivers the rest at the
                        // interval of each metric; an empty list stops the sampling.
                        SessionMetricsStateMessage? first = sampler.Subscribe(subscription.MetricIds);
                        if (first is not null)
                        {
                            await SendAsync(first).ConfigureAwait(false);
                        }

                        break;
                    case SessionNotifyMessage:
                        _log.Info("A notification arrived; notifications are not shown yet.");
                        break;
                    case SessionTrayStateMessage trayState:
                        TrayStateReceived?.Invoke(this, new SessionTrayStateEventArgs(trayState));
                        break;
                    case SessionHotkeysMessage hotkeys:
                        HotkeysReceived?.Invoke(this, new SessionHotkeysEventArgs(hotkeys));
                        break;
                    case SessionLoginTokenMessage tokenAnswer:
                        if (_tokenRequests.TryGetValue(tokenAnswer.RequestId, out TaskCompletionSource<SessionLoginTokenMessage>? waiting))
                        {
                            waiting.TrySetResult(tokenAnswer);
                        }

                        break;
                    case SessionShutdownMessage:
                        return Outcome.Shutdown;
                    default:
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return Outcome.Lost;
        }
        finally
        {
            // Whatever ended the connection: nothing can be sent any more, and the runs in flight lose their reader, so their
            // process trees are killed.
            _send = null;
            watching?.Dispose();
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(runs).ConfigureAwait(false);
            await metricsLoop.ConfigureAwait(false);
            if (initialOptions is not null)
            {
                await initialOptions.ConfigureAwait(false);
            }
        }
    }

    private static void Cancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run ended already.
        }
    }
}
