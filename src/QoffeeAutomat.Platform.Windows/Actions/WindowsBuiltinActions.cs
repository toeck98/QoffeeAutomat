using System.ComponentModel;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Windows.Actions;

/// <summary>
/// The built-in actions of Windows as the agent sees them (PLAN.md S11, T306). It lists all actions of
/// <see cref="WindowsActionCatalog"/> and executes the ones that need the service itself, the power actions, through the
/// <see cref="IPowerNativeApi"/>. The actions of the user's desktop are listed too, because the decorator
/// <see cref="Sessions.UserContextBuiltinActions"/> reads the context from these descriptors and hands such an action to
/// the SessionHost, whose own provider executes it; this class never does.
/// </summary>
public sealed class WindowsBuiltinActions : IBuiltinActionProvider
{
    private readonly IPowerNativeApi _power;

    /// <summary>Creates the provider.</summary>
    /// <param name="power">The calls that change the power state; tests pass a fake that never touches the computer.</param>
    public WindowsBuiltinActions(IPowerNativeApi power)
    {
        ArgumentNullException.ThrowIfNull(power);
        _power = power;
    }

    /// <inheritdoc />
    public IReadOnlyList<BuiltinActionDescriptor> Actions => WindowsActionCatalog.All;

    /// <inheritdoc />
    public ValueTask<ExecutionResult> ExecuteAsync(
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
            return ValueTask.FromResult(new ExecutionResult(127, string.Empty, $"Unknown built-in action '{name}'.", ErrorCodes.BadRequest));
        }

        if (descriptor.RunAs != RunAsValues.System)
        {
            // Reached only without the session decorator, which would have sent the action to the SessionHost.
            return ValueTask.FromResult(ExecutionResult.Failure(
                ErrorCodes.Internal,
                $"The action '{name}' runs in the session of the user and cannot run in the service."));
        }

        try
        {
            return ValueTask.FromResult(Run(name, variables));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return ValueTask.FromResult(new ExecutionResult(1, string.Empty, $"{descriptor.Title} failed: {exception.Message}"));
        }
    }

    private ExecutionResult Run(string name, IReadOnlyDictionary<string, object> variables)
    {
        bool force = BuiltinActionVariables.GetBool(variables, WindowsActionParameters.Force);
        switch (name)
        {
            case WindowsActionNames.Shutdown:
                _power.Shutdown(force);
                return Done("Shutdown was requested.");
            case WindowsActionNames.Restart:
                _power.Restart(force);
                return Done("Restart was requested.");
            case WindowsActionNames.Sleep:
                if (!_power.CanSleep)
                {
                    return Refused("This computer cannot sleep.");
                }

                _power.Sleep();
                return Done("Sleep was requested.");
            case WindowsActionNames.Hibernate:
                if (!_power.CanHibernate)
                {
                    return Refused("Hibernation is not enabled on this computer. Run 'powercfg /hibernate on' as administrator to enable it.");
                }

                _power.Hibernate();
                return Done("Hibernation was requested.");
            default:
                return ExecutionResult.Failure(ErrorCodes.Internal, $"The action '{name}' has no implementation.");
        }
    }

    private static ExecutionResult Done(string message) => new(0, message, string.Empty);

    private static ExecutionResult Refused(string message) => new(1, string.Empty, message);
}
