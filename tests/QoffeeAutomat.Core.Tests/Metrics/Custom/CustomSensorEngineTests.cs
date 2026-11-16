using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Metrics;
using static QoffeeAutomat.Core.Tests.Metrics.Custom.Sensors;

namespace QoffeeAutomat.Core.Tests.Metrics.Custom;

/// <summary>The custom sensor provider inside the metric engine: demand-driven execution and containment of failures (PLAN.md T215, D-007).</summary>
[Trait("Category", "CustomSensors")]
public sealed class CustomSensorEngineTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Five = TimeSpan.FromSeconds(5);

    private static MetricSelection Select(params string[] ids) => MetricSelection.From(ids);

    [Fact]
    public async Task A_script_sensor_is_only_executed_while_it_is_subscribed()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("s"), Process("p", "notepad"));
        harness.Launcher.Output("s", "42");

        // Nobody is subscribed: neither the script nor the process list is touched, however long we wait.
        await harness.TickAsync(OneSecond, 30);
        Assert.Equal(0, harness.Launcher.CallsFor("s"));
        Assert.Equal(0, harness.Processes.Reads);

        MetricSubscription subscription = harness.Engine.Subscribe(Select("custom.s"));
        await harness.Engine.PollAsync(CancellationToken.None);

        Assert.Equal(1, harness.Launcher.CallsFor("s"));
        Assert.Equal(0, harness.Processes.Reads);
        MetricUpdate snapshot = Assert.Single(SensorHarness.Drain(subscription));
        Assert.True(snapshot.IsSnapshot);
        Assert.Equal(42.0, snapshot.Values["custom.s"]);

        // It runs again once per interval ...
        await harness.TickAsync(OneSecond, 4);
        Assert.Equal(1, harness.Launcher.CallsFor("s"));
        await harness.TickAsync(OneSecond, 1);
        Assert.Equal(2, harness.Launcher.CallsFor("s"));

        // ... and stops with the last subscriber.
        subscription.Dispose();
        await harness.TickAsync(Five, 6);
        Assert.Equal(2, harness.Launcher.CallsFor("s"));
        Assert.Equal(0, harness.Processes.Reads);
    }

    [Fact]
    public async Task A_sensor_that_nobody_selected_is_not_executed_while_another_one_is()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("wanted"), Script("unwanted"));
        harness.Launcher.Output("wanted", "1");
        harness.Launcher.Output("unwanted", "2");
        harness.Engine.Subscribe(Select("custom.wanted"));

        await harness.Engine.PollAsync(CancellationToken.None);
        await harness.TickAsync(Five, 3);

        Assert.Equal(4, harness.Launcher.CallsFor("wanted"));
        Assert.Equal(0, harness.Launcher.CallsFor("unwanted"));
    }

    [Fact]
    public async Task A_disabled_script_sensor_is_never_executed_even_for_a_wildcard_subscriber()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("s", enabled: false));
        harness.Engine.Subscribe(MetricSelection.Everything);

        await harness.Engine.PollAsync(CancellationToken.None);
        await harness.TickAsync(Five, 4);
        Assert.Equal(0, harness.Launcher.CallsFor("s"));

        await harness.SaveAsync(Script("s", enabled: true));
        await harness.TickAsync(OneSecond);

        Assert.Equal(1, harness.Launcher.CallsFor("s"));
    }

    [Fact]
    public async Task The_engine_samples_a_script_sensor_no_more_often_than_every_five_seconds()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("fast", interval: 1));
        harness.Launcher.Output("fast", "1");
        harness.Engine.Subscribe(Select("custom.fast"));

        await harness.Engine.PollAsync(CancellationToken.None);
        await harness.TickAsync(OneSecond, 10);

        Assert.Equal(5, harness.Engine.Catalog.Single().IntervalSeconds);
        Assert.Equal(3, harness.Launcher.CallsFor("fast"));
    }

    [Fact]
    public async Task Parse_failures_make_the_metric_unavailable_without_affecting_other_metrics_or_the_provider()
    {
        var builtIn = new StubMetricProvider(TimeProvider.System, MetricHarness.Number("test.value"));
        builtIn.Set("test.value", 9);
        await using SensorHarness harness = await SensorHarness.CreateAsync(providers: [builtIn]);
        await harness.SaveAsync(Script("broken"), Script("fine"));
        harness.Launcher.Output("broken", "this is not a number");
        harness.Launcher.Output("fine", "7");
        var failures = new List<ProviderFailedEventArgs>();
        harness.Engine.ProviderFailed += (_, failure) => failures.Add(failure);
        MetricSubscription subscription = harness.Engine.Subscribe(Select("test.value", "custom.broken", "custom.fine"));

        await harness.Engine.PollAsync(CancellationToken.None);

        MetricUpdate snapshot = Assert.Single(SensorHarness.Drain(subscription));
        Assert.Equal(9.0, snapshot.Values["test.value"]);
        Assert.Null(snapshot.Values["custom.broken"]);
        Assert.Equal(7.0, snapshot.Values["custom.fine"]);
        Assert.Empty(failures);
        Assert.Contains("is not a number", harness.Provider.GetStatus("broken")!.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_sensor_is_tried_again_at_its_interval_and_recovers_when_the_script_is_fixed()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("flaky"));
        harness.Launcher.Output("flaky", string.Empty, exitCode: 2, standardError: "device offline");
        MetricSubscription subscription = harness.Engine.Subscribe(Select("custom.flaky"));
        await harness.Engine.PollAsync(CancellationToken.None);
        Assert.Null(Assert.Single(SensorHarness.Drain(subscription)).Values["custom.flaky"]);

        // The provider did not throw, so the engine applies no backoff: the next try is one interval later.
        await harness.TickAsync(OneSecond, 4);
        Assert.Equal(1, harness.Launcher.CallsFor("flaky"));
        await harness.TickAsync(OneSecond);
        Assert.Equal(2, harness.Launcher.CallsFor("flaky"));
        Assert.Equal(2, harness.Provider.GetStatus("flaky")!.ConsecutiveFailures);
        Assert.Equal("The script exited with code 2: device offline", harness.Provider.GetStatus("flaky")!.LastError);

        harness.Launcher.Output("flaky", "3.5");
        await harness.TickAsync(Five);

        MetricUpdate update = Assert.Single(SensorHarness.Drain(subscription));
        Assert.Equal(3.5, update.Values["custom.flaky"]);
        Assert.True(harness.Provider.GetStatus("flaky")!.IsAvailable);
    }

    [Fact]
    public async Task A_script_that_times_out_makes_its_metric_unavailable_without_affecting_other_metrics()
    {
        var builtIn = new StubMetricProvider(TimeProvider.System, MetricHarness.Number("test.value"));
        builtIn.Set("test.value", 1);
        await using SensorHarness harness = await SensorHarness.CreateAsync(providers: [builtIn]);
        await harness.SaveAsync(Script("slow"), Script("fine"));
        harness.Launcher.Hang("slow");
        harness.Launcher.Output("fine", "5");
        var failures = new List<ProviderFailedEventArgs>();
        harness.Engine.ProviderFailed += (_, failure) => failures.Add(failure);
        MetricSubscription subscription = harness.Engine.Subscribe(Select("test.value", "custom.slow", "custom.fine"));

        Task poll = harness.Engine.PollAsync(CancellationToken.None);
        await harness.Launcher.WaitForCallsAsync(2);
        harness.Clock.Advance(Five);
        await poll.WaitAsync(TimeSpan.FromSeconds(10));

        MetricUpdate snapshot = Assert.Single(SensorHarness.Drain(subscription));
        Assert.Equal(1.0, snapshot.Values["test.value"]);
        Assert.Null(snapshot.Values["custom.slow"]);
        Assert.Equal(5.0, snapshot.Values["custom.fine"]);
        Assert.Empty(failures);
        Assert.Equal("The script did not finish within 5 seconds.", harness.Provider.GetStatus("slow")!.LastError);
        Assert.Empty(harness.RunDirectories);
    }

    [Fact]
    public async Task A_process_sensor_follows_the_process_list_while_subscribed()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Process("app", "notepad", interval: 1));
        MetricSubscription subscription = harness.Engine.Subscribe(Select("custom.app"));

        await harness.Engine.PollAsync(CancellationToken.None);
        Assert.Equal(false, Assert.Single(SensorHarness.Drain(subscription)).Values["custom.app"]);

        harness.Processes.Names.Add("notepad");
        await harness.TickAsync(OneSecond);

        MetricUpdate update = Assert.Single(SensorHarness.Drain(subscription));
        Assert.Equal(true, update.Values["custom.app"]);
        Assert.Empty(harness.Launcher.Calls);
    }

    [Fact]
    public async Task A_new_script_sensor_reaches_an_existing_wildcard_subscriber()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        MetricSubscription subscription = harness.Engine.Subscribe(MetricSelection.Everything);
        await harness.Engine.PollAsync(CancellationToken.None);
        Assert.Empty(Assert.Single(SensorHarness.Drain(subscription)).Values);

        await harness.SaveAsync(Script("late"));
        harness.Launcher.Output("late", "11");
        await harness.TickAsync(OneSecond);

        MetricUpdate update = Assert.Single(SensorHarness.Drain(subscription));
        Assert.Equal(11.0, update.Values["custom.late"]);
        Assert.Equal(1, harness.Launcher.CallsFor("late"));
    }
}
