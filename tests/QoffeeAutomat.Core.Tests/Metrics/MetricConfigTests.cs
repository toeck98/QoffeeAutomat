using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Core.Tests.Config;
using QoffeeAutomat.Platform.Simulated;
using static QoffeeAutomat.Core.Tests.Metrics.MetricHarness;

namespace QoffeeAutomat.Core.Tests.Metrics;

/// <summary>The catalog: provider metrics, overrides and custom sensors (PLAN.md S06), and live reconfiguration.</summary>
[Trait("Category", "Metrics")]
public sealed class MetricConfigTests
{
    private const string SensorMetricId = "custom.my_sensor";

    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static async Task SaveOverridesAsync(MetricHarness harness, string id, MetricOverride metricOverride)
    {
        var document = new MetricOverridesDocument(
            DocumentSchema.CurrentVersion,
            new Dictionary<string, MetricOverride>(StringComparer.Ordinal) { [id] = metricOverride });
        SaveResult<MetricOverridesDocument> result = await harness.Store.Local.SaveAsync(document, null, CancellationToken.None);
        Assert.True(result.IsApplied, string.Join("; ", result.Errors.Select(error => error.Message)));
    }

    private static async Task SaveSensorAsync(MetricHarness harness, SensorDocument sensor)
    {
        SaveResult<SensorDocument> result = await harness.Store.Local.SaveAsync(sensor, null, CancellationToken.None);
        Assert.True(result.IsApplied, string.Join("; ", result.Errors.Select(error => error.Message)));
    }

    [Fact]
    public async Task The_catalog_lists_the_provider_metrics_with_their_defaults()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);

        IReadOnlyList<MetricCatalogEntry> catalog = harness.Engine.Catalog;

        Assert.Equal(4, catalog.Count);
        MetricCatalogEntry cpu = catalog.Single(entry => entry.Id == SimulatedMetricProvider.CpuId);
        Assert.True(cpu.Enabled);
        Assert.True(cpu.ExposeToHa);
        Assert.False(cpu.IsCustom);
        Assert.Equal(1, cpu.IntervalSeconds);
        Assert.Null(cpu.Deadband);
        Assert.Equal("Simulated CPU usage", cpu.Descriptor.Name);
    }

    [Fact]
    public async Task Overrides_are_merged_into_the_catalog_without_a_restart()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        int changes = 0;
        harness.Engine.CatalogChanged += (_, _) => changes++;

        await SaveOverridesAsync(
            harness,
            SimulatedMetricProvider.CpuId,
            new MetricOverride(Enabled: false, ExposeToHa: false, IntervalSeconds: 7, Deadband: 2.5));
        await harness.PollAsync();

        MetricCatalogEntry cpu = harness.Engine.Catalog.Single(entry => entry.Id == SimulatedMetricProvider.CpuId);
        Assert.False(cpu.Enabled);
        Assert.False(cpu.ExposeToHa);
        Assert.Equal(7, cpu.IntervalSeconds);
        Assert.Equal(2.5, cpu.Deadband);
        Assert.Equal(7, cpu.Descriptor.IntervalSeconds);
        Assert.Equal(2.5, cpu.Descriptor.Deadband);
        MetricCatalogEntry counter = harness.Engine.Catalog.Single(entry => entry.Id == SimulatedMetricProvider.CounterId);
        Assert.True(counter.Enabled);
        Assert.Equal(1, counter.IntervalSeconds);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Unrelated_configuration_changes_do_not_rebuild_the_catalog()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        int changes = 0;
        harness.Engine.CatalogChanged += (_, _) => changes++;

        await harness.Store.Local.SaveAsync(ConfigTestSupport.Command(), null, CancellationToken.None);
        await harness.PollAsync();

        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task Changing_the_interval_takes_effect_without_a_restart()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        MetricSubscription subscription = harness.Engine.Subscribe(Select(SimulatedMetricProvider.CounterId));
        await harness.PollAsync();
        Assert.Equal(1, sim.SampleCallCount);

        await SaveOverridesAsync(harness, SimulatedMetricProvider.CounterId, new MetricOverride(IntervalSeconds: 10));
        await harness.TickAsync(OneSecond, 60);

        // t=10, 20, ... 60 on top of the first sample.
        Assert.Equal(7, sim.SampleCallCount);
        Assert.Equal(6, Drain(subscription).Count(update => !update.IsSnapshot));

        await SaveOverridesAsync(harness, SimulatedMetricProvider.CounterId, new MetricOverride(IntervalSeconds: 1));
        await harness.TickAsync(OneSecond, 5);

        Assert.Equal(12, sim.SampleCallCount);
    }

    [Fact]
    public async Task Disabling_a_metric_stops_sampling_and_enabling_it_resumes_without_a_restart()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        MetricSubscription subscription = harness.Engine.Subscribe(Select(SimulatedMetricProvider.CounterId));
        await harness.PollAsync();
        Drain(subscription);

        await SaveOverridesAsync(harness, SimulatedMetricProvider.CounterId, new MetricOverride(Enabled: false));
        await harness.TickAsync(OneSecond);

        // The subscriber learns once that the metric is gone.
        MetricUpdate gone = Assert.Single(Drain(subscription));
        Assert.Null(gone.Values[SimulatedMetricProvider.CounterId]);

        await harness.TickAsync(OneSecond, 10);
        Assert.Equal(1, sim.SampleCallCount);
        Assert.Empty(Drain(subscription));

        await SaveOverridesAsync(harness, SimulatedMetricProvider.CounterId, new MetricOverride(Enabled: true));
        await harness.TickAsync(OneSecond);

        Assert.Equal(2, sim.SampleCallCount);
        MetricUpdate back = Assert.Single(Drain(subscription));
        Assert.Equal(
            SimulatedMetricProvider.CounterAt(clock.GetUtcNow()),
            AsNumber(back.Values[SimulatedMetricProvider.CounterId]));
    }

    [Fact]
    public async Task A_metric_that_is_disabled_when_somebody_subscribes_is_not_sampled_or_reported()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        await SaveOverridesAsync(harness, SimulatedMetricProvider.CounterId, new MetricOverride(Enabled: false));
        MetricSubscription subscription = harness.Engine.Subscribe(Select(SimulatedMetricProvider.CounterId, SimulatedMetricProvider.ToggleId));

        await harness.TickAsync(OneSecond, 3);

        MetricUpdate snapshot = Assert.Single(Drain(subscription));
        Assert.Equal([SimulatedMetricProvider.ToggleId], snapshot.Values.Keys);
        Assert.Equal(3, sim.SampledIdCount);
    }

    [Fact]
    public async Task Changing_the_deadband_takes_effect_without_a_restart()
    {
        var clock = new SimulatedClock();
        var stub = new StubMetricProvider(clock, Number("x"));
        stub.Set("x", 10);
        await using var harness = await MetricHarness.CreateAsync(clock, [stub]);
        MetricSubscription subscription = harness.Engine.Subscribe(Select("x"));
        await harness.PollAsync();
        Drain(subscription);

        await SaveOverridesAsync(harness, "x", new MetricOverride(Deadband: 5));
        stub.Set("x", 12);
        await harness.TickAsync(OneSecond, 2);
        Assert.Empty(Drain(subscription));

        stub.Set("x", 20);
        await harness.TickAsync(OneSecond);
        Assert.Equal(20.0, AsNumber(Assert.Single(Drain(subscription)).Values["x"]));
    }

    [Fact]
    public async Task A_custom_sensor_joins_the_catalog_and_is_sampled_by_the_custom_sensor_provider()
    {
        var clock = new SimulatedClock();
        var custom = new StubMetricProvider(clock);
        custom.Set(SensorMetricId, true);
        await using var harness = await MetricHarness.CreateAsync(clock, [], customSensors: custom);
        MetricSubscription subscription = harness.Engine.Subscribe(Select(SensorMetricId));

        await SaveSensorAsync(harness, ConfigTestSupport.Sensor("my_sensor") with { IntervalSeconds = 2, Unit = "x" });
        await harness.PollAsync();

        MetricCatalogEntry entry = Assert.Single(harness.Engine.Catalog);
        Assert.Equal(SensorMetricId, entry.Id);
        Assert.True(entry.IsCustom);
        Assert.True(entry.Enabled);
        Assert.False(entry.ExposeToHa);
        Assert.Equal(2, entry.IntervalSeconds);
        Assert.Equal(("binary_sensor", "bool", "x", "system"), (entry.Descriptor.Kind, entry.Descriptor.ValueType, entry.Descriptor.Unit, entry.Descriptor.Context));
        MetricUpdate snapshot = Assert.Single(Drain(subscription));
        Assert.True(AsBool(snapshot.Values[SensorMetricId]));
        Assert.Equal([SensorMetricId], Assert.Single(custom.Batches));
    }

    [Fact]
    public async Task A_custom_sensor_without_a_sampler_is_unavailable()
    {
        var clock = new SimulatedClock();
        await using var harness = await MetricHarness.CreateAsync(clock, []);
        await SaveSensorAsync(harness, ConfigTestSupport.Sensor("my_sensor"));
        MetricSubscription subscription = harness.Engine.Subscribe(Select(SensorMetricId));

        await harness.PollAsync();

        Assert.Null(Assert.Single(Drain(subscription)).Values[SensorMetricId]);
    }

    [Fact]
    public async Task A_disabled_or_deleted_custom_sensor_is_not_sampled()
    {
        var clock = new SimulatedClock();
        var custom = new StubMetricProvider(clock);
        custom.Set(SensorMetricId, true);
        await using var harness = await MetricHarness.CreateAsync(clock, [], customSensors: custom);
        await SaveSensorAsync(harness, ConfigTestSupport.Sensor("my_sensor") with { Enabled = false });
        harness.Engine.Subscribe(MetricSelection.Everything);

        await harness.TickAsync(OneSecond, 3);
        Assert.Equal(0, custom.CallCount);

        await harness.Store.Local.DeleteAsync<SensorDocument>("my_sensor", null, CancellationToken.None);
        await harness.PollAsync();

        Assert.Empty(harness.Engine.Catalog);
    }

    [Fact]
    public async Task A_new_custom_sensor_reaches_an_existing_wildcard_subscriber()
    {
        var clock = new SimulatedClock();
        var custom = new StubMetricProvider(clock);
        custom.Set(SensorMetricId, true);
        await using var harness = await MetricHarness.CreateAsync(clock, [], customSensors: custom);
        MetricSubscription subscription = harness.Engine.Subscribe(MetricSelection.Everything);
        await harness.PollAsync();
        Assert.Empty(Assert.Single(Drain(subscription)).Values);

        await SaveSensorAsync(harness, ConfigTestSupport.Sensor("my_sensor"));
        await harness.TickAsync(OneSecond);

        MetricUpdate update = Assert.Single(Drain(subscription));
        Assert.False(update.IsSnapshot);
        Assert.True(AsBool(update.Values[SensorMetricId]));
    }

    [Fact]
    public async Task User_metrics_of_the_session_bridge_join_the_catalog()
    {
        var clock = new SimulatedClock();
        var session = new SimulatedSessionBridge(clock);
        session.SimulateLogoff();
        await using var harness = await MetricHarness.CreateAsync(clock, [], session: session);

        MetricCatalogEntry user = harness.Engine.Catalog.Single(entry => entry.Id == SimulatedSessionBridge.UserMetricId);
        Assert.Equal("user", user.Descriptor.Context);

        MetricSubscription subscription = harness.Engine.Subscribe(Select(SimulatedSessionBridge.UserMetricId));
        await harness.PollAsync();
        Assert.Null(Assert.Single(Drain(subscription)).Values[SimulatedSessionBridge.UserMetricId]);

        session.SimulateLogon("alice", isAdministrator: false);
        session.SetReady(true);
        await harness.TickAsync(OneSecond, 2);

        MetricUpdate update = Assert.Single(Drain(subscription));
        Assert.Equal("alice", update.Values[SimulatedSessionBridge.UserMetricId]);
    }
}
