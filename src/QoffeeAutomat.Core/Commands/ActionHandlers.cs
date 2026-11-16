using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Commands;

/// <summary>
/// Executes one type of action (PLAN.md S06): <c>process</c> and <c>builtin</c> are provided here, the
/// PowerShell action follows in T208 as another implementation. <c>sequence</c> is handled by the
/// engine itself because it starts other runs. A handler reports a failure to run through
/// <see cref="ExecutionResult.ErrorCode"/>; an exception becomes the error <c>internal</c>.
/// </summary>
public interface IActionHandler
{
    /// <summary>The <c>action.type</c> that the handler executes, see <see cref="ActionTypes"/>.</summary>
    string ActionType { get; }

    /// <summary>Executes the action.</summary>
    /// <param name="context">Everything about the run.</param>
    /// <param name="cancellationToken">Cancelled on timeout, restart, cancel and shutdown; the handler stops the work and kills what it started.</param>
    /// <returns>The outcome.</returns>
    ValueTask<ExecutionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken);
}

/// <summary>Everything that an <see cref="IActionHandler"/> needs to execute one run.</summary>
public sealed class ActionContext
{
    /// <summary>Creates the context.</summary>
    /// <param name="command">The command; its <see cref="CommandDocument.Action"/> is the action to execute.</param>
    /// <param name="variables">The validated variables from <see cref="VariableBinder"/> (string, long, double, bool); optional parameters without value are absent.</param>
    /// <param name="runId">The run id.</param>
    /// <param name="source">The source of the run, see <see cref="RunSources"/>.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="timeout">The timeout of the command.</param>
    /// <param name="environment">The environment variables for processes: <c>QA_*</c>, <c>QA_VAR_*</c> and the local channel.</param>
    /// <param name="launcher">Starts processes in the run context of the command.</param>
    public ActionContext(
        CommandDocument command,
        IReadOnlyDictionary<string, object> variables,
        string runId,
        string source,
        string agentName,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string> environment,
        IProcessLauncher launcher)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(launcher);

        Command = command;
        Variables = variables;
        RunId = runId;
        Source = source;
        AgentName = agentName;
        Timeout = timeout;
        Environment = environment;
        Launcher = launcher;
    }

    /// <summary>The command.</summary>
    public CommandDocument Command { get; }

    /// <summary>The validated variables.</summary>
    public IReadOnlyDictionary<string, object> Variables { get; }

    /// <summary>The run id.</summary>
    public string RunId { get; }

    /// <summary>The source of the run.</summary>
    public string Source { get; }

    /// <summary>The agent name.</summary>
    public string AgentName { get; }

    /// <summary>The timeout of the command; the engine also enforces it.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>The environment variables for processes.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }

    /// <summary>Starts processes in the run context of the command (system, user or user_elevated).</summary>
    public IProcessLauncher Launcher { get; }

    /// <summary>Formats a variable value the way <c>QA_VAR_&lt;NAME&gt;</c> and <c>{{Name}}</c> show it: invariant culture, <c>true</c> and <c>false</c> for booleans.</summary>
    /// <param name="value">A bound value.</param>
    /// <returns>The text.</returns>
    public static string FormatValue(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value switch
        {
            string text => text,
            bool flag => flag ? "true" : "false",
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    /// <summary>The context values that placeholders such as <c>{{qa.run_id}}</c> stand for.</summary>
    /// <returns>The values by placeholder name.</returns>
    public IReadOnlyDictionary<string, string> ContextValues()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["qa.run_id"] = RunId,
            ["qa.source"] = Source,
            ["qa.command_id"] = Command.Id,
            ["qa.agent_name"] = AgentName,
        };
    }
}

/// <summary>Starts a process in the run context that a command asks for (PLAN.md D-010, D-036).</summary>
public interface IProcessLauncher
{
    /// <summary>Starts the process and waits for it as the request says.</summary>
    /// <param name="runAs">One of <see cref="RunAsValues"/>.</param>
    /// <param name="request">What to start; arguments stay separate entries.</param>
    /// <param name="cancellationToken">Cancelling kills the process tree.</param>
    /// <returns>
    /// The outcome; <see cref="ErrorCodes.NoSession"/> and <see cref="ErrorCodes.ElevationUnavailable"/>
    /// when the context is not available, <see cref="ErrorCodes.Timeout"/> and
    /// <see cref="ErrorCodes.Cancelled"/> when the process was stopped.
    /// </returns>
    ValueTask<ExecutionResult> LaunchAsync(string runAs, ProcessLaunchRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The default <see cref="IProcessLauncher"/>: <c>system</c> runs in the agent process through the
/// <see cref="IProcessRunner"/>, <c>user</c> through the <see cref="ISessionBridge"/> and
/// <c>user_elevated</c> through the <see cref="IElevatedLauncher"/>.
/// </summary>
public sealed class RoutingProcessLauncher : IProcessLauncher
{
    private readonly IProcessRunner _runner;
    private readonly ISessionBridge? _session;
    private readonly IElevatedLauncher? _elevated;

    /// <summary>Creates the launcher.</summary>
    /// <param name="runner">Runs system processes.</param>
    /// <param name="session">The session bridge; null makes <c>user</c> commands fail with <c>no_session</c>.</param>
    /// <param name="elevated">The elevated launcher; null makes <c>user_elevated</c> commands fail with <c>elevation_unavailable</c>.</param>
    public RoutingProcessLauncher(IProcessRunner runner, ISessionBridge? session = null, IElevatedLauncher? elevated = null)
    {
        ArgumentNullException.ThrowIfNull(runner);

        _runner = runner;
        _session = session;
        _elevated = elevated;
    }

    /// <inheritdoc />
    public async ValueTask<ExecutionResult> LaunchAsync(string runAs, ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        switch (runAs)
        {
            case RunAsValues.System:
                if (request.OnOutput is { } receiver && request.WaitForExit && request.CaptureOutput)
                {
                    return await RunStreamingAsync(request, receiver, cancellationToken);
                }

                ProcessRunResult result = await _runner.RunAsync(request, cancellationToken);
                return result.ToExecutionResult();
            case RunAsValues.User:
                return _session is null
                    ? ExecutionResult.Failure(ErrorCodes.NoSession, "This platform has no user session.")
                    : await _session.RunAsync(request, cancellationToken);
            case RunAsValues.UserElevated:
                return _elevated is null
                    ? ExecutionResult.Failure(ErrorCodes.ElevationUnavailable, "This platform cannot start elevated processes.")
                    : await _elevated.StartAsync(request, cancellationToken);
            default:
                return ExecutionResult.Failure(ErrorCodes.Internal, $"Unknown runAs value '{runAs}'.");
        }
    }

    /// <summary>Runs the process and hands its output to the receiver while it runs.</summary>
    private async ValueTask<ExecutionResult> RunStreamingAsync(
        ProcessLaunchRequest request,
        Action<ProcessOutputChunk> receiver,
        CancellationToken cancellationToken)
    {
        await using IProcessRun run = _runner.Start(request, cancellationToken);
        Task pump = PumpAsync(run, receiver);
        ProcessRunResult result = await run.Completion;
        await pump;
        return result.ToExecutionResult();
    }

    private static async Task PumpAsync(IProcessRun run, Action<ProcessOutputChunk> receiver)
    {
        // The sequence ends when the run ends. A receiver that throws must not stop the process from being supervised.
        await foreach (ProcessOutputChunk chunk in run.Output)
        {
#pragma warning disable CA1031 // The receiver is a best-effort live view; the captured result is unaffected.
            try
            {
                receiver(chunk);
            }
            catch (Exception)
            {
                // Ignored on purpose.
            }
#pragma warning restore CA1031
        }
    }
}

/// <summary>Replaces <c>{{Name}}</c> placeholders in one string (PLAN.md S06, D-012).</summary>
public static class PlaceholderExpander
{
    private static readonly Regex Placeholder = new(
        @"\{\{(?<name>[^{}]*)\}\}",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Replaces placeholders in a single string. A placeholder names a variable or one of
    /// <c>qa.run_id</c>, <c>qa.source</c>, <c>qa.command_id</c>, <c>qa.agent_name</c>. The text is
    /// scanned once, so a value that itself contains <c>{{x}}</c> is not expanded again, and the result is
    /// always one string: the caller puts it into one argument, never splits it.
    /// </summary>
    /// <param name="template">The string, for example an entry of <c>args</c>.</param>
    /// <param name="context">The run.</param>
    /// <returns>The string with values inserted. A declared variable without a value becomes the empty string; an unknown placeholder stays as written.</returns>
    public static string Expand(string template, ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);

        if (!template.Contains("{{", StringComparison.Ordinal))
        {
            return template;
        }

        IReadOnlyDictionary<string, string> values = context.ContextValues();
        return Placeholder.Replace(template, match =>
        {
            string name = match.Groups["name"].Value;
            if (values.TryGetValue(name, out string? contextValue))
            {
                return contextValue;
            }

            if (context.Variables.TryGetValue(name, out object? variable))
            {
                return ActionContext.FormatValue(variable);
            }

            bool declared = context.Command.Parameters?.Any(parameter => string.Equals(parameter.Name, name, StringComparison.Ordinal)) == true;
            return declared ? string.Empty : match.Value;
        });
    }
}

/// <summary>Executes <c>process</c> actions: the executable is started without a shell and every argument is one argv entry (D-012).</summary>
public sealed class ProcessActionHandler : IActionHandler
{
    /// <inheritdoc />
    public string ActionType => ActionTypes.Process;

    /// <inheritdoc />
    public ValueTask<ExecutionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Command.Action is not ProcessAction action)
        {
            return ValueTask.FromResult(ExecutionResult.Failure(ErrorCodes.Internal, "The command does not have a process action."));
        }

        string[] arguments = [.. (action.Args ?? []).Select(argument => PlaceholderExpander.Expand(argument, context))];
        var request = new ProcessLaunchRequest(
            action.File,
            arguments,
            action.WorkingDirectory,
            context.Environment,
            context.Timeout,
            action.WaitForExit,
            context.Command.CaptureOutput);
        return context.Launcher.LaunchAsync(context.Command.RunAs, request, cancellationToken);
    }
}

/// <summary>
/// Executes <c>builtin</c> actions through the platform's <see cref="IBuiltinActionProvider"/>. The
/// variables are checked once more against the parameters of the built-in action (only the ones it
/// declares are passed on). A command that runs as the user fails with <c>no_session</c> without a
/// ready session, and <c>user_elevated</c> fails with <c>elevation_unavailable</c> unless the user is an
/// administrator.
/// </summary>
public sealed class BuiltinActionHandler : IActionHandler
{
    private readonly IBuiltinActionProvider _provider;
    private readonly ISessionBridge? _session;

    /// <summary>Creates the handler.</summary>
    /// <param name="provider">The platform's built-in actions.</param>
    /// <param name="session">The session bridge, to check the run context; null means there is no user session.</param>
    public BuiltinActionHandler(IBuiltinActionProvider provider, ISessionBridge? session = null)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _provider = provider;
        _session = session;
    }

    /// <inheritdoc />
    public string ActionType => ActionTypes.Builtin;

    /// <inheritdoc />
    public async ValueTask<ExecutionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Command.Action is not BuiltinAction action)
        {
            return ExecutionResult.Failure(ErrorCodes.Internal, "The command does not have a built-in action.");
        }

        BuiltinActionDescriptor? descriptor = _provider.Actions.FirstOrDefault(
            candidate => string.Equals(candidate.Name, action.Name, StringComparison.Ordinal));
        if (descriptor is null)
        {
            return ExecutionResult.Failure(ErrorCodes.Internal, $"The built-in action '{action.Name}' is not available on this platform.");
        }

        ExecutionResult? contextFailure = CheckRunContext(context.Command.RunAs);
        if (contextFailure is not null)
        {
            return contextFailure;
        }

        BindResult bound = BindInputs(descriptor, context.Variables);
        if (!bound.IsValid)
        {
            return ExecutionResult.Failure(
                ErrorCodes.InvalidVars,
                string.Join("; ", bound.Errors.Select(error => $"{error.Path}: {error.Message}")));
        }

        return await _provider.ExecuteAsync(descriptor.Name, bound.Values, cancellationToken);
    }

    private ExecutionResult? CheckRunContext(string runAs)
    {
        if (runAs == RunAsValues.System)
        {
            return null;
        }

        SessionState state = _session?.State ?? SessionState.None;
        if (!state.IsReady)
        {
            return ExecutionResult.Failure(ErrorCodes.NoSession, "Nobody is logged in.");
        }

        if (runAs == RunAsValues.UserElevated && !state.IsUserAdministrator)
        {
            return ExecutionResult.Failure(ErrorCodes.ElevationUnavailable, "The logged-in user is not an administrator.");
        }

        return null;
    }

    private static BindResult BindInputs(BuiltinActionDescriptor descriptor, IReadOnlyDictionary<string, object> variables)
    {
        (string, object?)[] members = [.. descriptor.Parameters
            .Where(parameter => variables.ContainsKey(parameter.Name))
            .Select(parameter => (parameter.Name, (object?)variables[parameter.Name]))];
        JsonElement document = EventData.Create(members);

        var supplied = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.EnumerateObject())
        {
            supplied[property.Name] = property.Value;
        }

        return VariableBinder.Bind(descriptor.Parameters, supplied);
    }
}
