namespace QoffeeAutomat.Hub.Ui.Pages.Profiles;

/// <summary>
/// The targets of an assignment as the UI writes and reads them (PLAN.md S20, D-030, T916): <c>all</c>, <c>tag:&lt;tag&gt;</c> and <c>agent:&lt;id&gt;</c>.
/// More specific targets override less specific ones, so the lists show them in that order.
/// </summary>
public static class AssignmentTargets
{
    /// <summary>The kind of target that reaches every agent.</summary>
    public const string All = "all";

    /// <summary>The kind of target that reaches the agents that carry a tag.</summary>
    public const string Tag = "tag";

    /// <summary>The kind of target that reaches one agent.</summary>
    public const string Agent = "agent";

    /// <summary>The kinds, from the least to the most specific.</summary>
    public static IReadOnlyList<string> Kinds { get; } = [All, Tag, Agent];

    /// <summary>Builds the target text the hub expects.</summary>
    /// <param name="kind">One of <see cref="Kinds"/>.</param>
    /// <param name="value">The tag or the agent id; ignored for <see cref="All"/>.</param>
    /// <returns>The text, or <c>null</c> when the kind needs a value and there is none.</returns>
    public static string? Build(string kind, string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return kind switch
        {
            All => "all",
            Tag when trimmed.Length > 0 => "tag:" + trimmed.ToLowerInvariant(),
            Agent when trimmed.Length > 0 => "agent:" + trimmed,
            _ => null,
        };
    }

    /// <summary>Splits a target text.</summary>
    /// <param name="target">The text from the hub.</param>
    /// <returns>The kind (one of <see cref="Kinds"/>) and the value, which is empty for <see cref="All"/>.</returns>
    public static (string Kind, string Value) Split(string target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.StartsWith("tag:", StringComparison.Ordinal))
        {
            return (Tag, target["tag:".Length..]);
        }

        if (target.StartsWith("agent:", StringComparison.Ordinal))
        {
            return (Agent, target["agent:".Length..]);
        }

        return (All, string.Empty);
    }

    /// <summary>The position of a target in the lists: all first, then tags, then single agents.</summary>
    /// <param name="target">The text from the hub.</param>
    /// <returns>0, 1 or 2.</returns>
    public static int Specificity(string target) => Kinds.ToList().IndexOf(Split(target).Kind);
}
