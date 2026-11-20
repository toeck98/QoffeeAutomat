using System.Globalization;

namespace QoffeeAutomat.Agent.Hosting;

/// <summary>The verbs of the command-line interface.</summary>
public enum AgentVerb
{
    /// <summary>Runs the agent (the default).</summary>
    Run,

    /// <summary>Prints the version and exits.</summary>
    Version,

    /// <summary>Installs, removes and controls the Windows service (the Windows build, PLAN.md T301) or the systemd service (the Linux build, T802).</summary>
    Service,

    /// <summary>Sends a custom event to the running agent: <c>emit &lt;name&gt; [--data &lt;json&gt;]</c> (PLAN.md T217).</summary>
    Emit,

    /// <summary>Writes a new setup token so that the password can be set again in the UI (PLAN.md T212). Needs administrator or root.</summary>
    ResetPassword,

    /// <summary>Sets or reads the value of a sensor of type <c>state</c> in the running agent: <c>state set &lt;id&gt; &lt;value&gt;</c> and <c>state get &lt;id&gt;</c> (PLAN.md T714).</summary>
    State,

    /// <summary>
    /// Runs the session helper of the logged-in user: the process that the systemd user unit <c>qoffeeautomat-session.service</c> starts. It
    /// serves the agent on a Unix domain socket for commands in the user's context and for the metrics of the desktop (the Linux build,
    /// PLAN.md S17, T803).
    /// </summary>
    SessionHost,

    /// <summary>Joins or leaves the central hub, or reports the hub link of the running agent: <c>hub join &lt;string&gt;</c>, <c>hub leave</c>, <c>hub status</c> (PLAN.md S20, T905). Needs a local administrator or root.</summary>
    Hub,
}

/// <summary>What the verb <c>hub</c> does.</summary>
public enum HubAction
{
    /// <summary><c>hub join &lt;enrollment string&gt;</c>.</summary>
    Join,

    /// <summary><c>hub leave [--documents convert|remove]</c>.</summary>
    Leave,

    /// <summary><c>hub status</c>.</summary>
    Status,
}

/// <summary>What the verb <c>state</c> does.</summary>
public enum StateAction
{
    /// <summary><c>state set &lt;id&gt; &lt;value&gt;</c>.</summary>
    Set,

    /// <summary><c>state get &lt;id&gt;</c>.</summary>
    Get,
}

/// <summary>The parsed command line.</summary>
/// <param name="Verb">The verb; <see cref="AgentVerb.Run"/> when none is given.</param>
/// <param name="DataDir">Value of <c>--data-dir</c>.</param>
/// <param name="Platform">Value of <c>--platform</c>: <c>auto</c>, <c>simulated</c>, <c>windows</c> or <c>linux</c>.</param>
/// <param name="UiPort">Value of <c>--ui-port</c>; overrides the settings. Zero picks a free port.</param>
/// <param name="LinkPort">Value of <c>--link-port</c>; overrides the settings. Zero picks a free port.</param>
/// <param name="ShowHelp">True for <c>--help</c>.</param>
/// <param name="VerbArguments">The arguments after the verb <c>service</c>.</param>
/// <param name="PassThrough">Options that the agent does not know; they are handed to the ASP.NET Core configuration (for example <c>--environment</c>).</param>
/// <param name="Error">A message when the command line is invalid; null otherwise.</param>
/// <param name="EventName">The event name of the verb <c>emit</c>.</param>
/// <param name="EventData">The value of <c>--data</c> of the verb <c>emit</c>: a JSON object as text.</param>
/// <param name="StateAction">Whether the verb <c>state</c> sets or reads a value.</param>
/// <param name="StateSensorId">The sensor id of the verb <c>state</c>.</param>
/// <param name="StateValue">The value of <c>state set</c>, as typed (the agent converts it to the type of the sensor).</param>
/// <param name="HubAction">Whether the verb <c>hub</c> joins, leaves or reports.</param>
/// <param name="HubEnrollment">The enrollment string of <c>hub join</c>, or <c>-</c> to read it from standard input (it contains a token, which a process list would show).</param>
/// <param name="HubDocuments">The value of <c>--documents</c> of <c>hub leave</c>: <c>convert</c> or <c>remove</c>.</param>
public sealed record AgentOptions(
    AgentVerb Verb = AgentVerb.Run,
    string? DataDir = null,
    string? Platform = null,
    int? UiPort = null,
    int? LinkPort = null,
    bool ShowHelp = false,
    IReadOnlyList<string>? VerbArguments = null,
    IReadOnlyList<string>? PassThrough = null,
    string? Error = null,
    string? EventName = null,
    string? EventData = null,
    StateAction? StateAction = null,
    string? StateSensorId = null,
    string? StateValue = null,
    HubAction? HubAction = null,
    string? HubEnrollment = null,
    string? HubDocuments = null)
{
    /// <summary>The platforms that <c>--platform</c> accepts.</summary>
    public static readonly IReadOnlyList<string> KnownPlatforms = ["auto", "simulated", "windows", "linux"];
}

/// <summary>
/// Parses the command line: <c>qoffeeautomat [run|version|service] [options]</c>. Options the agent
/// does not know are kept in <see cref="AgentOptions.PassThrough"/> and given to the ASP.NET Core
/// configuration, which is how the test host and the service host set the environment and the
/// content root.
/// </summary>
public static class CommandLine
{
    /// <summary>The usage text.</summary>
    public const string Usage = """
        Usage: qoffeeautomat [run|version|service|emit|state|hub|reset-password|session-host] [options]

        Verbs:
          run                  Run the agent (default).
          version              Print the version and exit.
          service <install|uninstall|start|stop|status>
                               Install or control the Windows service (Windows build) or the systemd
                               service (Linux build); 'service --help' lists the options.
          emit <name> [--data <json>]
                               Send a custom event to the running agent, for example from a script
                               that the agent started. <json> must be an object.
          state set <id> <value>
                               Set the value of a sensor of type 'state' in the running agent; the value is
                               checked against the value type and the options of the sensor and published
                               at once. A value that starts with '-' goes after '--'.
          state get <id>       Print the value of a sensor of type 'state'.
          hub join <string>    Join the central hub with the enrollment string (qahub1.<code>) that the hub shows. This
                               gives the hub full control over this PC. Use '-' as the string to read it from
                               standard input. Needs a local administrator (Windows) or root (Linux).
          hub leave [--documents convert|remove]
                               Leave the hub; the credential is deleted and the hub link stops.
          hub status           Print the state of the hub link: not_joined, pending, connected, disconnected or
                               revoked.
          reset-password      Write a new setup token file so that a new password can be set in the
                               UI. Needs a local administrator (Windows) or root (Linux).
          session-host         Run the session helper of the logged-in user (Linux build). It is started by
                               the systemd user unit qoffeeautomat-session.service, not by hand.

        Options:
          --data-dir <path>    Keep all files below this directory (env QOFFEEAUTOMAT_DATA_DIR).
          --platform <name>    auto (default), simulated, windows or linux (env QOFFEE_PLATFORM).
          --ui-port <port>     Port of the loopback UI and API listener (default from settings, 8480).
          --link-port <port>   Port of the QoffeeLink listener (default from settings, 8481).
          --help               Show this text.
        """;

    /// <summary>Parses the arguments; a problem is reported in <see cref="AgentOptions.Error"/> instead of an exception.</summary>
    /// <param name="args">The arguments, as given to <c>Main</c>.</param>
    /// <returns>The parsed options.</returns>
    public static AgentOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var verb = AgentVerb.Run;
        bool verbSeen = false;
        bool showHelp = false;
        string? dataDir = null;
        string? platform = null;
        int? uiPort = null;
        int? linkPort = null;
        string? eventName = null;
        string? eventData = null;
        StateAction? stateAction = null;
        string? stateSensorId = null;
        string? stateValue = null;
        HubAction? hubAction = null;
        string? hubEnrollment = null;
        string? hubDocuments = null;
        bool onlyPositional = false;
        var verbArguments = new List<string>();
        var passThrough = new List<string>();

        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];

            if (verbSeen && verb == AgentVerb.Service)
            {
                verbArguments.Add(argument);
                continue;
            }

            // state <set|get> <id> [<value>]: a value may start with a single '-' (a negative number), and everything after '--'
            // is a positional argument.
            if (verbSeen && verb == AgentVerb.State
                && (onlyPositional
                    || !argument.StartsWith('-')
                    || (stateAction == StateAction.Set && stateSensorId is not null && stateValue is null
                        && !argument.StartsWith("--", StringComparison.Ordinal))))
            {
                string? problem = TakeStateArgument(argument, ref stateAction, ref stateSensorId, ref stateValue);
                if (problem is not null)
                {
                    return Fail(problem);
                }

                continue;
            }

            if (verbSeen && verb == AgentVerb.Emit && !argument.StartsWith('-'))
            {
                if (eventName is not null)
                {
                    return Fail($"emit takes one event name; '{argument}' is extra. Try --help.");
                }

                eventName = argument;
                continue;
            }

            // hub <join|leave|status> [<string>]: the enrollment string may be '-' (standard input).
            if (verbSeen && verb == AgentVerb.Hub && (!argument.StartsWith('-') || argument == "-"))
            {
                string? problem = TakeHubArgument(argument, ref hubAction, ref hubEnrollment);
                if (problem is not null)
                {
                    return Fail(problem);
                }

                continue;
            }

            if (!argument.StartsWith('-'))
            {
                if (verbSeen || !TryParseVerb(argument, out verb))
                {
                    return Fail($"Unknown command '{argument}'. Try --help.");
                }

                verbSeen = true;
                continue;
            }

            (string name, string? inlineValue) = SplitOption(argument);
            switch (name)
            {
                case "--" when verb == AgentVerb.State:
                    onlyPositional = true;
                    break;
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                case "--version":
                    verb = AgentVerb.Version;
                    verbSeen = true;
                    break;
                case "--data-dir":
                    if (!TryTakeValue(args, ref index, inlineValue, out dataDir) || string.IsNullOrWhiteSpace(dataDir))
                    {
                        return Fail("--data-dir needs a directory.");
                    }

                    break;
                case "--platform":
                    if (!TryTakeValue(args, ref index, inlineValue, out platform) || platform is null
                        || !AgentOptions.KnownPlatforms.Contains(platform.ToLowerInvariant()))
                    {
                        return Fail("--platform must be auto, simulated, windows or linux.");
                    }

                    platform = platform.ToLowerInvariant();
                    break;
                case "--ui-port":
                    if (!TryTakePort(args, ref index, inlineValue, out uiPort))
                    {
                        return Fail("--ui-port must be a number between 0 and 65535.");
                    }

                    break;
                case "--link-port":
                    if (!TryTakePort(args, ref index, inlineValue, out linkPort))
                    {
                        return Fail("--link-port must be a number between 0 and 65535.");
                    }

                    break;
                case "--documents":
                    if (verb != AgentVerb.Hub || hubAction != HubAction.Leave)
                    {
                        return Fail("--documents belongs to the hub leave verb: hub leave --documents convert|remove.");
                    }

                    if (!TryTakeValue(args, ref index, inlineValue, out hubDocuments)
                        || hubDocuments is not ("convert" or "remove"))
                    {
                        return Fail("--documents must be convert or remove.");
                    }

                    break;
                case "--data":
                    if (verb != AgentVerb.Emit)
                    {
                        return Fail("--data belongs to the emit verb: emit <name> --data <json>.");
                    }

                    if (!TryTakeValue(args, ref index, inlineValue, out eventData) || string.IsNullOrWhiteSpace(eventData))
                    {
                        return Fail("--data needs a JSON object.");
                    }

                    break;
                default:
                    passThrough.Add(argument);
                    if (inlineValue is null && index + 1 < args.Count && !args[index + 1].StartsWith('-'))
                    {
                        passThrough.Add(args[++index]);
                    }

                    break;
            }
        }

        if (verb == AgentVerb.Emit && eventName is null && !showHelp)
        {
            return Fail("emit needs an event name: emit <name> [--data <json>].");
        }

        if (verb == AgentVerb.State && !showHelp)
        {
            if (stateAction is null)
            {
                return Fail("state needs an action: state set <id> <value> or state get <id>.");
            }

            if (stateSensorId is null)
            {
                return Fail($"state {StateActionName(stateAction.Value)} needs a sensor id.");
            }

            if (stateAction == StateAction.Set && stateValue is null)
            {
                return Fail("state set needs a value: state set <id> <value>.");
            }
        }

        if (verb == AgentVerb.Hub && !showHelp)
        {
            if (hubAction is null)
            {
                return Fail("hub needs an action: hub join <string>, hub leave or hub status.");
            }

            if (hubAction == HubAction.Join && hubEnrollment is null)
            {
                return Fail("hub join needs the enrollment string: hub join <string>.");
            }
        }

        return new AgentOptions(
            verb,
            dataDir,
            platform,
            uiPort,
            linkPort,
            showHelp,
            verbArguments,
            passThrough,
            null,
            eventName,
            eventData,
            stateAction,
            stateSensorId,
            stateValue,
            hubAction,
            hubEnrollment,
            hubDocuments);
    }

    private static AgentOptions Fail(string message) => new(Error: message);

    private static string StateActionName(StateAction action) => action == StateAction.Set ? "set" : "get";

    /// <summary>Takes the next positional argument of the verb <c>state</c>: the action, then the sensor id, then (for <c>set</c>) the value.</summary>
    /// <returns>A message when the argument is not allowed; null otherwise.</returns>
    private static string? TakeStateArgument(string argument, ref StateAction? action, ref string? sensorId, ref string? value)
    {
        if (action is null)
        {
            switch (argument.ToLowerInvariant())
            {
                case "set":
                    action = StateAction.Set;
                    return null;
                case "get":
                    action = StateAction.Get;
                    return null;
                default:
                    return $"Unknown state action '{argument}'; use 'set' or 'get'. Try --help.";
            }
        }

        if (sensorId is null)
        {
            sensorId = argument;
            return null;
        }

        if (action == StateAction.Set && value is null)
        {
            value = argument;
            return null;
        }

        return $"state {StateActionName(action.Value)} takes {(action == StateAction.Set ? "an id and a value" : "one id")}; '{argument}' is extra. Try --help.";
    }

    /// <summary>Takes the next positional argument of the verb <c>hub</c>: the action, then (for <c>join</c>) the enrollment string.</summary>
    /// <returns>A message when the argument is not allowed; null otherwise.</returns>
    private static string? TakeHubArgument(string argument, ref HubAction? action, ref string? enrollment)
    {
        if (action is null)
        {
            switch (argument.ToLowerInvariant())
            {
                case "join":
                    action = HubAction.Join;
                    return null;
                case "leave":
                    action = HubAction.Leave;
                    return null;
                case "status":
                    action = HubAction.Status;
                    return null;
                default:
                    return $"Unknown hub action '{argument}'; use 'join', 'leave' or 'status'. Try --help.";
            }
        }

        if (action == HubAction.Join && enrollment is null)
        {
            enrollment = argument;
            return null;
        }

        // The argument is not echoed: it may be an enrollment string with a token.
        return $"hub {action.Value.ToString().ToLowerInvariant()} takes {(action == HubAction.Join ? "one enrollment string" : "no argument")}; there is an extra argument. Try --help.";
    }

    private static bool TryParseVerb(string text, out AgentVerb verb)
    {
        switch (text.ToLowerInvariant())
        {
            case "hub":
                verb = AgentVerb.Hub;
                return true;
            case "run":
                verb = AgentVerb.Run;
                return true;
            case "version":
                verb = AgentVerb.Version;
                return true;
            case "service":
                verb = AgentVerb.Service;
                return true;
            case "emit":
                verb = AgentVerb.Emit;
                return true;
            case "state":
                verb = AgentVerb.State;
                return true;
            case "reset-password":
                verb = AgentVerb.ResetPassword;
                return true;
            case "session-host":
                verb = AgentVerb.SessionHost;
                return true;
            default:
                verb = AgentVerb.Run;
                return false;
        }
    }

    private static (string Name, string? Value) SplitOption(string argument)
    {
        int equals = argument.IndexOf('=', StringComparison.Ordinal);
        return equals < 0 ? (argument, null) : (argument[..equals], argument[(equals + 1)..]);
    }

    private static bool TryTakeValue(IReadOnlyList<string> args, ref int index, string? inlineValue, out string? value)
    {
        if (inlineValue is not null)
        {
            value = inlineValue;
            return true;
        }

        if (index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = args[++index];
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryTakePort(IReadOnlyList<string> args, ref int index, string? inlineValue, out int? port)
    {
        port = null;
        if (!TryTakeValue(args, ref index, inlineValue, out string? text)
            || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            || parsed is < 0 or > 65535)
        {
            return false;
        }

        port = parsed;
        return true;
    }
}
