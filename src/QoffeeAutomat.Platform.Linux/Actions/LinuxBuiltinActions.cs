using System.Globalization;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Linux.Service;
using QoffeeAutomat.Platform.Linux.Sessions;

namespace QoffeeAutomat.Platform.Linux.Actions;

/// <summary>
/// The built-in actions of Linux as the agent sees them (PLAN.md S11, S17, T803). It lists all actions of <see cref="LinuxActionCatalog"/>
/// and executes the ones that belong to the service: the power actions through <c>systemctl</c> (logind decides, as it does for the
/// power button), <c>session.lock</c> and <c>session.logoff</c> through <c>loginctl</c>, and <c>power.cancel</c>. A power action with
/// <c>DelaySeconds</c> waits in the <see cref="LinuxPowerScheduler"/>. The actions of the user's desktop are handed to the session helper,
/// whose own provider (<see cref="LinuxUserActions"/>) executes them; without a helper they end with <c>no_session</c>. A name that is not
/// in the catalog does not exist on Linux, which is how the Windows-only actions are reported as unsupported.
/// </summary>
public sealed class LinuxBuiltinActions : IBuiltinActionProvider
{
    private const string Systemctl = "systemctl";
    private const string Loginctl = "loginctl";
    private const string Busctl = "busctl";

    private static readonly string[] SuspendArguments = ["suspend"];
    private static readonly string[] HibernateArguments = ["hibernate"];
    private static readonly TimeSpan SystemProgramTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SessionActionTimeout = TimeSpan.FromSeconds(60);

    private readonly ICommandRunner _runner;
    private readonly LinuxPowerScheduler _scheduler;
    private readonly ILinuxUserSession? _session;

    /// <summary>Creates the provider.</summary>
    /// <param name="runner">Runs <c>systemctl</c>, <c>loginctl</c> and <c>busctl</c>; tests pass a fake that never touches the computer.</param>
    /// <param name="scheduler">Holds the one delayed power action; null creates a scheduler of its own that shows no notification.</param>
    /// <param name="session">The session helper of the user, for the desktop actions and the id of the session; null means there is none.</param>
    public LinuxBuiltinActions(ICommandRunner runner, LinuxPowerScheduler? scheduler = null, ILinuxUserSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(runner);

        _runner = runner;
        _scheduler = scheduler ?? new LinuxPowerScheduler();
        _session = session;
    }

    /// <inheritdoc />
    public IReadOnlyList<BuiltinActionDescriptor> Actions => LinuxActionCatalog.All;

    /// <inheritdoc />
    public async ValueTask<ExecutionResult> ExecuteAsync(
        string name,
        IReadOnlyDictionary<string, object> variables,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(variables);
        cancellationToken.ThrowIfCancellationRequested();

        BuiltinActionDescriptor? descriptor = LinuxActionCatalog.Find(name);
        if (descriptor is null)
        {
            return new ExecutionResult(127, string.Empty, $"The built-in action '{name}' does not exist on Linux.", ErrorCodes.BadRequest);
        }

        if (descriptor.RunAs == RunAsValues.User)
        {
            return _session is null
                ? ExecutionResult.Failure(ErrorCodes.NoSession, "The desktop actions need the session helper of the logged-in user, which is not connected.")
                : await _session.RunBuiltinAsync(name, variables, SessionActionTimeout, cancellationToken).ConfigureAwait(false);
        }

        return name switch
        {
            LinuxActionNames.CancelPending => CancelAction(),
            LinuxActionNames.LockSession => await LockAsync(cancellationToken).ConfigureAwait(false),
            LinuxActionNames.Shutdown or LinuxActionNames.Restart or LinuxActionNames.Sleep or LinuxActionNames.Hibernate or LinuxActionNames.Logoff
                => await PowerAsync(name, descriptor.Title, variables, cancellationToken).ConfigureAwait(false),
            _ => ExecutionResult.Failure(ErrorCodes.Internal, $"The action '{name}' has no implementation."),
        };
    }

    /// <summary>Reads <c>DelaySeconds</c> of a power action and checks it once more, whatever the binder validated.</summary>
    /// <param name="variables">The variables of the run.</param>
    /// <param name="delaySeconds">The delay; 0 when the variable is absent.</param>
    /// <returns>False when the value is outside 0 to <see cref="PowerActionSpec.MaxDelaySeconds"/>.</returns>
    public static bool TryReadDelay(IReadOnlyDictionary<string, object> variables, out int delaySeconds)
    {
        long? value = BuiltinActionVariables.GetLong(variables, PowerActionSpec.DelaySeconds);
        if (value is null)
        {
            delaySeconds = 0;
            return !variables.ContainsKey(PowerActionSpec.DelaySeconds);
        }

        delaySeconds = (int)Math.Clamp(value.Value, int.MinValue, int.MaxValue);
        return value is >= 0 and <= PowerActionSpec.MaxDelaySeconds;
    }

    /// <summary>Reads the output of <c>busctl call ... CanSuspend</c> (<c>s "yes"</c>).</summary>
    /// <param name="output">What <c>busctl</c> wrote.</param>
    /// <returns>True when logind says that the action is possible (<c>yes</c> or <c>challenge</c>); false for <c>no</c> and <c>na</c>; null when the answer is not understood.</returns>
    public static bool? ParseCanAnswer(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        int first = output.IndexOf('"', StringComparison.Ordinal);
        int last = output.LastIndexOf('"');
        if (first < 0 || last <= first)
        {
            return null;
        }

        return output[(first + 1)..last] switch
        {
            "yes" or "challenge" => true,
            "no" or "na" => false,
            _ => null,
        };
    }

    private static ExecutionResult Done(string message) => new(0, message, string.Empty);

    private static ExecutionResult Refused(string message) => new(1, string.Empty, message);

    private static ExecutionResult Invalid(string message) => ExecutionResult.Failure(ErrorCodes.InvalidVars, message);

    private ExecutionResult CancelAction()
    {
        LinuxPendingPowerAction? cancelled = _scheduler.Cancel(LinuxPowerCancelSources.Action);
        return Done(cancelled is null
            ? "No delayed power action was pending."
            : "The delayed " + cancelled.Title.ToLowerInvariant() + " was cancelled.");
    }

    private async ValueTask<ExecutionResult> LockAsync(CancellationToken cancellationToken)
    {
        string? session = _session?.SessionId;
        if (session is null)
        {
            return ExecutionResult.Failure(ErrorCodes.NoSession, "Nobody is logged in at the computer.");
        }

        return await RunProgramAsync(Loginctl, ["lock-session", "--", session], "Lock", "The session was asked to lock.", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ExecutionResult> PowerAsync(
        string name,
        string title,
        IReadOnlyDictionary<string, object> variables,
        CancellationToken cancellationToken)
    {
        if (!TryReadDelay(variables, out int delaySeconds))
        {
            return Invalid("DelaySeconds must be a whole number from 0 to " + PowerActionSpec.MaxDelaySeconds.ToString(CultureInfo.InvariantCulture) + ".");
        }

        string? message = BuiltinActionVariables.GetString(variables, PowerActionSpec.Message);
        message = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        if (message is { Length: > PowerActionSpec.MaxMessageLength })
        {
            return Invalid("Message may have at most " + PowerActionSpec.MaxMessageLength.ToString(CultureInfo.InvariantCulture) + " characters.");
        }

        bool force = BuiltinActionVariables.GetBool(variables, "Force");

        // Whether the computer can do it is checked before the delay starts, so nobody is told that it will sleep in five minutes when it cannot.
        ExecutionResult? refusal = await CheckAvailableAsync(name, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        if (name == LinuxActionNames.Logoff && _session?.SessionId is null)
        {
            return ExecutionResult.Failure(ErrorCodes.NoSession, "Nobody is logged in at the computer.");
        }

        if (delaySeconds == 0)
        {
            return await ExecuteNowAsync(name, force, cancellationToken).ConfigureAwait(false);
        }

        return await _scheduler.RunDelayedAsync(
            name,
            title,
            delaySeconds,
            message,
            token => ExecuteNowAsync(name, force, token),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ExecutionResult?> CheckAvailableAsync(string name, CancellationToken cancellationToken)
    {
        string? method = name switch
        {
            LinuxActionNames.Sleep => "CanSuspend",
            LinuxActionNames.Hibernate => "CanHibernate",
            _ => null,
        };
        if (method is null)
        {
            return null;
        }

        CommandResult? answer = await _runner.RunAsync(
            Busctl,
            ["--system", "call", "org.freedesktop.login1", "/org/freedesktop/login1", "org.freedesktop.login1.Manager", method],
            SystemProgramTimeout,
            cancellationToken).ConfigureAwait(false);

        // An answer that cannot be read (no busctl, no logind in a container) lets the real request decide.
        bool? possible = answer is { Succeeded: true } ? ParseCanAnswer(answer.StandardOutput) : null;
        if (possible == false)
        {
            return name == LinuxActionNames.Hibernate
                ? Refused("Hibernation is not available on this computer (no swap space that can hold the memory, or the kernel does not allow it).")
                : Refused("This computer cannot sleep.");
        }

        return null;
    }

    private async ValueTask<ExecutionResult> ExecuteNowAsync(string name, bool force, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case LinuxActionNames.Shutdown:
                return await RunProgramAsync(Systemctl, Power("poweroff", force), "Shut down", "Shutdown was requested.", cancellationToken).ConfigureAwait(false);
            case LinuxActionNames.Restart:
                return await RunProgramAsync(Systemctl, Power("reboot", force), "Restart", "Restart was requested.", cancellationToken).ConfigureAwait(false);
            case LinuxActionNames.Sleep:
                return await RunProgramAsync(Systemctl, SuspendArguments, "Sleep", "Sleep was requested.", cancellationToken).ConfigureAwait(false);
            case LinuxActionNames.Hibernate:
                return await RunProgramAsync(Systemctl, HibernateArguments, "Hibernate", "Hibernation was requested.", cancellationToken).ConfigureAwait(false);
            case LinuxActionNames.Logoff:
                string? session = _session?.SessionId;
                return session is null
                    ? ExecutionResult.Failure(ErrorCodes.NoSession, "Nobody is logged in at the computer.")
                    : await RunProgramAsync(Loginctl, ["terminate-session", "--", session], "Log off", "The session was ended.", cancellationToken).ConfigureAwait(false);
            default:
                return ExecutionResult.Failure(ErrorCodes.Internal, $"The action '{name}' has no implementation.");
        }
    }

    private static string[] Power(string verb, bool force) => force ? [verb, "--ignore-inhibitors"] : [verb];

    private async ValueTask<ExecutionResult> RunProgramAsync(
        string program,
        IReadOnlyList<string> arguments,
        string title,
        string success,
        CancellationToken cancellationToken)
    {
        CommandResult? result = await _runner.RunAsync(program, arguments, SystemProgramTimeout, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return new ExecutionResult(1, string.Empty, $"{title} failed: {program} is not installed or did not answer.");
        }

        return result.Succeeded
            ? Done(success)
            : new ExecutionResult(result.ExitCode, string.Empty, $"{title} failed: {FirstLine(result.StandardError, program + " reported an error.")}");
    }

    private static string FirstLine(string text, string fallback)
    {
        string line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        return line.Length == 0 ? fallback : line;
    }
}
