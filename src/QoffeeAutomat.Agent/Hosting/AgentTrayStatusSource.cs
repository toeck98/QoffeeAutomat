using System.Globalization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using QoffeeAutomat.Agent.Link;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Agent.Hosting;

/// <summary>
/// Tells the platform what the tray icon of the SessionHost shows (PLAN.md S11, T310): the agent name and the setting
/// <c>trayIcon</c> from the settings, the paired clients from the pairing store, the open QoffeeLink connections from the
/// link server and the loopback address of the UI from the listener that Kestrel opened.
/// </summary>
public sealed class AgentTrayStatusSource : ITrayStatusSource
{
    private readonly AgentIdentity _identity;
    private readonly IConfigStore _config;
    private readonly AgentOptions _options;
    private readonly PairingService _pairing;
    private readonly LinkServer _link;
    private readonly IServer? _server;

    /// <summary>Creates the source.</summary>
    /// <param name="identity">The agent name.</param>
    /// <param name="config">The settings.</param>
    /// <param name="options">The command-line options; their UI port overrides the settings.</param>
    /// <param name="pairing">Lists the paired clients.</param>
    /// <param name="link">Counts the open connections.</param>
    /// <param name="server">The web server, for the address of the loopback listener; null uses the configured port.</param>
    public AgentTrayStatusSource(
        AgentIdentity identity,
        IConfigStore config,
        AgentOptions options,
        PairingService pairing,
        LinkServer link,
        IServer? server = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(link);

        _identity = identity;
        _config = config;
        _options = options;
        _pairing = pairing;
        _link = link;
        _server = server;
    }

    /// <inheritdoc />
    public async ValueTask<TrayStatus> GetAsync(CancellationToken cancellationToken)
    {
        AgentSettingsDocument? settings = _config.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings);
        IReadOnlyList<LinkClient> clients = await _pairing.ListClientsAsync(cancellationToken).ConfigureAwait(false);

        return new TrayStatus(
            _identity.Name,
            clients.Count,
            _link.ConnectionCount,
            settings?.EffectiveTrayIcon ?? true,
            LoopbackUiUrl(settings));
    }

    private string? LoopbackUiUrl(AgentSettingsDocument? settings)
    {
        // The listener that is really open wins: with --ui-port 0 the port is only known after the start.
        IServerAddressesFeature? addresses = _server?.Features.Get<IServerAddressesFeature>();
        if (addresses is not null)
        {
            foreach (string address in addresses.Addresses)
            {
                if (address.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase)
                    || address.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase))
                {
                    return address.TrimEnd('/') + "/";
                }
            }
        }

        int port = _options.UiPort ?? settings?.EffectiveUiPort ?? AgentSettingsDefaults.UiPort;
        return port > 0 ? string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/") : null;
    }
}
