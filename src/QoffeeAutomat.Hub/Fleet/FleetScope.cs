namespace QoffeeAutomat.Hub.Fleet;

/// <summary>The values of the scope type of a fleet token (PLAN.md S20 "Fleet link").</summary>
public static class FleetScopeTypes
{
    /// <summary>The token sees and controls every agent of the hub.</summary>
    public const string All = "all";

    /// <summary>The token sees and controls only the agents that carry at least one of its tags.</summary>
    public const string Tags = "tags";

    /// <summary>Tells whether a text is a scope type.</summary>
    /// <param name="type">The text.</param>
    /// <returns>True for <c>all</c> and <c>tags</c>.</returns>
    public static bool IsValid(string? type) => type is All or Tags;
}

/// <summary>
/// What a fleet token may see and control (PLAN.md S20 "Fleet link"): every agent, or the agents that carry at least one of a set of tags. The scope is
/// checked against the current tags of an agent every time, so a change of tags takes effect at once for every connected client.
/// </summary>
public sealed class FleetScope
{
    private FleetScope(bool all, IReadOnlyList<string> tags)
    {
        All = all;
        Tags = tags;
    }

    /// <summary>The scope of a token that sees every agent.</summary>
    public static FleetScope Everything { get; } = new(true, []);

    /// <summary>True when every agent is in scope; <see cref="Tags"/> is empty then.</summary>
    public bool All { get; }

    /// <summary>The tags of a tag scope, normalised (see <see cref="Agents.AgentTags"/>); never empty for a tag scope.</summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>The scope type: <see cref="FleetScopeTypes.All"/> or <see cref="FleetScopeTypes.Tags"/>.</summary>
    public string Type => All ? FleetScopeTypes.All : FleetScopeTypes.Tags;

    /// <summary>Creates a scope of tags.</summary>
    /// <param name="tags">The tags; at least one, validated and normalised by the caller (<see cref="Agents.AgentTags.TryNormalize"/>).</param>
    /// <returns>The scope.</returns>
    /// <exception cref="ArgumentException">The list is empty.</exception>
    public static FleetScope OfTags(IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Count == 0)
        {
            throw new ArgumentException("A tag scope needs at least one tag.", nameof(tags));
        }

        return new FleetScope(false, tags);
    }

    /// <summary>Restores a scope from its stored form.</summary>
    /// <param name="type">The stored type.</param>
    /// <param name="tags">The stored tags.</param>
    /// <returns>The scope. A tag scope without tags (which cannot be created) matches nothing rather than everything.</returns>
    public static FleetScope From(string type, IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(tags);

        return type == FleetScopeTypes.All ? Everything : new FleetScope(false, tags);
    }

    /// <summary>Tells whether an agent with these tags is in scope.</summary>
    /// <param name="agentTags">The current tags of the agent.</param>
    /// <returns>True for every agent when the scope is <c>all</c>, otherwise when the agent has at least one of the tags of the scope.</returns>
    public bool Allows(IReadOnlyList<string> agentTags)
    {
        ArgumentNullException.ThrowIfNull(agentTags);

        if (All)
        {
            return true;
        }

        foreach (string tag in Tags)
        {
            if (agentTags.Contains(tag, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
