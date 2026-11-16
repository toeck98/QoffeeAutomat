namespace QoffeeAutomat.Ui.Components.Pages.Dashboard;

/// <summary>Tuning of the dashboard. The defaults are right for users; tests shorten the intervals.</summary>
public sealed class DashboardOptions
{
    /// <summary>How often the status, the link clients, the runs and the events are read while the page is visible.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait before the metric stream is opened again after it ended or failed.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>The metric stream sends at most one value per this many seconds and metric.</summary>
    public int StreamIntervalSeconds { get; set; } = 2;

    /// <summary>How many seconds of history the charts show.</summary>
    public int HistorySeconds { get; set; } = 900;

    /// <summary>How many runs the list shows.</summary>
    public int RecentRuns { get; set; } = 8;

    /// <summary>How many events the list shows.</summary>
    public int RecentEvents { get; set; } = 8;

    /// <summary>The most metric tiles.</summary>
    public int MaxTiles { get; set; } = 6;
}
