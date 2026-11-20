using System.Globalization;
using System.Text;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Hub.Gateway;

/// <summary>
/// What one internal consumer of the gateway wants to receive from an agent (PLAN.md S20 "Fleet link": the hub holds one subscription per agent that is the union of
/// its collection policy, the fleet clients' subscriptions and open hub UI views). Either every metric, unthrottled (<see cref="Everything"/>), or a set of metric ids,
/// each live (no interval) or at most once per interval. The gateway combines the demands of all consumers of an agent with <see cref="MetricUnion"/>.
/// </summary>
public sealed class MetricDemand
{
    /// <summary>The longest metric id that is accepted.</summary>
    public const int MaxMetricIdLength = 128;

    /// <summary>The longest interval that is accepted: one day.</summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromDays(1);

    private MetricDemand(bool all, IReadOnlyDictionary<string, TimeSpan?> metrics)
    {
        All = all;
        Metrics = metrics;
    }

    /// <summary>No metric at all.</summary>
    public static MetricDemand None { get; } = new(false, new Dictionary<string, TimeSpan?>(StringComparer.Ordinal));

    /// <summary>Every metric of the agent, unthrottled (the wildcard <c>*</c> of <c>subscribe</c>).</summary>
    public static MetricDemand Everything { get; } = new(true, new Dictionary<string, TimeSpan?>(StringComparer.Ordinal));

    /// <summary>True when every metric is wanted; <see cref="Metrics"/> is empty then.</summary>
    public bool All { get; }

    /// <summary>The metric ids that are wanted, with the interval (the most often that a value is wanted) or null for live values.</summary>
    public IReadOnlyDictionary<string, TimeSpan?> Metrics { get; }

    /// <summary>True when nothing is wanted.</summary>
    public bool IsEmpty => !All && Metrics.Count == 0;

    /// <summary>Wants the given metrics live.</summary>
    /// <param name="metricIds">The metric ids.</param>
    /// <returns>The demand.</returns>
    /// <exception cref="ArgumentException">An id is empty, too long or contains a control character.</exception>
    public static MetricDemand Live(IEnumerable<string> metricIds)
    {
        ArgumentNullException.ThrowIfNull(metricIds);

        return Of(metricIds.Select(id => new KeyValuePair<string, TimeSpan?>(id, null)));
    }

    /// <summary>Wants the given metrics, each live or with an interval.</summary>
    /// <param name="metrics">The metric ids with their intervals; a null interval means live. A metric that is listed twice keeps the shorter interval.</param>
    /// <returns>The demand.</returns>
    /// <exception cref="ArgumentException">An id is empty, too long or contains a control character.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An interval is not longer than zero or longer than <see cref="MaxInterval"/>.</exception>
    public static MetricDemand Of(IEnumerable<KeyValuePair<string, TimeSpan?>> metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        var result = new Dictionary<string, TimeSpan?>(StringComparer.Ordinal);
        foreach ((string id, TimeSpan? interval) in metrics)
        {
            CheckId(id);
            if (interval is { } span)
            {
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(span, TimeSpan.Zero, nameof(metrics));
                ArgumentOutOfRangeException.ThrowIfGreaterThan(span, MaxInterval, nameof(metrics));
            }

            result[id] = result.TryGetValue(id, out TimeSpan? existing) ? Shorter(existing, interval) : interval;
        }

        return result.Count == 0 ? None : new MetricDemand(false, result);
    }

    private static TimeSpan? Shorter(TimeSpan? left, TimeSpan? right)
    {
        // Live is shorter than any interval.
        if (left is null || right is null)
        {
            return null;
        }

        return left <= right ? left : right;
    }

    private static void CheckId(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (id.Length > MaxMetricIdLength)
        {
            throw new ArgumentException("A metric id has at most " + MaxMetricIdLength.ToString(CultureInfo.InvariantCulture) + " characters.", nameof(id));
        }

        foreach (char c in id)
        {
            if (char.IsControl(c))
            {
                throw new ArgumentException("A metric id must not contain control characters.", nameof(id));
            }
        }
    }
}

/// <summary>
/// The union of the demands of all consumers of one agent: the one <c>subscribe</c> that the gateway keeps the agent at (PLAN.md S07, S20). A metric that any consumer
/// wants live is live in the union; a metric that every consumer that wants it wants at an interval gets the shortest of these intervals. When a consumer wants every
/// metric, the union is the wildcard, unthrottled. A union is a value: two unions of the same demands have the same <see cref="Key"/>, which is how the gateway
/// recognises that the agent already has this subscription.
/// </summary>
public sealed class MetricUnion
{
    private MetricUnion(bool all, IReadOnlyList<string> metrics, IReadOnlyDictionary<string, double> intervals)
    {
        All = all;
        Metrics = metrics;
        Intervals = intervals;
        Key = BuildKey(all, metrics, intervals);
    }

    /// <summary>The union of nothing: the agent needs no subscription.</summary>
    public static MetricUnion Empty { get; } = new(false, [], new SortedDictionary<string, double>(StringComparer.Ordinal));

    /// <summary>True when every metric is subscribed; <see cref="Metrics"/> is empty then.</summary>
    public bool All { get; }

    /// <summary>The subscribed metric ids in ordinal order (empty for the wildcard).</summary>
    public IReadOnlyList<string> Metrics { get; }

    /// <summary>Metric id to interval in seconds, for the metrics that are not live.</summary>
    public IReadOnlyDictionary<string, double> Intervals { get; }

    /// <summary>True when the union subscribes nothing.</summary>
    public bool IsEmpty => !All && Metrics.Count == 0;

    /// <summary>A canonical text of the union; equal unions have equal keys, and the empty union has the key of an agent that has no subscription (the empty string).</summary>
    public string Key { get; }

    /// <summary>The number of metric ids in the union (zero for the wildcard).</summary>
    public int Count => Metrics.Count;

    /// <summary>Combines demands.</summary>
    /// <param name="demands">The demands of the consumers of one agent.</param>
    /// <returns>The union.</returns>
    public static MetricUnion Compute(IEnumerable<MetricDemand> demands)
    {
        ArgumentNullException.ThrowIfNull(demands);

        bool all = false;
        var live = new HashSet<string>(StringComparer.Ordinal);
        var shortest = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        foreach (MetricDemand demand in demands)
        {
            if (demand.All)
            {
                all = true;
                continue;
            }

            foreach ((string id, TimeSpan? interval) in demand.Metrics)
            {
                if (interval is not { } span)
                {
                    live.Add(id);
                }
                else if (!shortest.TryGetValue(id, out TimeSpan existing) || span < existing)
                {
                    shortest[id] = span;
                }
            }
        }

        if (all)
        {
            // Every metric is wanted live by some consumer, so no interval can apply.
            return new MetricUnion(true, [], new SortedDictionary<string, double>(StringComparer.Ordinal));
        }

        var ids = new SortedSet<string>(live, StringComparer.Ordinal);
        ids.UnionWith(shortest.Keys);
        if (ids.Count == 0)
        {
            return Empty;
        }

        var intervals = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            if (!live.Contains(id))
            {
                intervals[id] = shortest[id].TotalSeconds;
            }
        }

        return new MetricUnion(false, [.. ids], intervals);
    }

    /// <summary>Creates the <c>subscribe</c> request that puts the agent in this state.</summary>
    /// <param name="id">The request id.</param>
    /// <returns>The message; for the empty union it has an empty metric list, which ends the subscription.</returns>
    public SubscribeMessage ToMessage(long id)
    {
        return new SubscribeMessage(
            id,
            All ? MetricSelection.Everything : MetricSelection.From(Metrics),
            Intervals.Count > 0 ? Intervals : null);
    }

    private static string BuildKey(bool all, IReadOnlyList<string> metrics, IReadOnlyDictionary<string, double> intervals)
    {
        if (!all && metrics.Count == 0)
        {
            return string.Empty;
        }

        var key = new StringBuilder();
        key.Append(all ? "*" : string.Join(',', metrics)).Append(';');
        bool first = true;
        foreach ((string id, double seconds) in intervals)
        {
            if (!first)
            {
                key.Append(',');
            }

            first = false;
            key.Append(id).Append('=').Append(seconds.ToString("R", CultureInfo.InvariantCulture));
        }

        return key.ToString();
    }
}
