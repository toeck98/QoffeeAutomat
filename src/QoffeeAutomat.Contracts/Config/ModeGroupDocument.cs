namespace QoffeeAutomat.Contracts.Config;

/// <summary>
/// A mode group document, <c>config/modes/&lt;id&gt;.json</c> (PLAN.md S06, T717): a set of mutually exclusive modes of
/// the PC, for example Normal, Study and TV gaming. On the wire a group appears as the generated command
/// <c>mode_&lt;id&gt;</c> and the enum metric <c>mode.&lt;id&gt;</c> (D-037). Members that the schema requires have no
/// default; a missing member is reported by <c>DocumentValidator</c>.
/// </summary>
/// <param name="SchemaVersion">The schema version, 1.</param>
/// <param name="Id">The group id, <c>^[a-z][a-z0-9_]{0,62}$</c>.</param>
/// <param name="Name">The display name.</param>
/// <param name="Enabled">A disabled group cannot be switched and is not announced to Home Assistant.</param>
/// <param name="ExposeToHa">Whether the generated command and the state metric are announced to Home Assistant.</param>
/// <param name="DefaultMode">The id of the mode that <see cref="OnBoot"/> refers to; it must be one of <paramref name="Modes"/>.</param>
/// <param name="Modes">The modes, at least one, with unique ids.</param>
/// <param name="Description">What the group is for.</param>
/// <param name="Icon">An <c>mdi:</c> icon.</param>
/// <param name="OnBoot">What happens after an operating-system start, see <see cref="ModeBootBehaviors"/>.</param>
/// <param name="OnError">What a failing step does, see <see cref="SequenceErrorModes"/>.</param>
/// <param name="Revision">Incremented by the store on every save.</param>
/// <param name="UpdatedAt">The time of the last save.</param>
/// <param name="Origin">Where the document was authored, see <see cref="DocumentOrigins"/>.</param>
public sealed record ModeGroupDocument(
    int SchemaVersion,
    string Id,
    string Name,
    bool Enabled,
    bool ExposeToHa,
    string DefaultMode,
    IReadOnlyList<ModeDefinition> Modes,
    string? Description = null,
    string? Icon = null,
    string OnBoot = ModeBootBehaviors.Reset,
    string OnError = SequenceErrorModes.Stop,
    int? Revision = null,
    DateTimeOffset? UpdatedAt = null,
    string? Origin = null) : IConfigDocument;

/// <summary>One mode of a group: what to run when it is left and when it is entered.</summary>
/// <param name="Id">The mode id, <c>^[a-z][a-z0-9_]{0,62}$</c>, unique in the group. It is the value of the variable <c>Mode</c> and of the metric.</param>
/// <param name="Name">The display name.</param>
/// <param name="Enter">The steps that run when the mode is entered (same step objects as the sequence action).</param>
/// <param name="Exit">The steps that run when the mode is left.</param>
public sealed record ModeDefinition(
    string Id,
    string Name,
    IReadOnlyList<SequenceStep>? Enter = null,
    IReadOnlyList<SequenceStep>? Exit = null);

/// <summary>The values of <c>onBoot</c> of a mode group: what happens after an operating-system start (not after an agent restart).</summary>
public static class ModeBootBehaviors
{
    /// <summary>The last mode is remembered and nothing runs.</summary>
    public const string Keep = "keep";

    /// <summary>The state is set to the default mode and nothing runs.</summary>
    public const string Reset = "reset";

    /// <summary>The enter steps of the default mode run once the session is ready.</summary>
    public const string ApplyDefault = "apply_default";
}

/// <summary>The names that a mode group has on the wire (PLAN.md S06, D-037).</summary>
public static class ModeNames
{
    /// <summary>The prefix of the generated command: <c>mode_&lt;groupId&gt;</c>.</summary>
    public const string CommandPrefix = "mode_";

    /// <summary>The prefix of the state metric: <c>mode.&lt;groupId&gt;</c>.</summary>
    public const string MetricPrefix = "mode.";

    /// <summary>The name of the one variable of the generated command.</summary>
    public const string Variable = "Mode";

    /// <summary>The name of the built-in action of the generated command; the command engine hands such a run to the mode engine.</summary>
    public const string ActionName = "agent.mode";

    /// <summary>Returns the id of the generated command of a group.</summary>
    /// <param name="groupId">The group id.</param>
    /// <returns><c>mode_&lt;groupId&gt;</c>.</returns>
    public static string CommandId(string groupId) => CommandPrefix + groupId;

    /// <summary>Returns the id of the state metric of a group.</summary>
    /// <param name="groupId">The group id.</param>
    /// <returns><c>mode.&lt;groupId&gt;</c>.</returns>
    public static string MetricId(string groupId) => MetricPrefix + groupId;

    /// <summary>Returns the group id that a generated command id belongs to, if the id has the form of one.</summary>
    /// <param name="commandId">A command id.</param>
    /// <param name="groupId">The group id when the method returns true.</param>
    /// <returns>True when <paramref name="commandId"/> is <c>mode_</c> followed by something.</returns>
    public static bool TryGetGroupId(string? commandId, out string groupId)
    {
        if (commandId is { Length: > 5 } && commandId.StartsWith(CommandPrefix, StringComparison.Ordinal))
        {
            groupId = commandId[CommandPrefix.Length..];
            return true;
        }

        groupId = string.Empty;
        return false;
    }
}
