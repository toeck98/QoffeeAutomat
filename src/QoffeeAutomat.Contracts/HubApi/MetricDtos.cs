namespace QoffeeAutomat.Hub.Metrics;

/// <summary>A point of a series: a raw sample, a bucket of a roll-up, or an entry of the change log.</summary>
/// <param name="Ts">The time of the sample, the start of the bucket, or the time the text was first seen.</param>
/// <param name="Value">The value of a raw sample of a numeric metric (0 or 1 for a boolean).</param>
/// <param name="Min">The smallest value of the bucket.</param>
/// <param name="Avg">The average of the values of the bucket.</param>
/// <param name="Max">The largest value of the bucket.</param>
/// <param name="Count">The number of samples of the bucket.</param>
/// <param name="Text">The text of an entry of the change log.</param>
public sealed record MetricPoint(DateTimeOffset Ts, double? Value, double? Min, double? Avg, double? Max, long? Count, string? Text);

/// <summary>The points of one series.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="MetricId">The metric id.</param>
/// <param name="ValueType">The value type.</param>
/// <param name="Unit">The unit, if the metric has one.</param>
/// <param name="Resolution">The resolution of <paramref name="Points"/>: <c>raw</c>, <c>5m</c> or <c>1h</c> for a numeric metric, <c>changes</c> for a text.</param>
/// <param name="Points">The points, oldest first.</param>
/// <param name="Truncated">True when the range holds more points than the query allowed; the oldest points are returned.</param>
public sealed record MetricSeriesData(
    string AgentId,
    string MetricId,
    string ValueType,
    string? Unit,
    string Resolution,
    IReadOnlyList<MetricPoint> Points,
    bool Truncated);

/// <summary>The newest value of one series.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="MetricId">The metric id.</param>
/// <param name="ValueType">The value type.</param>
/// <param name="Unit">The unit, if the metric has one.</param>
/// <param name="Ts">When the value was reported; absent when the series has no value.</param>
/// <param name="Value">The value of a numeric metric (0 or 1 for a boolean).</param>
/// <param name="Text">The value of a text metric.</param>
public sealed record LatestMetric(string AgentId, string MetricId, string ValueType, string? Unit, DateTimeOffset? Ts, double? Value, string? Text);

/// <summary>The answer of <c>GET /api/v1/metrics/query</c>.</summary>
/// <param name="Resolution">The resolution that was used for numeric metrics: <c>raw</c>, <c>5m</c> or <c>1h</c>.</param>
/// <param name="From">The start of the range.</param>
/// <param name="To">The end of the range.</param>
/// <param name="Series">The series, ordered by agent and metric id.</param>
public sealed record MetricQueryResult(string Resolution, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<MetricSeriesData> Series);

/// <summary>The answer of <c>GET /api/v1/agents/{id}/metrics</c>.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="Metrics">The series of the agent with their newest value, ordered by metric id.</param>
public sealed record AgentMetricList(string AgentId, IReadOnlyList<LatestMetric> Metrics);
