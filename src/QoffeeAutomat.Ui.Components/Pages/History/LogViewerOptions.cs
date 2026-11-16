namespace QoffeeAutomat.Ui.Components.Pages.History;

/// <summary>How the log viewer behaves; tests make the interval short.</summary>
public sealed class LogViewerOptions
{
    /// <summary>How often follow mode reads the newest entries again.</summary>
    public TimeSpan FollowInterval { get; set; } = TimeSpan.FromSeconds(2);
}
