using System.ComponentModel;
using System.Globalization;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Actions.Power;

namespace QoffeeAutomat.Platform.Windows.Actions;

/// <summary>
/// The built-in actions of Windows as the agent sees them (PLAN.md S11, T306, T715). It lists all actions of
/// <see cref="WindowsActionCatalog"/> and executes the ones that need the service itself: the power actions through the
/// <see cref="IPowerNativeApi"/>, <c>power.keep_awake</c> through the <see cref="KeepAwakeService"/> and <c>power.cancel</c>
/// through the <see cref="PowerActionScheduler"/>. A power action with <c>DelaySeconds</c> waits in the scheduler, which shows the
/// countdown dialog and carries the action out when the time is over. The actions of the user's desktop are listed too, because the
/// decorator <see cref="Sessions.UserContextBuiltinActions"/> reads the context from these descriptors and hands such an action to
/// the SessionHost, whose own provider executes it; this class never does.
/// </summary>
public sealed class WindowsBuiltinActions : IBuiltinActionProvider
{
    private readonly IPowerNativeApi _power;
    private readonly PowerActionScheduler _scheduler;
    private readonly KeepAwakeService? _keepAwake;

    /// <summary>Creates the provider.</summary>
    /// <param name="power">The calls that change the power state; tests pass a fake that never touches the computer.</param>
    /// <param name="scheduler">Holds the one delayed power action; null creates a scheduler of its own that shows no dialog.</param>
    /// <param name="keepAwake">Keeps the computer awake for <c>power.keep_awake</c>; null makes that action fail with a message.</param>
    public WindowsBuiltinActions(IPowerNativeApi power, PowerActionScheduler? scheduler = null, KeepAwakeService? keepAwake = null)
    {
        ArgumentNullException.ThrowIfNull(power);

        _power = power;
        _scheduler = scheduler ?? new PowerActionScheduler();
        _keepAwake = keepAwake;
    }

    /// <inheritdoc />
    public IReadOnlyList<BuiltinActionDescriptor> Actions => WindowsActionCatalog.All;

    /// <inheritdoc />
    public async ValueTask<ExecutionResult> ExecuteAsync(
        string name,
        IReadOnlyDictionary<string, object> variables,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(variables);
        cancellationToken.ThrowIfCancellationRequested();

        BuiltinActionDescriptor? descriptor = WindowsActionCatalog.Find(name);
        if (descriptor is null)
        {
            return new ExecutionResult(127, string.Empty, $"Unknown built-in action '{name}'.", ErrorCodes.BadRequest);
        }

        if (descriptor.RunAs != RunAsValues.System)
        {
            // Reached only without the session decorator, which would have sent the action to the SessionHost.
            return ExecutionResult.Failure(
                ErrorCodes.Internal,
                $"The action '{name}' runs in the session of the user and cannot run in the service.");
        }

        try
        {
            return await RunAsync(name, descriptor.Title, variables, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return new ExecutionResult(1, string.Empty, $"{descriptor.Title} failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Reads <c>DelaySeconds</c> of a power action and checks it once more, whatever the binder validated.
    /// </summary>
    /// <param name="variables">The variables of the run.</param>
    /// <param name="delaySeconds">The delay; 0 when the variable is absent.</param>
    /// <returns>False when the value is outside 0 to <see cref="PowerActionSpec.MaxDelaySeconds"/>.</returns>
    internal static bool TryReadDelay(IReadOnlyDictionary<string, object> variables, out int delaySeconds)
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

    /// <summary>Reads <c>Message</c> of a power action; a blank text is no message.</summary>
    /// <param name="variables">The variables of the run.</param>
    /// <returns>The text, or null.</returns>
    internal static string? ReadMessage(IReadOnlyDictionary<string, object> variables)
    {
        string? message = BuiltinActionVariables.GetString(variables, PowerActionSpec.Message);
        return string.IsNullOrWhiteSpace(message) ? null : message.Trim();
    }

    private static ExecutionResult Done(string message) => new(0, message, string.Empty);

    private static ExecutionResult Refused(string message) => new(1, string.Empty, message);

    private static ExecutionResult Invalid(string message) => ExecutionResult.Failure(ErrorCodes.InvalidVars, message);

    private async ValueTask<ExecutionResult> RunAsync(
        string name,
        string title,
        IReadOnlyDictionary<string, object> variables,
        CancellationToken cancellationToken)
    {
        switch (name)
        {
            case WindowsActionNames.KeepAwake:
                return KeepAwakeAction(variables);
            case WindowsActionNames.CancelPending:
                return CancelAction();
            case WindowsActionNames.Shutdown:
            case WindowsActionNames.Restart:
            case WindowsActionNames.Sleep:
            case WindowsActionNames.Hibernate:
                return await PowerAction(name, title, variables, cancellationToken).ConfigureAwait(false);
            default:
                return ExecutionResult.Failure(ErrorCodes.Internal, $"The action '{name}' has no implementation.");
        }
    }

    /// <summary>
    /// A power action, at once or after <c>DelaySeconds</c>. Whether the computer can do it is checked before the countdown starts, so
    /// nobody is told that it will sleep in five minutes when it cannot.
    /// </summary>
    private async ValueTask<ExecutionResult> PowerAction(
        string name,
        string title,
        IReadOnlyDictionary<string, object> variables,
        CancellationToken cancellationToken)
    {
        if (!TryReadDelay(variables, out int delaySeconds))
        {
            return Invalid("DelaySeconds must be a whole number from 0 to " + PowerActionSpec.MaxDelaySeconds.ToString(CultureInfo.InvariantCulture) + ".");
        }

        string? message = ReadMessage(variables);
        if (message is { Length: > PowerActionSpec.MaxMessageLength })
        {
            return Invalid("Message may have at most " + PowerActionSpec.MaxMessageLength.ToString(CultureInfo.InvariantCulture) + " characters.");
        }

        bool force = BuiltinActionVariables.GetBool(variables, WindowsActionParameters.Force);
        ExecutionResult? refusal = CheckAvailable(name);
        if (refusal is not null)
        {
            return refusal;
        }

        if (delaySeconds == 0)
        {
            return Execute(name, force);
        }

        ExecutionResult result = await _scheduler.RunDelayedAsync(
            name,
            title,
            delaySeconds,
            message,
            _ => ValueTask.FromResult(Execute(name, force)),
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? result with { StandardOutput = result.StandardOutput + " (after a delay of " + delaySeconds.ToString(CultureInfo.InvariantCulture) + " s)" }
            : result;
    }

    private ExecutionResult? CheckAvailable(string name)
    {
        return name switch
        {
            WindowsActionNames.Sleep when !_power.CanSleep => Refused("This computer cannot sleep."),
            WindowsActionNames.Hibernate when !_power.CanHibernate =>
                Refused("Hibernation is not enabled on this computer. Run 'powercfg /hibernate on' as administrator to enable it."),
            _ => null,
        };
    }

    private ExecutionResult Execute(string name, bool force)
    {
        switch (name)
        {
            case WindowsActionNames.Shutdown:
                _power.Shutdown(force);
                return Done("Shutdown was requested.");
            case WindowsActionNames.Restart:
                _power.Restart(force);
                return Done("Restart was requested.");
            case WindowsActionNames.Sleep:
                _power.Sleep();
                return Done("Sleep was requested.");
            case WindowsActionNames.Hibernate:
                _power.Hibernate();
                return Done("Hibernation was requested.");
            default:
                return ExecutionResult.Failure(ErrorCodes.Internal, $"The action '{name}' has no implementation.");
        }
    }

    /// <summary>
    /// <c>power.keep_awake</c> (T715): <c>On</c> keeps the computer awake (default true), <c>Display</c> keeps the screen on, and
    /// <c>Minutes</c> ends it after that time (0, the default, means until it is switched off).
    /// </summary>
    private ExecutionResult KeepAwakeAction(IReadOnlyDictionary<string, object> variables)
    {
        if (_keepAwake is null)
        {
            return ExecutionResult.Failure(ErrorCodes.Internal, "Keeping the computer awake is not available in this agent.");
        }

        bool on = BuiltinActionVariables.GetBool(variables, PowerActionSpec.On, fallback: true);
        bool display = BuiltinActionVariables.GetBool(variables, PowerActionSpec.Display);
        long? minutes = BuiltinActionVariables.GetLong(variables, PowerActionSpec.Minutes);
        if (minutes is < 0 or > PowerActionSpec.MaxKeepAwakeMinutes || (minutes is null && variables.ContainsKey(PowerActionSpec.Minutes)))
        {
            return Invalid("Minutes must be a whole number from 0 to " + PowerActionSpec.MaxKeepAwakeMinutes.ToString(CultureInfo.InvariantCulture) + ".");
        }

        KeepAwakeState state = _keepAwake.Switch(on, display, (int)(minutes ?? 0));
        if (!state.IsOn)
        {
            return Done("The computer is no longer kept awake by the agent.");
        }

        string what = state.KeepDisplayOn ? "The computer and the screen are kept awake" : "The computer is kept awake";
        return Done(state.Until is { } until
            ? what + " until " + until.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC."
            : what + " until it is switched off.");
    }

    /// <summary><c>power.cancel</c> (T715): ends the pending delayed power action; nothing pending is not an error.</summary>
    private ExecutionResult CancelAction()
    {
        PendingPowerAction? cancelled = _scheduler.Cancel(PowerCancelSources.Action);
        return Done(cancelled is null
            ? "No delayed power action was pending."
            : "The delayed " + cancelled.Title.ToLowerInvariant() + " was cancelled.");
    }
}
