using QoffeeAutomat.Hub.Gateway;

namespace QoffeeAutomat.Hub.Ui.Services;

/// <summary>What the frame of an agent page knows about the agent, handed to the page inside it (PLAN.md S20, T915).</summary>
public sealed class AgentContext
{
    private readonly Func<Task> _reload;

    /// <summary>Creates the context.</summary>
    /// <param name="detail">The agent as the hub shows it.</param>
    /// <param name="reload">Reads the agent again and renders the frame again.</param>
    public AgentContext(InventoryDetail detail, Func<Task> reload)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(reload);

        Detail = detail;
        _reload = reload;
    }

    /// <summary>The agent with its presence, manifest, live state and events.</summary>
    public InventoryDetail Detail { get; }

    /// <summary>Reads the agent again.</summary>
    /// <returns>A task that completes when the frame shows the new state.</returns>
    public Task ReloadAsync() => _reload();
}

/// <summary>One tab of the pages of an agent: where it goes and which role may open it.</summary>
/// <param name="Key">The key of the tab: its text is <c>Agent.Tab.&lt;key&gt;</c> and its address is below <c>agents/{id}/&lt;key&gt;</c>.</param>
/// <param name="MinRole">The role that may open the tab (one of <see cref="HubRoleNames"/>).</param>
/// <param name="RequiresOnline">True when the page talks to the agent through the tunnel and so needs the agent to be connected.</param>
public sealed record AgentTab(string Key, string MinRole, bool RequiresOnline)
{
    /// <summary>The tab of the overview; its address is the one of the agent itself.</summary>
    public const string Overview = "overview";

    /// <summary>The tabs, in the order they are shown.</summary>
    public static IReadOnlyList<AgentTab> All { get; } =
    [
        new(Overview, HubRoleNames.Viewer, RequiresOnline: false),
        new("dashboard", HubRoleNames.Viewer, RequiresOnline: true),
        new("commands", HubRoleNames.Operator, RequiresOnline: true),
        new("sensors", HubRoleNames.Admin, RequiresOnline: true),
        new("metrics", HubRoleNames.Admin, RequiresOnline: true),
        new("history", HubRoleNames.Viewer, RequiresOnline: true),
        new("logs", HubRoleNames.Viewer, RequiresOnline: true),
        new("secrets", HubRoleNames.Admin, RequiresOnline: true),
    ];

    /// <summary>The address of the tab, relative to the base address of the app.</summary>
    /// <param name="agentId">The agent id.</param>
    /// <returns><c>agents/{id}</c> for the overview, otherwise <c>agents/{id}/{key}</c>.</returns>
    public string Href(string agentId)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        string agent = "agents/" + Uri.EscapeDataString(agentId);
        return Key == Overview ? agent : agent + "/" + Key;
    }
}
