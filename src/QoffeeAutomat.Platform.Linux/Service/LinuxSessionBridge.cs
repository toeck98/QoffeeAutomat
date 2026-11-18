using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Execution.Bash;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Linux.Metrics.Session;
using QoffeeAutomat.Platform.Linux.Sessions;

namespace QoffeeAutomat.Platform.Linux.Service;

/// <summary>What the bridge needs to talk to the session helper; without it the bridge only knows logind (T802).</summary>
/// <param name="Transport">Opens the stream to the helper of a user.</param>
/// <param name="AgentVersion">The product version, for the <c>hello</c> of the agent.</param>
public sealed record LinuxSessionHelperOptions(ISessionHelperTransport Transport, string AgentVersion);

/// <summary>
/// The session as the agent sees it on Linux (PLAN.md S11, S17, T802, T803). Who is logged in at the computer and whether the session is
/// locked comes from logind, read at the start and then every <see cref="PollInterval"/>; a change is published through
/// <see cref="StateChanged"/>. The session helper of the user (the systemd user service <c>qoffeeautomat-session</c>) adds what only the
/// session can do: the bridge connects to its Unix domain socket as soon as a session with a user id exists (retrying every
/// <see cref="HelperRetryInterval"/>), the state is <i>ready</i> while the connection stands, <see cref="RunAsync(ProcessLaunchRequest, CancellationToken)"/> and
/// <see cref="RunAsync(BashInvocation, CancellationToken)"/> run commands of <c>runAs: user</c> there, and the desktop metrics (idle time,
/// volume, media player) come from it. Without a helper (not installed, not running, or the bridge is built without helper options) the
/// state is never ready, a command in the user's context answers <c>no_session</c>, and the desktop metrics are unavailable.
/// </summary>
public sealed partial class LinuxSessionBridge : ISessionBridge, IUserContextBash, ILinuxUserSession, IHostedService, IDisposable
{
    /// <summary>How often logind is asked.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    /// <summary>How often a connection to the helper is attempted while a user is logged in and no helper is connected.</summary>
    public static readonly TimeSpan HelperRetryInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long the handshake with a helper may take.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a call waits for the first values of a newly subscribed metric.</summary>
    public static readonly TimeSpan FirstValueTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The shortest time a metric stays subscribed after it was last asked for; longer for slow metrics (four intervals).</summary>
    public static readonly TimeSpan SubscriptionExpiry = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly LinuxSessionMetricProvider _sessions;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly LinuxSessionHelperOptions? _helper;
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _requested = new(StringComparer.Ordinal);
    private LogindSession? _current;
    private SessionState _state = SessionState.None;
    private LinuxSessionConnection? _connection;
    private TaskCompletionSource _arrival = NewArrival();
    private ITimer? _timer;
    private ITimer? _helperTimer;
    private bool _disposed;

    /// <summary>Creates the bridge.</summary>
    /// <param name="sessions">Reads the sessions of logind.</param>
    /// <param name="logger">Receives the messages about the session.</param>
    /// <param name="time">The clock that drives the polling; null uses the system clock.</param>
    /// <param name="helper">How to reach the session helper; null makes a bridge that only knows logind.</param>
    public LinuxSessionBridge(
        LinuxSessionMetricProvider sessions,
        ILogger logger,
        TimeProvider? time = null,
        LinuxSessionHelperOptions? helper = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(logger);

        _sessions = sessions;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _helper = helper;
    }

    /// <inheritdoc />
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public SessionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public string? SessionId
    {
        get
        {
            lock (_gate)
            {
                return _current?.Id;
            }
        }
    }

    /// <summary>The metrics that the helper is subscribed to now, sorted.</summary>
    public IReadOnlyList<string> Subscribed
    {
        get
        {
            lock (_gate)
            {
                return [.. _requested.Keys.Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<MetricDescriptor> DescribeUserMetrics() =>
        _helper is null ? SessionMetricCatalog.AgentSide : [.. SessionMetricCatalog.AgentSide, .. LinuxSessionMetricCatalog.HelperSide];

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, object?>> SampleUserMetricsAsync(
        IReadOnlyCollection<string> metricIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metricIds);

        LogindSession? session;
        SessionState state;
        lock (_gate)
        {
            session = _current;
            state = _state;
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var fromHelper = new List<string>();
        foreach (string id in metricIds.Distinct(StringComparer.Ordinal))
        {
            switch (id)
            {
                case SessionMetricIds.Ready:
                    values[id] = state.IsReady;
                    break;
                case SessionMetricIds.User:
                    values[id] = session?.UserName;
                    break;
                case SessionMetricIds.Locked:
                    values[id] = session?.IsLocked;
                    break;
                default:
                    if (_helper is not null && LinuxSessionMetricCatalog.IsHelperMetric(id))
                    {
                        values[id] = null;
                        fromHelper.Add(id);
                    }

                    break;
            }
        }

        if (fromHelper.Count > 0 && state.IsReady)
        {
            foreach ((string id, object? value) in await SampleHelperAsync(fromHelper, cancellationToken).ConfigureAwait(false))
            {
                values[id] = value;
            }
        }

        return values;
    }

    /// <inheritdoc />
    public ValueTask<ExecutionResult> RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Connection is { } connection
            ? connection.RunProcessAsync(request, cancellationToken)
            : ValueTask.FromResult(NoSession());
    }

    /// <inheritdoc />
    public ValueTask<ExecutionResult> RunAsync(BashInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        return Connection is { } connection
            ? connection.RunBashAsync(invocation, cancellationToken)
            : ValueTask.FromResult(NoSession());
    }

    /// <inheritdoc />
    public ValueTask<ExecutionResult> RunBuiltinAsync(
        string name,
        IReadOnlyDictionary<string, object> variables,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(variables);

        return Connection is { } connection
            ? connection.RunBuiltinAsync(name, variables, timeout, cancellationToken)
            : ValueTask.FromResult(NoSession());
    }

    /// <inheritdoc />
    public async ValueTask NotifyAsync(string title, string message, CancellationToken cancellationToken)
    {
        if (Connection is { } connection)
        {
            await connection.NotifyAsync(title, message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the session from logind, connects to the helper when there is none, and publishes a change.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the state is current.</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        // A slow logind must not pile up reads: a read in progress is the current one.
        if (!await _refreshing.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            LogindSession? session = await _sessions.ReadSessionAsync(cancellationToken).ConfigureAwait(false);
            await EnsureHelperAsync(session, cancellationToken).ConfigureAwait(false);

            bool changed;
            SessionState state;
            lock (_gate)
            {
                bool ready = _connection is { IsOpen: true };
                state = new SessionState(ready, session?.UserName, session?.IsLocked ?? false, false);
                changed = state != _state || session != _current;
                _current = session;
                _state = state;
            }

            if (changed)
            {
                StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state));
            }
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>Reads the session once and starts polling.</summary>
    /// <param name="cancellationToken">Cancels the first read.</param>
    /// <returns>A task that completes when the first read is done.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        _timer = _time.CreateTimer(state => _ = PollAsync(), null, PollInterval, PollInterval);
        if (_helper is not null)
        {
            _helperTimer = _time.CreateTimer(state => _ = HelperTickAsync(), null, HelperRetryInterval, HelperRetryInterval);
        }
    }

    /// <summary>Stops polling and tells the helper that the agent stops.</summary>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A task that completes when the helper was told.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        _timer = null;
        _helperTimer?.Dispose();
        _helperTimer = null;

        LinuxSessionConnection? connection;
        lock (_gate)
        {
            connection = _connection;
            _connection = null;
        }

        if (connection is not null)
        {
            await connection.ShutdownAsync(SessionShutdownReasons.AgentStopping).ConfigureAwait(false);
        }
    }

    /// <summary>Stops polling, closes the connection and releases the locks of the object.</summary>
    public void Dispose()
    {
        LinuxSessionConnection? connection;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            connection = _connection;
            _connection = null;
            ReleaseWaiters();
        }

        _timer?.Dispose();
        _timer = null;
        _helperTimer?.Dispose();
        _helperTimer = null;
        connection?.Dispose();
        _refreshing.Dispose();
        _sendGate.Dispose();
    }

    [LoggerMessage(EventId = 3210, Level = LogLevel.Warning, Message = "The session could not be read from logind: {Reason}")]
    private static partial void ReadFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 3330, Level = LogLevel.Information, Message = "The session helper of {User} is connected.")]
    private static partial void HelperConnected(ILogger logger, string user);

    [LoggerMessage(EventId = 3331, Level = LogLevel.Information, Message = "The session helper of {User} is gone.")]
    private static partial void HelperGone(ILogger logger, string user);

    [LoggerMessage(EventId = 3332, Level = LogLevel.Warning, Message = "The session helper of user {Uid} was refused: {Reason}")]
    private static partial void HelperRefused(ILogger logger, long uid, string reason);

    private static ExecutionResult NoSession()
    {
        return ExecutionResult.Failure(
            ErrorCodes.NoSession,
            "Commands in the user's context need the session helper of the logged-in user (the systemd user service qoffeeautomat-session), which is not connected.");
    }

    private static TaskCompletionSource NewArrival()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static object? ToValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDouble(out double number) ? number : null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => element.GetString(),
            _ => null,
        };
    }

    private LinuxSessionConnection? Connection
    {
        get
        {
            lock (_gate)
            {
                return _connection is { IsOpen: true } open ? open : null;
            }
        }
    }

    /// <summary>Wakes everybody who waits for values. The caller holds the lock.</summary>
    private void ReleaseWaiters()
    {
        TaskCompletionSource released = _arrival;
        _arrival = NewArrival();
        released.TrySetResult();
    }

    /// <summary>
    /// Makes sure that the connection belongs to the user of the session: keeps an open one, drops one of another user or a closed one,
    /// and connects to the helper of the user when there is none. A helper that is not there is the normal case until the user session
    /// has started it, and is not an error.
    /// </summary>
    private async Task EnsureHelperAsync(LogindSession? session, CancellationToken cancellationToken)
    {
        if (_helper is null)
        {
            return;
        }

        long? uid = session?.Uid;
        LinuxSessionConnection? existing;
        lock (_gate)
        {
            existing = _connection;
        }

        if (existing is { IsOpen: true } && uid is not null && existing.UserId == uid)
        {
            return;
        }

        if (existing is not null)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_connection, existing))
                {
                    _connection = null;
                    _values.Clear();
                    ReleaseWaiters();
                }
            }

            existing.Dispose();
        }

        if (uid is not { } wanted)
        {
            return;
        }

        string userName = session?.UserName ?? string.Empty;
        LinuxSessionConnection? created = await ConnectAsync(wanted, userName, cancellationToken).ConfigureAwait(false);
        if (created is null)
        {
            return;
        }

        bool resubscribe;
        lock (_gate)
        {
            if (_disposed)
            {
                created.Dispose();
                return;
            }

            _connection = created;
            _values.Clear();
            resubscribe = _requested.Count > 0;
            ReleaseWaiters();
        }

        created.MetricsStateReceived += OnMetricsState;
        _ = created.Closed.ContinueWith(_ => OnConnectionClosed(created), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        HelperConnected(_logger, userName);
        if (resubscribe)
        {
            _ = SendSubscriptionAsync(created);
        }
    }

    private async Task<LinuxSessionConnection?> ConnectAsync(long uid, string userName, CancellationToken cancellationToken)
    {
        Stream? stream = await _helper!.Transport.ConnectAsync(uid, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return null;
        }

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(HandshakeTimeout);

            // The helper speaks first, like the SessionHost on Windows; the agent answers when it is satisfied.
            SessionMessage? first = await SessionFrameCodec.ReadAsync(stream, limit.Token).ConfigureAwait(false);
            if (first is not SessionHelloMessage { Role: SessionRoles.SessionHost } hello)
            {
                HelperRefused(_logger, uid, "it did not start with a hello of a session helper");
                stream.Dispose();
                return null;
            }

            if (hello.ProtocolVersion != SessionIpc.ProtocolVersion)
            {
                HelperRefused(_logger, uid, $"it speaks protocol version {hello.ProtocolVersion}, the agent {SessionIpc.ProtocolVersion}");
                await SessionFrameCodec.WriteAsync(stream, new SessionShutdownMessage(SessionShutdownReasons.Rejected), limit.Token).ConfigureAwait(false);
                stream.Dispose();
                return null;
            }

            await SessionFrameCodec.WriteAsync(
                stream,
                new SessionHelloMessage(SessionIpc.ProtocolVersion, SessionRoles.Agent, Environment.ProcessId, 0, _helper.AgentVersion),
                limit.Token).ConfigureAwait(false);

            var connection = new LinuxSessionConnection(stream, hello, uid, _time, _logger);
            connection.Start();
            return connection;
        }
        catch (Exception exception) when (exception is IOException or SessionProtocolException or OperationCanceledException or ObjectDisposedException)
        {
            stream.Dispose();
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            HelperRefused(_logger, uid, $"the handshake of {userName} failed: {exception.Message}");
            return null;
        }
    }

    private void OnConnectionClosed(LinuxSessionConnection connection)
    {
        bool current;
        lock (_gate)
        {
            current = ReferenceEquals(_connection, connection);
            if (current)
            {
                _connection = null;
                _values.Clear();
                ReleaseWaiters();
            }
        }

        if (current)
        {
            HelperGone(_logger, connection.UserName ?? "?");

            // The state was ready and is not any more: publish it now instead of with the next poll.
            _ = PollAsync();
        }
    }

    private void OnMetricsState(object? sender, SessionMetricsReceivedEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _connection))
            {
                return;
            }

            foreach ((string id, JsonElement element) in e.Message.Values)
            {
                _values[id] = ToValue(element);
            }

            ReleaseWaiters();
        }
    }

    /// <summary>Returns the latest values of the helper's metrics, subscribing the helper to them first.</summary>
    private async Task<IReadOnlyDictionary<string, object?>> SampleHelperAsync(IReadOnlyList<string> wanted, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string id in wanted)
        {
            result[id] = null;
        }

        LinuxSessionConnection? connection;
        Task? arrival = null;
        bool subscriptionChanged = false;
        lock (_gate)
        {
            connection = _connection;
            if (connection is not { IsOpen: true } || _disposed)
            {
                return result;
            }

            DateTimeOffset now = _time.GetUtcNow();
            foreach (string id in wanted)
            {
                if (!_requested.ContainsKey(id))
                {
                    subscriptionChanged = true;
                }

                _requested[id] = now;
            }

            if (wanted.Any(id => !_values.ContainsKey(id)))
            {
                // Taken before the subscription is sent, so that the values of the reply cannot be missed.
                arrival = _arrival.Task;
            }
        }

        if (subscriptionChanged)
        {
            await SendSubscriptionAsync(connection).ConfigureAwait(false);
        }

        if (arrival is not null)
        {
            try
            {
                await arrival.WaitAsync(FirstValueTimeout, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The helper did not answer in time: the metrics are unavailable for now and arrive with a later sample.
            }
        }

        lock (_gate)
        {
            foreach (string id in wanted)
            {
                if (ReferenceEquals(_connection, connection) && _values.TryGetValue(id, out object? value))
                {
                    result[id] = value;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Drops the metrics that were not asked for within their expiry from the subscription. The metric engine stops asking when nobody
    /// subscribes to a metric, so this is what ends the sampling in the session.
    /// </summary>
    private void Prune()
    {
        LinuxSessionConnection? connection;
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            bool changed = false;
            foreach ((string id, DateTimeOffset askedAt) in _requested.ToList())
            {
                TimeSpan keep = TimeSpan.FromSeconds(Math.Max(SubscriptionExpiry.TotalSeconds, 4 * SessionMetricCatalog.IntervalSecondsOf(id)));
                if (now - askedAt > keep)
                {
                    _requested.Remove(id);
                    _values.Remove(id);
                    changed = true;
                }
            }

            connection = changed ? _connection : null;
        }

        if (connection is not null)
        {
            _ = SendSubscriptionAsync(connection);
        }
    }

    /// <summary>Sends the metrics that are wanted now. The set is read inside the send gate, so concurrent callers cannot deliver an older set after a newer one.</summary>
    private async Task SendSubscriptionAsync(LinuxSessionConnection connection)
    {
        try
        {
            await _sendGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            string[] ids;
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_connection, connection))
                {
                    return;
                }

                ids = [.. _requested.Keys.Order(StringComparer.Ordinal)];
            }

            await connection.SubscribeMetricsAsync(ids, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException or SessionProtocolException)
        {
            // The connection is closing; the bridge connects again, and the next connection is subscribed again.
        }
        finally
        {
            try
            {
                _sendGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Disposed while sending.
            }
        }
    }

    private async Task HelperTickAsync()
    {
        try
        {
            Prune();

            // A helper that started after the last poll is found within a few seconds, not after the next logind poll.
            bool needsHelper;
            lock (_gate)
            {
                needsHelper = _current?.Uid is not null && _connection is not { IsOpen: true };
            }

            if (needsHelper)
            {
                await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            ReadFailed(_logger, exception.Message);
        }
    }

    private async Task PollAsync()
    {
        try
        {
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // A failed read keeps the state of the last one; the next poll tries again.
            ReadFailed(_logger, exception.Message);
        }
    }
}

/// <summary>The launcher of <c>runAs: user_elevated</c> (D-036), which is a concept of Windows: Linux has no linked administrator token.</summary>
public sealed class LinuxElevatedLauncher : IElevatedLauncher
{
    /// <inheritdoc />
    public ValueTask<ExecutionResult> StartAsync(ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ValueTask.FromResult(ExecutionResult.Failure(
            ErrorCodes.ElevationUnavailable,
            "runAs: user_elevated is not available on Linux; use runAs: system for commands that need root."));
    }
}
