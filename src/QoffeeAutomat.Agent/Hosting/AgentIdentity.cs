using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Agent.Hosting;

/// <summary>
/// Who this agent is, as announced in <c>hello</c>, in the manifest and on the network (zeroconf): the persistent
/// id, the name from the settings, the version and the facts about the computer. The name and the UI address
/// follow the settings, so a rename shows up in the next manifest and in the next advertisement.
/// </summary>
public sealed class AgentIdentity
{
    private readonly IPlatformInfo _platform;
    private readonly IConfigStore _config;
    private readonly AgentOptions _options;
    private readonly string _id;

    /// <summary>Creates the identity and reads or creates the agent id.</summary>
    /// <param name="platform">The platform facts.</param>
    /// <param name="config">The configuration, for the agent settings.</param>
    /// <param name="options">The command-line options; their link port overrides the settings.</param>
    public AgentIdentity(IPlatformInfo platform, IConfigStore config, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);

        _platform = platform;
        _config = config;
        _options = options;

        // The manifest is built when the service provider creates it, which is before any hosted service starts, and
        // a service factory cannot await. The id lives in a small file (or in memory), so the wait is short and happens once.
        _id = platform.GetOrCreateAgentIdAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The persistent agent id.</summary>
    public string Id => _id;

    /// <summary>The agent name from the settings; the host name when none is set.</summary>
    public string Name => Settings?.Name ?? Environment.MachineName;

    /// <summary>The port of the HTTPS link listener.</summary>
    public int LinkPort => _options.LinkPort ?? Settings?.EffectiveLinkPort ?? AgentSettingsDefaults.LinkPort;

    /// <summary>Whether the agent announces itself on the network (the setting <c>zeroconf</c>, on by default).</summary>
    public bool ZeroconfEnabled => Settings?.EffectiveZeroconf ?? true;

    private AgentSettingsDocument? Settings => _config.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings);

    /// <summary>Describes the agent as it is right now.</summary>
    /// <returns>The identity record of the protocol.</returns>
    public AgentInfo Current()
    {
        Uri? uiUrl = null;
        if (Settings?.RemoteUi == true && LinkPort > 0)
        {
            // The UI is only served on the link listener when remote UI is on (D-018); until then there is no address to show.
            _ = Uri.TryCreate($"https://{_platform.Hostname.ToLowerInvariant()}.local:{LinkPort}/", UriKind.Absolute, out uiUrl);
        }

        return new AgentInfo(
            _id,
            Name,
            AgentVersion.Current,
            _platform.Os.Name,
            _platform.Os.Architecture,
            _platform.Hostname,
            _platform.MacAddresses,
            _platform.Os.Version,
            uiUrl);
    }
}
