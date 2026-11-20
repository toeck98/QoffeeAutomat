using System.Globalization;
using QoffeeAutomat.Hub.Gateway;

namespace QoffeeAutomat.Hub.Metrics;

/// <summary>Matching of metric ids against the patterns of a collection policy and of a query.</summary>
public static class MetricPattern
{
    /// <summary>The wildcard: at the end of a pattern it stands for any ending; alone it matches every metric.</summary>
    public const char Wildcard = '*';

    /// <summary>Tells whether a pattern is acceptable: an exact metric id, or a prefix followed by <c>*</c>.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <returns>True for a non-empty pattern of at most <see cref="MetricDemand.MaxMetricIdLength"/> characters without control characters and without a <c>*</c> anywhere but at the end.</returns>
    public static bool IsValid(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern.Length > MetricDemand.MaxMetricIdLength + 1)
        {
            return false;
        }

        for (int index = 0; index < pattern.Length; index++)
        {
            char c = pattern[index];
            if (char.IsControl(c) || (c == Wildcard && index != pattern.Length - 1))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Tells whether a metric id matches a pattern.</summary>
    /// <param name="pattern">The pattern; see <see cref="IsValid"/>.</param>
    /// <param name="metricId">The metric id.</param>
    /// <returns>True for an equal id, or for an id that starts with the pattern without its final <c>*</c>.</returns>
    public static bool Matches(string pattern, string metricId)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(metricId);

        return pattern.EndsWith(Wildcard)
            ? metricId.AsSpan().StartsWith(pattern.AsSpan(0, pattern.Length - 1), StringComparison.Ordinal)
            : string.Equals(pattern, metricId, StringComparison.Ordinal);
    }

    /// <summary>Tells whether a metric id matches any of the patterns.</summary>
    /// <param name="patterns">The patterns.</param>
    /// <param name="metricId">The metric id.</param>
    /// <returns>True when one pattern matches.</returns>
    public static bool MatchesAny(IReadOnlyList<string> patterns, string metricId)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        for (int index = 0; index < patterns.Count; index++)
        {
            if (Matches(patterns[index], metricId))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Which metrics of an agent the hub stores and how often (PLAN.md S20 "Metrics on the hub"): ids or prefixes (<c>cpu.*</c>) and a collection interval. The hub
/// subscribes to the metrics of the agent's manifest that match, at the interval, and keeps what the agent reports.
/// </summary>
/// <param name="Metrics">The metric ids or prefixes; <c>*</c> alone is every metric.</param>
/// <param name="Interval">The collection interval; the hub keeps at most about one sample per interval for a metric.</param>
public sealed record MetricCollectionPolicy(IReadOnlyList<string> Metrics, TimeSpan Interval)
{
    /// <summary>The default collection interval: 60 seconds.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

    /// <summary>The shortest collection interval.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    /// <summary>The default policy: every metric of the agent every 60 seconds.</summary>
    public static MetricCollectionPolicy Default { get; } = new([MetricPattern.Wildcard.ToString()], DefaultInterval);

    /// <summary>A policy that collects nothing.</summary>
    public static MetricCollectionPolicy Nothing { get; } = new([], DefaultInterval);

    /// <summary>Tells whether a metric is collected.</summary>
    /// <param name="metricId">The metric id.</param>
    /// <returns>True when a pattern matches.</returns>
    public bool Collects(string metricId) => MetricPattern.MatchesAny(Metrics, metricId);

    /// <summary>Checks the policy.</summary>
    /// <exception cref="ArgumentException">A pattern is not valid, or the interval is out of range.</exception>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Metrics);

        foreach (string pattern in Metrics)
        {
            if (!MetricPattern.IsValid(pattern))
            {
                throw new ArgumentException("The pattern '" + pattern + "' is not a metric id or a prefix that ends in '*'.", nameof(Metrics));
            }
        }

        if (Interval < MinInterval || Interval > MetricDemand.MaxInterval)
        {
            throw new ArgumentException(
                "The collection interval must be between " + MinInterval.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                + " seconds and one day.",
                nameof(Interval));
        }
    }
}

/// <summary>
/// Tells the collector which policy applies to an agent. The default (<see cref="GlobalMetricPolicySource"/>) is the global policy for every agent. A source that
/// resolves a policy of the profiles that apply to the agent plugs in here without a change of the collector.
/// </summary>
public interface IMetricPolicySource
{
    /// <summary>The policy of an agent.</summary>
    /// <param name="agentId">The agent id.</param>
    /// <returns>The policy.</returns>
    MetricCollectionPolicy GetPolicy(string agentId);
}

/// <summary>The policy source of the hub: the global policy of <see cref="HubMetricsOptions.DefaultPolicy"/> for every agent.</summary>
/// <param name="options">The options.</param>
public sealed class GlobalMetricPolicySource(HubMetricsOptions options) : IMetricPolicySource
{
    /// <inheritdoc />
    public MetricCollectionPolicy GetPolicy(string agentId) => options.DefaultPolicy;
}
