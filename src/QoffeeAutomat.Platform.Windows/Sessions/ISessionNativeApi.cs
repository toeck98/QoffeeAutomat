namespace QoffeeAutomat.Platform.Windows.Sessions;

/// <summary>The connection state of a terminal services session (<c>WTS_CONNECTSTATE_CLASS</c>).</summary>
public enum WtsConnectState
{
    /// <summary>A user is logged on and attached to the console or a remote client.</summary>
    Active = 0,

    /// <summary>The session is connected to a client but nobody is logged on yet.</summary>
    Connected = 1,

    /// <summary>The session is being connected.</summary>
    ConnectQuery = 2,

    /// <summary>The session is shadowing another one.</summary>
    Shadow = 3,

    /// <summary>A user is logged on but the session is not attached to a client.</summary>
    Disconnected = 4,

    /// <summary>The session waits for a client to connect.</summary>
    Idle = 5,

    /// <summary>The session is listening for connections (the services session 0 never is interactive).</summary>
    Listen = 6,

    /// <summary>The session is being reset.</summary>
    Resetting = 7,

    /// <summary>The session is shutting down.</summary>
    Down = 8,

    /// <summary>The session is being initialised.</summary>
    Init = 9,
}

/// <summary>A session as listed by the terminal services.</summary>
/// <param name="SessionId">The session id.</param>
/// <param name="State">The connection state.</param>
/// <param name="UserName">The user; null or empty when nobody is logged on.</param>
public sealed record WtsSessionInfo(int SessionId, WtsConnectState State, string? UserName)
{
    /// <summary>
    /// True when a user works in the session and a SessionHost belongs there: not the services session 0 and a
    /// logged-on user in an active or disconnected session.
    /// </summary>
    public bool IsInteractive =>
        SessionId != 0
        && !string.IsNullOrWhiteSpace(UserName)
        && State is WtsConnectState.Active or WtsConnectState.Connected or WtsConnectState.Disconnected;
}

/// <summary>What to start in a session.</summary>
/// <param name="ExecutablePath">The full path of the executable.</param>
/// <param name="Arguments">The command line arguments, one entry per argument.</param>
/// <param name="WorkingDirectory">The working directory, or null for the directory of the executable.</param>
public sealed record SessionLaunch(string ExecutablePath, IReadOnlyList<string> Arguments, string? WorkingDirectory = null);

/// <summary>A process that was started in a session.</summary>
public interface ISessionProcess : IDisposable
{
    /// <summary>The process id.</summary>
    int ProcessId { get; }

    /// <summary>Completes when the process ended, and with its exit code.</summary>
    /// <param name="cancellationToken">Stops waiting; the process keeps running.</param>
    /// <returns>The exit code.</returns>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Kills the process; nothing happens when it ended already.</summary>
    void Kill();
}

/// <summary>The process could not be started in the session, for example because nobody is logged on there any more.</summary>
public sealed class SessionLaunchException : Exception
{
    /// <summary>Creates an exception without a message.</summary>
    public SessionLaunchException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public SessionLaunchException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public SessionLaunchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The Windows calls that the session manager needs (PLAN.md S11): the list of sessions and starting a process in the
/// session of a user. A seam, so that the manager is tested without being LocalSystem and without real sessions.
/// </summary>
public interface ISessionNativeApi
{
    /// <summary>Lists the sessions of this computer.</summary>
    /// <returns>All sessions, interactive or not.</returns>
    IReadOnlyList<WtsSessionInfo> EnumerateSessions();

    /// <summary>
    /// Starts a process in a session with the (not elevated) token of the user who is logged on there, the environment
    /// block of that user and the interactive desktop.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="launch">What to start.</param>
    /// <returns>The running process.</returns>
    /// <exception cref="SessionLaunchException">Nobody is logged on in the session, or the process could not be created.</exception>
    ISessionProcess StartInSession(int sessionId, SessionLaunch launch);
}
