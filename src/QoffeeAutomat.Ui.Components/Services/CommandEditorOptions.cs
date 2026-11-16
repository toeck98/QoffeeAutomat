namespace QoffeeAutomat.Ui.Components.Services;

/// <summary>
/// Settings of the command editor that tests change: whether the code is edited in Monaco (which needs the browser) or in
/// a plain text area, and how long the editor waits after a change before it asks the agent to validate the draft.
/// </summary>
public sealed class CommandEditorOptions
{
    /// <summary>
    /// True to edit PowerShell in the Monaco editor that ships with the UI. False shows a text area instead, which is what a
    /// component test (no browser, no JavaScript) needs.
    /// </summary>
    public bool UseMonaco { get; set; } = true;

    /// <summary>How long the draft has to rest before it is validated.</summary>
    public TimeSpan ValidationDelay { get; set; } = TimeSpan.FromMilliseconds(500);
}
