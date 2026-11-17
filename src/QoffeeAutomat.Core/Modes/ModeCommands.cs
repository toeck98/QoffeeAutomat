using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Core.Modes;

/// <summary>
/// The command that a mode group provides on the wire (PLAN.md S06, D-037): <c>mode_&lt;groupId&gt;</c> with one enum variable
/// <c>Mode</c> whose options are the ids of the modes, a <c>select</c> control and the state metric <c>mode.&lt;groupId&gt;</c>. It is
/// not a stored document: the <see cref="Commands.CommandRegistry"/> derives it from the group, the manifest announces it like a
/// stored command, and the <see cref="Commands.CommandEngine"/> hands its runs to the <see cref="ModeEngine"/>, so every entry
/// point (QoffeeLink, the management API, the command line) switches the mode the same way.
/// </summary>
public static class ModeCommands
{
    /// <summary>The longest time that a switch may take; the steps have their own timeouts.</summary>
    public const int TimeoutSeconds = 3600;

    /// <summary>Builds the generated command of a group.</summary>
    /// <param name="group">The group.</param>
    /// <returns>The command document. It is enabled and exposed exactly when the group is.</returns>
    public static CommandDocument Generate(ModeGroupDocument group)
    {
        ArgumentNullException.ThrowIfNull(group);

        string[] options = [.. group.Modes.Select(mode => mode.Id)];
        return new CommandDocument(
            DocumentSchema.CommandVersion,
            ModeNames.CommandId(group.Id),
            group.Name,
            group.Enabled,
            group.ExposeToHa,
            RunAsValues.System,
            new BuiltinAction(ModeNames.ActionName),
            Description: group.Description,
            Icon: group.Icon,
            Parameters:
            [
                new ParameterDefinition(
                    ModeNames.Variable,
                    ParameterTypes.Choice,
                    Required: true,
                    Options: options,
                    Description: "The mode to switch to."),
            ],
            HaControl: new HaControl(HaControlTypes.Select, ModeNames.Variable, ModeNames.MetricId(group.Id)),
            TimeoutSeconds: TimeoutSeconds,
            Concurrency: ConcurrencyModes.Exclusive,
            CooldownSeconds: 0,
            CaptureOutput: false,
            Revision: group.Revision,
            UpdatedAt: group.UpdatedAt,
            Origin: group.Origin);
    }
}
