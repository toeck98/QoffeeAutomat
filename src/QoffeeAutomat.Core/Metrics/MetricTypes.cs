using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Core.Metrics;

/// <summary>
/// A metric as the engine knows it: the descriptor of a provider or custom sensor merged with the
/// overrides of <c>config/metrics.json</c> (PLAN.md S06).
/// </summary>
/// <param name="Descriptor">
/// The descriptor with the effective <see cref="MetricDescriptor.IntervalSeconds"/> and
/// <see cref="MetricDescriptor.Deadband"/> filled in; this is what the manifest announces.
/// </param>
/// <param name="Enabled">False metrics are never sampled, even when somebody subscribes to them.</param>
/// <param name="ExposeToHa">Whether the metric belongs in the manifest.</param>
/// <param name="IntervalSeconds">The sampling interval while subscribed; zero means event-driven (sampled on subscription and on request only).</param>
/// <param name="Deadband">The minimum absolute change before a new value is published; null publishes every change.</param>
/// <param name="IsCustom">True for metrics that come from a custom sensor document.</param>
public sealed record MetricCatalogEntry(
    MetricDescriptor Descriptor,
    bool Enabled,
    bool ExposeToHa,
    double IntervalSeconds,
    double? Deadband,
    bool IsCustom)
{
    /// <summary>The metric id.</summary>
    public string Id => Descriptor.Id;
}

/// <summary>One recorded value of a metric, as kept for charts.</summary>
/// <param name="Timestamp">When the value was sampled.</param>
/// <param name="Value">The value after precision rounding; null means unavailable.</param>
public readonly record struct MetricSample(DateTimeOffset Timestamp, object? Value);

/// <summary>
/// What a subscriber receives: either the full snapshot that follows a subscription, or the values
/// that changed since the previous update (PLAN.md S07, <c>state</c>).
/// </summary>
/// <param name="Timestamp">When the update was produced.</param>
/// <param name="Values">Metric id to value; null means unavailable.</param>
/// <param name="IsSnapshot">True when the update carries every subscribed metric.</param>
public sealed record MetricUpdate(DateTimeOffset Timestamp, IReadOnlyDictionary<string, object?> Values, bool IsSnapshot);

/// <summary>Raised when a provider throws while it is sampled.</summary>
public sealed class ProviderFailedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="metricIds">The metrics that were being sampled and are unavailable now.</param>
    /// <param name="exception">What the provider threw.</param>
    /// <param name="consecutiveFailures">How many samples in a row failed, including this one.</param>
    /// <param name="retryIn">How long the engine waits before it asks the provider again.</param>
    public ProviderFailedEventArgs(
        IReadOnlyList<string> metricIds,
        Exception exception,
        int consecutiveFailures,
        TimeSpan retryIn)
    {
        MetricIds = metricIds;
        Exception = exception;
        ConsecutiveFailures = consecutiveFailures;
        RetryIn = retryIn;
    }

    /// <summary>The metrics that became unavailable.</summary>
    public IReadOnlyList<string> MetricIds { get; }

    /// <summary>What the provider threw.</summary>
    public Exception Exception { get; }

    /// <summary>How many samples in a row failed.</summary>
    public int ConsecutiveFailures { get; }

    /// <summary>The backoff before the next attempt.</summary>
    public TimeSpan RetryIn { get; }
}
