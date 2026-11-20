using System.Collections.Concurrent;
using System.Text.Json;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Hosting;

namespace QoffeeAutomat.Hub.Metrics.Prometheus;

/// <summary>
/// Builds the document of <c>GET /metrics</c> (PLAN.md S20 "Metrics on the hub", T911). Per agent that is enrolled: <c>qoffeeautomat_agent_up</c>, and, only while the agent
/// is online, the latest value of every numeric and boolean metric that the hub collects, taken from the live state of the gateway as
/// <c>qoffeeautomat_&lt;metric id&gt;{agent_id="...",agent="..."}</c>. An agent that is offline exports no metric values, so Prometheus sees a gap and not a stale line. Text metrics
/// are not exported. Then the health of the hub itself. Nothing in the document is a secret: it holds ids, names the administrators chose, version numbers and numbers.
/// </summary>
public sealed class PrometheusExporter : IDisposable
{
    private static readonly TimeSpan StatsMaxAge = TimeSpan.FromSeconds(30);

    private readonly AgentRegistry _registry;
    private readonly IAgentGateway _gateway;
    private readonly IGatewayStatistics _statistics;
    private readonly IMetricStore _store;
    private readonly MetricCollector _collector;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, KnownTypes> _types = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _statsGate = new(1, 1);
    private MetricStoreStats? _stats;
    private DateTimeOffset _statsAt;

    /// <summary>Creates the exporter.</summary>
    /// <param name="registry">The enrolled agents.</param>
    /// <param name="gateway">Presence, manifests and live state.</param>
    /// <param name="statistics">The counters of the gateway.</param>
    /// <param name="store">The metric store, for the size of the database.</param>
    /// <param name="collector">The collector, for the number of dropped samples.</param>
    /// <param name="time">The clock.</param>
    public PrometheusExporter(
        AgentRegistry registry,
        IAgentGateway gateway,
        IGatewayStatistics statistics,
        IMetricStore store,
        MetricCollector collector,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(time);

        _registry = registry;
        _gateway = gateway;
        _statistics = statistics;
        _store = store;
        _collector = collector;
        _time = time;
    }

    private sealed record KnownTypes(string Rev, IReadOnlyDictionary<string, string> ValueTypes);

    private sealed record Sample(string AgentId, string AgentName, double Value);

    /// <summary>Renders the document.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The text exposition format, version 0.0.4.</returns>
    public async Task<PrometheusTextWriter> ExportAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentRecord> records = await _registry.ListAsync(null, cancellationToken).ConfigureAwait(false);
        MetricStoreStats stats = await GetStatsAsync(cancellationToken).ConfigureAwait(false);
        var writer = new PrometheusTextWriter();

        AgentRecord[] enrolled = [.. records.Where(record => record.Status == AgentStatuses.Enrolled).OrderBy(record => record.Id, StringComparer.Ordinal)];
        var online = new HashSet<string>(StringComparer.Ordinal);
        var families = new SortedDictionary<string, List<Sample>>(StringComparer.Ordinal);
        foreach (AgentRecord agent in enrolled)
        {
            if (!_gateway.GetPresence(agent.Id).Online)
            {
                continue;
            }

            _ = online.Add(agent.Id);
            CollectValues(agent, families);
        }

        _ = writer.Family("qoffeeautomat_agent_up", "gauge", "1 while the agent is connected to the hub, 0 while it is not.");
        foreach (AgentRecord agent in enrolled)
        {
            writer.Sample("qoffeeautomat_agent_up", online.Contains(agent.Id) ? 1 : 0, ("agent_id", agent.Id), ("agent", agent.Name));
        }

        WriteHubMetrics(writer, records, online.Count, stats);

        foreach ((string name, List<Sample> samples) in families)
        {
            // A metric id that maps onto the name of a hub metric, or onto the name of another metric id, is dropped: a family must exist once.
            if (!writer.Family(name, "gauge", "Latest value of an agent metric; the unit is the one of the metric in the agent manifest."))
            {
                continue;
            }

            foreach (Sample sample in samples)
            {
                writer.Sample(name, sample.Value, ("agent_id", sample.AgentId), ("agent", sample.AgentName));
            }
        }

        // Forget the manifests of agents that are gone.
        var known = new HashSet<string>(enrolled.Select(agent => agent.Id), StringComparer.Ordinal);
        foreach (string id in _types.Keys.Where(id => !known.Contains(id)))
        {
            _ = _types.TryRemove(id, out _);
        }

        return writer;
    }

    private void CollectValues(AgentRecord agent, SortedDictionary<string, List<Sample>> families)
    {
        CachedManifest? manifest = _gateway.GetManifest(agent.Id);
        AgentStateSnapshot? state = _gateway.GetState(agent.Id);
        if (manifest is null || state is null)
        {
            return;
        }

        IReadOnlyDictionary<string, string>? types = TypesOf(agent.Id, manifest);
        if (types is null)
        {
            return;
        }

        // Within one agent two metric ids can map onto one name (cpu.usage and cpu_usage); the first in id order wins, so the output is stable.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string metricId, JsonElement value) in state.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!types.TryGetValue(metricId, out string? valueType) || !TryNumber(valueType, value, out double number))
            {
                continue;
            }

            string name = PrometheusText.MetricName(metricId);
            if (!seen.Add(name))
            {
                continue;
            }

            if (!families.TryGetValue(name, out List<Sample>? samples))
            {
                samples = [];
                families[name] = samples;
            }

            samples.Add(new Sample(agent.Id, agent.Name, number));
        }
    }

    private IReadOnlyDictionary<string, string>? TypesOf(string agentId, CachedManifest cached)
    {
        if (_types.TryGetValue(agentId, out KnownTypes? known) && known.Rev == cached.Rev)
        {
            return known.ValueTypes;
        }

        try
        {
            Manifest manifest = LinkCodec.ParseManifest(cached.Manifest);
            var types = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (MetricDescriptor metric in manifest.Metrics)
            {
                if (MetricValueTypes.IsNumeric(metric.ValueType))
                {
                    types[metric.Id] = metric.ValueType;
                }
            }

            _types[agentId] = new KnownTypes(cached.Rev, types);
            return types;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static bool TryNumber(string valueType, JsonElement value, out double number)
    {
        number = 0;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when valueType == MetricValueTypes.Number:
                return value.TryGetDouble(out number) && double.IsFinite(number);
            case JsonValueKind.True when valueType == MetricValueTypes.Bool:
                number = 1;
                return true;
            case JsonValueKind.False when valueType == MetricValueTypes.Bool:
                return true;
            default:
                // Null means unavailable; a value of another type than the manifest says is not exported.
                return false;
        }
    }

    private void WriteHubMetrics(PrometheusTextWriter writer, IReadOnlyList<AgentRecord> records, int online, MetricStoreStats stats)
    {
        _ = writer.Family("qoffeeautomat_hub_info", "gauge", "Always 1; the version of the hub is the label.");
        writer.Sample("qoffeeautomat_hub_info", 1, ("version", HubVersion.Current));

        _ = writer.Family("qoffeeautomat_hub_agents", "gauge", "Agents known to the hub by enrollment status.");
        foreach (string status in AgentStatuses.All)
        {
            writer.Sample("qoffeeautomat_hub_agents", records.Count(record => record.Status == status), ("status", status));
        }

        _ = writer.Family("qoffeeautomat_hub_agents_online", "gauge", "Enrolled agents with an open agent link.");
        writer.Sample("qoffeeautomat_hub_agents_online", online);

        _ = writer.Family("qoffeeautomat_hub_link_reconnects_total", "counter", "Times an agent opened its link again since the hub started; the first link of an agent does not count.");
        writer.Sample("qoffeeautomat_hub_link_reconnects_total", _statistics.LinkReconnects);

        _ = writer.Family("qoffeeautomat_hub_metric_samples_dropped_total", "counter", "Metric samples dropped since the hub started because the database could not keep up.");
        writer.Sample("qoffeeautomat_hub_metric_samples_dropped_total", _collector.DroppedSamples);

        _ = writer.Family("qoffeeautomat_hub_database_size_bytes", "gauge", "Size of the hub database, free pages included.");
        writer.Sample("qoffeeautomat_hub_database_size_bytes", stats.DatabaseBytes);

        _ = writer.Family("qoffeeautomat_hub_database_free_bytes", "gauge", "Part of the hub database that is free for reuse.");
        writer.Sample("qoffeeautomat_hub_database_free_bytes", stats.FreeBytes);

        _ = writer.Family("qoffeeautomat_hub_metric_series", "gauge", "Metric series in the store.");
        writer.Sample("qoffeeautomat_hub_metric_series", stats.Series);

        _ = writer.Family("qoffeeautomat_hub_metric_rows", "gauge", "Rows of the metric tables.");
        writer.Sample("qoffeeautomat_hub_metric_rows", stats.RawRows, ("table", "raw"));
        writer.Sample("qoffeeautomat_hub_metric_rows", stats.StateRows, ("table", "states"));
        writer.Sample("qoffeeautomat_hub_metric_rows", stats.Rollup5mRows, ("table", "rollup_5m"));
        writer.Sample("qoffeeautomat_hub_metric_rows", stats.Rollup1hRows, ("table", "rollup_1h"));
    }

    private async Task<MetricStoreStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        // Counting the rows of a large table takes a moment and Prometheus scrapes every few seconds, so the sizes are at most half a minute old.
        await _statsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (_stats is null || now - _statsAt >= StatsMaxAge || now < _statsAt)
            {
                _stats = await _store.GetStatsAsync(cancellationToken).ConfigureAwait(false);
                _statsAt = now;
            }

            return _stats;
        }
        finally
        {
            _ = _statsGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _statsGate.Dispose();
}
