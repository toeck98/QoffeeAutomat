using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QoffeeAutomat.Contracts.Session;

/// <summary>
/// A message on the session pipe (PLAN.md S11). On the wire it is a JSON object whose member <c>t</c> names the
/// message type; the members use snake_case like the QoffeeLink frames. The messages are:
/// <c>hello</c>, <c>run</c>, <c>run_output</c>, <c>run_result</c>, <c>cancel</c>, <c>metrics_subscribe</c>,
/// <c>metrics_state</c>, <c>metric_options</c> (T713), <c>notify</c>, <c>event</c>, <c>shutdown</c>, <c>tray_state</c>, <c>set_pause</c>,
/// <c>login_token_request</c>, <c>login_token</c> (T711), <c>hotkeys</c> and <c>hotkey_state</c> (T708). A notification from
/// Home Assistant is a <c>run</c> of the built-in action <c>notify.toast</c>; the press of one of its buttons comes back as the
/// <c>event</c> <c>notification_action</c> (T702).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(SessionHelloMessage), "hello")]
[JsonDerivedType(typeof(SessionRunMessage), "run")]
[JsonDerivedType(typeof(SessionRunOutputMessage), "run_output")]
[JsonDerivedType(typeof(SessionRunResultMessage), "run_result")]
[JsonDerivedType(typeof(SessionCancelMessage), "cancel")]
[JsonDerivedType(typeof(SessionMetricsSubscribeMessage), "metrics_subscribe")]
[JsonDerivedType(typeof(SessionMetricsStateMessage), "metrics_state")]
[JsonDerivedType(typeof(SessionMetricOptionsMessage), "metric_options")]
[JsonDerivedType(typeof(SessionNotifyMessage), "notify")]
[JsonDerivedType(typeof(SessionEventMessage), "event")]
[JsonDerivedType(typeof(SessionShutdownMessage), "shutdown")]
[JsonDerivedType(typeof(SessionTrayStateMessage), "tray_state")]
[JsonDerivedType(typeof(SessionSetPauseMessage), "set_pause")]
[JsonDerivedType(typeof(SessionLoginTokenRequestMessage), "login_token_request")]
[JsonDerivedType(typeof(SessionLoginTokenMessage), "login_token")]
[JsonDerivedType(typeof(SessionHotkeysMessage), "hotkeys")]
[JsonDerivedType(typeof(SessionHotkeyStateMessage), "hotkey_state")]
public abstract record SessionMessage;

/// <summary>
/// The first message in each direction. The SessionHost sends it first; the agent answers with its own after it
/// verified the peer. The handshake is complete when the SessionHost received the agent's answer.
/// </summary>
/// <param name="ProtocolVersion">The <see cref="SessionIpc.ProtocolVersion"/> of the sender.</param>
/// <param name="Role">One of <see cref="SessionRoles"/>.</param>
/// <param name="ProcessId">The process id of the sender.</param>
/// <param name="SessionId">The Windows session id of the SessionHost; the agent sends <c>0</c>.</param>
/// <param name="Version">The product version of the sender.</param>
/// <param name="UserName">The user of the session (SessionHost only).</param>
/// <param name="IsAdministrator">The user is an administrator, with or without a split token (SessionHost only).</param>
/// <param name="ScriptsDirectory">The scripts directory that <c>source: file</c> scripts refer to (agent only).</param>
public sealed record SessionHelloMessage(
    int ProtocolVersion,
    string Role,
    int ProcessId,
    int SessionId,
    string Version,
    string? UserName = null,
    bool IsAdministrator = false,
    string? ScriptsDirectory = null) : SessionMessage;

/// <summary>A variable value; text, so that integers, floating point numbers and booleans survive the trip unchanged.</summary>
/// <param name="Type">One of <see cref="SessionVariableTypes"/>.</param>
/// <param name="Value">The value in invariant culture, <c>true</c> or <c>false</c> for booleans.</param>
public sealed record SessionVariable(string Type, string Value)
{
    /// <summary>Converts a bound value (string, bool, integer or floating point number).</summary>
    /// <param name="value">The value as produced by the variable binder.</param>
    /// <returns>The wire form.</returns>
    /// <exception cref="ArgumentException">The type is not supported.</exception>
    public static SessionVariable From(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value switch
        {
            string text => new SessionVariable(SessionVariableTypes.Text, text),
            bool flag => new SessionVariable(SessionVariableTypes.Flag, flag ? "true" : "false"),
            long integer => new SessionVariable(SessionVariableTypes.WholeNumber, integer.ToString(CultureInfo.InvariantCulture)),
            int integer => new SessionVariable(SessionVariableTypes.WholeNumber, integer.ToString(CultureInfo.InvariantCulture)),
            double number => new SessionVariable(SessionVariableTypes.Number, number.ToString("R", CultureInfo.InvariantCulture)),
            float number => new SessionVariable(SessionVariableTypes.Number, ((double)number).ToString("R", CultureInfo.InvariantCulture)),
            _ => throw new ArgumentException($"Variables of type {value.GetType().Name} cannot be sent to a session.", nameof(value)),
        };
    }

    /// <summary>Converts the wire form back to the bound value.</summary>
    /// <returns>A string, long, double or bool.</returns>
    /// <exception cref="FormatException">The value does not match the type.</exception>
    public object ToValue()
    {
        return Type switch
        {
            SessionVariableTypes.Text => Value,
            SessionVariableTypes.Flag => bool.Parse(Value),
            SessionVariableTypes.WholeNumber => long.Parse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            SessionVariableTypes.Number => double.Parse(Value, NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => throw new FormatException($"Unknown variable type '{Type}'."),
        };
    }

    /// <summary>Converts a set of bound values.</summary>
    /// <param name="values">The values by variable name.</param>
    /// <returns>The wire forms.</returns>
    public static IReadOnlyDictionary<string, SessionVariable> FromAll(IReadOnlyDictionary<string, object> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var result = new Dictionary<string, SessionVariable>(values.Count, StringComparer.Ordinal);
        foreach ((string name, object value) in values)
        {
            result[name] = From(value);
        }

        return result;
    }

    /// <summary>Converts a set of wire forms back to bound values.</summary>
    /// <param name="variables">The wire forms by variable name.</param>
    /// <returns>The values.</returns>
    /// <exception cref="FormatException">A value does not match its type.</exception>
    public static IReadOnlyDictionary<string, object> ToAll(IReadOnlyDictionary<string, SessionVariable> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        var result = new Dictionary<string, object>(variables.Count, StringComparer.Ordinal);
        foreach ((string name, SessionVariable variable) in variables)
        {
            result[name] = variable.ToValue();
        }

        return result;
    }
}

/// <summary>An executable to start without a shell; arguments stay separate entries (PLAN.md D-012).</summary>
/// <param name="FileName">The executable.</param>
/// <param name="Arguments">The arguments, one entry per argv element.</param>
/// <param name="EnvironmentVariables">Extra environment variables (<c>QA_*</c>, <c>QA_VAR_*</c>).</param>
/// <param name="WorkingDirectory">The working directory, or absent for the default.</param>
/// <param name="WaitForExit">False starts the process and answers at once (programs with a window).</param>
public sealed record SessionProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    string? WorkingDirectory = null,
    bool WaitForExit = true);

/// <summary>PowerShell code or script to run in the session through the wrapper of Core (PLAN.md D-011, D-012).</summary>
/// <param name="Source">The source: <c>inline</c> or <c>file</c>.</param>
/// <param name="Engine">The engine: <c>auto</c>, <c>pwsh</c> or <c>powershell</c>.</param>
/// <param name="Variables">The validated variables; bound as named parameters, never spliced into the code.</param>
/// <param name="EnvironmentVariables">Extra environment variables (<c>QA_*</c>, <c>QA_VAR_*</c>).</param>
/// <param name="Code">The code when the source is inline.</param>
/// <param name="Script">The file name in the scripts directory when the source is a file.</param>
public sealed record SessionPowerShellSpec(
    string Source,
    string Engine,
    IReadOnlyDictionary<string, SessionVariable> Variables,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    string? Code = null,
    string? Script = null);

/// <summary>
/// A bash script to run in the session (PLAN.md D-012, T803). The values travel as environment variables and as positional arguments;
/// they are never part of the script text or of the command line of the shell. A script that lies in the scripts directory of the
/// agent is sent as inline code, because the directory belongs to root and the helper runs as the user.
/// </summary>
/// <param name="Source">The source: <c>inline</c> or <c>file</c>.</param>
/// <param name="Arguments">The positional arguments <c>$1</c>, <c>$2</c> and so on, one entry per argument.</param>
/// <param name="EnvironmentVariables">Extra environment variables (<c>QA_*</c>, <c>QA_VAR_*</c>).</param>
/// <param name="Code">The script when the source is inline.</param>
/// <param name="Script">The file name in the scripts directory when the source is a file.</param>
public sealed record SessionBashSpec(
    string Source,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    string? Code = null,
    string? Script = null);

/// <summary>A built-in action of the SessionHost.</summary>
/// <param name="Name">The action name, for example <c>audio.set_volume</c>.</param>
/// <param name="Variables">The validated variables.</param>
public sealed record SessionBuiltinSpec(string Name, IReadOnlyDictionary<string, SessionVariable> Variables);

/// <summary>Agent to SessionHost: execute something in the user's context. Exactly one spec matches <see cref="Kind"/>.</summary>
/// <param name="RunId">An id chosen by the agent; the answers carry it.</param>
/// <param name="Kind">One of <see cref="SessionRunKinds"/>.</param>
/// <param name="TimeoutMs">How long the run may take; the process tree is killed afterwards. Ignored when the process is not awaited.</param>
/// <param name="CaptureOutput">Whether standard output and error are captured (and streamed).</param>
/// <param name="Process">The process, for kind <c>process</c>.</param>
/// <param name="PowerShell">The script, for kind <c>powershell</c>.</param>
/// <param name="Builtin">The action, for kind <c>builtin</c>.</param>
/// <param name="Bash">The script, for kind <c>bash</c> (T803; an addition to protocol version 1, which a peer without it ignores).</param>
public sealed record SessionRunMessage(
    string RunId,
    string Kind,
    long TimeoutMs,
    bool CaptureOutput = true,
    SessionProcessSpec? Process = null,
    SessionPowerShellSpec? PowerShell = null,
    SessionBuiltinSpec? Builtin = null,
    SessionBashSpec? Bash = null) : SessionMessage;

/// <summary>SessionHost to agent: output of a running command, as it arrives.</summary>
/// <param name="RunId">The run.</param>
/// <param name="Stream">One of <see cref="SessionOutputStreams"/>.</param>
/// <param name="Text">The text; a chunk is not necessarily a whole line.</param>
/// <param name="Ts">Unix time in milliseconds when the chunk was read.</param>
public sealed record SessionRunOutputMessage(string RunId, string Stream, string Text, long Ts) : SessionMessage;

/// <summary>SessionHost to agent: how a run ended. Every <c>run</c> is answered with exactly one.</summary>
/// <param name="RunId">The run.</param>
/// <param name="ExitCode">The exit code; <c>-1</c> when the work could not be started.</param>
/// <param name="StandardOutput">Captured standard output (at most 64 KiB).</param>
/// <param name="StandardError">Captured standard error (at most 64 KiB); the message of a failure.</param>
/// <param name="DurationMs">How long the run took.</param>
/// <param name="ErrorCode">A QoffeeLink error code when the work could not be started, timed out or was cancelled.</param>
/// <param name="OutputTruncated">More output was produced than is kept.</param>
public sealed record SessionRunResultMessage(
    string RunId,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    long DurationMs,
    string? ErrorCode = null,
    bool OutputTruncated = false) : SessionMessage;

/// <summary>Agent to SessionHost: stop a run and kill its process tree. The run is still answered with a <c>run_result</c>.</summary>
/// <param name="RunId">The run.</param>
public sealed record SessionCancelMessage(string RunId) : SessionMessage;

/// <summary>Agent to SessionHost: the user metrics to sample (replaces the previous subscription); empty stops sampling.</summary>
/// <param name="MetricIds">The metric ids.</param>
public sealed record SessionMetricsSubscribeMessage(IReadOnlyList<string> MetricIds) : SessionMessage;

/// <summary>SessionHost to agent: the values of the subscribed user metrics; null means unavailable.</summary>
/// <param name="Ts">Unix time in milliseconds.</param>
/// <param name="Values">The values by metric id.</param>
public sealed record SessionMetricsStateMessage(long Ts, IReadOnlyDictionary<string, JsonElement> Values) : SessionMessage;

/// <summary>
/// SessionHost to agent: the options of the metrics whose options are not fixed (T713, for example the audio devices). The
/// message is sent after the handshake and whenever an option set changes; it names every such metric and replaces what the agent
/// knew, so a metric that is missing has no options (the SessionHost cannot see any). It is additive: protocol version 1 stays.
/// </summary>
/// <param name="Options">The options by metric id, in the order that the manifest announces them.</param>
public sealed record SessionMetricOptionsMessage(IReadOnlyDictionary<string, IReadOnlyList<string>> Options) : SessionMessage;

/// <summary>Agent to SessionHost: show a notification to the user.</summary>
/// <param name="Title">The title.</param>
/// <param name="Message">The text.</param>
public sealed record SessionNotifyMessage(string Title, string Message) : SessionMessage;

/// <summary>SessionHost to agent: something happened in the session (for example the screen was locked).</summary>
/// <param name="Type">The event type.</param>
/// <param name="Ts">Unix time in milliseconds.</param>
/// <param name="Data">Optional event data (an object).</param>
public sealed record SessionEventMessage(string Type, long Ts, JsonElement? Data = null) : SessionMessage;

/// <summary>Either direction: the sender closes the connection. A SessionHost that receives it ends.</summary>
/// <param name="Reason">One of <see cref="SessionShutdownReasons"/>.</param>
public sealed record SessionShutdownMessage(string Reason) : SessionMessage;

/// <summary>
/// Agent to SessionHost: what the tray icon shows (PLAN.md S11, T310). The agent sends it right after the handshake and
/// again whenever one of the values changes, so the icon never has to ask.
/// </summary>
/// <param name="AgentName">The agent name, for the tooltip.</param>
/// <param name="Paused">The global pause flag: every run is rejected with <c>paused</c> while it is set.</param>
/// <param name="PairedClients">The number of paired Home Assistant instances (link clients); zero means not paired.</param>
/// <param name="ConnectedClients">The number of open QoffeeLink connections.</param>
/// <param name="ShowTrayIcon">False hides the icon (the setting <c>trayIcon</c>).</param>
/// <param name="UiUrl">The loopback address of the UI, for the menu entry Open UI; absent when the agent knows none.</param>
public sealed record SessionTrayStateMessage(
    string AgentName,
    bool Paused,
    int PairedClients,
    int ConnectedClients,
    bool ShowTrayIcon,
    string? UiUrl = null) : SessionMessage;

/// <summary>
/// SessionHost to agent: the user chose Pause commands or Resume commands in the tray menu. The agent applies it only when
/// it comes from the SessionHost of the active console session and answers with a <c>tray_state</c> either way.
/// </summary>
/// <param name="Paused">The wanted value of the global pause flag.</param>
public sealed record SessionSetPauseMessage(bool Paused) : SessionMessage;

/// <summary>
/// SessionHost to agent: the user chose Open UI in the tray menu and wants a single-use login token (PLAN.md S09 item 12,
/// T711). The agent answers with exactly one <c>login_token</c>. It never trusts the <c>is_administrator</c> flag of the
/// <c>hello</c> for this: it looks at the token of the user that the operating system holds for the session, and it answers
/// only the SessionHost of the active console session.
/// </summary>
/// <param name="RequestId">An id chosen by the SessionHost; the answer carries it.</param>
public sealed record SessionLoginTokenRequestMessage(string RequestId) : SessionMessage;

/// <summary>
/// Agent to SessionHost: the answer to <c>login_token_request</c>. Either <see cref="Token"/> or <see cref="Reason"/> is set.
/// A standard user always gets a reason and never a token; the SessionHost then opens the normal login page.
/// </summary>
/// <param name="RequestId">The id of the request.</param>
/// <param name="Token">The single-use token (valid 30 seconds, loopback only); absent when none was issued.</param>
/// <param name="Reason">One of <see cref="SessionLoginDenials"/> when no token was issued.</param>
public sealed record SessionLoginTokenMessage(string RequestId, string? Token = null, string? Reason = null) : SessionMessage;

/// <summary>One global hotkey that the SessionHost registers (PLAN.md S16, T708).</summary>
/// <param name="Name">The name of the hotkey; it is the <c>name</c> in the event <c>hotkey_pressed</c>.</param>
/// <param name="Keys">The key combination, for example <c>Ctrl+Alt+F9</c>; the SessionHost parses it.</param>
public sealed record SessionHotkeyDefinition(string Name, string Keys);

/// <summary>
/// Agent to SessionHost: the complete list of hotkeys to register. It replaces the previous list; hotkeys that are no longer
/// in it are unregistered. The agent sends it after the handshake and whenever the settings change.
/// </summary>
/// <param name="Hotkeys">The hotkeys.</param>
public sealed record SessionHotkeysMessage(IReadOnlyList<SessionHotkeyDefinition> Hotkeys) : SessionMessage;

/// <summary>What became of one hotkey.</summary>
/// <param name="Name">The name of the hotkey.</param>
/// <param name="Keys">The key combination as configured.</param>
/// <param name="State">One of <see cref="SessionHotkeyStates"/>.</param>
/// <param name="Message">An explanation when the state is not <c>registered</c>.</param>
public sealed record SessionHotkeyStatus(string Name, string Keys, string State, string? Message = null);

/// <summary>
/// SessionHost to agent: the outcome of the last <c>hotkeys</c> message, one entry per hotkey in it. The agent shows the
/// conflicts in the UI. A press of a registered hotkey is reported as the <c>event</c> <c>hotkey_pressed</c> with the data
/// <c>name</c>.
/// </summary>
/// <param name="Hotkeys">The status of every hotkey of the last list.</param>
public sealed record SessionHotkeyStateMessage(IReadOnlyList<SessionHotkeyStatus> Hotkeys) : SessionMessage;

/// <summary>The values of <see cref="SessionLoginTokenMessage.Reason"/>.</summary>
public static class SessionLoginDenials
{
    /// <summary>The user of the session is not a member of the local Administrators group.</summary>
    public const string NotAdministrator = "not_administrator";

    /// <summary>The request did not come from the SessionHost of the active console session.</summary>
    public const string NotConsole = "not_console";

    /// <summary>The agent could not find out who the user is, or cannot issue tokens on this build.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>The values of <see cref="SessionHotkeyStatus.State"/>.</summary>
public static class SessionHotkeyStates
{
    /// <summary>The hotkey is registered; pressing it raises <c>hotkey_pressed</c>.</summary>
    public const string Registered = "registered";

    /// <summary>Windows refused the registration because another program or another hotkey already owns the combination.</summary>
    public const string Conflict = "conflict";

    /// <summary>The key combination cannot be understood or Windows rejected it for another reason.</summary>
    public const string Invalid = "invalid";
}

/// <summary>The <c>type</c> of the <c>event</c> message that reports the press of a notification button (T702).</summary>
public static class SessionNotificationEvents
{
    /// <summary>The event type <c>notification_action</c>; the data has the member <c>action</c> (the id of the button) and optionally <c>tag</c>.</summary>
    public const string Action = "notification_action";
}

/// <summary>The <c>type</c> of the <c>event</c> message that reports a hotkey press.</summary>
public static class SessionHotkeyEvents
{
    /// <summary>The event type <c>hotkey_pressed</c>; the data has the member <c>name</c>.</summary>
    public const string Pressed = "hotkey_pressed";
}
