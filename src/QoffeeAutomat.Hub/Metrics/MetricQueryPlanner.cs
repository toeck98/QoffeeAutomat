namespace QoffeeAutomat.Hub.Metrics;

/// <summary>
/// Chooses the resolution of a metric query from its time range (PLAN.md S20 "Metrics on the hub": "The query API picks the resolution from the requested time range").
/// A range of up to 6 hours is answered with raw samples (360 points at the default interval), up to 7 days with five minute buckets (2016 points) and anything longer
/// with one hour buckets (about 17 500 points for two years). When the start of the range is older than the retention of the chosen resolution, the next coarser one is
/// used, which still has the data.
/// </summary>
public static class MetricQueryPlanner
{
    /// <summary>The longest range that is answered with raw samples.</summary>
    public static readonly TimeSpan RawMaxRange = TimeSpan.FromHours(6);

    /// <summary>The longest range that is answered with five minute buckets.</summary>
    public static readonly TimeSpan FiveMinuteMaxRange = TimeSpan.FromDays(7);

    /// <summary>Chooses the resolution.</summary>
    /// <param name="from">The start of the range.</param>
    /// <param name="to">The end of the range.</param>
    /// <param name="now">The time now.</param>
    /// <param name="options">The retention of the tables.</param>
    /// <returns>The resolution.</returns>
    public static MetricResolution Choose(DateTimeOffset from, DateTimeOffset to, DateTimeOffset now, HubMetricsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        TimeSpan range = to - from;
        MetricResolution resolution = range <= RawMaxRange
            ? MetricResolution.Raw
            : range <= FiveMinuteMaxRange ? MetricResolution.FiveMinutes : MetricResolution.OneHour;

        if (resolution == MetricResolution.Raw && from < now - options.RawRetention)
        {
            resolution = MetricResolution.FiveMinutes;
        }

        if (resolution == MetricResolution.FiveMinutes && from < now - options.Rollup5mRetention)
        {
            resolution = MetricResolution.OneHour;
        }

        return resolution;
    }
}
