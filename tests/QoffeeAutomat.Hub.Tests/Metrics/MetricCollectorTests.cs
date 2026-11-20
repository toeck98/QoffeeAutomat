using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Metrics;

namespace QoffeeAutomat.Hub.Tests.Metrics;

/// <summary>
/// The collector (T910): which metrics it asks the gateway for, how the <c>state</c> frames of agents become samples, the bound of the queue, the writer, and the jobs.
/// The gateway is a fake that records the demands; the store is the real SQLite one.
/// </summary>
[Trait("Category", "Metrics")]
public sealed class MetricCollectorTests
{
    private static readonly DateTimeOffset T0 = MetricKit.T0;

    /// <summary>A policy source that a test can change.</summary>
    private sealed class SwitchablePolicies(MetricCollectionPolicy initial) : IMetricPolicySource
    {
        public MetricCollectionPolicy Current { get; set; } = initial;

        public MetricCollectionPolicy GetPolicy(string agentId) => Current;
    }

    /// <summary>A policy source with a policy per agent; an agent that is not listed collects nothing.</summary>
    private sealed class PerAgentPolicies(Dictionary<string, MetricCollectionPolicy> policies) : IMetricPolicySource
    {
        public MetricCollectionPolicy GetPolicy(string agentId) => policies.TryGetValue(agentId, out MetricCollectionPolicy? policy) ? policy : MetricCollectionPolicy.Nothing;
    }

    private static async Task<IReadOnlyList<MetricSeriesData>> ReadAsync(CollectorHarness harness, string agent, params string[] metrics)
    {
        return await harness.Store.QueryAsync(MetricKit.Query([agent], metrics.Length == 0 ? ["*"] : metrics, T0.AddDays(-1), T0.AddDays(1)));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not become true within 10 seconds.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public void The_collector_subscribes_to_the_metrics_of_the_policy_at_its_interval()
    {
        var policy = new MetricCollectionPolicy(["cpu.*", "network.up"], TimeSpan.FromSeconds(30));
        using var harness = new CollectorHarness(new HubMetricsOptions { DefaultPolicy = policy });

        harness.Collector.OnManifestChanged(
            "a1",
            MetricKit.Manifest(
                "a1",
                MetricKit.Metric("cpu.usage"),
                MetricKit.Metric("cpu.temperature"),
                MetricKit.Metric("memory.used"),
                MetricKit.Metric("network.up", MetricValueTypes.Bool),
                MetricKit.Metric("session.state", MetricValueTypes.Choice)));

        FakeSubscription subscription = Assert.Single(harness.Gateway.Subscriptions);
        Assert.Equal("a1", subscription.AgentId);
        Assert.False(subscription.Demand.All);
        Assert.Equal(["cpu.temperature", "cpu.usage", "network.up"], subscription.Demand.Metrics.Keys.Order(StringComparer.Ordinal));
        Assert.All(subscription.Demand.Metrics.Values, interval => Assert.Equal(TimeSpan.FromSeconds(30), interval));
    }

    [Fact]
    public void The_default_policy_collects_every_metric_of_a_known_type_every_60_seconds()
    {
        using var harness = new CollectorHarness();

        harness.Collector.OnManifestChanged(
            "a1",
            MetricKit.Manifest(
                "a1",
                MetricKit.Metric("cpu.usage"),
                MetricKit.Metric("network.up", MetricValueTypes.Bool),
                MetricKit.Metric("session.state", MetricValueTypes.Choice),
                MetricKit.Metric("os.edition", MetricValueTypes.Text),
                MetricKit.Metric("boot.time", MetricValueTypes.Timestamp),
                MetricKit.Metric("odd.blob", "blob")));

        FakeSubscription subscription = Assert.Single(harness.Gateway.Subscriptions);
        Assert.Equal(["boot.time", "cpu.usage", "network.up", "os.edition", "session.state"], subscription.Demand.Metrics.Keys.Order(StringComparer.Ordinal));
        Assert.All(subscription.Demand.Metrics.Values, interval => Assert.Equal(TimeSpan.FromSeconds(60), interval));
    }

    [Fact]
    public void A_new_manifest_updates_the_subscription_instead_of_adding_one()
    {
        using var harness = new CollectorHarness();
        harness.Collector.OnManifestChanged("a1", MetricKit.Manifest("a1", MetricKit.Metric("cpu.usage")));

        harness.Collector.OnManifestChanged("a1", MetricKit.Manifest("a1", MetricKit.Metric("cpu.usage"), MetricKit.Metric("memory.used")));

        FakeSubscription subscription = Assert.Single(harness.Gateway.Subscriptions);
        Assert.False(subscription.Disposed);
        Assert.Equal(["cpu.usage", "memory.used"], subscription.Demand.Metrics.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_policy_that_collects_nothing_drops_the_subscription_and_the_frames()
    {
        var policies = new SwitchablePolicies(MetricCollectionPolicy.Default);
        using var harness = new CollectorHarness(policies: policies);
        harness.Store.AddAgents("a1");
        harness.Collector.OnManifestChanged("a1", MetricKit.Manifest("a1", MetricKit.Metric("cpu.usage")));
        FakeSubscription subscription = Assert.Single(harness.Gateway.Subscriptions);

        policies.Current = MetricCollectionPolicy.Nothing;
        harness.Collector.OnManifestChanged("a1", MetricKit.Manifest("a1", MetricKit.Metric("cpu.usage")));
        harness.Collector.OnStateChanged(MetricKit.State("a1", T0, snapshot: true, ("cpu.usage", 5)));

        Assert.True(subscription.Disposed);
        Assert.Equal(0, await harness.Collector.FlushAsync(CancellationToken.None));
        Assert.Empty(await ReadAsync(harness, "a1"));
    }

    [Fact]
    public void An_agent_gets_the_policy_that_the_source_names_for_it()
    {
        var policies = new PerAgentPolicies(new Dictionary<string, MetricCollectionPolicy>
        {
            ["a1"] = new(["cpu.*"], TimeSpan.FromSeconds(10)),
        });
        using var harness = new CollectorHarness(policies: policies);

        harness.Collector.OnManifestChanged("a1", MetricKit.Manifest("a1", MetricKit.Metric("cpu.usage"), MetricKit.Metric("memory.used")));
        harness.Collector.OnManifestChanged("a2", MetricKit.Manifest("a2", MetricKit.Metric("cpu.usage")));

        FakeSubscription subscription = Assert.Single(harness.Gateway.Subscriptions);
        Assert.Equal("a1", subscription.AgentId);
        Assert.Equal(["cpu.usage"], subscription.Demand.Metrics.Keys);
        Assert.Equal(TimeSpan.FromSeconds(10), subscription.Demand.Metrics["cpu.usage"]);
    }

    [Fact]
    public void A_manifest_that_cannot_be_read_is_survived()
    {
        using var harness = new CollectorHarness();

        harness.Collector.OnManifestChanged("a1", new CachedManifest("x", MetricKit.Json("not a manifest"), T0));

        Assert.Empty(harness.Gateway.Subscriptions);
    }

    [Fact]
    public async Task State_frames_become_samples_of_the_right_type_and_are_written_by_a_flush()
    {
        using var harness = new CollectorHarness();
        harness.Store.AddAgents("a1");
        harness.Collector.OnManifestChanged(
            "a1",
            MetricKit.Manifest(
                "a1",
                MetricKit.Metric("cpu.usage", unit: "%"),
                MetricKit.Metric("network.up", MetricValueTypes.Bool),
                MetricKit.Metric("session.state", MetricValueTypes.Choice)));

        harness.Collector.OnStateChanged(MetricKit.State("a1", T0, snapshot: true, ("cpu.usage", 12.5), ("network.up", true), ("session.state", "active")));
        int written = await harness.Collector.FlushAsync(CancellationToken.None);

        Assert.Equal(3, written);
        IReadOnlyList<MetricSeriesData> series = await ReadAsync(harness, "a1");
        Assert.Equal(["cpu.usage", "network.up", "session.state"], series.Select(item => item.MetricId));
        Assert.Equal(12.5, series[0].Points.Single().Value);
        Assert.Equal("%", series[0].Unit);
        Assert.Equal(1d, series[1].Points.Single().Value);
        Assert.Equal("active", series[2].Points.Single().Text);
        Assert.Equal(T0, series[0].Points.Single().Ts);

        // Nothing is written twice: the queue is empty after a flush.
        Assert.Equal(0, await harness.Collector.FlushAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Values_of_metrics_outside_the_policy_are_ignored_even_when_another_consumer_made_the_agent_send_them()
    {
        var policy = new MetricCollectionPolicy(["cpu.*"], TimeSpan.FromSeconds(60));
        using var harness = new CollectorHarness(new HubMetricsOptions { DefaultPolicy = policy });
        harness.Store.AddAgents("a1");
        harness.Collector.OnManifestChanged("a1", MetricKit.Manifest("a1", MetricKit.Metric("cpu.usage"), MetricKit.Metric("memory.used")));

        harness.Collector.OnStateChanged(MetricKit.State("a1", T0, snapshot: false, ("cpu.usage", 1), ("memory.used", 2), ("never.announced", 3)));
        _ = await harness.Collector.FlushAsync(CancellationToken.None);

        Assert.Equal(["cpu.usage"], (await ReadAsync(harness, "a1")).Select(item => item.MetricId));
    }

    [Fact]
    public async Task Values_that_do_not_fit_the_type_in_the_manifest_are_dropped()
    {
        using var harness = new CollectorHarness();
        harness.Store.AddAgents("a1");
        harness.Collector.OnManifestChanged(
            "a1",
            MetricKit.Manifest(
                "a1",
                MetricKit.Metric("cpu.usage"),
                MetricKit.Metric("network.up", MetricValueTypes.Bool),
                MetricKit.Metric("session.state", MetricValueTypes.Choice),
                MetricKit.Metric("memory.used"),
                MetricKit.Metric("disk.free")));

        harness.Collector.OnStateChanged(
            MetricKit.State(
                "a1",
                T0,
                snapshot: true,
                ("cpu.usage", "high"),
                ("network.up", 1),
                ("session.state", 5),
                ("memory.used", null),
                ("disk.free", 42)));
        int written = await harness.Collector.FlushAsync(CancellationToken.None);

        Assert.Equal(1, written);
        MetricSeriesData only = Assert.Single(await ReadAsync(harness, "a1"));
        Assert.Equal("disk.free", only.MetricId);
    }

    [Fact]
    public async Task Frames_of_an_agent_that_has_no_manifest_are_ignored()
    {
        using var harness = new CollectorHarness();

        harness.Collector.OnStateChanged(MetricKit.State("ghost", T0, snapshot: true, ("cpu.usage", 1)));

        Assert.Equal(0, await harness.Collector.FlushAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Live_values_that_come_faster_than_the_interval_are_thinned_out_but_a_snapshot_is_always_kept()
    {
        // Another consumer (a hub UI view) makes the agent send live values. The store keeps about one sample per interval (60 s, so at least 30 s apart).
        using var harness = new CollectorHarness();
        harness.Store.AddAgents("a1");
        harness.Collector.OnManifestChanged("a1", MetricKit.Manifest("a1", MetricKit.Metric("cpu.usage")));

        foreach ((double seconds, int value) in new[] { (0d, 1), (10d, 2), (20d, 3), (35d, 4), (70d, 5) })
        {
            harness.Collector.OnStateChanged(MetricKit.State("a1", T0.AddSeconds(seconds), snapshot: false, ("cpu.usage", value)));
        }

        // The snapshot after a re-subscribe is kept although it comes 1 s after the previous value.
        harness.Collector.OnStateChanged(MetricKit.State("a1", T0.AddSeconds(71), snapshot: true, ("cpu.usage", 6)));
        _ = await harness.Collector.FlushAsync(CancellationToken.None);

        MetricSeriesData series = Assert.Single(await ReadAsync(harness, "a1"));
        Assert.Equal([1d, 4d, 5d, 6d], series.Points.Select(point => point.Value!.Value));
    }

    [Fact]
    public async Task A_text_that_does_not_change_is_not_stored_again_however_often_it_is_reported()
    {
        using var harness = new CollectorHarness();
        harness.Store.AddAgents("a1");
        harness.Collector.OnManifestChanged("a1", MetricKit.Manifest("a1", MetricKit.Metric("session.state", MetricValueTypes.Choice)));

        foreach ((double seconds, string value) in new[] { (0d, "active"), (60d, "active"), (120d, "locked"), (180d, "locked") })
        {
            harness.Collector.OnStateChanged(MetricKit.State("a1", T0.AddSeconds(seconds), snapshot: false, ("session.state", value)));
        }

        // The writer counts what it stored: only the changes.
        Assert.Equal(2, await harness.Collector.FlushAsync(CancellationToken.None));
        MetricSeriesData series = Assert.Single(await ReadAsync(harness, "a1"));
        Assert.Equal(["active", "locked"], series.Points.Select(point => point.Text));
    }

    [Fact]
    public async Task A_sample_with_a_time_far_from_the_clock_of_the_hub_gets_the_time_of_the_hub()
    {
        using var harness = new CollectorHarness();
        harness.Store.AddAgents("a1");
        harness.Collector.OnManifestChanged("a1", MetricKit.Manifest("a1", MetricKit.Metric("cpu.usage")));

        harness.Collector.OnStateChanged(MetricKit.State("a1", T0.AddHours(3), snapshot: true, ("cpu.usage", 1)));
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        harness.Collector.OnStateChanged(MetricKit.State("a1", T0.AddMinutes(1).AddSeconds(-30), snapshot: true, ("cpu.usage", 2)));
        _ = await harness.Collector.FlushAsync(CancellationToken.None);

        MetricSeriesData series = Assert.Single(await ReadAsync(harness, "a1"));

        // The first frame claimed to be from 3 hours ahead and is stored at the hub's time of that moment; the second is within the tolerance and keeps its time.
        Assert.Equal([T0, T0.AddSeconds(30)], series.Points.Select(point => point.Ts));
    }

    [Fact]
    public async Task Removing_an_agent_ends_its_subscription_and_removes_its_stored_series()
    {
        using var harness = new CollectorHarness();
        harness.Store.AddAgents("a1", "a2");
        harness.Collector.OnManifestChanged("a1", MetricKit.Manifest("a1", MetricKit.Metric("cpu.usage")));
        harness.Collector.OnManifestChanged("a2", MetricKit.Manifest("a2", MetricKit.Metric("cpu.usage")));
        harness.Collector.OnStateChanged(MetricKit.State("a1", T0, snapshot: true, ("cpu.usage", 1)));
        harness.Collector.OnStateChanged(MetricKit.State("a2", T0, snapshot: true, ("cpu.usage", 2)));
        _ = await harness.Collector.FlushAsync(CancellationToken.None);

        harness.Collector.OnAgentRemoved("a1");

        Assert.True(harness.Gateway.Of("a1").Disposed);
        Assert.False(harness.Gateway.Of("a2").Disposed);
        await WaitUntilAsync(async () => (await harness.Store.Store.GetStatsAsync(CancellationToken.None)).Series == 1);
        Assert.Empty(await ReadAsync(harness, "a1"));
        Assert.Single(await ReadAsync(harness, "a2"));

        // Frames that are still on their way are ignored.
        harness.Collector.OnStateChanged(MetricKit.State("a1", T0.AddSeconds(60), snapshot: false, ("cpu.usage", 3)));
        Assert.Equal(0, await harness.Collector.FlushAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_queue_is_bounded_and_the_samples_that_do_not_fit_are_dropped_and_counted()
    {
        var options = new HubMetricsOptions { MaxBufferedSamples = 10, FlushBatchSize = 5 };
        using var harness = new CollectorHarness(options);
        harness.Store.AddAgents("a1");
        MetricDescriptor[] metrics = [.. Enumerable.Range(0, 30).Select(index => MetricKit.Metric("m" + index.ToString("00", System.Globalization.CultureInfo.InvariantCulture)))];
        harness.Collector.OnManifestChanged("a1", MetricKit.Manifest("a1", metrics));

        harness.Collector.OnStateChanged(
            MetricKit.State("a1", T0, snapshot: true, [.. metrics.Select(metric => (metric.Id, (object?)1))]));

        Assert.Equal(20, harness.Collector.DroppedSamples);
        Assert.Equal(10, await harness.Collector.FlushAsync(CancellationToken.None));

        // After the flush there is room again.
        harness.Collector.OnStateChanged(MetricKit.State("a1", T0.AddSeconds(60), snapshot: true, ("m00", 2)));
        Assert.Equal(1, await harness.Collector.FlushAsync(CancellationToken.None));
        Assert.Equal(20, harness.Collector.DroppedSamples);
    }

    [Fact]
    public async Task A_flush_writes_everything_that_is_queued_in_batches()
    {
        var options = new HubMetricsOptions { MaxBufferedSamples = 1000, FlushBatchSize = 10 };
        using var harness = new CollectorHarness(options);
        harness.Store.AddAgents("a1");
        MetricDescriptor[] metrics = [.. Enumerable.Range(0, 25).Select(index => MetricKit.Metric("m" + index.ToString("00", System.Globalization.CultureInfo.InvariantCulture)))];
        harness.Collector.OnManifestChanged("a1", MetricKit.Manifest("a1", metrics));
        harness.Collector.OnStateChanged(MetricKit.State("a1", T0, snapshot: true, [.. metrics.Select(metric => (metric.Id, (object?)1))]));

        int written = await harness.Collector.FlushAsync(CancellationToken.None);

        Assert.Equal(25, written);
        Assert.Equal(25, (await harness.Store.Store.GetStatsAsync(CancellationToken.None)).RawRows);
    }

    [Fact]
    public async Task The_maintenance_job_writes_the_queue_rolls_up_and_applies_the_retention_at_its_interval()
    {
        var options = new HubMetricsOptions { RetentionInterval = TimeSpan.FromDays(10) };
        options.Validate();
        using var harness = new CollectorHarness(options);
        harness.Store.AddAgents("a1");
        harness.Collector.OnManifestChanged("a1", MetricKit.Manifest("a1", MetricKit.Metric("cpu.usage")));
        harness.Collector.OnStateChanged(MetricKit.State("a1", T0, snapshot: true, ("cpu.usage", 1)));

        // The first run: the sample is written (nobody flushed), the buckets that ended are rolled up, and the retention runs for the first time.
        harness.Clock.Advance(TimeSpan.FromHours(2));
        await harness.Collector.RunMaintenanceAsync(CancellationToken.None);
        MetricStoreStats first = await harness.Store.Store.GetStatsAsync(CancellationToken.None);
        Assert.Equal([1L, 1L, 1L], MetricKit.Seq(first.RawRows, first.Rollup5mRows, first.Rollup1hRows));

        // 49 hours: the raw sample is expired, but the retention ran 47 hours ago and runs every 10 days.
        harness.Clock.Advance(TimeSpan.FromHours(47));
        await harness.Collector.RunMaintenanceAsync(CancellationToken.None);
        Assert.Equal(1, (await harness.Store.Store.GetStatsAsync(CancellationToken.None)).RawRows);

        // 11 days: the retention is due.
        harness.Clock.Advance(TimeSpan.FromDays(10));
        await harness.Collector.RunMaintenanceAsync(CancellationToken.None);
        MetricStoreStats last = await harness.Store.Store.GetStatsAsync(CancellationToken.None);
        Assert.Equal([0L, 1L, 1L], MetricKit.Seq(last.RawRows, last.Rollup5mRows, last.Rollup1hRows));
    }

    [Fact]
    public async Task Stopping_the_collector_writes_what_is_queued_and_ends_the_subscriptions()
    {
        using var harness = new CollectorHarness();
        harness.Store.AddAgents("a1");
        await harness.Collector.StartAsync(CancellationToken.None);
        harness.Collector.OnManifestChanged("a1", MetricKit.Manifest("a1", MetricKit.Metric("cpu.usage")));
        harness.Collector.OnStateChanged(MetricKit.State("a1", T0, snapshot: true, ("cpu.usage", 7)));

        await harness.Collector.StopAsync(CancellationToken.None);

        Assert.True(harness.Gateway.Of("a1").Disposed);
        MetricSeriesData series = Assert.Single(await ReadAsync(harness, "a1"));
        Assert.Equal(7d, Assert.Single(series.Points).Value);
    }

    [Fact]
    public void The_collector_refuses_options_that_are_not_valid()
    {
        var options = new HubMetricsOptions { FlushBatchSize = 0 };
        using var store = new MetricStoreHarness();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MetricCollector(new FakeGateway(), store.Store, new GlobalMetricPolicySource(options), options, new ManualTimeProvider(), MetricKit.Quiet<MetricCollector>()));
    }
}
