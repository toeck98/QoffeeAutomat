using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Platform.Simulated;
using static QoffeeAutomat.Core.Tests.Metrics.MetricHarness;

namespace QoffeeAutomat.Core.Tests.Metrics;

/// <summary>Failing providers, the history for charts and the background scheduler.</summary>
[Trait("Category", "Metrics")]
public sealed class MetricResilienceTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static DateTimeOffset Start => new SimulatedClock().GetUtcNow();

    private static IEnumerable<double> Offsets(IEnumerable<DateTimeOffset> times)
    {
        return times.Select(time => (time - Start).TotalSeconds);
    }

    private static async Task<MetricUpdate> NextAsync(MetricSubscription subscription)
    {
        using var timeout = new CancellationTokenSource(Patience);
        return await subscription.Updates.ReadAsync(timeout.Token);
    }

    [Fact]
    public async Task A_provider_that_throws_makes_its_metrics_unavailable_and_is_retried_with_doubling_backoff()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        var broken = new StubMetricProvider(clock, Number("x")) { Failure = new InvalidOperationException("boom") };
        await using var harness = await MetricHarness.CreateAsync(clock, [sim, broken]);
        var failures = new List<ProviderFailedEventArgs>();
        harness.Engine.ProviderFailed += (_, args) => failures.Add(args);
        MetricSubscription subscription = harness.Engine.Subscribe(Select("x", SimulatedMetricProvider.CounterId));

        await harness.PollAsync();
        await harness.TickAsync(OneSecond, 40);

        Assert.Equal([0.0, 5.0, 15.0, 35.0], Offsets(broken.CallTimes));
        Assert.Equal([1, 2, 3, 4], failures.Select(failure => failure.ConsecutiveFailures));
        Assert.Equal([5.0, 10.0, 20.0, 40.0], failures.Select(failure => failure.RetryIn.TotalSeconds));
        Assert.All(failures, failure =>
        {
            Assert.Equal(["x"], failure.MetricIds);
            Assert.Equal("boom", failure.Exception.Message);
        });

        // The healthy provider is not affected: sampled every second.
        Assert.Equal(41, sim.SampleCallCount);

        // The broken metric is unavailable from the snapshot on and is not repeated.
        List<MetricUpdate> updates = Drain(subscription);
        Assert.Null(updates[0].Values["x"]);
        Assert.All(updates.Skip(1), update => Assert.DoesNotContain("x", update.Values.Keys));

        // Charts show the gap.
        IReadOnlyList<MetricSample> history = harness.Engine.GetHistory("x");
        Assert.Equal(4, history.Count);
        Assert.All(history, sample => Assert.Null(sample.Value));
    }

    [Fact]
    public async Task The_backoff_is_capped()
    {
        var clock = new SimulatedClock();
        var broken = new StubMetricProvider(clock, Number("x")) { Failure = new InvalidOperationException("boom") };
        var options = new MetricEngineOptions { ProviderBackoffMax = TimeSpan.FromSeconds(15) };
        await using var harness = await MetricHarness.CreateAsync(clock, [broken], options);
        harness.Engine.Subscribe(Select("x"));

        await harness.PollAsync();
        await harness.TickAsync(OneSecond, 46);

        Assert.Equal([0.0, 5.0, 15.0, 30.0, 45.0], Offsets(broken.CallTimes));
    }

    [Fact]
    public async Task A_recovered_provider_delivers_values_again_and_the_backoff_starts_over()
    {
        var clock = new SimulatedClock();
        var provider = new StubMetricProvider(clock, Number("x")) { Failure = new InvalidOperationException("boom") };
        await using var harness = await MetricHarness.CreateAsync(clock, [provider]);
        var failures = new List<ProviderFailedEventArgs>();
        harness.Engine.ProviderFailed += (_, args) => failures.Add(args);
        MetricSubscription subscription = harness.Engine.Subscribe(Select("x"));
        await harness.PollAsync();
        await harness.TickAsync(OneSecond, 5);
        Assert.Equal(2, failures.Count);

        provider.Failure = null;
        provider.Set("x", 7);
        await harness.TickAsync(OneSecond, 10);

        Assert.Equal([0.0, 5.0, 15.0], Offsets(provider.CallTimes));
        List<MetricUpdate> updates = Drain(subscription);
        Assert.Equal(2, updates.Count);
        Assert.Null(updates[0].Values["x"]);
        Assert.Equal(7.0, AsNumber(updates[1].Values["x"]));
        Assert.Equal(Start + TimeSpan.FromSeconds(15), updates[1].Timestamp);

        provider.Failure = new InvalidOperationException("again");
        await harness.TickAsync(OneSecond);

        ProviderFailedEventArgs failure = failures[^1];
        Assert.Equal(1, failure.ConsecutiveFailures);
        Assert.Equal(TimeSpan.FromSeconds(5), failure.RetryIn);
        Assert.Null(Assert.Single(Drain(subscription)).Values["x"]);
    }

    [Fact]
    public async Task A_provider_that_hangs_is_cancelled_after_the_timeout_and_counts_as_failed()
    {
        var clock = new SimulatedClock();
        var provider = new StubMetricProvider(clock, Number("x")) { Hang = true };
        await using var harness = await MetricHarness.CreateAsync(clock, [provider]);
        var failures = new List<ProviderFailedEventArgs>();
        harness.Engine.ProviderFailed += (_, args) => failures.Add(args);
        MetricSubscription subscription = harness.Engine.Subscribe(Select("x"));

        Task poll = harness.PollAsync();
        await provider.FirstCall.WaitAsync(Patience);
        clock.Advance(TimeSpan.FromSeconds(31));
        await poll.WaitAsync(Patience);

        ProviderFailedEventArgs failure = Assert.Single(failures);
        Assert.IsAssignableFrom<OperationCanceledException>(failure.Exception);
        Assert.Null(Assert.Single(Drain(subscription)).Values["x"]);
    }

    [Fact]
    public async Task The_history_keeps_one_hour_of_samples()
    {
        var clock = new SimulatedClock();
        var provider = new StubMetricProvider(clock, Number("x", interval: 10));
        provider.Set("x", 1.5);
        await using var harness = await MetricHarness.CreateAsync(clock, [provider]);
        harness.Engine.Subscribe(Select("x"));

        await harness.PollAsync();
        await harness.TickAsync(TimeSpan.FromSeconds(10), 720);

        IReadOnlyList<MetricSample> history = harness.Engine.GetHistory("x");
        Assert.Equal(361, history.Count);
        Assert.Equal(Start + TimeSpan.FromHours(1), history[0].Timestamp);
        Assert.Equal(Start + TimeSpan.FromHours(2), history[^1].Timestamp);
        Assert.All(history, sample => Assert.Equal(1.5, AsNumber(sample.Value)));

        IReadOnlyList<MetricSample> recent = harness.Engine.GetHistory("x", clock.GetUtcNow() - TimeSpan.FromMinutes(5));
        Assert.Equal(31, recent.Count);
        Assert.Empty(harness.Engine.GetHistory("does.not.exist"));
    }

    [Fact]
    public async Task The_history_is_bounded_by_the_sample_limit()
    {
        var clock = new SimulatedClock();
        var provider = new StubMetricProvider(clock, Number("x"));
        provider.Set("x", 1);
        var options = new MetricEngineOptions { HistoryMaxSamples = 5 };
        await using var harness = await MetricHarness.CreateAsync(clock, [provider], options);
        harness.Engine.Subscribe(Select("x"));

        await harness.PollAsync();
        await harness.TickAsync(OneSecond, 20);

        IReadOnlyList<MetricSample> history = harness.Engine.GetHistory("x");
        Assert.Equal(5, history.Count);
        Assert.Equal(Start + TimeSpan.FromSeconds(16), history[0].Timestamp);
        Assert.Equal(Start + TimeSpan.FromSeconds(20), history[^1].Timestamp);
    }

    [Fact]
    public async Task The_history_records_the_rounded_value_even_when_the_deadband_hides_the_change()
    {
        var clock = new SimulatedClock();
        var provider = new StubMetricProvider(clock, Number("x", precision: 0, deadband: 5));
        provider.Set("x", 10.4);
        await using var harness = await MetricHarness.CreateAsync(clock, [provider]);
        MetricSubscription subscription = harness.Engine.Subscribe(Select("x"));
        await harness.PollAsync();
        provider.Set("x", 12.6);
        await harness.TickAsync(OneSecond);

        Assert.Single(Drain(subscription));
        Assert.Equal([10.0, 13.0], harness.Engine.GetHistory("x").Select(sample => AsNumber(sample.Value)));
    }

    [Fact]
    public async Task The_started_engine_serves_a_snapshot_and_follows_the_clock()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        harness.Engine.Start();
        harness.Engine.Start();

        MetricSubscription subscription = harness.Engine.Subscribe(Select(SimulatedMetricProvider.CounterId));
        MetricUpdate snapshot = await NextAsync(subscription);
        Assert.True(snapshot.IsSnapshot);

        // The scheduler waits on the fake clock; keep moving it until the next change arrives.
        MetricUpdate? change = null;
        for (int attempt = 0; attempt < 1000 && change is null; attempt++)
        {
            clock.Advance(OneSecond);
            if (subscription.Updates.TryRead(out MetricUpdate? update))
            {
                change = update;
            }
            else
            {
                await Task.Delay(5);
            }
        }

        Assert.NotNull(change);
        Assert.False(change.IsSnapshot);
        Assert.Equal(SimulatedMetricProvider.CounterAt(change.Timestamp), AsNumber(change.Values[SimulatedMetricProvider.CounterId]));
    }

    [Fact]
    public async Task The_started_engine_does_not_sample_without_subscribers()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        harness.Engine.Start();

        for (int minute = 0; minute < 30; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(2);
        }

        Assert.Equal(0, sim.SampleCallCount);
    }

    [Fact]
    public async Task The_started_engine_picks_up_configuration_changes()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        harness.Engine.Start();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Engine.CatalogChanged += (_, _) => changed.TrySetResult();

        var document = new MetricOverridesDocument(
            DocumentSchema.CurrentVersion,
            new Dictionary<string, MetricOverride>(StringComparer.Ordinal)
            {
                [SimulatedMetricProvider.CpuId] = new MetricOverride(IntervalSeconds: 42),
            });
        await harness.Store.Local.SaveAsync(document, null, CancellationToken.None);

        await changed.Task.WaitAsync(Patience);
        Assert.Equal(42, harness.Engine.Catalog.Single(entry => entry.Id == SimulatedMetricProvider.CpuId).IntervalSeconds);
    }
}
