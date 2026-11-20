namespace QoffeeAutomat.Hub.Ui.Services;

/// <summary>Tuning of the hub pages. The defaults are right for users; tests shorten the intervals.</summary>
public sealed class HubUiOptions
{
    /// <summary>How often the fleet overview and the agent header are read again while the page is open.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How many entries of the audit log are read at a time.</summary>
    public int AuditPageSize { get; set; } = 50;

    /// <summary>The clock of the pages that work with time ranges (the metric charts); tests replace it.</summary>
    public TimeProvider Time { get; set; } = TimeProvider.System;
}
