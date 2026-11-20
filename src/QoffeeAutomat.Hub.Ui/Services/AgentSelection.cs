using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Hub.Ui.Services;

/// <summary>
/// The agent whose pages are open (PLAN.md S20, D-033). The pages of the component library know no agent id: they call <c>IAgentApi</c>, and the
/// <c>TunnelAgentApi</c> that answers reads the agent from here at every call. <c>AgentPageFrame</c> sets it before it renders a page.
/// </summary>
public sealed class AgentSelection
{
    /// <summary>The id of the selected agent; <c>null</c> while no agent page is open.</summary>
    public string? AgentId { get; private set; }

    /// <summary>The display name of the selected agent, if it is known.</summary>
    public string? AgentName { get; private set; }

    /// <summary>
    /// True while a page of the library is open (PLAN.md S20, T916): the editors of the component library then edit documents of the hub library
    /// (<c>LibraryAgentApi</c>) instead of the documents of the selected agent. <see cref="Select"/> ends it.
    /// </summary>
    public bool LibraryMode { get; private set; }

    /// <summary>
    /// The online agent that the test panel of a library editor runs a draft on, through the tunnel; <c>null</c> while none is chosen. It is kept
    /// when the user moves between library pages and is independent of <see cref="AgentId"/>.
    /// </summary>
    public string? TestTargetId { get; private set; }

    /// <summary>Selects an agent; the pages below it work on the documents of that agent.</summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="agentName">The display name, if known.</param>
    public void Select(string agentId, string? agentName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        LibraryMode = false;
        AgentId = agentId;
        AgentName = agentName;
    }

    /// <summary>Switches to the library: the editors of the component library edit hub documents. No agent is selected afterwards.</summary>
    public void UseLibrary()
    {
        LibraryMode = true;
        AgentId = null;
        AgentName = null;
    }

    /// <summary>Chooses the agent that library drafts are tested on.</summary>
    /// <param name="agentId">The agent id, or <c>null</c> for none.</param>
    public void SelectTestTarget(string? agentId)
    {
        TestTargetId = string.IsNullOrEmpty(agentId) ? null : agentId;
    }

    /// <summary>Forgets the selection.</summary>
    public void Clear()
    {
        AgentId = null;
        AgentName = null;
        LibraryMode = false;
    }
}

/// <summary>
/// The addresses of the pages of the component library below an agent: <c>/commands/new</c> becomes <c>agents/pc-1/commands/new</c>. The address is
/// relative to the base address of the page, so it stays below a path prefix (Home Assistant ingress); an address with a leading slash would leave it.
/// </summary>
public sealed class AgentScopedLinks : IAppLinks
{
    private readonly AgentSelection _selection;

    /// <summary>Creates the links.</summary>
    /// <param name="selection">The selected agent.</param>
    public AgentScopedLinks(AgentSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        _selection = selection;
    }

    /// <inheritdoc />
    public string Resolve(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string relative = path.TrimStart('/');
        if (_selection.LibraryMode)
        {
            return LibraryLink(relative);
        }

        return _selection.AgentId is { } id
            ? "agents/" + Uri.EscapeDataString(id) + (relative.Length == 0 ? string.Empty : "/" + relative)
            : relative;
    }

    /// <summary>
    /// The address of a page of the component library while a library page is open: the lists of commands and sensors become the library list,
    /// <c>commands/{id}</c> and <c>sensors/{id}</c> become the library editors, and anything else (secrets, which the library has none of) the library.
    /// </summary>
    /// <param name="relative">The address the component library asks for, without the leading slash.</param>
    /// <returns>The address of the library page, relative to the base address of the app.</returns>
    public static string LibraryLink(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);

        string[] parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[0] == "commands")
        {
            return "library/command/" + parts[1];
        }

        if (parts.Length == 2 && parts[0] == "sensors")
        {
            return "library/sensor/" + parts[1];
        }

        return "library";
    }
}
