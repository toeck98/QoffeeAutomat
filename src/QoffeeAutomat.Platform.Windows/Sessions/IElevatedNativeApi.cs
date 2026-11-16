namespace QoffeeAutomat.Platform.Windows.Sessions;

/// <summary>Why <see cref="IElevatedNativeApi.OpenElevatedToken"/> did or did not produce a token.</summary>
public enum ElevatedTokenStatus
{
    /// <summary>The elevated token of the user is available.</summary>
    Available,

    /// <summary>Nobody is logged on in the session (for example the sign-in screen is showing); maps to <c>no_session</c>.</summary>
    NoSession,

    /// <summary>
    /// The token of the user has no elevated linked token: the user is a standard user, or User Account Control is switched
    /// off; maps to <c>elevation_unavailable</c>.
    /// </summary>
    NoLinkedToken,

    /// <summary>A Windows call failed for another reason, for example because the agent does not run as LocalSystem; maps to <c>internal</c>.</summary>
    Failed,
}

/// <summary>The elevated token of the interactive user (PLAN.md S11, D-036). Disposing it closes the handle.</summary>
public interface IElevatedToken : IDisposable
{
    /// <summary>The security identifier of the user in string form; the parameter file of a PowerShell run is made readable for it.</summary>
    string UserSid { get; }

    /// <summary>The account name of the user, for log messages; null when it is not known.</summary>
    string? UserName { get; }
}

/// <summary>The outcome of <see cref="IElevatedNativeApi.OpenElevatedToken"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Token">The token when <paramref name="Status"/> is <see cref="ElevatedTokenStatus.Available"/>; the caller disposes it.</param>
/// <param name="Message">An explanation for the other statuses.</param>
public sealed record ElevatedTokenResult(ElevatedTokenStatus Status, IElevatedToken? Token = null, string? Message = null)
{
    /// <summary>The token is available.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The result.</returns>
    public static ElevatedTokenResult Success(IElevatedToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return new ElevatedTokenResult(ElevatedTokenStatus.Available, token);
    }

    /// <summary>Nobody is logged on.</summary>
    /// <param name="message">The explanation.</param>
    /// <returns>The result.</returns>
    public static ElevatedTokenResult NoSession(string message) => new(ElevatedTokenStatus.NoSession, null, message);

    /// <summary>The user has no elevated linked token.</summary>
    /// <param name="message">The explanation.</param>
    /// <returns>The result.</returns>
    public static ElevatedTokenResult NoLinkedToken(string message) => new(ElevatedTokenStatus.NoLinkedToken, null, message);

    /// <summary>A Windows call failed.</summary>
    /// <param name="message">The explanation.</param>
    /// <returns>The result.</returns>
    public static ElevatedTokenResult Failed(string message) => new(ElevatedTokenStatus.Failed, null, message);
}

/// <summary>What <see cref="IElevatedNativeApi.Start"/> starts.</summary>
/// <param name="FileName">The executable; a name without a directory is searched like the shell does.</param>
/// <param name="Arguments">The arguments, one entry per argument.</param>
/// <param name="WorkingDirectory">The working directory; null selects the profile directory of the user.</param>
/// <param name="Environment">Variables that are added to the environment block of the user (and replace variables of the same name).</param>
/// <param name="Attached">
/// True when the caller waits for the process: output goes to pipes that the agent owns, standard input is closed, and the
/// process joins a job object so that the whole tree can be killed. False starts the process and lets go of it.
/// </param>
public sealed record ElevatedLaunch(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    bool Attached);

/// <summary>A process that was started with the elevated token. Disposing it releases the handles but does not end the process.</summary>
public interface IElevatedProcess : IDisposable
{
    /// <summary>The process id.</summary>
    int ProcessId { get; }

    /// <summary>The standard output, read from a pipe that the agent owns; null for a process that is not attached.</summary>
    Stream? StandardOutput { get; }

    /// <summary>The standard error, read from a pipe that the agent owns; null for a process that is not attached.</summary>
    Stream? StandardError { get; }

    /// <summary>Completes with the exit code when the process has ended.</summary>
    /// <param name="cancellationToken">Stops waiting; the process keeps running.</param>
    /// <returns>The exit code.</returns>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Kills the process and everything it started (the job object); nothing happens when they are gone already.</summary>
    void Kill();
}

/// <summary>The process could not be started with the elevated token, for example because the file does not exist.</summary>
public sealed class ElevatedLaunchException : Exception
{
    /// <summary>Creates an exception without a message.</summary>
    public ElevatedLaunchException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public ElevatedLaunchException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public ElevatedLaunchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The Windows calls that <see cref="ElevatedLauncher"/> needs (PLAN.md S11, D-036, T311): the active console session, the
/// token of its user and the linked elevated token, and <c>CreateProcessAsUser</c> on the interactive desktop with pipes and
/// a job object. A seam, so that the launcher is tested without being LocalSystem and without a second desktop; the real
/// implementation is <see cref="WindowsElevatedNativeApi"/>.
/// </summary>
public interface IElevatedNativeApi
{
    /// <summary>Finds the session of the console.</summary>
    /// <returns>The session id; null when no session is attached to the console.</returns>
    int? GetActiveConsoleSessionId();

    /// <summary>Takes the token of the user who is logged on in the session and the elevated token linked to it.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The token or the reason there is none; never throws for a missing session or a missing linked token.</returns>
    ElevatedTokenResult OpenElevatedToken(int sessionId);

    /// <summary>
    /// Starts a process with the elevated token on the interactive desktop (<c>winsta0\default</c>), with the environment
    /// block of the user, without a window of its own for console programs.
    /// </summary>
    /// <param name="token">A token returned by <see cref="OpenElevatedToken"/>.</param>
    /// <param name="launch">What to start.</param>
    /// <returns>The running process.</returns>
    /// <exception cref="ElevatedLaunchException">The process could not be created.</exception>
    IElevatedProcess Start(IElevatedToken token, ElevatedLaunch launch);
}
