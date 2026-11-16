using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Metrics.SessionMetrics;
using QoffeeAutomat.Platform.Windows.Service;

namespace QoffeeAutomat.Platform.Windows.Sessions;

/// <summary>
/// The <see cref="ISessionBridge"/> of the Windows agent (PLAN.md D-009, S11). It combines what the service sees of
/// the console session (the logged-in user and the lock state, from <see cref="WindowsSessionMonitor"/>) with the
/// connection of the SessionHost of that session (from <see cref="WindowsSessionManager"/>): the state is ready, and the
/// event <c>session_ready</c> follows from it (<c>PlatformEventPublisher</c>), exactly while a SessionHost of the
/// active console session has completed its handshake. Commands of <c>runAs: user</c> go to that SessionHost. Without a
/// session or a SessionHost every run ends with <see cref="ErrorCodes.NoSession"/>; the agent never fails because of it.
/// </summary>
public sealed class WindowsSessionBridge : ISessionBridge, IUserContextPowerShell, IDisposable
{
    private readonly object _gate = new();
    private readonly WindowsSessionMonitor _monitor;
    private readonly WindowsSessionManager _manager;
    private readonly ISessionUserLookup _lookup;
    private readonly UserMetricsRelay _metrics;
    private SessionState _published;
    private SessionConnection? _publishedConnection;

    /// <summary>Creates the bridge.</summary>
    /// <param name="monitor">Knows the user and the lock state of the console session.</param>
    /// <param name="manager">Holds the connections of the SessionHosts.</param>
    /// <param name="lookup">Finds the active console session.</param>
    /// <param name="time">The clock of the user metric subscription; null uses the system clock.</param>
    /// <param name="metrics">The relay of the user metrics; null creates one. The bridge disposes it.</param>
    public WindowsSessionBridge(
        WindowsSessionMonitor monitor,
        WindowsSessionManager manager,
        ISessionUserLookup lookup,
        TimeProvider? time = null,
        UserMetricsRelay? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(lookup);

        _monitor = monitor;
        _manager = manager;
        _lookup = lookup;
        _metrics = metrics ?? new UserMetricsRelay(time ?? TimeProvider.System);
        _publishedConnection = ActiveConnection;
        _published = Compute(_publishedConnection);
        _metrics.Attach(_publishedConnection);
        _monitor.StateChanged += OnSourceChanged;
        _manager.ConnectionsChanged += OnSourceChanged;
    }

    /// <inheritdoc />
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public SessionState State => Compute();

    /// <summary>
    /// True while the SessionHost of the active console session accepts user commands: the ready state that the metric
    /// <c>session.ready</c> reports (T305).
    /// </summary>
    public bool IsReady => State.IsReady;

    /// <summary>The connection of the SessionHost of the active console session; null when there is none.</summary>
    public SessionConnection? ActiveConnection
    {
        get
        {
            int? console = _lookup.GetActiveConsoleSessionId();
            return console is { } id ? _manager.GetConnection(id) : null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<MetricDescriptor> DescribeUserMetrics() => SessionMetricCatalog.All;

    /// <summary>
    /// Samples the session metrics (T305). <c>session.ready</c>, <c>session.locked</c> and <c>session.user</c> come from the
    /// state that the agent knows itself, so a lock or a logoff shows up at once; the others come from the SessionHost through
    /// the metric subscription and are sampled there only while somebody asks for them. With no session every user metric is
    /// unavailable (null) and nothing is logged: no session is a normal state; only <c>session.ready</c> is then false.
    /// </summary>
    /// <param name="metricIds">The ids to sample; unknown ids are unavailable.</param>
    /// <param name="cancellationToken">Cancels the wait for the SessionHost.</param>
    /// <returns>The values by metric id.</returns>
    public async ValueTask<IReadOnlyDictionary<string, object?>> SampleUserMetricsAsync(
        IReadOnlyCollection<string> metricIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metricIds);

        SessionState state = State;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var fromSession = new List<string>();
        foreach (string id in metricIds.Distinct(StringComparer.Ordinal))
        {
            switch (id)
            {
                case SessionMetricIds.Ready:
                    values[id] = state.IsReady;
                    break;
                case SessionMetricIds.Locked:
                    values[id] = state.UserName is null ? null : state.IsLocked;
                    break;
                case SessionMetricIds.User:
                    values[id] = state.UserName;
                    break;
                default:
                    values[id] = null;
                    if (SessionMetricCatalog.IsSessionHostMetric(id))
                    {
                        fromSession.Add(id);
                    }

                    break;
            }
        }

        if (fromSession.Count > 0 && state.IsReady)
        {
            // Normally the events attach the connection; this covers an active console that changed without one.
            _metrics.Attach(ActiveConnection);
            IReadOnlyDictionary<string, object?> sampled = await _metrics.SampleAsync(fromSession, cancellationToken).ConfigureAwait(false);
            foreach ((string id, object? value) in sampled)
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

        return ActiveConnection is { } connection
            ? connection.RunProcessAsync(request, cancellationToken)
            : ValueTask.FromResult(NoSession());
    }

    /// <inheritdoc />
    public ValueTask<ExecutionResult> RunAsync(PowerShellInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        return ActiveConnection is { } connection
            ? connection.RunPowerShellAsync(invocation, cancellationToken)
            : ValueTask.FromResult(NoSession());
    }

    /// <summary>Runs a built-in action in the user's session.</summary>
    /// <param name="name">The action name.</param>
    /// <param name="variables">The validated variables.</param>
    /// <param name="timeout">How long the action may take.</param>
    /// <param name="cancellationToken">Cancels the action.</param>
    /// <returns>The outcome; <see cref="ErrorCodes.NoSession"/> while no session is ready.</returns>
    public ValueTask<ExecutionResult> RunBuiltinAsync(
        string name,
        IReadOnlyDictionary<string, object> variables,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return ActiveConnection is { } connection
            ? connection.RunBuiltinAsync(name, variables, timeout, cancellationToken)
            : ValueTask.FromResult(NoSession());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _monitor.StateChanged -= OnSourceChanged;
        _manager.ConnectionsChanged -= OnSourceChanged;
        _metrics.Dispose();
    }

    private static ExecutionResult NoSession()
    {
        return ExecutionResult.Failure(
            ErrorCodes.NoSession,
            "No session helper is connected, so commands cannot run in the user's session.");
    }

    private SessionState Compute()
    {
        return Compute(ActiveConnection);
    }

    private SessionState Compute(SessionConnection? host)
    {
        SessionState basis = _monitor.State;
        if (host is null)
        {
            return basis with { IsReady = false, IsUserAdministrator = false };
        }

        return new SessionState(
            IsReady: true,
            basis.UserName ?? host.UserName,
            basis.IsLocked,
            host.IsAdministrator);
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            SessionConnection? connection = ActiveConnection;
            SessionState next = Compute(connection);

            // A SessionHost that connects before the loss of its predecessor was noticed (it restarted quickly) is a new
            // connection all the same: report the gap, so that session_ready is published once per connection.
            if (next.IsReady && _published.IsReady && !ReferenceEquals(connection, _publishedConnection))
            {
                _published = _published with { IsReady = false };
                StateChanged?.Invoke(this, new SessionStateChangedEventArgs(_published));
            }

            _publishedConnection = connection;
            _metrics.Attach(connection);
            if (next == _published)
            {
                return;
            }

            _published = next;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(next));
        }
    }
}

/// <summary>
/// Decorates the built-in actions of the platform: an action whose descriptor says <c>runAs: user</c> is executed by the
/// SessionHost of the interactive user (the SessionHost has its own <see cref="IBuiltinActionProvider"/>), all others by
/// the inner provider in the service (PLAN.md S11, T303).
/// </summary>
public sealed class UserContextBuiltinActions : IBuiltinActionProvider
{
    /// <summary>How long a built-in action of the session may take; the command's own timeout cancels it earlier.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(1);

    private readonly IBuiltinActionProvider _inner;
    private readonly WindowsSessionBridge _bridge;

    /// <summary>Creates the decorator.</summary>
    /// <param name="inner">The actions that run in the service.</param>
    /// <param name="bridge">Reaches the SessionHost.</param>
    public UserContextBuiltinActions(IBuiltinActionProvider inner, WindowsSessionBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(bridge);

        _inner = inner;
        _bridge = bridge;
    }

    /// <inheritdoc />
    public IReadOnlyList<BuiltinActionDescriptor> Actions => _inner.Actions;

    /// <inheritdoc />
    public ValueTask<ExecutionResult> ExecuteAsync(
        string name,
        IReadOnlyDictionary<string, object> variables,
        CancellationToken cancellationToken)
    {
        BuiltinActionDescriptor? descriptor = _inner.Actions.FirstOrDefault(action => string.Equals(action.Name, name, StringComparison.Ordinal));
        return descriptor?.RunAs == RunAsValues.User
            ? _bridge.RunBuiltinAsync(name, variables, DefaultTimeout, cancellationToken)
            : _inner.ExecuteAsync(name, variables, cancellationToken);
    }
}
