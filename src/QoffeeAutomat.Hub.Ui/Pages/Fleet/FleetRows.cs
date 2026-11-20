using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Config.Rollout;
using QoffeeAutomat.Hub.Gateway;

namespace QoffeeAutomat.Hub.Ui.Pages.Fleet;

/// <summary>One line of the fleet overview: the registry record of an agent with what the hub knows about it right now.</summary>
/// <param name="Agent">The registry record.</param>
/// <param name="Presence">Whether the agent is connected; <c>null</c> for an agent that is not enrolled (it has no presence).</param>
/// <param name="Config">The configuration status (desired and applied generation); <c>null</c> when the hub has none or the roll-out could not be read.</param>
public sealed record FleetRow(AgentSummary Agent, AgentPresenceInfo? Presence, AgentConfigStatusInfo? Config)
{
    /// <summary>True when the agent is enrolled and its link is open.</summary>
    public bool IsOnline => Presence is { Online: true };

    /// <summary>
    /// The state that the overview shows and filters by: <c>online</c> or <c>offline</c> for an enrolled agent, otherwise the enrollment status
    /// (<c>pending</c>, <c>approved</c> or <c>rejected</c>).
    /// </summary>
    public string StateKey => Agent.Status == "enrolled" ? (IsOnline ? FleetFilter.Online : FleetFilter.Offline) : Agent.Status;
}

/// <summary>Building, filtering and tag handling of the fleet overview; no state, so the rules can be tested without a page.</summary>
public static class FleetFilter
{
    /// <summary>The filter value that lets everything through.</summary>
    public const string All = "";

    private static readonly char[] TagSeparators = [',', ';', ' ', '\t', '\n'];

    /// <summary>State: enrolled and connected.</summary>
    public const string Online = "online";

    /// <summary>State: enrolled and not connected.</summary>
    public const string Offline = "offline";

    /// <summary>The states that the filter offers.</summary>
    public static IReadOnlyList<string> States { get; } = [Online, Offline, "pending", "approved", "rejected"];

    /// <summary>Joins the registry, the presence and the configuration status into rows, in the order of the registry.</summary>
    /// <param name="agents">All agents of the registry.</param>
    /// <param name="inventory">The presence of the enrolled agents; <c>null</c> when it could not be read.</param>
    /// <param name="rollouts">The configuration status per agent; <c>null</c> when it could not be read.</param>
    /// <returns>One row per agent.</returns>
    public static IReadOnlyList<FleetRow> Build(AgentList agents, InventoryList? inventory, RolloutOverview? rollouts)
    {
        ArgumentNullException.ThrowIfNull(agents);

        Dictionary<string, AgentPresenceInfo> presence = inventory is null
            ? new Dictionary<string, AgentPresenceInfo>(StringComparer.Ordinal)
            : inventory.Agents.ToDictionary(item => item.Agent.Id, item => item.Presence, StringComparer.Ordinal);
        Dictionary<string, AgentConfigStatusInfo> config = rollouts is null
            ? new Dictionary<string, AgentConfigStatusInfo>(StringComparer.Ordinal)
            : rollouts.Agents.ToDictionary(item => item.AgentId, item => item, StringComparer.Ordinal);
        return
        [
            .. agents.Agents.Select(agent => new FleetRow(
                agent,
                presence.GetValueOrDefault(agent.Id),
                config.GetValueOrDefault(agent.Id))),
        ];
    }

    /// <summary>Applies the filters of the overview.</summary>
    /// <param name="rows">The rows.</param>
    /// <param name="search">Text that the name, the host name, the operating system or a tag must contain (case ignored); empty lets everything through.</param>
    /// <param name="state">One of <see cref="States"/>, or <see cref="All"/>.</param>
    /// <param name="tag">A tag that the agent must carry, or <see cref="All"/>.</param>
    /// <returns>The rows that pass, in their order.</returns>
    public static IReadOnlyList<FleetRow> Apply(IEnumerable<FleetRow> rows, string? search, string? state, string? tag)
    {
        ArgumentNullException.ThrowIfNull(rows);

        string needle = (search ?? string.Empty).Trim();
        return
        [
            .. rows.Where(row =>
                (string.IsNullOrEmpty(state) || row.StateKey == state)
                && (string.IsNullOrEmpty(tag) || row.Agent.Tags.Contains(tag, StringComparer.Ordinal))
                && (needle.Length == 0 || Matches(row.Agent, needle))),
        ];
    }

    /// <summary>The tags that at least one agent carries, sorted.</summary>
    /// <param name="rows">The rows.</param>
    /// <returns>The distinct tags.</returns>
    public static IReadOnlyList<string> Tags(IEnumerable<FleetRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return [.. rows.SelectMany(row => row.Agent.Tags).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Reads the tags that a user typed: separated by commas, semicolons or spaces, trimmed, lower case, without repeats, in the order typed. The
    /// hub checks the form of each tag (1 to 32 characters of <c>a-z 0-9 . _ -</c>, at most 16) and answers 422 with the reason.
    /// </summary>
    /// <param name="text">The text of the field.</param>
    /// <returns>The tags.</returns>
    public static IReadOnlyList<string> ParseTags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return
        [
            .. text.Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(tag => tag.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal),
        ];
    }

    private static bool Matches(AgentSummary agent, string needle)
    {
        return Contains(agent.Name, needle)
            || Contains(agent.Hostname, needle)
            || Contains(agent.Os, needle)
            || agent.Tags.Any(tag => Contains(tag, needle));
    }

    private static bool Contains(string? text, string needle) => text?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
}
