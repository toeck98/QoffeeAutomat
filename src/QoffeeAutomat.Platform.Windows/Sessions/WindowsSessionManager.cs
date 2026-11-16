using System.ComponentModel;
using System.Globalization;
using System.ServiceProcess;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Session;

namespace QoffeeAutomat.Platform.Windows.Sessions;

/// <summary>Settings of <see cref="WindowsSessionManager"/>.</summary>
public sealed class SessionManagerOptions
{
    /// <summary>The path of the installed SessionHost executable.</summary>
    public required string SessionHostPath { get; init; }

    /// <summary>The pipe name that the SessionHost is told to connect to; only passed when it differs from <see cref="SessionIpc.PipeName"/>.</summary>
    public string PipeName { get; init; } = SessionIpc.PipeName;

    /// <summary>The wait before the first restart of a SessionHost that ended.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between two restarts.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A SessionHost that ran at least this long is considered healthy: the next restart starts at <see cref="InitialBackoff"/> again.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait before looking again when another SessionHost already runs in the session.</summary>
    public TimeSpan AlreadyRunningRetry { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>When the agent stops: how long the SessionHosts get to end after <c>shutdown</c> before they are killed.</summary>
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Waits for a delay; null uses the clock of the manager. A seam for tests, which do not want to wait.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
}

/// <summary>
/// Keeps one SessionHost per interactive session running (PLAN.md D-009, S11): it lists the sessions when the agent
/// starts, follows logon and logoff notifications of the service control manager, starts the SessionHost of a session
/// with the token of its user (<see cref="ISessionNativeApi"/>), restarts it with an exponential backoff when it ends and
/// keeps the connections that the <see cref="SessionPipeServer"/> accepts. The agent tolerates the absence of any session:
/// nothing is started while nobody is logged on, and a missing SessionHost executable is logged once.
/// </summary>
public sealed class WindowsSessionManager : IHostedService, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<int, Supervisor> _supervisors = [];
    private readonly Dictionary<int, SessionConnection> _connections = [];
    private readonly ISessionNativeApi _native;
    private readonly SessionPipeServer _server;
    private readonly SessionManagerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private bool _started;
    private bool _stopping;

    /// <summary>Creates the manager.</summary>
    /// <param name="native">Lists sessions and starts processes in them.</param>
    /// <param name="server">The pipe server that accepts the SessionHosts.</param>
    /// <param name="options">The settings.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public WindowsSessionManager(
        ISessionNativeApi native,
        SessionPipeServer server,
        SessionManagerOptions options,
        TimeProvider time,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _native = native;
        _server = server;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised after a connection was added or removed.</summary>
    public event EventHandler? ConnectionsChanged;

    /// <summary>The sessions that have a supervisor right now.</summary>
    public IReadOnlyCollection<int> SupervisedSessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _supervisors.Keys];
            }
        }
    }

    /// <summary>Returns the open connections of all SessionHosts.</summary>
    /// <returns>A snapshot, ordered by session id.</returns>
    public IReadOnlyList<SessionConnection> GetConnections()
    {
        lock (_gate)
        {
            return [.. _connections.OrderBy(pair => pair.Key).Select(pair => pair.Value).Where(connection => connection.IsOpen)];
        }
    }

    /// <summary>Returns the connection of the SessionHost of a session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The connection after its handshake; null when no SessionHost is connected in that session.</returns>
    public SessionConnection? GetConnection(int sessionId)
    {
        lock (_gate)
        {
            return _connections.TryGetValue(sessionId, out SessionConnection? connection) && connection.IsOpen ? connection : null;
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_started)
            {
                return Task.CompletedTask;
            }

            _started = true;
        }

        _server.ConnectionEstablished += OnConnectionEstablished;
        _server.Start();

        if (!File.Exists(_options.SessionHostPath))
        {
            SessionLog.SessionHostMissing(_logger, _options.SessionHostPath);
        }

        foreach (WtsSessionInfo session in ListSessions().Where(candidate => candidate.IsInteractive))
        {
            Ensure(session.SessionId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Supervisor[] supervisors;
        SessionConnection[] connections;
        lock (_gate)
        {
            if (!_started || _stopping)
            {
                return;
            }

            _stopping = true;
            supervisors = [.. _supervisors.Values];
            connections = [.. _connections.Values];
        }

        _server.ConnectionEstablished -= OnConnectionEstablished;
        await _server.StopAsync().ConfigureAwait(false);

        // Ask politely first: the SessionHosts end, and their supervisors do not start them again because the manager stops.
        await Task.WhenAll(connections.Select(connection => connection.ShutdownAsync(SessionShutdownReasons.AgentStopping))).ConfigureAwait(false);

        Task all = Task.WhenAll(supervisors.Select(supervisor => supervisor.Task));
        try
        {
            await all.WaitAsync(_options.ShutdownGrace, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (System.TimeoutException)
        {
            // The ones that are left are killed below.
        }
        catch (OperationCanceledException)
        {
            // The host does not want to wait any longer.
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await all.WaitAsync(TimeSpan.FromSeconds(10), _time, CancellationToken.None).ConfigureAwait(false);
        }
        catch (System.TimeoutException)
        {
            // A supervisor that does not end is abandoned with the process.
        }

        lock (_gate)
        {
            _supervisors.Clear();
        }
    }

    /// <summary>Handles a session change notification of the service control manager (logon, logoff, connect).</summary>
    /// <param name="reason">What happened.</param>
    /// <param name="sessionId">The session it happened in.</param>
    public void Handle(SessionChangeReason reason, int sessionId)
    {
        if (sessionId == 0)
        {
            return;
        }

        switch (reason)
        {
            case SessionChangeReason.SessionLogon:
            case SessionChangeReason.ConsoleConnect:
            case SessionChangeReason.RemoteConnect:
            case SessionChangeReason.SessionUnlock:
                Ensure(sessionId);
                break;
            case SessionChangeReason.SessionLogoff:
                Remove(sessionId);
                break;
            default:
                break;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _server.ConnectionEstablished -= OnConnectionEstablished;
        _stop.Cancel();
        _stop.Dispose();
        lock (_gate)
        {
            foreach (SessionConnection connection in _connections.Values)
            {
                connection.Dispose();
            }

            _connections.Clear();
        }
    }

    private static TimeSpan Backoff(SessionManagerOptions options, int failures)
    {
        double factor = Math.Pow(2, Math.Max(0, failures - 1));
        double milliseconds = Math.Min(options.MaxBackoff.TotalMilliseconds, options.InitialBackoff.TotalMilliseconds * factor);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private IReadOnlyList<WtsSessionInfo> ListSessions()
    {
        try
        {
            return _native.EnumerateSessions();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            SessionLog.SessionEnumerationFailed(_logger, exception.Message);
            return [];
        }
    }

    private bool IsSessionAlive(int sessionId)
    {
        try
        {
            WtsSessionInfo? session = _native.EnumerateSessions().FirstOrDefault(candidate => candidate.SessionId == sessionId);
            return session is not null
                && session.State is WtsConnectState.Active or WtsConnectState.Connected or WtsConnectState.Disconnected
                    or WtsConnectState.ConnectQuery or WtsConnectState.Shadow;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // A transient failure must not end the supervision of a healthy session.
            SessionLog.SessionEnumerationFailed(_logger, exception.Message);
            return true;
        }
    }

    private void Ensure(int sessionId)
    {
        lock (_gate)
        {
            if (!_started || _stopping || _supervisors.ContainsKey(sessionId))
            {
                return;
            }

            var supervisor = new Supervisor(sessionId, CancellationTokenSource.CreateLinkedTokenSource(_stop.Token));
            _supervisors[sessionId] = supervisor;
            supervisor.Task = Task.Run(() => SuperviseAsync(supervisor), CancellationToken.None);
        }
    }

    private void Remove(int sessionId)
    {
        Supervisor? supervisor;
        SessionConnection? connection;
        lock (_gate)
        {
            _supervisors.Remove(sessionId, out supervisor);
            _connections.TryGetValue(sessionId, out connection);
        }

        supervisor?.Cancel();
        if (connection is not null)
        {
            _ = connection.ShutdownAsync(SessionShutdownReasons.Logoff);
        }
    }

    private async Task SuperviseAsync(Supervisor supervisor)
    {
        try
        {
            await RunSupervisionAsync(supervisor).ConfigureAwait(false);
        }
        finally
        {
            supervisor.Dispose();
        }
    }

    private async Task RunSupervisionAsync(Supervisor supervisor)
    {
        CancellationToken token = supervisor.Token;
        int sessionId = supervisor.SessionId;
        int failures = 0;
        while (!token.IsCancellationRequested)
        {
            TimeSpan delay;
            ISessionProcess? process = null;
            DateTimeOffset started = _time.GetUtcNow();
            try
            {
                process = _native.StartInSession(sessionId, CreateLaunch(sessionId));
                SessionLog.SessionHostStarted(_logger, process.ProcessId, sessionId);

                int exitCode;
                try
                {
                    exitCode = await process.WaitForExitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    process.Kill();
                    return;
                }

                if (_stopping)
                {
                    return;
                }

                TimeSpan lived = _time.GetUtcNow() - started;
                if (exitCode == SessionHostExitCodes.AlreadyRunning)
                {
                    SessionLog.SessionHostAlreadyRunning(_logger, sessionId);
                    failures = 0;
                    delay = _options.AlreadyRunningRetry;
                }
                else
                {
                    failures = lived >= _options.StableAfter ? 1 : failures + 1;
                    delay = Backoff(_options, failures);
                    SessionLog.SessionHostExited(_logger, sessionId, exitCode, (int)lived.TotalSeconds, (int)delay.TotalSeconds);
                }
            }
            catch (Exception exception) when (exception is SessionLaunchException or Win32Exception or InvalidOperationException)
            {
                failures++;
                delay = Backoff(_options, failures);
                SessionLog.SessionHostStartFailed(_logger, sessionId, exception.Message, (int)delay.TotalSeconds);
            }
            finally
            {
                process?.Dispose();
            }

            if (!IsSessionAlive(sessionId))
            {
                ForgetSupervisor(supervisor);
                return;
            }

            try
            {
                await WaitAsync(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return _options.Delay is { } custom ? custom(delay, cancellationToken) : Task.Delay(delay, _time, cancellationToken);
    }

    private SessionLaunch CreateLaunch(int sessionId)
    {
        List<string> arguments = ["--session", sessionId.ToString(CultureInfo.InvariantCulture)];
        if (!string.Equals(_options.PipeName, SessionIpc.PipeName, StringComparison.Ordinal))
        {
            arguments.Add("--pipe");
            arguments.Add(_options.PipeName);
        }

        return new SessionLaunch(_options.SessionHostPath, arguments);
    }

    private void ForgetSupervisor(Supervisor supervisor)
    {
        lock (_gate)
        {
            if (_supervisors.TryGetValue(supervisor.SessionId, out Supervisor? current) && ReferenceEquals(current, supervisor))
            {
                _supervisors.Remove(supervisor.SessionId);
            }
        }
    }

    private void OnConnectionEstablished(object? sender, SessionConnectionEventArgs e)
    {
        SessionConnection connection = e.Connection;
        SessionConnection? replaced;
        lock (_gate)
        {
            _connections.TryGetValue(connection.SessionId, out replaced);
            _connections[connection.SessionId] = connection;
        }

        SessionLog.SessionHostConnected(_logger, connection.SessionId, connection.UserName, connection.ProcessId);
        _ = WatchAsync(connection);
        if (replaced is not null && !ReferenceEquals(replaced, connection))
        {
            _ = replaced.ShutdownAsync(SessionShutdownReasons.Replaced);
        }

        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task WatchAsync(SessionConnection connection)
    {
        await connection.Closed.ConfigureAwait(false);

        bool removed = false;
        lock (_gate)
        {
            if (_connections.TryGetValue(connection.SessionId, out SessionConnection? current) && ReferenceEquals(current, connection))
            {
                _connections.Remove(connection.SessionId);
                removed = true;
            }
        }

        if (removed)
        {
            SessionLog.SessionHostDisconnected(_logger, connection.SessionId);
            ConnectionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class Supervisor : IDisposable
    {
        private readonly CancellationTokenSource _cancel;

        public Supervisor(int sessionId, CancellationTokenSource cancel)
        {
            SessionId = sessionId;
            _cancel = cancel;
            Token = cancel.Token;
        }

        public int SessionId { get; }

        public CancellationToken Token { get; }

        public Task Task { get; set; } = Task.CompletedTask;

        public void Cancel()
        {
            try
            {
                _cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The supervisor ended already.
            }
        }

        public void Dispose()
        {
            _cancel.Dispose();
        }
    }
}
