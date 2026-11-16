using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Execution;

namespace QoffeeAutomat.Core.Platform;

/// <summary>
/// How a process or built-in action ended. A failure to even start the work (no session, no
/// elevation) is reported through <see cref="ErrorCode"/>, one of <see cref="ErrorCodes"/>; a process
/// that ran and exited non-zero has no error code.
/// </summary>
/// <param name="ExitCode">The exit code; zero means success.</param>
/// <param name="StandardOutput">Captured standard output.</param>
/// <param name="StandardError">Captured standard error.</param>
/// <param name="ErrorCode">A QoffeeLink error code when the work could not be started, otherwise null.</param>
/// <param name="OutputTruncated">More output was produced than is kept (the 64 KiB cap per stream); the excess was dropped.</param>
public sealed record ExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string? ErrorCode = null,
    bool OutputTruncated = false)
{
    /// <summary>True when the work ran and exited with zero.</summary>
    public bool Succeeded => ErrorCode is null && ExitCode == 0;

    /// <summary>Creates the result of work that could not be started.</summary>
    /// <param name="errorCode">One of <see cref="ErrorCodes"/>.</param>
    /// <param name="message">What went wrong; shown in the run history.</param>
    /// <returns>A failed result with exit code -1.</returns>
    public static ExecutionResult Failure(string errorCode, string message)
    {
        return new ExecutionResult(-1, string.Empty, message, errorCode);
    }
}

/// <summary>A request to start a process without a shell (PLAN.md D-012: arguments are separate entries).</summary>
/// <param name="FileName">The executable.</param>
/// <param name="Arguments">The arguments, one entry per argv element; never split or joined.</param>
/// <param name="WorkingDirectory">The working directory, or null for the default.</param>
/// <param name="EnvironmentVariables">Extra environment variables, for example <c>QA_VAR_&lt;NAME&gt;</c>.</param>
/// <param name="Timeout">How long the process may run; the whole process tree is killed afterwards.</param>
/// <param name="WaitForExit">False starts the process and returns immediately.</param>
/// <param name="CaptureOutput">Whether standard output and error are captured.</param>
/// <param name="OnOutput">
/// Optional receiver of the captured output while the process runs (live output of a run, PLAN.md S08).
/// Only launchers that supervise the process themselves honour it; the others ignore it and deliver the
/// output in the result. The receiver is called from the thread that reads the process and must not throw.
/// </param>
public sealed record ProcessLaunchRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    TimeSpan Timeout,
    bool WaitForExit = true,
    bool CaptureOutput = true,
    Action<ProcessOutputChunk>? OnOutput = null);

/// <summary>The operating system as shown in <c>hello</c> and the manifest.</summary>
/// <param name="Name">The family: <c>windows</c>, <c>linux</c> or <c>simulated</c>.</param>
/// <param name="Version">The OS version, for example <c>10.0.26200</c>.</param>
/// <param name="Architecture">The CPU architecture: <c>x64</c>, <c>arm64</c> and so on.</param>
public sealed record OsInfo(string Name, string Version, string Architecture);

/// <summary>The state of the interactive user's session as seen by the agent.</summary>
/// <param name="IsReady">The session helper of the active session accepts user commands (the metric <c>session.ready</c>).</param>
/// <param name="UserName">The logged-in user; null when nobody is logged in.</param>
/// <param name="IsLocked">The session is locked.</param>
/// <param name="IsUserAdministrator">The user is an administrator, which <c>user_elevated</c> needs (D-036).</param>
public sealed record SessionState(bool IsReady, string? UserName, bool IsLocked, bool IsUserAdministrator)
{
    /// <summary>Nobody is logged in and no session helper is connected.</summary>
    public static SessionState None { get; } = new(false, null, false, false);
}

/// <summary>Raised when <see cref="ISessionBridge.State"/> changes.</summary>
public sealed class SessionStateChangedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="state">The new state.</param>
    public SessionStateChangedEventArgs(SessionState state)
    {
        State = state;
    }

    /// <summary>The new state.</summary>
    public SessionState State { get; }
}

/// <summary>What happened to the power state.</summary>
public enum PowerEventKind
{
    /// <summary>The computer is about to sleep (suspend to RAM).</summary>
    Sleep,

    /// <summary>The computer is about to hibernate.</summary>
    Hibernate,

    /// <summary>The computer woke up.</summary>
    Resume,

    /// <summary>The computer is shutting down.</summary>
    Shutdown,

    /// <summary>The computer is restarting.</summary>
    Restart,
}

/// <summary>Raised by <see cref="IPowerEvents"/>.</summary>
public sealed class PowerEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="kind">What happened.</param>
    public PowerEventArgs(PowerEventKind kind)
    {
        Kind = kind;
    }

    /// <summary>What happened.</summary>
    public PowerEventKind Kind { get; }
}

/// <summary>What a service advertises on the network (zeroconf, PLAN.md D-016).</summary>
/// <param name="ServiceType">For example <c>_qoffeeautomat._tcp.local.</c>.</param>
/// <param name="InstanceName">The instance name, usually the agent name.</param>
/// <param name="Port">The port of the link listener.</param>
/// <param name="TxtRecords">The TXT records, for example the agent id and protocol version.</param>
public sealed record ServiceAdvertisement(
    string ServiceType,
    string InstanceName,
    int Port,
    IReadOnlyDictionary<string, string> TxtRecords);
