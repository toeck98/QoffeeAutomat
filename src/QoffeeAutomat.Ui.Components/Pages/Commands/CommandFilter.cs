using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Ui.Components.Pages.Commands;

/// <summary>A filter on a yes-or-no member of a command.</summary>
public enum SwitchFilter
{
    /// <summary>Both values pass.</summary>
    Any = 0,

    /// <summary>Only commands where the member is on pass.</summary>
    On,

    /// <summary>Only commands where the member is off pass.</summary>
    Off,
}

/// <summary>
/// What the user typed and picked above the command list (T403): a search text and one filter per member that the list
/// shows as a badge or switch. Every filter that is set must match.
/// </summary>
public sealed record CommandFilter
{
    /// <summary>The value of <see cref="ActionType"/> and <see cref="RunAs"/> that does not filter.</summary>
    public const string All = "all";

    /// <summary>Text that must be part of the id, the name or the description, ignoring case; blank does not filter.</summary>
    public string Search { get; init; } = string.Empty;

    /// <summary>Filter on <see cref="CommandDocument.Enabled"/>.</summary>
    public SwitchFilter Enabled { get; init; }

    /// <summary>Filter on <see cref="CommandDocument.ExposeToHa"/>.</summary>
    public SwitchFilter ExposeToHa { get; init; }

    /// <summary>An action type (see <see cref="ActionTypes"/>) or <see cref="All"/>.</summary>
    public string ActionType { get; init; } = All;

    /// <summary>A run context (see <see cref="RunAsValues"/>) or <see cref="All"/>.</summary>
    public string RunAs { get; init; } = All;

    /// <summary>True when at least one filter narrows the list.</summary>
    public bool IsActive =>
        !string.IsNullOrWhiteSpace(Search)
        || Enabled != SwitchFilter.Any
        || ExposeToHa != SwitchFilter.Any
        || ActionType != All
        || RunAs != All;

    /// <summary>Tells whether a command passes every filter.</summary>
    /// <param name="command">The command.</param>
    /// <returns>True when the command is shown.</returns>
    public bool Matches(CommandDocument command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return Passes(Enabled, command.Enabled)
            && Passes(ExposeToHa, command.ExposeToHa)
            && (ActionType == All || string.Equals(command.Action.Type, ActionType, StringComparison.Ordinal))
            && (RunAs == All || string.Equals(command.RunAs, RunAs, StringComparison.Ordinal))
            && MatchesSearch(command);
    }

    /// <summary>Keeps the commands that pass, in their order.</summary>
    /// <param name="commands">The commands.</param>
    /// <returns>The commands that pass.</returns>
    public IReadOnlyList<CommandDocument> Apply(IEnumerable<CommandDocument> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        return [.. commands.Where(Matches)];
    }

    private static bool Passes(SwitchFilter filter, bool value)
    {
        return filter switch
        {
            SwitchFilter.On => value,
            SwitchFilter.Off => !value,
            _ => true,
        };
    }

    private bool MatchesSearch(CommandDocument command)
    {
        string text = Search.Trim();
        if (text.Length == 0)
        {
            return true;
        }

        return Contains(command.Id, text) || Contains(command.Name, text) || Contains(command.Description, text);
    }

    private static bool Contains(string? haystack, string needle)
    {
        return haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}
