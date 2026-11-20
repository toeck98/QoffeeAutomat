namespace QoffeeAutomat.Hub.Metrics;

/// <summary>
/// Where the hub keeps the metrics of its agents (PLAN.md D-032, S20 "Metrics on the hub", T910). The store has raw samples of numeric and boolean metrics, a change log of
/// text metrics, and roll-ups of the numeric ones; it is the seam to another time-series store. The SQLite implementation is <see cref="SqliteMetricStore"/>.
/// Every method may be called from any thread.
/// </summary>
public interface IMetricStore
{
    /// <summary>Writes samples in one transaction. Series are created as needed. A text that equals the newest text of its series is not written again.</summary>
    /// <param name="samples">The samples; the order of the samples of one series is not significant.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>How many samples were written, unchanged, or skipped because their agent is gone.</returns>
    Task<MetricAppendResult> AppendAsync(IReadOnlyList<MetricSample> samples, CancellationToken cancellationToken);

    /// <summary>Reads series.</summary>
    /// <param name="query">What to read.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The series that match, ordered by agent and metric id; a series without points in the range is returned with no points.</returns>
    Task<IReadOnlyList<MetricSeriesData>> QueryAsync(MetricQuery query, CancellationToken cancellationToken);

    /// <summary>Counts the series that a query would read, without reading their points.</summary>
    /// <param name="agentIds">The agents.</param>
    /// <param name="metricIds">The metric ids or prefixes, as in <see cref="MetricQuery.MetricIds"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The number of series that match.</returns>
    Task<int> CountSeriesAsync(IReadOnlyList<string> agentIds, IReadOnlyList<string> metricIds, CancellationToken cancellationToken);

    /// <summary>The newest value of every series of an agent.</summary>
    /// <param name="agentId">The agent.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The series ordered by metric id; empty for an agent without series.</returns>
    Task<IReadOnlyList<LatestMetric>> GetLatestAsync(string agentId, CancellationToken cancellationToken);

    /// <summary>Writes the roll-ups for the buckets that have ended.</summary>
    /// <param name="now">The time now; a bucket is rolled up when it ended at least the grace period before.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The number of buckets that were written.</returns>
    Task<MetricRollupResult> RollUpAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Removes what is older than the retention of its table. Raw samples that were not rolled up yet are kept.</summary>
    /// <param name="now">The time now.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The number of rows that were removed.</returns>
    Task<MetricRetentionResult> ApplyRetentionAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Removes every series of an agent with all of its data.</summary>
    /// <param name="agentId">The agent.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The number of series that were removed.</returns>
    Task<int> RemoveAgentAsync(string agentId, CancellationToken cancellationToken);

    /// <summary>Counts the rows of the tables and measures the database.</summary>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The sizes.</returns>
    Task<MetricStoreStats> GetStatsAsync(CancellationToken cancellationToken);
}
