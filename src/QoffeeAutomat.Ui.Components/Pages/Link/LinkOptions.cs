namespace QoffeeAutomat.Ui.Components.Pages.Link;

/// <summary>How often the Home Assistant link page asks the agent; tests make the intervals short.</summary>
public sealed class LinkOptions
{
    /// <summary>
    /// How often the pairing dialog asks whether the code is still valid, which is also the step of its countdown. A client
    /// that pairs closes the dialog within this time.
    /// </summary>
    public TimeSpan PairingPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How often the list of paired clients is read again (connections and last seen change while the page is open).</summary>
    public TimeSpan ClientRefreshInterval { get; set; } = TimeSpan.FromSeconds(10);
}
