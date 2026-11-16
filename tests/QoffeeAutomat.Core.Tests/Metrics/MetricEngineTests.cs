using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Platform.Simulated;
using static QoffeeAutomat.Core.Tests.Metrics.MetricHarness;

namespace QoffeeAutomat.Core.Tests.Metrics;

/// <summary>Demand-driven sampling, snapshots and the change feed (PLAN.md D-007, S07).</summary>
[Trait("Category", "Metrics")]
public sealed class MetricEngineTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static DateTimeOffset Start => new SimulatedClock().GetUtcNow();

    [Fact]
    public async Task With_zero_subscribers_no_provider_is_sampled()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);

        await harness.TickAsync(TimeSpan.FromMinutes(10), 6);

        Assert.Equal(0, sim.SampleCallCount);
        Assert.Equal(0, sim.SampledIdCount);
        Assert.Empty(harness.Engine.GetHistory(SimulatedMetricProvider.CounterId));
    }

    [Fact]
    public async Task Sampling_stops_when_the_last_subscriber_leaves()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        MetricSubscription subscription = harness.Engine.Subscribe(Select(SimulatedMetricProvider.CounterId));

        await harness.PollAsync();
        await harness.TickAsync(OneSecond, 3);
        Assert.Equal(4, sim.SampleCallCount);

        subscription.Dispose();
        await harness.TickAsync(TimeSpan.FromSeconds(10), 6);

        Assert.Equal(4, sim.SampleCallCount);
    }

    [Fact]
    public async Task Unknown_metric_ids_are_not_sampled_and_the_snapshot_is_empty()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        MetricSubscription subscription = harness.Engine.Subscribe(Select("does.not.exist"));

        await harness.TickAsync(OneSecond, 3);

        MetricUpdate snapshot = Assert.Single(Drain(subscription));
        Assert.True(snapshot.IsSnapshot);
        Assert.Empty(snapshot.Values);
        Assert.Equal(0, sim.SampleCallCount);
    }

    [Fact]
    public async Task A_subscription_starts_with_a_snapshot_of_every_selected_metric()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        MetricSubscription subscription = harness.Engine.Subscribe(
            Select(SimulatedMetricProvider.CounterId, SimulatedMetricProvider.ToggleId));

        await harness.PollAsync();

        MetricUpdate snapshot = Assert.Single(Drain(subscription));
        Assert.True(snapshot.IsSnapshot);
        Assert.Equal(Start, snapshot.Timestamp);
        Assert.Equal(2, snapshot.Values.Count);
        Assert.Equal(SimulatedMetricProvider.CounterAt(Start), AsNumber(snapshot.Values[SimulatedMetricProvider.CounterId]));
        Assert.True(AsBool(snapshot.Values[SimulatedMetricProvider.ToggleId]));
        Assert.Equal(1, sim.SampleCallCount);
        Assert.Equal(2, sim.SampledIdCount);
    }

    [Fact]
    public async Task The_wildcard_selects_every_enabled_metric()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        MetricSubscription subscription = harness.Engine.Subscribe(MetricSelection.Everything);

        await harness.PollAsync();

        MetricUpdate snapshot = Assert.Single(Drain(subscription));
        Assert.Equal(4, snapshot.Values.Count);
        Assert.Equal(SimulatedMetricProvider.ModeAt(Start), snapshot.Values[SimulatedMetricProvider.ModeId]);
    }

    [Fact]
    public async Task A_metric_that_does_not_change_produces_no_update_after_the_snapshot()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        MetricSubscription subscription = harness.Engine.Subscribe(Select(SimulatedMetricProvider.ToggleId));
        await harness.PollAsync();
        Assert.Single(Drain(subscription));

        await harness.TickAsync(OneSecond, 9);

        Assert.Empty(Drain(subscription));
        Assert.Equal(10, sim.SampleCallCount);

        await harness.TickAsync(OneSecond);

        MetricUpdate change = Assert.Single(Drain(subscription));
        Assert.False(change.IsSnapshot);
        Assert.Equal(Start + TimeSpan.FromSeconds(10), change.Timestamp);
        KeyValuePair<string, object?> entry = Assert.Single(change.Values);
        Assert.Equal(SimulatedMetricProvider.ToggleId, entry.Key);
        Assert.False(AsBool(entry.Value));
    }

    [Fact]
    public async Task Two_subscribers_with_different_metric_sets_each_receive_only_their_metrics()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        MetricSubscription counters = harness.Engine.Subscribe(Select(SimulatedMetricProvider.CounterId));
        MetricSubscription toggles = harness.Engine.Subscribe(Select(SimulatedMetricProvider.ToggleId));

        await harness.PollAsync();
        await harness.TickAsync(OneSecond, 12);

        List<MetricUpdate> counterUpdates = Drain(counters);
        List<MetricUpdate> toggleUpdates = Drain(toggles);
        Assert.Equal(13, counterUpdates.Count);
        Assert.All(counterUpdates, update => Assert.Equal([SimulatedMetricProvider.CounterId], update.Values.Keys));
        Assert.Equal(2, toggleUpdates.Count);
        Assert.All(toggleUpdates, update => Assert.Equal([SimulatedMetricProvider.ToggleId], update.Values.Keys));
        Assert.True(toggleUpdates[0].IsSnapshot);
        Assert.False(AsBool(toggleUpdates[1].Values[SimulatedMetricProvider.ToggleId]));
    }

    [Fact]
    public async Task A_subscriber_with_a_30_second_interval_gets_one_value_per_30_seconds_while_another_gets_every_change()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        MetricSubscription everyChange = harness.Engine.Subscribe(Select(SimulatedMetricProvider.CounterId));
        MetricSubscription throttled = harness.Engine.Subscribe(
            Select(SimulatedMetricProvider.CounterId),
            new Dictionary<string, double> { [SimulatedMetricProvider.CounterId] = 30 });

        await harness.PollAsync();
        await harness.TickAsync(OneSecond, 61);

        List<MetricUpdate> fast = Drain(everyChange);
        List<MetricUpdate> slow = Drain(throttled);

        Assert.Equal(61, fast.Count(update => !update.IsSnapshot));
        Assert.True(slow[0].IsSnapshot);
        List<MetricUpdate> slowChanges = [.. slow.Where(update => !update.IsSnapshot)];
        Assert.Equal(
            [Start + TimeSpan.FromSeconds(30), Start + TimeSpan.FromSeconds(60)],
            slowChanges.Select(update => update.Timestamp));
        Assert.All(slowChanges, update =>
            Assert.Equal(SimulatedMetricProvider.CounterAt(update.Timestamp), AsNumber(update.Values[SimulatedMetricProvider.CounterId])));
    }

    [Fact]
    public async Task The_scheduler_samples_at_the_shortest_interval_any_subscriber_needs()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        MetricSubscription throttled = harness.Engine.Subscribe(
            Select(SimulatedMetricProvider.CounterId),
            new Dictionary<string, double> { [SimulatedMetricProvider.CounterId] = 30 });

        await harness.PollAsync();
        await harness.TickAsync(OneSecond, 61);

        // t=0, t=30 and t=60: the metric's own one-second interval is not needed by anybody.
        Assert.Equal(3, sim.SampleCallCount);
        Assert.Equal(2, Drain(throttled).Count(update => !update.IsSnapshot));

        // A second subscriber without interval brings the rate back to one sample per second.
        MetricSubscription fast = harness.Engine.Subscribe(Select(SimulatedMetricProvider.CounterId));
        await harness.PollAsync();
        await harness.TickAsync(OneSecond, 5);
        Assert.Equal(3 + 1 + 5, sim.SampleCallCount);
        Assert.Equal(6, Drain(fast).Count);
    }

    [Fact]
    public async Task Changes_are_coalesced_and_flushed_at_most_once_per_second()
    {
        var clock = new SimulatedClock();
        var fast = new StubMetricProvider(clock, Number("fast", interval: 0.1));
        fast.Set("fast", 0);
        await using var harness = await MetricHarness.CreateAsync(clock, [fast]);
        MetricSubscription subscription = harness.Engine.Subscribe(Select("fast"));
        await harness.PollAsync();

        for (int step = 1; step <= 25; step++)
        {
            fast.Set("fast", step);
            await harness.TickAsync(TimeSpan.FromMilliseconds(100));
        }

        List<MetricUpdate> updates = Drain(subscription);
        Assert.Equal(3, updates.Count);
        Assert.True(updates[0].IsSnapshot);
        Assert.Equal(10.0, AsNumber(updates[1].Values["fast"]));
        Assert.Equal(20.0, AsNumber(updates[2].Values["fast"]));
        Assert.All(updates.Zip(updates.Skip(1)), pair => Assert.True(pair.Second.Timestamp - pair.First.Timestamp >= OneSecond));
    }

    [Fact]
    public async Task A_value_that_changes_and_changes_back_between_flushes_is_not_sent()
    {
        var clock = new SimulatedClock();
        var fast = new StubMetricProvider(clock, Number("fast", interval: 0.1));
        fast.Set("fast", 1);
        await using var harness = await MetricHarness.CreateAsync(clock, [fast]);
        MetricSubscription subscription = harness.Engine.Subscribe(Select("fast"));
        await harness.PollAsync();
        Drain(subscription);

        fast.Set("fast", 2);
        await harness.TickAsync(TimeSpan.FromMilliseconds(100), 3);
        fast.Set("fast", 1);
        await harness.TickAsync(TimeSpan.FromMilliseconds(100), 10);

        Assert.Empty(Drain(subscription));
    }

    [Fact]
    public async Task Numbers_are_rounded_to_the_precision_before_they_are_compared()
    {
        var clock = new SimulatedClock();
        var stub = new StubMetricProvider(clock, Number("x", precision: 1));
        stub.Set("x", 12.34);
        await using var harness = await MetricHarness.CreateAsync(clock, [stub]);
        MetricSubscription subscription = harness.Engine.Subscribe(Select("x"));
        await harness.PollAsync();
        Assert.Equal(12.3, AsNumber(Assert.Single(Drain(subscription)).Values["x"]));

        stub.Set("x", 12.31);
        await harness.TickAsync(OneSecond, 2);
        Assert.Empty(Drain(subscription));

        stub.Set("x", 12.36);
        await harness.TickAsync(OneSecond);
        Assert.Equal(12.4, AsNumber(Assert.Single(Drain(subscription)).Values["x"]));

        stub.Set("x", 0.25);
        await harness.TickAsync(OneSecond);
        Assert.Equal(0.3, AsNumber(Assert.Single(Drain(subscription)).Values["x"]));
    }

    [Fact]
    public async Task A_change_below_the_deadband_is_not_published()
    {
        var clock = new SimulatedClock();
        var stub = new StubMetricProvider(clock, Number("x", deadband: 5));
        stub.Set("x", 10);
        await using var harness = await MetricHarness.CreateAsync(clock, [stub]);
        MetricSubscription subscription = harness.Engine.Subscribe(Select("x"));
        await harness.PollAsync();
        Drain(subscription);

        foreach (double quiet in new[] { 12.0, 14.0, 10.5 })
        {
            stub.Set("x", quiet);
            await harness.TickAsync(OneSecond);
        }

        Assert.Empty(Drain(subscription));

        stub.Set("x", 15);
        await harness.TickAsync(OneSecond);
        Assert.Equal(15.0, AsNumber(Assert.Single(Drain(subscription)).Values["x"]));

        // The deadband is measured from the last published value, 15, not from the last sample.
        stub.Set("x", 12);
        await harness.TickAsync(OneSecond);
        Assert.Empty(Drain(subscription));

        stub.Set("x", 9);
        await harness.TickAsync(OneSecond);
        Assert.Equal(9.0, AsNumber(Assert.Single(Drain(subscription)).Values["x"]));
    }

    [Fact]
    public async Task The_deadband_never_hides_the_change_to_or_from_unavailable()
    {
        var clock = new SimulatedClock();
        var stub = new StubMetricProvider(clock, Number("x", deadband: 5));
        stub.Set("x", 10);
        await using var harness = await MetricHarness.CreateAsync(clock, [stub]);
        MetricSubscription subscription = harness.Engine.Subscribe(Select("x"));
        await harness.PollAsync();
        Drain(subscription);

        stub.Set("x", null);
        await harness.TickAsync(OneSecond);
        Assert.Null(Assert.Single(Drain(subscription)).Values["x"]);

        stub.Set("x", 10.5);
        await harness.TickAsync(OneSecond);
        Assert.Equal(10.5, AsNumber(Assert.Single(Drain(subscription)).Values["x"]));
    }

    [Fact]
    public async Task Unavailable_metrics_are_delivered_as_null_and_come_back()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        sim.SetAvailable(SimulatedMetricProvider.ModeId, false);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        MetricSubscription subscription = harness.Engine.Subscribe(Select(SimulatedMetricProvider.ModeId));

        await harness.PollAsync();
        Assert.Null(Assert.Single(Drain(subscription)).Values[SimulatedMetricProvider.ModeId]);

        sim.SetAvailable(SimulatedMetricProvider.ModeId, true);
        await harness.TickAsync(OneSecond);

        MetricUpdate update = Assert.Single(Drain(subscription));
        Assert.Equal(SimulatedMetricProvider.ModeAt(Start + OneSecond), update.Values[SimulatedMetricProvider.ModeId]);
    }

    [Fact]
    public async Task Provider_values_are_normalised_to_number_bool_string_or_null()
    {
        var clock = new SimulatedClock();
        var stub = new StubMetricProvider(clock, Number("int"), Number("nan"), Number("text"), Number("when"), Number("weird"));
        stub.Set("int", 7);
        stub.Set("nan", double.NaN);
        stub.Set("text", "hello");
        stub.Set("when", new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.FromHours(2)));
        stub.Set("weird", new object());
        await using var harness = await MetricHarness.CreateAsync(clock, [stub]);
        MetricSubscription subscription = harness.Engine.Subscribe(MetricSelection.Everything);

        await harness.PollAsync();

        IReadOnlyDictionary<string, object?> values = Assert.Single(Drain(subscription)).Values;
        Assert.Equal(7.0, AsNumber(values["int"]));
        Assert.Null(values["nan"]);
        Assert.Equal("hello", values["text"]);
        Assert.Equal("2026-05-06T05:08:09Z", values["when"]);
        Assert.Null(values["weird"]);
    }

    [Fact]
    public async Task One_provider_call_serves_all_metrics_that_are_due()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        var stub = new StubMetricProvider(clock, Number("a"), Number("b"), Number("c"));
        stub.Set("a", 1);
        stub.Set("b", 2);
        stub.Set("c", 3);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim, stub]);
        harness.Engine.Subscribe(MetricSelection.Everything);

        await harness.PollAsync();
        await harness.TickAsync(OneSecond, 4);

        Assert.Equal(5, stub.CallCount);
        Assert.All(stub.Batches, batch => Assert.Equal(["a", "b", "c"], batch.Order(StringComparer.Ordinal)));
        Assert.Equal(5, sim.SampleCallCount);
        Assert.Equal(20, sim.SampledIdCount);
    }

    [Fact]
    public async Task Metrics_with_different_intervals_share_a_call_only_when_both_are_due()
    {
        var clock = new SimulatedClock();
        var stub = new StubMetricProvider(clock, Number("a", interval: 1), Number("b", interval: 2));
        stub.Set("a", 1);
        stub.Set("b", 2);
        await using var harness = await MetricHarness.CreateAsync(clock, [stub]);
        harness.Engine.Subscribe(MetricSelection.Everything);

        await harness.PollAsync();
        await harness.TickAsync(OneSecond, 4);

        List<string> batches = [.. stub.Batches.Select(batch => string.Join(',', batch.Order(StringComparer.Ordinal)))];
        Assert.Equal(["a,b", "a", "a,b", "a", "a,b"], batches);
    }

    [Fact]
    public async Task Event_driven_metrics_are_sampled_on_subscription_and_on_request_only()
    {
        var clock = new SimulatedClock();
        var stub = new StubMetricProvider(clock, Number("event", interval: 0));
        stub.Set("event", 1);
        await using var harness = await MetricHarness.CreateAsync(clock, [stub]);
        MetricSubscription subscription = harness.Engine.Subscribe(Select("event"));

        await harness.PollAsync();
        await harness.TickAsync(TimeSpan.FromMinutes(1), 5);
        Assert.Equal(1, stub.CallCount);
        Assert.Single(Drain(subscription));

        stub.Set("event", 2);
        harness.Engine.RequestSample("event");
        await harness.PollAsync();

        Assert.Equal(2, stub.CallCount);
        Assert.Equal(2.0, AsNumber(Assert.Single(Drain(subscription)).Values["event"]));
    }

    [Fact]
    public async Task Requesting_a_sample_of_a_metric_nobody_subscribed_to_does_nothing()
    {
        var clock = new SimulatedClock();
        var stub = new StubMetricProvider(clock, Number("event", interval: 0));
        await using var harness = await MetricHarness.CreateAsync(clock, [stub]);

        harness.Engine.RequestSample("event");
        await harness.PollAsync();

        Assert.Equal(0, stub.CallCount);
    }

    [Fact]
    public async Task A_new_selection_replaces_the_old_one_and_starts_with_a_snapshot()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        MetricSubscription subscription = harness.Engine.Subscribe(Select(SimulatedMetricProvider.CounterId));
        await harness.PollAsync();
        Drain(subscription);

        subscription.SetSelection(Select(SimulatedMetricProvider.ToggleId));
        await harness.TickAsync(OneSecond);

        MetricUpdate snapshot = Assert.Single(Drain(subscription));
        Assert.True(snapshot.IsSnapshot);
        Assert.Equal([SimulatedMetricProvider.ToggleId], snapshot.Values.Keys);

        await harness.TickAsync(OneSecond, 3);
        Assert.Empty(Drain(subscription));
    }

    [Fact]
    public async Task Nonsense_intervals_are_ignored()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        MetricSubscription subscription = harness.Engine.Subscribe(
            Select(SimulatedMetricProvider.CounterId),
            new Dictionary<string, double>
            {
                [SimulatedMetricProvider.CounterId] = -5,
                ["other"] = double.NaN,
            });

        await harness.PollAsync();
        await harness.TickAsync(OneSecond, 3);

        Assert.Equal(3, Drain(subscription).Count(update => !update.IsSnapshot));
    }

    [Fact]
    public async Task The_current_value_is_available_while_somebody_subscribes()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        Assert.False(harness.Engine.TryGetCurrentValue(SimulatedMetricProvider.ToggleId, out _));

        harness.Engine.Subscribe(Select(SimulatedMetricProvider.ToggleId));
        await harness.PollAsync();

        Assert.True(harness.Engine.TryGetCurrentValue(SimulatedMetricProvider.ToggleId, out object? value));
        Assert.True(AsBool(value));
        Assert.False(harness.Engine.TryGetCurrentValue("does.not.exist", out _));
    }

    [Fact]
    public async Task Disposing_the_engine_completes_the_subscriptions()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);
        MetricSubscription subscription = harness.Engine.Subscribe(Select(SimulatedMetricProvider.CounterId));

        await harness.Engine.DisposeAsync();

        Assert.False(await subscription.Updates.WaitToReadAsync());
        Assert.Throws<ObjectDisposedException>(() => harness.Engine.Subscribe());
    }

    [Fact]
    public async Task Invalid_options_are_rejected()
    {
        var clock = new SimulatedClock();
        var sim = new SimulatedMetricProvider(clock);
        await using var harness = await MetricHarness.CreateAsync(clock, [sim]);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MetricEngine(harness.Store, [sim], clock, new MetricEngineOptions { HistoryMaxSamples = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MetricEngine(harness.Store, [sim], clock, new MetricEngineOptions { FlushInterval = TimeSpan.FromSeconds(-1) }));
    }
}
