namespace QoffeeAutomat.Core.Platform;

/// <summary>What the tray icon of the SessionHost shows besides the global pause flag (PLAN.md S11, T310).</summary>
/// <param name="AgentName">The agent name.</param>
/// <param name="PairedClients">The number of paired link clients (Home Assistant instances); zero means not paired.</param>
/// <param name="ConnectedClients">The number of open QoffeeLink connections.</param>
/// <param name="ShowTrayIcon">False when the settings hide the icon.</param>
/// <param name="UiUrl">The loopback address of the UI; null when it is not known.</param>
public sealed record TrayStatus(
    string AgentName,
    int PairedClients,
    int ConnectedClients,
    bool ShowTrayIcon,
    string? UiUrl);

/// <summary>
/// Reports the state that the platform shows to the user in the tray icon. The host of the agent implements it, because it
/// knows the settings, the pairing and the link connections; the Windows platform sends the result to the SessionHost.
/// </summary>
public interface ITrayStatusSource
{
    /// <summary>Reads the current state.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The state.</returns>
    ValueTask<TrayStatus> GetAsync(CancellationToken cancellationToken);
}
