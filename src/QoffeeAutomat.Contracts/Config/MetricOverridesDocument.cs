namespace QoffeeAutomat.Contracts.Config;

/// <summary>
/// Overrides for built-in metrics, <c>config/metrics.json</c> (PLAN.md S06). Only the members
/// that are present override the defaults of the metric.
/// </summary>
/// <param name="SchemaVersion">Version of the document schema.</param>
/// <param name="Overrides">Metric id to override.</param>
/// <param name="Revision">Revision, incremented on every save.</param>
/// <param name="UpdatedAt">Time of the last save.</param>
/// <param name="Origin">Where the document was authored.</param>
public sealed record MetricOverridesDocument(
    int SchemaVersion,
    IReadOnlyDictionary<string, MetricOverride> Overrides,
    int? Revision = null,
    DateTimeOffset? UpdatedAt = null,
    string? Origin = null) : IConfigDocument;

/// <summary>The override of one built-in metric.</summary>
/// <param name="Enabled">Whether the metric is sampled when subscribed.</param>
/// <param name="ExposeToHa">Whether the metric appears in the manifest.</param>
/// <param name="IntervalSeconds">The sampling interval while subscribed.</param>
/// <param name="Deadband">The minimum absolute change before a new value is published.</param>
public sealed record MetricOverride(
    bool? Enabled = null,
    bool? ExposeToHa = null,
    double? IntervalSeconds = null,
    double? Deadband = null);
