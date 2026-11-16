using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Simulated;

/// <summary>
/// An in-process stand-in for the SessionHost (PLAN.md S11). It reports a logged-in user, runs
/// "commands" by recording them, and offers user metrics. Switches make the session unavailable
/// (<see cref="SimulateLogoff"/>, <see cref="SetReady"/>) so tests can exercise <c>no_session</c>.
/// </summary>
public sealed class SimulatedSessionBridge : ISessionBridge
{
    /// <summary>The logged-in user.</summary>
    public const string UserMetricId = "session.user";

    /// <summary>Whether the session is locked.</summary>
    public const string LockedMetricId = "session.locked";

    /// <summary>Seconds since the last user input.</summary>
    public const string IdleMetricId = "session.idle_seconds";

    private static readonly MetricDescriptor[] Descriptors =
    [
        new(
            UserMetricId,
            "Logged-in user",
            "sensor",
            "string",
            TranslationKey: "session_user",
            EntityCategory: "diagnostic",
            EnabledDefault: true,
            IntervalSeconds: 0,
            Context: "user"),
        new(
            LockedMetricId,
            "Session locked",
            "binary_sensor",
            "bool",
            TranslationKey: "session_locked",
            EnabledDefault: true,
            IntervalSeconds: 0,
            Context: "user"),
        new(
            IdleMetricId,
            "Idle time",
            "sensor",
            "number",
            TranslationKey: "session_idle_seconds",
            Unit: "s",
            StateClass: "measurement",
            Precision: 0,
            EnabledDefault: true,
            IntervalSeconds: 5,
            Context: "user"),
    ];

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<ProcessLaunchRequest> _runs = [];
    private SessionState _state;
    private DateTimeOffset _lastInput;

    /// <summary>Creates the bridge.</summary>
    /// <param name="time">The clock for the idle time.</param>
    /// <param name="initialState">The initial state; null means a ready session of the administrator <c>sim-user</c>.</param>
    public SimulatedSessionBridge(TimeProvider time, SessionState? initialState = null)
    {
        ArgumentNullException.ThrowIfNull(time);

        _time = time;
        _state = initialState ?? new SessionState(true, "sim-user", false, true);
        _lastInput = time.GetUtcNow();
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

    /// <summary>The processes that were run through <see cref="RunAsync"/>, oldest first.</summary>
    public IReadOnlyList<ProcessLaunchRequest> Runs
    {
        get
        {
            lock (_gate)
            {
                return [.. _runs];
            }
        }
    }

    /// <summary>Replaces the state and raises <see cref="StateChanged"/> when it differs.</summary>
    /// <param name="state">The new state.</param>
    public void SetState(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (_gate)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state));
    }

    /// <summary>Switches the readiness of the session helper; false makes the bridge unavailable.</summary>
    /// <param name="ready">Whether user commands are accepted.</param>
    public void SetReady(bool ready)
    {
        SetState(State with { IsReady = ready });
    }

    /// <summary>Locks or unlocks the session.</summary>
    /// <param name="locked">The new lock state.</param>
    public void SetLocked(bool locked)
    {
        SetState(State with { IsLocked = locked });
    }

    /// <summary>Simulates that the user logs off: nobody is logged in and the bridge is unavailable.</summary>
    public void SimulateLogoff()
    {
        SetState(SessionState.None);
    }

    /// <summary>Simulates a logon: the session becomes ready.</summary>
    /// <param name="userName">The user.</param>
    /// <param name="isAdministrator">Whether the user is an administrator.</param>
    public void SimulateLogon(string userName, bool isAdministrator)
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);

        lock (_gate)
        {
            _lastInput = _time.GetUtcNow();
        }

        SetState(new SessionState(true, userName, false, isAdministrator));
    }

    /// <summary>Simulates keyboard or mouse input: the idle time restarts at zero.</summary>
    public void SimulateInput()
    {
        lock (_gate)
        {
            _lastInput = _time.GetUtcNow();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<MetricDescriptor> DescribeUserMetrics()
    {
        return Descriptors;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyDictionary<string, object?>> SampleUserMetricsAsync(
        IReadOnlyCollection<string> metricIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metricIds);
        cancellationToken.ThrowIfCancellationRequested();

        SessionState state;
        DateTimeOffset lastInput;
        lock (_gate)
        {
            state = _state;
            lastInput = _lastInput;
        }

        double idle = Math.Max(0, Math.Floor((_time.GetUtcNow() - lastInput).TotalSeconds));
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string id in metricIds)
        {
            switch (id)
            {
                case UserMetricId:
                    values[id] = state.IsReady ? state.UserName : null;
                    break;
                case LockedMetricId:
                    values[id] = state.IsReady ? state.IsLocked : null;
                    break;
                case IdleMetricId:
                    values[id] = state.IsReady ? idle : null;
                    break;
                default:
                    break;
            }
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, object?>>(values);
    }

    /// <inheritdoc />
    public ValueTask<ExecutionResult> RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!State.IsReady)
        {
            return ValueTask.FromResult(ExecutionResult.Failure(ErrorCodes.NoSession, "Nobody is logged in."));
        }

        lock (_gate)
        {
            _runs.Add(request);
        }

        string output = request.Arguments.Count == 0
            ? request.FileName
            : request.FileName + " " + string.Join(' ', request.Arguments);
        return ValueTask.FromResult(new ExecutionResult(0, output, string.Empty));
    }
}
