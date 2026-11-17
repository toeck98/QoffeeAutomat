using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Agent.Link.Outbound;

/// <summary>Where an outbound link is.</summary>
public enum OutboundLinkState
{
    /// <summary>Not started, or stopped.</summary>
    Stopped,

    /// <summary>Started, but there is nothing to dial: the link is off or has no credential.</summary>
    Idle,

    /// <summary>A dial attempt is in progress.</summary>
    Connecting,

    /// <summary>The WebSocket is open and the session runs.</summary>
    Connected,

    /// <summary>The last attempt failed or the connection ended; the connector waits before it dials again.</summary>
    Waiting,

    /// <summary>The peer rejected the credential. The connector does not dial until it is told that the configuration changed.</summary>
    Rejected,
}

/// <summary>Arguments of <see cref="OutboundConnector.StateChanged"/>.</summary>
/// <param name="State">The new state.</param>
/// <param name="Detail">Why the link is in this state (for example the reason of a failed attempt); never contains a credential.</param>
public sealed record OutboundStateChange(OutboundLinkState State, string? Detail);

/// <summary>
/// The dial-out loop of an outbound link (ADR 0003). It is the part that the reverse link to Home Assistant (T710) and the hub link
/// (T905) have in common: it asks a resolver for the current endpoint, dials it through an <see cref="IOutboundWebSocketConnector"/>,
/// hands the open socket to a session handler (which speaks QoffeeLink until the connection ends), and dials again with the
/// <see cref="OutboundBackoff"/> of the specification. A credential that the peer rejects ends the dialling: the state becomes
/// <see cref="OutboundLinkState.Rejected"/> until <see cref="Reconfigure"/> is called.
/// </summary>
public sealed class OutboundConnector : IAsyncDisposable
{
    private readonly Func<CancellationToken, ValueTask<OutboundEndpoint?>> _resolve;
    private readonly IOutboundWebSocketConnector _transport;
    private readonly Func<WebSocket, CancellationToken, Task<int?>> _session;
    private readonly OutboundBackoff _backoff;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();

    private TaskCompletionSource _wake = NewWake();
    private CancellationTokenSource? _current;
    private Task? _loop;
    private OutboundLinkState _state = OutboundLinkState.Stopped;
    private string? _detail;
    private bool _reconfigured;

    /// <summary>Creates the connector; call <see cref="Start"/> to begin dialling.</summary>
    /// <param name="resolve">Returns the endpoint to dial, or null when there is nothing to dial (the link is off); asked before every attempt, so a changed configuration is picked up.</param>
    /// <param name="transport">Opens the WebSocket.</param>
    /// <param name="session">Runs the QoffeeLink session on an open socket until it ends and returns the close code that the peer sent, if any.</param>
    /// <param name="options">Timings.</param>
    /// <param name="time">The clock for the delays.</param>
    /// <param name="logger">The logger.</param>
    public OutboundConnector(
        Func<CancellationToken, ValueTask<OutboundEndpoint?>> resolve,
        IOutboundWebSocketConnector transport,
        Func<WebSocket, CancellationToken, Task<int?>> session,
        OutboundOptions options,
        TimeProvider time,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();

        _resolve = resolve;
        _transport = transport;
        _session = session;
        _backoff = new OutboundBackoff(options.BackoffMin, options.BackoffMax, options.Random);
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised when <see cref="State"/> changes, on the thread of the connector loop.</summary>
    public event EventHandler<OutboundStateChange>? StateChanged;

    /// <summary>Where the link is.</summary>
    public OutboundLinkState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Why the link is in its state, for example the reason of the last failed attempt; null when there is nothing to say.</summary>
    public string? Detail
    {
        get
        {
            lock (_gate)
            {
                return _detail;
            }
        }
    }

    /// <summary>The number of reconnect delays since the last successful connection.</summary>
    public int FailedAttempts => _backoff.Attempts;

    /// <summary>Starts the loop. Calling it again has no effect.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }

            _loop = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        }
    }

    /// <summary>
    /// Makes the connector dial now instead of after its delay, and start again at the first delay: the network changed, or the
    /// agent woke up. A connection that is open stays open, and a rejected credential stays rejected, because dialling again with
    /// it would be refused again.
    /// </summary>
    public void Nudge()
    {
        lock (_gate)
        {
            _wake.TrySetResult();
        }
    }

    /// <summary>
    /// Tells the connector that the endpoint or the credential changed (the link was joined, left or edited): an open connection is
    /// ended, the resolver is asked again and the connector dials at once, also after a rejection.
    /// </summary>
    public void Reconfigure()
    {
        CancellationTokenSource? current;
        lock (_gate)
        {
            _reconfigured = true;
            _wake.TrySetResult();
            current = _current;
        }

        try
        {
            current?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The attempt ended in the meantime; the next one reads the new configuration anyway.
        }
    }

    /// <summary>Stops dialling and ends the session that is running; waits for the loop to end.</summary>
    /// <returns>A task that completes when the connector has stopped.</returns>
    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            loop = _loop;
        }

        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        if (loop is not null)
        {
            await loop.ConfigureAwait(false);
        }

        SetState(OutboundLinkState.Stopped, null);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TimeSpan? delay;
            try
            {
                delay = await AttemptAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The configuration may be unreadable for a moment, or a session may fail in an unforeseen way; the link must not die, it tries again.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                TimeSpan wait = _backoff.Next();
                OutboundLog.Fault(_logger, exception.GetType().Name, wait.TotalSeconds);
                SetState(OutboundLinkState.Waiting, "Unexpected failure: " + exception.GetType().Name + ".");
                delay = wait;
            }

            try
            {
                await WaitAsync(delay, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>One round of the loop: resolve, dial, run the session. Returns the time to wait, or null to wait until the configuration changes.</summary>
    private async Task<TimeSpan?> AttemptAsync(CancellationToken stop)
    {
        // This attempt reads the configuration as it is now, so a reconfiguration that came earlier is taken care of.
        lock (_gate)
        {
            _reconfigured = false;
            if (_wake.Task.IsCompleted)
            {
                _wake = NewWake();
            }
        }

        OutboundEndpoint? endpoint = await _resolve(stop).ConfigureAwait(false);
        if (endpoint is null)
        {
            SetState(OutboundLinkState.Idle, null);
            return null;
        }

        // Reconfigure ends this attempt (the dial or the open connection) without stopping the loop.
        using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lock (_gate)
        {
            _current = attempt;
        }

        SetState(OutboundLinkState.Connecting, null);
        try
        {
            using WebSocket socket = await _transport.ConnectAsync(endpoint, attempt.Token).ConfigureAwait(false);
            _backoff.Reset();
            SetState(OutboundLinkState.Connected, null);
            OutboundLog.Connected(_logger, endpoint.DisplayUrl);

            int? closeCode = await _session(socket, attempt.Token).ConfigureAwait(false);
            OutboundLog.Disconnected(_logger, endpoint.DisplayUrl);
            if (closeCode == CloseCodes.Unauthorized)
            {
                return Reject(endpoint, "the peer closed the connection with code 4401");
            }

            SetState(OutboundLinkState.Waiting, "The connection ended.");
            return _backoff.Next();
        }
        catch (OutboundDialException exception) when (exception.Failure == OutboundFailure.Unauthorized)
        {
            return Reject(endpoint, exception.Message);
        }
        catch (OutboundDialException exception)
        {
            TimeSpan wait = _backoff.Next();
            OutboundLog.DialFailed(_logger, endpoint.DisplayUrl, exception.Message, wait.TotalSeconds);
            SetState(OutboundLinkState.Waiting, exception.Message);
            return wait;
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !stop.IsCancellationRequested)
        {
            // Reconfigure ended the dial; start the next attempt at once with the new configuration.
            return TimeSpan.Zero;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, attempt))
                {
                    _current = null;
                }
            }
        }
    }

    private TimeSpan? Reject(OutboundEndpoint endpoint, string reason)
    {
        OutboundLog.Rejected(_logger, endpoint.DisplayUrl, reason);
        SetState(OutboundLinkState.Rejected, reason);
        return null;
    }

    /// <summary>
    /// Waits for the delay, or until <see cref="Nudge"/> is called. A null delay waits for a nudge only; in the rejected state only
    /// a nudge that says the configuration changed ends the wait.
    /// </summary>
    private async Task WaitAsync(TimeSpan? delay, CancellationToken stop)
    {
        while (true)
        {
            Task wake;
            lock (_gate)
            {
                wake = _wake.Task;
            }

            using var timerStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
            Task timer = delay is { } span ? Task.Delay(span, _time, timerStop.Token) : Task.Delay(Timeout.InfiniteTimeSpan, timerStop.Token);
            Task finished = await Task.WhenAny(timer, wake).ConfigureAwait(false);
            await timerStop.CancelAsync().ConfigureAwait(false);
            stop.ThrowIfCancellationRequested();

            if (ReferenceEquals(finished, timer))
            {
                return;
            }

            bool rejected;
            bool reconfigured;
            lock (_gate)
            {
                _wake = NewWake();
                rejected = _state == OutboundLinkState.Rejected;
                reconfigured = _reconfigured;
                _reconfigured = false;
            }

            if (reconfigured)
            {
                _backoff.Reset();
                return;
            }

            if (!rejected)
            {
                // The network changed or the agent woke up: dial now and start with the short delays again.
                if (delay is not null)
                {
                    _backoff.Reset();
                    return;
                }

                // Idle: a nudge changes nothing, only a reconfiguration can give the link something to dial.
            }
        }
    }

    private void SetState(OutboundLinkState state, string? detail)
    {
        bool changed;
        lock (_gate)
        {
            changed = _state != state || !string.Equals(_detail, detail, StringComparison.Ordinal);
            _state = state;
            _detail = detail;
        }

        if (changed)
        {
            StateChanged?.Invoke(this, new OutboundStateChange(state, detail));
        }
    }
}
