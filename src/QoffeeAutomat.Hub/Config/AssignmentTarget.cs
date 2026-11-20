using System.Diagnostics.CodeAnalysis;
using QoffeeAutomat.Hub.Agents;

namespace QoffeeAutomat.Hub.Config;

/// <summary>The kinds of assignment target, from the least to the most specific (PLAN.md S20, D-030).</summary>
public enum TargetKind
{
    /// <summary>Every agent.</summary>
    All = 0,

    /// <summary>The agents that have a tag.</summary>
    Tag = 1,

    /// <summary>One agent.</summary>
    Agent = 2,
}

/// <summary>An agent as the configuration service sees it.</summary>
/// <param name="Id">The agent id.</param>
/// <param name="Name">The name of the agent.</param>
/// <param name="Os">The operating system family: <c>windows</c> or <c>linux</c>.</param>
/// <param name="Tags">The tags of the agent.</param>
/// <param name="Status">The enrollment status (<see cref="AgentStatuses"/>); only enrolled agents get a desired state.</param>
public sealed record ConfigAgent(string Id, string Name, string Os, IReadOnlyList<string> Tags, string Status);

/// <summary>
/// The target of an assignment: <c>all</c>, <c>tag:&lt;tag&gt;</c> or <c>agent:&lt;id&gt;</c>. More specific targets override less specific
/// ones (D-030).
/// </summary>
/// <param name="Kind">The kind of target.</param>
/// <param name="Value">The tag or the agent id; null for <see cref="TargetKind.All"/>.</param>
public sealed record AssignmentTarget(TargetKind Kind, string? Value)
{
    /// <summary>The text of the target that matches every agent.</summary>
    public const string AllText = "all";

    /// <summary>The prefix of a tag target.</summary>
    public const string TagPrefix = "tag:";

    /// <summary>The prefix of an agent target.</summary>
    public const string AgentPrefix = "agent:";

    /// <summary>The longest agent id.</summary>
    public const int MaxAgentIdLength = 64;

    /// <summary>The rank of the target kind: 0 for all, 1 for tag, 2 for agent. A higher rank overrides a lower one.</summary>
    public int Specificity => (int)Kind;

    /// <summary>The text form that is stored and shown.</summary>
    public string Text => Kind switch
    {
        TargetKind.All => AllText,
        TargetKind.Tag => TagPrefix + Value,
        _ => AgentPrefix + Value,
    };

    /// <summary>Parses the text form.</summary>
    /// <param name="text">The text.</param>
    /// <param name="target">The target when the text is valid.</param>
    /// <param name="error">What is wrong when it is not.</param>
    /// <returns>True when the text is a valid target.</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out AssignmentTarget? target, out string? error)
    {
        target = null;
        error = null;

        if (string.Equals(text, AllText, StringComparison.Ordinal))
        {
            target = new AssignmentTarget(TargetKind.All, null);
            return true;
        }

        if (text is not null && text.StartsWith(TagPrefix, StringComparison.Ordinal))
        {
            string tag = text[TagPrefix.Length..].ToLowerInvariant();
            if (AgentTags.IsValid(tag))
            {
                target = new AssignmentTarget(TargetKind.Tag, tag);
                return true;
            }

            error = "A tag has 1 to 32 characters (a-z, 0-9, '.', '_', '-') and starts with a letter or a digit.";
            return false;
        }

        if (text is not null && text.StartsWith(AgentPrefix, StringComparison.Ordinal))
        {
            string agent = text[AgentPrefix.Length..];
            if (agent.Length is > 0 and <= MaxAgentIdLength && !agent.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character)))
            {
                target = new AssignmentTarget(TargetKind.Agent, agent);
                return true;
            }

            error = "An agent id has 1 to 64 characters without white space.";
            return false;
        }

        error = "The target must be 'all', 'tag:<tag>' or 'agent:<id>'.";
        return false;
    }

    /// <summary>Tells whether an agent is addressed by this target.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>True for <c>all</c>, for a tag the agent has and for the agent itself.</returns>
    public bool Matches(ConfigAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return Kind switch
        {
            TargetKind.All => true,
            TargetKind.Tag => agent.Tags.Contains(Value!, StringComparer.Ordinal),
            _ => string.Equals(agent.Id, Value, StringComparison.Ordinal),
        };
    }
}

/// <summary>The source of the agents that configuration is computed for.</summary>
public interface IConfigAgentSource
{
    /// <summary>Lists every agent of the registry, whatever its status.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agents.</returns>
    Task<IReadOnlyList<ConfigAgent>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>The agents of the agent registry (T904).</summary>
public sealed class RegistryConfigAgentSource : IConfigAgentSource
{
    private readonly AgentRegistry _registry;

    /// <summary>Creates the source.</summary>
    /// <param name="registry">The agent registry.</param>
    public RegistryConfigAgentSource(AgentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConfigAgent>> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentRecord> all = await _registry.ListAsync(null, cancellationToken).ConfigureAwait(false);
        return [.. all.Select(static agent => new ConfigAgent(agent.Id, agent.Name, agent.Os, agent.Tags, agent.Status))];
    }
}
