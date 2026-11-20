using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Metrics;
using QoffeeAutomat.Hub.Storage;
using QoffeeAutomat.Hub.Tests.Gateway;

namespace QoffeeAutomat.Hub.Tests.Metrics;

/// <summary>Builders for the samples, queries and manifests of the metric tests.</summary>
internal static class MetricKit
{
    /// <summary>The time every test starts at; the clock of <see cref="ManualTimeProvider"/> starts at the same time. It is on an hour boundary.</summary>
    public static readonly DateTimeOffset T0 = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    public static MetricSample Num(string agent, string metric, DateTimeOffset at, double value, string? unit = null)
    {
        return new MetricSample(agent, metric, MetricValueTypes.Number, unit, at.ToUnixTimeMilliseconds(), value);
    }

    public static MetricSample Flag(string agent, string metric, DateTimeOffset at, bool value)
    {
        return new MetricSample(agent, metric, MetricValueTypes.Bool, null, at.ToUnixTimeMilliseconds(), value ? 1 : 0);
    }

    public static MetricSample Txt(string agent, string metric, DateTimeOffset at, string value, string type = MetricValueTypes.Choice)
    {
        return new MetricSample(agent, metric, type, null, at.ToUnixTimeMilliseconds(), 0, value);
    }

    public static MetricQuery Query(
        string[] agents,
        string[] metrics,
        DateTimeOffset from,
        DateTimeOffset to,
        MetricResolution resolution = MetricResolution.Raw,
        int maxPoints = 100_000)
    {
        return new MetricQuery(agents, metrics, from, to, resolution, maxPoints);
    }

    /// <summary>A JSON value as a state frame carries it.</summary>
    public static JsonElement Json(object? value)
    {
        string text = value switch
        {
            null => "null",
            bool flag => flag ? "true" : "false",
            string content => JsonValue.Create(content)!.ToJsonString(),
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            int number => number.ToString(CultureInfo.InvariantCulture),
            _ => throw new ArgumentException("Unsupported value " + value),
        };
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public static AgentStateUpdate State(string agentId, DateTimeOffset at, bool snapshot, params (string Metric, object? Value)[] values)
    {
        return new AgentStateUpdate(
            agentId,
            at.ToUnixTimeMilliseconds(),
            values.ToDictionary(pair => pair.Metric, pair => Json(pair.Value), StringComparer.Ordinal),
            snapshot);
    }

    public static MetricDescriptor Metric(string id, string valueType = MetricValueTypes.Number, string? unit = null)
    {
        return new MetricDescriptor(id, id, "sensor", valueType, Unit: unit);
    }

    public static CachedManifest Manifest(string agentId, params MetricDescriptor[] metrics)
    {
        var manifest = new Manifest("rev-" + metrics.Length.ToString(CultureInfo.InvariantCulture), ScriptedAgent.Info(agentId), metrics, []);
        return new CachedManifest(manifest.Rev, LinkCodec.ToElement(manifest), T0);
    }

    /// <summary>Puts an enrolled agent in the registry table, so that the series of the metrics have an agent to belong to.</summary>
    public static void InsertAgent(HubDatabase database, string id)
    {
        database.Query(connection => HubSql.Execute(
            connection,
            null,
            "INSERT INTO agents (id, name, hostname, os, arch, version, status, enrolled_at) VALUES (@id, @name, 'HOST', 'windows', 'x64', '1.0.0', 'enrolled', 1);",
            ("@id", id),
            ("@name", "Agent " + id)));
    }

    public static void DeleteAgentRow(HubDatabase database, string id)
    {
        database.Query(connection => HubSql.Execute(connection, null, "DELETE FROM agents WHERE id = @id;", ("@id", id)));
    }

    public static NullLogger<T> Quiet<T>() => NullLogger<T>.Instance;

    /// <summary>Values as a sequence, so that <c>Assert.Equal</c> compares them as a collection (and not as arrays or spans).</summary>
    public static IEnumerable<T> Seq<T>(params T[] values) => values;
}

/// <summary>A hub database in a temporary directory with the metric store on it. Agents are rows in <c>agents</c>, so add them first.</summary>
internal sealed class MetricStoreHarness : IDisposable
{
    private readonly TempDirectory _directory = new();

    public MetricStoreHarness(HubMetricsOptions? options = null)
    {
        Options = options ?? new HubMetricsOptions();
        Database = HubDatabase.Open(_directory.Below("hub.db"));
        Store = new SqliteMetricStore(Database, Options);
    }

    public HubMetricsOptions Options { get; }

    public HubDatabase Database { get; }

    public SqliteMetricStore Store { get; }

    /// <summary>The size of the database file; the write-ahead log is not part of it. Call <see cref="CheckpointAsync"/> first for a figure that includes everything.</summary>
    public long FileBytes => new FileInfo(Database.Path).Length;

    public void AddAgents(params string[] ids)
    {
        foreach (string id in ids)
        {
            MetricKit.InsertAgent(Database, id);
        }
    }

    /// <summary>Moves the write-ahead log into the database file.</summary>
    public Task CheckpointAsync() => Database.ExecuteAsync(connection => HubSql.Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);"), CancellationToken.None);

    public Task<IReadOnlyList<MetricSeriesData>> QueryAsync(MetricQuery query) => Store.QueryAsync(query, CancellationToken.None);

    public Task<MetricAppendResult> AppendAsync(params MetricSample[] samples) => Store.AppendAsync(samples, CancellationToken.None);

    public void Dispose()
    {
        Database.Dispose();
        _directory.Dispose();
    }
}

/// <summary>The gateway as the metric collector sees it: it records the demands and hands out nothing else.</summary>
internal sealed class FakeGateway : IAgentGateway
{
    public List<FakeSubscription> Subscriptions { get; } = [];

    public FakeSubscription Of(string agentId) => Subscriptions.Last(subscription => subscription.AgentId == agentId);

    public IReadOnlyList<AgentPresence> GetAllPresence() => [];

    public AgentPresence GetPresence(string agentId) => new(agentId, false, null, null, PowerStates.Unknown, null, null, [], null);

    public CachedManifest? GetManifest(string agentId) => null;

    public AgentStateSnapshot? GetState(string agentId) => null;

    public Task<IReadOnlyList<StoredAgentEvent>> GetEventsAsync(string agentId, int limit, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<StoredAgentEvent>>([]);
    }

    public IMetricSubscription SubscribeMetrics(string agentId, MetricDemand demand)
    {
        var subscription = new FakeSubscription(agentId, demand);
        Subscriptions.Add(subscription);
        return subscription;
    }

    public IDisposable Observe(IAgentGatewayObserver observer) => new Nothing();

    public Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(AgentRunOutcome.Failure(HubErrorCodes.AgentOffline, "No agent in this test."));
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

internal sealed class FakeSubscription(string agentId, MetricDemand demand) : IMetricSubscription
{
    public string AgentId { get; } = agentId;

    public MetricDemand Demand { get; private set; } = demand;

    public bool Disposed { get; private set; }

    public void Update(MetricDemand demand) => Demand = demand;

    public void Dispose() => Disposed = true;
}

/// <summary>The collector on a real store with a fake gateway and a clock that the test moves; its background jobs are not started, the test calls what they would call.</summary>
internal sealed class CollectorHarness : IDisposable
{
    public CollectorHarness(HubMetricsOptions? options = null, IMetricPolicySource? policies = null)
    {
        Options = options ?? new HubMetricsOptions();
        Store = new MetricStoreHarness(Options);
        Gateway = new FakeGateway();
        Collector = new MetricCollector(
            Gateway,
            Store.Store,
            policies ?? new GlobalMetricPolicySource(Options),
            Options,
            Clock,
            MetricKit.Quiet<MetricCollector>());
    }

    public HubMetricsOptions Options { get; }

    public MetricStoreHarness Store { get; }

    public FakeGateway Gateway { get; }

    public ManualTimeProvider Clock { get; } = new();

    public MetricCollector Collector { get; }

    public void Dispose()
    {
        Collector.Dispose();
        Store.Dispose();
    }
}
