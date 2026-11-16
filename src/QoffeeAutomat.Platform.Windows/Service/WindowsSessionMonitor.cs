using System.Runtime.InteropServices;
using System.ServiceProcess;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Windows.Service;

/// <summary>Finds out who is logged in to a session; the seam between the monitor and the Windows terminal services.</summary>
public interface ISessionUserLookup
{
    /// <summary>The id of the session that is attached to the physical console, or null when there is none.</summary>
    /// <returns>The session id.</returns>
    int? GetActiveConsoleSessionId();

    /// <summary>Returns the user of a session.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The user name, or null when nobody is logged in to the session.</returns>
    string? GetUserName(int sessionId);
}

/// <summary>The <see cref="ISessionUserLookup"/> backed by the Windows terminal services API.</summary>
public sealed class WtsSessionUserLookup : ISessionUserLookup
{
    private const uint NoSession = 0xFFFFFFFF;
    private const int UserNameInfoClass = 5;

    /// <inheritdoc />
    public int? GetActiveConsoleSessionId()
    {
        uint id = WTSGetActiveConsoleSessionId();
        return id == NoSession ? null : (int)id;
    }

    /// <inheritdoc />
    public string? GetUserName(int sessionId)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, (uint)sessionId, UserNameInfoClass, out IntPtr buffer, out _))
        {
            return null;
        }

        try
        {
            string? name = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void WTSFreeMemory(IntPtr memory);
}

/// <summary>
/// The <see cref="ISessionBridge"/> of the agent as far as the service itself can see (PLAN.md S11): it follows the
/// session change notifications of the control manager (logon, logoff, lock, unlock, console connect and
/// disconnect) for the session of the physical console and publishes the user and the lock state.
/// <see cref="PlatformEventPublisher"/> turns the changes into the events <c>user_logged_in</c>,
/// <c>user_logged_out</c>, <c>session_locked</c> and <c>session_unlocked</c>. It runs no commands: the
/// <see cref="SessionState.IsReady"/> flag and <c>RunAsync</c> belong to the SessionHost connection (T302) and stay
/// off until that exists, so every <c>runAs: user</c> run ends with <see cref="ErrorCodes.NoSession"/>.
/// </summary>
public sealed class WindowsSessionMonitor : ISessionBridge
{
    /// <summary>The session of the services; never an interactive session.</summary>
    private const int ServicesSessionId = 0;

    private readonly object _gate = new();
    private readonly ISessionUserLookup _lookup;
    private SessionState _state = SessionState.None;
    private int? _trackedSessionId;

    /// <summary>Creates the monitor and reads who is logged in at the console now.</summary>
    /// <param name="lookup">Finds the console session and its user.</param>
    public WindowsSessionMonitor(ISessionUserLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        _lookup = lookup;
        Refresh();
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

    /// <summary>Handles a session change notification of the control manager.</summary>
    /// <param name="reason">What happened.</param>
    /// <param name="sessionId">The session it happened in.</param>
    public void Handle(SessionChangeReason reason, int sessionId)
    {
        SessionState? changed = null;
        lock (_gate)
        {
            SessionState next = Next(_state, reason, sessionId);
            if (next != _state)
            {
                _state = next;
                changed = next;
            }
        }

        if (changed is { } state)
        {
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state));
        }
    }

    /// <summary>Reads the console session again, for example after the service started.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            if (_lookup.GetActiveConsoleSessionId() is { } console and not ServicesSessionId
                && _lookup.GetUserName(console) is { } user)
            {
                _trackedSessionId = console;

                // The lock state is not known at start; a lock screen shows up with the next notification.
                _state = new SessionState(IsReady: false, user, IsLocked: false, IsUserAdministrator: false);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<MetricDescriptor> DescribeUserMetrics() => [];

    /// <inheritdoc />
    public ValueTask<IReadOnlyDictionary<string, object?>> SampleUserMetricsAsync(
        IReadOnlyCollection<string> metricIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metricIds);

        IReadOnlyDictionary<string, object?> unavailable = metricIds.Distinct().ToDictionary(id => id, _ => (object?)null);
        return ValueTask.FromResult(unavailable);
    }

    /// <inheritdoc />
    public ValueTask<ExecutionResult> RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(ExecutionResult.Failure(
            ErrorCodes.NoSession,
            "No session helper is connected, so commands cannot run in the user's session."));
    }

    private SessionState Next(SessionState current, SessionChangeReason reason, int sessionId)
    {
        switch (reason)
        {
            case SessionChangeReason.SessionLogon:
            case SessionChangeReason.ConsoleConnect:
                if (sessionId != ServicesSessionId && _lookup.GetActiveConsoleSessionId() == sessionId
                    && _lookup.GetUserName(sessionId) is { } user)
                {
                    _trackedSessionId = sessionId;
                    return new SessionState(current.IsReady, user, IsLocked: false, current.IsUserAdministrator);
                }

                return current;
            case SessionChangeReason.SessionLogoff when sessionId == _trackedSessionId:
                _trackedSessionId = null;
                return SessionState.None;
            case SessionChangeReason.SessionLock when sessionId == _trackedSessionId:
                return current with { IsLocked = true };
            case SessionChangeReason.SessionUnlock when sessionId == _trackedSessionId:
                return current with { IsLocked = false };
            case SessionChangeReason.ConsoleDisconnect when sessionId == _trackedSessionId && current.UserName is not null:
                // Fast user switching: the session is still logged in but nobody sits in front of it.
                return current with { IsLocked = true };
            default:
                return current;
        }
    }
}
