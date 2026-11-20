using System.Globalization;
using System.Text;
using QoffeeAutomat.Hub.Metrics;

namespace QoffeeAutomat.Hub.Ui.Pages.Metrics;

/// <summary>One agent's line in a chart that compares a metric across agents.</summary>
/// <param name="AgentId">The agent id.</param>
/// <param name="AgentName">The name shown in the legend.</param>
/// <param name="Slot">The index of the colour, <c>0</c> and up; the CSS classes <c>qh-series-N</c> repeat after eight.</param>
/// <param name="Points">The points of the SVG polyline in the view box of <see cref="MetricChartModel"/>; empty when there are fewer than two.</param>
/// <param name="Latest">The newest value in the range; <c>null</c> when the agent has none.</param>
/// <param name="Minimum">The smallest value in the range.</param>
/// <param name="Average">The average over the range, weighted by the number of samples that each point stands for.</param>
/// <param name="Maximum">The largest value in the range.</param>
/// <param name="PointCount">The number of points of the series.</param>
/// <param name="Truncated">True when the hub cut the series because the range holds more points than the query allowed.</param>
public sealed record ChartSeries(
    string AgentId,
    string AgentName,
    int Slot,
    string Points,
    double? Latest,
    double? Minimum,
    double? Average,
    double? Maximum,
    int PointCount,
    bool Truncated);

/// <summary>
/// The chart of one metric for several agents (PLAN.md S20, T916): the lines share one time axis (the range that was asked for) and one value axis, so
/// they can be compared. It is built from the answer of the query API without any charting library; the view box is 100 by 40 units like the small chart
/// of the component library, drawn with <c>preserveAspectRatio="none"</c>, so the numbers on the axes are text next to the SVG.
/// </summary>
/// <param name="Series">One line per agent, in the order of the agents.</param>
/// <param name="Minimum">The smallest value over all lines; the bottom of the value axis.</param>
/// <param name="Maximum">The largest value over all lines; the top of the value axis.</param>
/// <param name="From">The start of the time axis.</param>
/// <param name="To">The end of the time axis.</param>
/// <param name="Unit">The unit of the metric, if it has one.</param>
/// <param name="Resolution">The resolution of the points: <c>raw</c>, <c>5m</c> or <c>1h</c>.</param>
public sealed record MetricChartModel(
    IReadOnlyList<ChartSeries> Series,
    double Minimum,
    double Maximum,
    DateTimeOffset From,
    DateTimeOffset To,
    string? Unit,
    string Resolution)
{
    /// <summary>The width of the view box.</summary>
    public const double Width = 100;

    /// <summary>The height of the view box.</summary>
    public const double Height = 40;

    private const double Padding = 2;

    /// <summary>True when at least one line has two points, so there is something to draw.</summary>
    public bool HasLines => Series.Any(series => series.Points.Length > 0);

    /// <summary>Builds the chart of one metric from the answer of a query.</summary>
    /// <param name="result">The answer of <c>GET /api/v1/metrics/query</c>.</param>
    /// <param name="metricId">The metric to draw.</param>
    /// <param name="agents">The agents in the order of the legend, with their display names.</param>
    /// <returns>The chart. An agent without a series for the metric still has an (empty) entry, so the legend and the table show it.</returns>
    public static MetricChartModel Build(MetricQueryResult result, string metricId, IReadOnlyList<(string Id, string Name)> agents)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(metricId);
        ArgumentNullException.ThrowIfNull(agents);

        var values = new List<(string Id, string Name, int Slot, IReadOnlyList<(DateTimeOffset Time, double Value, double Min, double Max, double Weight)> Points, bool Truncated)>();
        string? unit = null;
        double minimum = double.MaxValue;
        double maximum = double.MinValue;
        for (int slot = 0; slot < agents.Count; slot++)
        {
            (string id, string name) = agents[slot];
            MetricSeriesData? data = result.Series.FirstOrDefault(series => series.AgentId == id && series.MetricId == metricId);
            var points = new List<(DateTimeOffset, double, double, double, double)>();
            if (data is not null)
            {
                unit ??= data.Unit;
                foreach (MetricPoint point in data.Points)
                {
                    double? value = point.Value ?? point.Avg;
                    if (value is not { } plotted)
                    {
                        continue;
                    }

                    double low = point.Min ?? plotted;
                    double high = point.Max ?? plotted;
                    points.Add((point.Ts, plotted, low, high, point.Count is { } count and > 0 ? count : 1));
                    minimum = Math.Min(minimum, Math.Min(plotted, low));
                    maximum = Math.Max(maximum, Math.Max(plotted, high));
                }
            }

            values.Add((id, name, slot, points, data?.Truncated ?? false));
        }

        if (minimum > maximum)
        {
            minimum = 0;
            maximum = 0;
        }

        double span = (result.To - result.From).TotalMilliseconds;
        double range = maximum - minimum;
        var lines = new List<ChartSeries>(values.Count);
        foreach ((string id, string name, int slot, var points, bool truncated) in values)
        {
            string polyline = string.Empty;
            if (points.Count >= 2)
            {
                var builder = new StringBuilder();
                foreach ((DateTimeOffset time, double value, _, _, _) in points)
                {
                    double x = span <= 0 ? 0 : Math.Clamp((time - result.From).TotalMilliseconds / span, 0, 1) * Width;
                    double y = range <= 0 ? Height / 2 : Height - Padding - ((value - minimum) / range * (Height - (2 * Padding)));
                    if (builder.Length > 0)
                    {
                        builder.Append(' ');
                    }

                    builder.Append(x.ToString("0.##", CultureInfo.InvariantCulture)).Append(',').Append(y.ToString("0.##", CultureInfo.InvariantCulture));
                }

                polyline = builder.ToString();
            }

            double? latest = points.Count > 0 ? points[^1].Value : null;
            double? low = points.Count > 0 ? points.Min(point => Math.Min(point.Value, point.Min)) : null;
            double? high = points.Count > 0 ? points.Max(point => Math.Max(point.Value, point.Max)) : null;
            double? average = points.Count > 0 ? points.Sum(point => point.Value * point.Weight) / points.Sum(point => point.Weight) : null;
            lines.Add(new ChartSeries(id, name, slot, polyline, latest, low, average, high, points.Count, truncated));
        }

        return new MetricChartModel(lines, minimum, maximum, result.From, result.To, unit, result.Resolution);
    }
}

/// <summary>The time ranges that the metrics page offers (PLAN.md S20, T916).</summary>
public static class MetricRanges
{
    /// <summary>The ranges: key and length. The keys are the text keys <c>Metrics.Range.&lt;key&gt;</c> of the hub resources.</summary>
    public static IReadOnlyList<(string Key, TimeSpan Length)> All { get; } =
    [
        ("1h", TimeSpan.FromHours(1)),
        ("6h", TimeSpan.FromHours(6)),
        ("24h", TimeSpan.FromHours(24)),
        ("7d", TimeSpan.FromDays(7)),
        ("30d", TimeSpan.FromDays(30)),
    ];

    /// <summary>The range that is selected first.</summary>
    public const string Default = "6h";

    /// <summary>The resolutions the user may force; <c>auto</c> lets the hub choose from the range.</summary>
    public static IReadOnlyList<string> Resolutions { get; } = ["auto", "raw", "5m", "1h"];

    /// <summary>Looks a range up.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The length of the range; the default range when the key is unknown.</returns>
    public static TimeSpan LengthOf(string key)
    {
        foreach ((string candidate, TimeSpan length) in All)
        {
            if (candidate == key)
            {
                return length;
            }
        }

        return LengthOf(Default);
    }
}
