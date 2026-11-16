using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Simulated;
using static QoffeeAutomat.Core.Tests.Metrics.MetricHarness;

namespace QoffeeAutomat.Core.Tests.Metrics;

/// <summary>A provider whose set of metrics the test changes while the engine runs, like volumes and network adapters do.</summary>
internal sealed class DynamicStubProvider : IDynamicMetricProvider
{
    private readonly object _gate = new();
    private MetricDescriptor[] _descriptors;

    public DynamicStubProvider(params MetricDescriptor[] descriptors)
    {
        _descriptors = descriptors;
    }

    public event EventHandler? DescriptorsChanged;

    /// <summary>Replaces the metrics and tells the engine, the way a provider does when a volume appears.</summary>
    public void Change(params MetricDescriptor[] descriptors)
    {
        lock (_gate)
        {
            _descriptors = descriptors;
        }

        DescriptorsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<MetricDescriptor> Describe()
    {
        lock (_gate)
        {
            return _descriptors;
        }
    }

    public ValueTask<IReadOnlyDictionary<string, object?>> SampleAsync(IReadOnlyCollection<string> metricIds, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string id in metricIds)
        {
            values[id] = 1.0;
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, object?>>(values);
    }
}

/// <summary>Providers that add and remove metrics while the agent runs (PLAN.md S06, T304: fixed volumes and adapters).</summary>
[Trait("Category", "Metrics")]
public sealed class DynamicMetricProviderTests
{
    [Fact]
    public async Task A_new_metric_of_a_dynamic_provider_joins_the_catalog_and_raises_catalog_changed()
    {
        var clock = new SimulatedClock();
        var provider = new DynamicStubProvider(Number("disk.c.free"));
        await using var harness = await MetricHarness.CreateAsync(clock, [provider]);
        int changes = 0;
        harness.Engine.CatalogChanged += (_, _) => Interlocked.Increment(ref changes);
        Assert.Equal(["disk.c.free"], harness.Engine.Catalog.Select(entry => entry.Id));

        provider.Change(Number("disk.c.free"), Number("disk.e.free"));
        await harness.PollAsync();

        Assert.Equal(["disk.c.free", "disk.e.free"], harness.Engine.Catalog.Select(entry => entry.Id));
        Assert.Equal(1, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task A_metric_that_disappears_leaves_the_catalog_and_the_other_metrics_stay_in_order()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        var provider = new DynamicStubProvider(Number("disk.c.free"), Number("disk.e.free"));
        await using var harness = await MetricHarness.CreateAsync(clock, [provider, sim]);
        string[] before = [.. harness.Engine.Catalog.Select(entry => entry.Id)];

        provider.Change(Number("disk.c.free"));
        await harness.PollAsync();

        string[] after = [.. harness.Engine.Catalog.Select(entry => entry.Id)];
        Assert.DoesNotContain("disk.e.free", after);
        Assert.Equal(before.Where(id => id != "disk.e.free"), after);
        Assert.Contains(SimulatedMetricProvider.CpuId, after);
    }

    [Fact]
    public async Task A_subscriber_to_everything_receives_the_value_of_a_metric_that_appeared_later()
    {
        var clock = new SimulatedClock();
        var provider = new DynamicStubProvider(Number("disk.c.free"));
        await using var harness = await MetricHarness.CreateAsync(clock, [provider]);
        MetricSubscription subscription = harness.Engine.Subscribe(MetricSelection.Everything);
        await harness.PollAsync();
        Drain(subscription);

        provider.Change(Number("disk.c.free"), Number("disk.e.free"));
        await harness.TickAsync(TimeSpan.FromSeconds(1), 2);

        Assert.True(harness.Engine.TryGetCurrentValue("disk.e.free", out object? value));
        Assert.Equal(1.0, value);
    }

    [Fact]
    public async Task A_change_without_a_difference_in_the_provider_still_rebuilds_without_harm()
    {
        var clock = new SimulatedClock();
        var provider = new DynamicStubProvider(Number("net.ethernet.rx"));
        await using var harness = await MetricHarness.CreateAsync(clock, [provider]);

        provider.Change(Number("net.ethernet.rx"));
        await harness.PollAsync();

        Assert.Equal(["net.ethernet.rx"], harness.Engine.Catalog.Select(entry => entry.Id));
    }

    [Fact]
    public async Task A_disposed_engine_ignores_the_provider()
    {
        var clock = new SimulatedClock();
        var provider = new DynamicStubProvider(Number("disk.c.free"));
        MetricHarness harness = await MetricHarness.CreateAsync(clock, [provider]);
        await harness.DisposeAsync();

        provider.Change(Number("disk.c.free"), Number("disk.e.free"));

        Assert.Equal(["disk.c.free"], harness.Engine.Catalog.Select(entry => entry.Id));
    }
}
