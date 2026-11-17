using QoffeeAutomat.Contracts.Api;

namespace QoffeeAutomat.Ui.Components.Pages.Dashboard;

/// <summary>Chooses the metrics that get a tile on the dashboard.</summary>
public static class MetricSelector
{
    /// <summary>The metrics a PC owner wants to see first, in this order, when they are enabled.</summary>
    public static readonly IReadOnlyList<string> Preferred = ["cpu.usage", "memory.used_percent", "gpu.usage", "battery.level"];

    private const string NumberType = "number";
    private const string DiagnosticCategory = "diagnostic";

    /// <summary>Picks the tiles: the preferred metrics that are enabled, then other enabled numeric metrics in catalog order.</summary>
    /// <param name="metrics">The catalog.</param>
    /// <param name="maximum">The most tiles.</param>
    /// <returns>The metrics to show.</returns>
    public static IReadOnlyList<MetricInfo> Pick(IReadOnlyList<MetricInfo> metrics, int maximum)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        var chosen = new List<MetricInfo>();
        foreach (string id in Preferred)
        {
            MetricInfo? match = metrics.FirstOrDefault(metric => IsCandidate(metric) && string.Equals(metric.Id, id, StringComparison.Ordinal));
            if (match is not null)
            {
                chosen.Add(match);
            }
        }

        foreach (MetricInfo metric in metrics)
        {
            if (IsCandidate(metric) && !chosen.Contains(metric))
            {
                chosen.Add(metric);
            }
        }

        return chosen.Count > maximum ? chosen.GetRange(0, Math.Max(0, maximum)) : chosen;
    }

    private static bool IsCandidate(MetricInfo metric)
    {
        return metric.Enabled
            && string.Equals(metric.ValueType, NumberType, StringComparison.Ordinal)
            && !string.Equals(metric.EntityCategory, DiagnosticCategory, StringComparison.Ordinal);
    }
}
