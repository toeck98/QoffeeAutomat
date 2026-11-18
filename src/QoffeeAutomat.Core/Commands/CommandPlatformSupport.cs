using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Commands;

/// <summary>
/// Tells whether this platform can run a command (PLAN.md S17, T803): the counterpart of <see cref="QoffeeAutomat.Core.Metrics.Custom.SensorTypeSupport"/> for
/// commands. A command document with a Windows-only action type (<c>powershell</c>) on Linux, or a built-in action that the platform does
/// not offer, is <b>flagged</b> as unsupported and never deleted or rewritten: the document stays in the configuration, appears in the
/// editor with the explanation, is not announced to Home Assistant, and is refused with a clear message when somebody runs it. Moving the
/// configuration back to a platform that supports it makes the command work again without any change.
/// </summary>
public sealed class CommandPlatformSupport
{
    /// <summary>A policy that supports every command; the platforms that have no restriction use it.</summary>
    public static CommandPlatformSupport Unrestricted { get; } = new(null);

    private readonly string? _os;
    private readonly Func<IBuiltinActionProvider>? _builtins;

    /// <summary>Creates the policy of a platform.</summary>
    /// <param name="os">The operating system family (<c>windows</c>, <c>linux</c>); null or another value supports every action type.</param>
    /// <param name="builtins">
    /// The built-in actions of the platform; a built-in action that is not among them is unsupported. Null does not check built-in actions.
    /// The provider is asked for every check, so a decorator that is registered later is seen.
    /// </param>
    public CommandPlatformSupport(string? os, Func<IBuiltinActionProvider>? builtins = null)
    {
        _os = os;
        _builtins = builtins;
    }

    /// <summary>The operating system family that the policy is for; null for <see cref="Unrestricted"/>.</summary>
    public string? Os => _os;

    /// <summary>Checks a command.</summary>
    /// <param name="command">The command document.</param>
    /// <returns>
    /// <see cref="ValidationCodes.Unsupported"/> at <c>/action/type</c> for an action type that the platform lacks, or at <c>/action/name</c>
    /// for a built-in action that it does not offer; empty when the command can run here. The messages never contain variable values.
    /// </returns>
    public IReadOnlyList<ValidationError> Check(CommandDocument command)
    {
        ArgumentNullException.ThrowIfNull(command);

        CommandAction? action = command.Action;
        if (action is null)
        {
            return [];
        }

        if (!PlatformActionSupport.IsActionTypeSupported(_os, action.Type))
        {
            return [new ValidationError("/action/type", ValidationCodes.Unsupported, PlatformActionSupport.Explain(_os, action.Type))];
        }

        if (action is BuiltinAction builtin && _builtins is not null && !string.IsNullOrEmpty(builtin.Name))
        {
            IReadOnlyList<BuiltinActionDescriptor> offered = _builtins().Actions;
            if (!offered.Any(descriptor => string.Equals(descriptor.Name, builtin.Name, StringComparison.Ordinal)))
            {
                return
                [
                    new ValidationError(
                        "/action/name",
                        ValidationCodes.Unsupported,
                        $"The built-in action '{builtin.Name}' is not available on this platform. The command is kept."),
                ];
            }
        }

        return [];
    }

    /// <summary>Tells whether the platform can run a command.</summary>
    /// <param name="command">The command document.</param>
    /// <returns>True when <see cref="Check"/> finds nothing.</returns>
    public bool IsSupported(CommandDocument command) => Check(command).Count == 0;
}
