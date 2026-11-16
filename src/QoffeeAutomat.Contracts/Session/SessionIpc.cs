namespace QoffeeAutomat.Contracts.Session;

/// <summary>
/// Constants of the session pipe protocol between the agent and the SessionHost (PLAN.md S11). The agent is
/// the pipe server (running as SYSTEM); one SessionHost per interactive session is the client.
/// </summary>
public static class SessionIpc
{
    /// <summary>The pipe name (the pipe lives at <c>\\.\pipe\QoffeeAutomat.Session</c>).</summary>
    public const string PipeName = "QoffeeAutomat.Session";

    /// <summary>The protocol version of this build; both sides send it in <c>hello</c> and must agree.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>The file name of the SessionHost executable, which lives next to the agent executable.</summary>
    public const string SessionHostFileName = "QoffeeAutomat.SessionHost.exe";

    /// <summary>The file name of the agent executable.</summary>
    public const string AgentFileName = "QoffeeAutomat.Agent.exe";
}

/// <summary>The values of <c>hello.role</c>.</summary>
public static class SessionRoles
{
    /// <summary>The agent (pipe server).</summary>
    public const string Agent = "agent";

    /// <summary>A SessionHost (pipe client).</summary>
    public const string SessionHost = "session_host";
}

/// <summary>The values of <c>run.kind</c>.</summary>
public static class SessionRunKinds
{
    /// <summary>An executable started without a shell.</summary>
    public const string Process = "process";

    /// <summary>PowerShell code or script, run through the wrapper of Core inside the session.</summary>
    public const string PowerShell = "powershell";

    /// <summary>A built-in action of the SessionHost.</summary>
    public const string Builtin = "builtin";
}

/// <summary>The values of <c>run_output.stream</c>.</summary>
public static class SessionOutputStreams
{
    /// <summary>Standard output.</summary>
    public const string StandardOutput = "stdout";

    /// <summary>Standard error.</summary>
    public const string StandardError = "stderr";
}

/// <summary>The values of <c>shutdown.reason</c>.</summary>
public static class SessionShutdownReasons
{
    /// <summary>The agent stops.</summary>
    public const string AgentStopping = "agent_stopping";

    /// <summary>Another SessionHost of the same session connected and replaces this one.</summary>
    public const string Replaced = "replaced";

    /// <summary>The peer failed the checks of the handshake.</summary>
    public const string Rejected = "rejected";

    /// <summary>The user logged off.</summary>
    public const string Logoff = "logoff";
}

/// <summary>The values of <see cref="SessionVariable.Type"/>.</summary>
public static class SessionVariableTypes
{
    /// <summary>Text.</summary>
    public const string Text = "string";

    /// <summary>A 64-bit integer.</summary>
    public const string WholeNumber = "int";

    /// <summary>A floating point number.</summary>
    public const string Number = "float";

    /// <summary>A true or false value.</summary>
    public const string Flag = "bool";
}

/// <summary>Exit codes of the SessionHost process.</summary>
public static class SessionHostExitCodes
{
    /// <summary>The SessionHost ended normally (the agent asked it to shut down).</summary>
    public const int Ok = 0;

    /// <summary>The command line is wrong.</summary>
    public const int Usage = 2;

    /// <summary>Another SessionHost already runs in this session; the supervisor must not restart in a loop.</summary>
    public const int AlreadyRunning = 3;

    /// <summary>The peer on the pipe failed the checks or speaks another protocol version.</summary>
    public const int Rejected = 4;

    /// <summary>Unexpected failure.</summary>
    public const int Failure = 1;
}
