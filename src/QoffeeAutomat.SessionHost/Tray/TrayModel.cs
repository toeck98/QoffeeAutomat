using QoffeeAutomat.Contracts.Session;

namespace QoffeeAutomat.SessionHost.Tray;

/// <summary>The look of the tray icon (PLAN.md S11, T310).</summary>
public enum TrayIconState
{
    /// <summary>The agent cannot be reached.</summary>
    Disconnected,

    /// <summary>The agent is not paired with a Home Assistant instance.</summary>
    NotPaired,

    /// <summary>The agent is paired and the commands are enabled.</summary>
    Connected,

    /// <summary>The global pause flag is set: every run is rejected with <c>paused</c>.</summary>
    Paused,
}

/// <summary>What the tray icon shows and which menu entries work; the shell applies it without further thought.</summary>
/// <param name="Visible">Whether the icon is in the notification area at all.</param>
/// <param name="Icon">The look of the icon.</param>
/// <param name="Tooltip">The tooltip: agent name and link status.</param>
/// <param name="AgentName">The agent name, for the About box.</param>
/// <param name="UiUri">The loopback address that Open UI opens; null when none is known.</param>
/// <param name="CanOpenUi">Whether the entry Open UI works.</param>
/// <param name="CanPause">Whether the entry Pause commands works.</param>
/// <param name="CanResume">Whether the entry Resume commands works.</param>
public sealed record TrayView(
    bool Visible,
    TrayIconState Icon,
    string Tooltip,
    string AgentName,
    Uri? UiUri,
    bool CanOpenUi,
    bool CanPause,
    bool CanResume)
{
    /// <summary>No icon at all.</summary>
    public static TrayView Hidden { get; } = new(false, TrayIconState.Disconnected, string.Empty, string.Empty, null, false, false, false);
}

/// <summary>
/// Decides what the tray icon shows from what the agent reported and whether the pipe is connected. It is a pure function,
/// so every state can be tested without a desktop.
/// </summary>
public static class TrayPresenter
{
    /// <summary>The longest tooltip that a notification icon accepts.</summary>
    public const int MaxTooltipLength = 127;

    /// <summary>Derives the view.</summary>
    /// <param name="state">The last <c>tray_state</c> of the agent; null before the first one arrived.</param>
    /// <param name="connected">Whether the pipe to the agent is connected right now.</param>
    /// <returns>
    /// Hidden until the agent said that the icon is wanted (a hidden icon never flashes up at logon) and when the setting
    /// <c>trayIcon</c> is off. The pause wins over the pairing, the pairing over the plain connected state.
    /// </returns>
    public static TrayView Present(SessionTrayStateMessage? state, bool connected)
    {
        if (state is null || !state.ShowTrayIcon)
        {
            return TrayView.Hidden;
        }

        string name = string.IsNullOrWhiteSpace(state.AgentName) ? "QoffeeAutomat" : state.AgentName;
        Uri? ui = ParseUiUrl(state.UiUrl);

        if (!connected)
        {
            // The last known name stays; the menu has nothing to offer while the agent is away.
            return new TrayView(true, TrayIconState.Disconnected, Tooltip(name + ": agent not reachable"), name, ui, false, false, false);
        }

        string link = state.ConnectedClients > 0
            ? "connected to Home Assistant"
            : state.PairedClients > 0
                ? "paired, Home Assistant not connected"
                : "not paired with Home Assistant";
        TrayIconState icon = state.Paused
            ? TrayIconState.Paused
            : state.PairedClients > 0 ? TrayIconState.Connected : TrayIconState.NotPaired;
        string text = name + ": " + link + (state.Paused ? " - commands paused" : string.Empty);

        return new TrayView(true, icon, Tooltip(text), name, ui, CanOpenUi: ui is not null, CanPause: !state.Paused, CanResume: state.Paused);
    }

    /// <summary>
    /// Accepts only a plain http address on this computer. The address comes from the agent, but a menu entry that opens
    /// whatever it is told to would be a way to open anything in the user's browser.
    /// </summary>
    /// <param name="url">The address from <c>tray_state</c>.</param>
    /// <returns>The address; null when it is absent or not a loopback http address.</returns>
    public static Uri? ParseUiUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || !uri.IsLoopback
            || uri.UserInfo.Length > 0)
        {
            return null;
        }

        return uri;
    }

    private static string Tooltip(string text)
    {
        return text.Length <= MaxTooltipLength ? text : string.Concat(text.AsSpan(0, MaxTooltipLength - 3), "...");
    }
}
