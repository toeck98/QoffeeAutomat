using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Core.Tests.Platform;

[Trait("Category", "Platform")]
public sealed class SimulatedMetricTests
{
    private static readonly string[] AllIds =
    [
        SimulatedMetricProvider.CpuId,
        SimulatedMetricProvider.CounterId,
        SimulatedMetricProvider.ToggleId,
        SimulatedMetricProvider.ModeId,
    ];

    private static async Task<IReadOnlyDictionary<string, object?>> SampleAllAsync(SimulatedMetricProvider provider)
    {
        return await provider.SampleAsync(AllIds, CancellationToken.None);
    }

    [Fact]
    public void The_provider_describes_a_sine_a_counter_a_binary_sensor_and_an_enum()
    {
        var provider = new SimulatedMetricProvider(new SimulatedClock());

        IReadOnlyList<MetricDescriptor> metrics = provider.Describe();

        Assert.Equal(AllIds.Order(StringComparer.Ordinal), metrics.Select(metric => metric.Id).Order(StringComparer.Ordinal));
        MetricDescriptor cpu = metrics.Single(metric => metric.Id == SimulatedMetricProvider.CpuId);
        Assert.Equal(("sensor", "number"), (cpu.Kind, cpu.ValueType));
        MetricDescriptor toggle = metrics.Single(metric => metric.Id == SimulatedMetricProvider.ToggleId);
        Assert.Equal(("binary_sensor", "bool"), (toggle.Kind, toggle.ValueType));
        MetricDescriptor mode = metrics.Single(metric => metric.Id == SimulatedMetricProvider.ModeId);
        Assert.Equal("enum", mode.ValueType);
        Assert.Equal(SimulatedMetricProvider.ModeOptions, mode.Options);
        Assert.All(metrics, metric =>
        {
            Assert.Equal("system", metric.Context);
            Assert.False(string.IsNullOrWhiteSpace(metric.Name));
        });
    }

    [Fact]
    public async Task Metric_values_are_reproducible_for_a_given_fake_time()
    {
        var clock = new SimulatedClock(new DateTimeOffset(2026, 6, 1, 12, 0, 7, TimeSpan.Zero));
        var first = new SimulatedMetricProvider(clock);
        var second = new SimulatedMetricProvider(new SimulatedClock(clock.GetUtcNow()));

        IReadOnlyDictionary<string, object?> a = await SampleAllAsync(first);
        IReadOnlyDictionary<string, object?> again = await SampleAllAsync(first);
        IReadOnlyDictionary<string, object?> b = await SampleAllAsync(second);

        Assert.Equal(a.OrderBy(pair => pair.Key, StringComparer.Ordinal), again.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.Equal(a.OrderBy(pair => pair.Key, StringComparer.Ordinal), b.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.Equal(4, a.Count);
    }

    [Fact]
    public async Task Time_alone_moves_the_values()
    {
        var clock = new SimulatedClock(DateTimeOffset.UnixEpoch);
        var provider = new SimulatedMetricProvider(clock);

        IReadOnlyDictionary<string, object?> start = await SampleAllAsync(provider);
        clock.Advance(TimeSpan.FromSeconds(15));
        IReadOnlyDictionary<string, object?> later = await SampleAllAsync(provider);

        Assert.NotEqual(start[SimulatedMetricProvider.CpuId], later[SimulatedMetricProvider.CpuId]);
        Assert.NotEqual(start[SimulatedMetricProvider.CounterId], later[SimulatedMetricProvider.CounterId]);
        Assert.NotEqual(start[SimulatedMetricProvider.ToggleId], later[SimulatedMetricProvider.ToggleId]);
        Assert.NotEqual(start[SimulatedMetricProvider.ModeId], later[SimulatedMetricProvider.ModeId]);
    }

    [Fact]
    public async Task The_cpu_value_is_a_sine_wave_with_a_period_of_sixty_seconds()
    {
        var clock = new SimulatedClock(DateTimeOffset.UnixEpoch);
        var provider = new SimulatedMetricProvider(clock);
        string[] cpu = [SimulatedMetricProvider.CpuId];
        var samples = new List<double>();

        for (int second = 0; second <= 60; second += 15)
        {
            IReadOnlyDictionary<string, object?> values = await provider.SampleAsync(cpu, CancellationToken.None);
            samples.Add(Assert.IsType<double>(values[SimulatedMetricProvider.CpuId]));
            clock.Advance(TimeSpan.FromSeconds(15));
        }

        double[] expected = [50, 90, 50, 10, 50];
        Assert.Equal(expected, samples);
    }

    [Fact]
    public async Task The_counter_grows_by_one_per_second()
    {
        var clock = new SimulatedClock(DateTimeOffset.UnixEpoch.AddSeconds(100));
        var provider = new SimulatedMetricProvider(clock);
        string[] counter = [SimulatedMetricProvider.CounterId];

        IReadOnlyDictionary<string, object?> first = await provider.SampleAsync(counter, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        IReadOnlyDictionary<string, object?> sameSecond = await provider.SampleAsync(counter, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        IReadOnlyDictionary<string, object?> next = await provider.SampleAsync(counter, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(9));
        IReadOnlyDictionary<string, object?> ten = await provider.SampleAsync(counter, CancellationToken.None);

        Assert.Equal(100d, first[SimulatedMetricProvider.CounterId]);
        Assert.Equal(100d, sameSecond[SimulatedMetricProvider.CounterId]);
        Assert.Equal(101d, next[SimulatedMetricProvider.CounterId]);
        Assert.Equal(110d, ten[SimulatedMetricProvider.CounterId]);
    }

    [Fact]
    public async Task The_binary_sensor_toggles_every_ten_seconds_and_the_enum_cycles_every_fifteen()
    {
        var clock = new SimulatedClock(DateTimeOffset.UnixEpoch);
        var provider = new SimulatedMetricProvider(clock);
        string[] ids = [SimulatedMetricProvider.ToggleId, SimulatedMetricProvider.ModeId];
        var toggles = new List<object?>();
        var modes = new List<object?>();

        for (int second = 0; second < 60; second += 5)
        {
            IReadOnlyDictionary<string, object?> values = await provider.SampleAsync(ids, CancellationToken.None);
            toggles.Add(values[SimulatedMetricProvider.ToggleId]);
            modes.Add(values[SimulatedMetricProvider.ModeId]);
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        object?[] expectedToggles = [true, true, false, false, true, true, false, false, true, true, false, false];
        object?[] expectedModes = ["idle", "idle", "idle", "active", "active", "active", "busy", "busy", "busy", "idle", "idle", "idle"];
        Assert.Equal(expectedToggles, toggles);
        Assert.Equal(expectedModes, modes);
    }

    [Fact]
    public void The_pure_functions_agree_with_the_provider()
    {
        DateTimeOffset at = new(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

        double cpu = SimulatedMetricProvider.CpuAt(at);

        Assert.InRange(cpu, 10d, 90d);
        Assert.Equal(cpu, SimulatedMetricProvider.CpuAt(at.AddMinutes(1)));
        Assert.Contains(SimulatedMetricProvider.ModeAt(at), SimulatedMetricProvider.ModeOptions);
    }

    [Fact]
    public async Task A_metric_can_be_made_unavailable_and_unknown_ids_are_ignored()
    {
        var provider = new SimulatedMetricProvider(new SimulatedClock());
        string[] ids = [SimulatedMetricProvider.CpuId, "no.such.metric"];

        provider.SetAvailable(SimulatedMetricProvider.CpuId, false);
        IReadOnlyDictionary<string, object?> unavailable = await provider.SampleAsync(ids, CancellationToken.None);
        provider.SetAvailable(SimulatedMetricProvider.CpuId, true);
        IReadOnlyDictionary<string, object?> available = await provider.SampleAsync(ids, CancellationToken.None);

        Assert.True(unavailable.ContainsKey(SimulatedMetricProvider.CpuId));
        Assert.Null(unavailable[SimulatedMetricProvider.CpuId]);
        Assert.False(unavailable.ContainsKey("no.such.metric"));
        Assert.NotNull(available[SimulatedMetricProvider.CpuId]);
    }

    [Fact]
    public async Task Only_the_requested_metrics_are_sampled()
    {
        var provider = new SimulatedMetricProvider(new SimulatedClock());
        string[] ids = [SimulatedMetricProvider.ToggleId];

        IReadOnlyDictionary<string, object?> values = await provider.SampleAsync(ids, CancellationToken.None);

        Assert.Equal(SimulatedMetricProvider.ToggleId, Assert.Single(values).Key);
        Assert.Empty(await provider.SampleAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_sample_throws()
    {
        var provider = new SimulatedMetricProvider(new SimulatedClock());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await provider.SampleAsync(AllIds, cancelled.Token));
    }
}
