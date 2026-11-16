using System.Text.Json;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Agent.Tests.Link;

/// <summary>
/// <c>subscribe</c> and <c>state</c> of the QoffeeLink server (T210, PLAN.md S07, R-06): snapshot, deltas,
/// intervals, independent clients and the end of sampling with the last client. The values come from the
/// simulated metric provider; the sine, the counter and the toggle follow the clock.
/// </summary>
[Trait("Category", "Link")]
public sealed class LinkSubscriptionTests
{
    private const string Counter = SimulatedMetricProvider.CounterId;
    private const string Cpu = SimulatedMetricProvider.CpuId;
    private const string Toggle = SimulatedMetricProvider.ToggleId;

    [Fact]
    public async Task A_subscription_is_answered_with_a_result_and_then_a_full_snapshot_of_exactly_the_subscribed_metrics()
    {
        await using var host = new LinkTestHost();
        await using LinkTestClient client = await host.ConnectAsync();

        long id = await client.SendRequestAsync(MessageTypes.Subscribe, "\"metrics\":[\"" + Counter + "\",\"" + Toggle + "\"]");

        JsonElement result = await client.WaitForResultAsync(id);
        JsonElement snapshot = await client.WaitForTypeAsync(MessageTypes.State);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(new[] { Counter, Toggle }, LinkTestClient.MetricsOf(snapshot).Order(StringComparer.Ordinal).ToArray());
        JsonElement values = snapshot.GetProperty("v");
        Assert.Equal(JsonValueKind.Number, values.GetProperty(Counter).ValueKind);
        Assert.True(values.GetProperty(Toggle).ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(snapshot.GetProperty("ts").GetInt64() > 0);
        JsonElement[] frames = client.Frames;
        Assert.True(Array.FindIndex(frames, frame => LinkTestClient.IsResult(frame, id)) < Array.FindIndex(frames, frame => LinkTestClient.TypeOf(frame) == MessageTypes.State));
    }

    [Fact]
    public async Task Subscribing_to_everything_delivers_all_metrics_and_later_frames_carry_only_changes()
    {
        await using var host = new LinkTestHost();
        await using LinkTestClient client = await host.ConnectAsync();

        await client.RequestAsync(MessageTypes.Subscribe, "\"metrics\":\"*\"");

        JsonElement snapshot = await client.WaitForAsync(
            frame => LinkTestClient.TypeOf(frame) == MessageTypes.State && LinkTestClient.MetricsOf(frame).Contains(Cpu),
            "the snapshot with the simulated metrics");
        string[] all = LinkTestClient.MetricsOf(snapshot);
        Assert.Contains(Counter, all);
        Assert.Contains(Toggle, all);
        Assert.Contains(SimulatedMetricProvider.ModeId, all);

        // A later frame has the counter, which grows every second, but not the mode, which changes every 15 seconds.
        int after = client.IndexOf(snapshot) + 1;
        JsonElement delta = await client.WaitForAsync(
            frame => LinkTestClient.TypeOf(frame) == MessageTypes.State && LinkTestClient.MetricsOf(frame).Contains(Counter),
            "a state frame with a changed counter",
            after);
        Assert.True(delta.GetProperty("v").GetProperty(Counter).GetDouble() > snapshot.GetProperty("v").GetProperty(Counter).GetDouble());
        Assert.True(LinkTestClient.MetricsOf(delta).Length < all.Length);
    }

    [Fact]
    public async Task A_metric_with_an_interval_is_delivered_at_most_once_per_interval()
    {
        await using var host = new LinkTestHost();
        await using LinkTestClient client = await host.ConnectAsync();

        await client.RequestAsync(
            MessageTypes.Subscribe,
            "\"metrics\":[\"" + Counter + "\",\"" + Cpu + "\"],\"intervals\":{\"" + Counter + "\":3600}");

        JsonElement snapshot = await client.WaitForAsync(
            frame => LinkTestClient.TypeOf(frame) == MessageTypes.State && LinkTestClient.MetricsOf(frame).Contains(Counter),
            "the snapshot");
        int afterSnapshot = client.IndexOf(snapshot) + 1;
        Assert.Contains(Cpu, LinkTestClient.MetricsOf(snapshot));

        // The sine keeps changing while the counter, which also changes every second, stays at the value of the snapshot.
        await client.WaitForAsync(
            frame => LinkTestClient.TypeOf(frame) == MessageTypes.State && LinkTestClient.MetricsOf(frame).Contains(Cpu),
            "a later value of the sine",
            afterSnapshot);
        await Task.Delay(1500);
        Assert.DoesNotContain(
            client.FramesOf(MessageTypes.State, afterSnapshot),
            frame => LinkTestClient.MetricsOf(frame).Contains(Counter));
    }

    [Fact]
    public async Task A_new_subscription_replaces_the_old_one_and_nothing_of_the_old_one_follows_its_result()
    {
        await using var host = new LinkTestHost();
        await using LinkTestClient client = await host.ConnectAsync();
        await client.RequestAsync(MessageTypes.Subscribe, "\"metrics\":[\"" + Counter + "\"]");
        await client.WaitForTypeAsync(MessageTypes.State);

        long second = await client.SendRequestAsync(MessageTypes.Subscribe, "\"metrics\":[\"" + Cpu + "\"]");
        JsonElement result = await client.WaitForResultAsync(second);
        int afterResult = client.IndexOf(result) + 1;
        await client.WaitForAsync(
            frame => LinkTestClient.TypeOf(frame) == MessageTypes.State && LinkTestClient.MetricsOf(frame).Contains(Cpu),
            "the snapshot of the new subscription",
            afterResult);
        await Task.Delay(1500);

        Assert.All(
            client.FramesOf(MessageTypes.State, afterResult),
            frame => Assert.Equal(new[] { Cpu }, LinkTestClient.MetricsOf(frame)));
    }

    [Fact]
    public async Task An_empty_subscription_ends_the_feed()
    {
        await using var host = new LinkTestHost();
        await using LinkTestClient client = await host.ConnectAsync();
        await client.RequestAsync(MessageTypes.Subscribe, "\"metrics\":[\"" + Counter + "\"]");
        await client.WaitForTypeAsync(MessageTypes.State);

        JsonElement result = await client.RequestAsync(MessageTypes.Subscribe, "\"metrics\":[]");
        int settled = client.Count;
        await Task.Delay(1800);

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(settled, client.Count);
    }

    [Fact]
    public async Task A_client_that_never_subscribes_receives_no_state()
    {
        await using var host = new LinkTestHost();
        await using LinkTestClient client = await host.ConnectAsync();

        await client.RequestAsync(MessageTypes.GetManifest);
        await Task.Delay(1500);

        Assert.Empty(client.FramesOf(MessageTypes.State));
    }

    [Fact]
    public async Task Clients_have_independent_subscriptions_and_intervals()
    {
        await using var host = new LinkTestHost();
        await using LinkTestClient eager = await host.ConnectAsync();
        await using LinkTestClient patient = await host.ConnectAsync();
        await using LinkTestClient other = await host.ConnectAsync();

        await eager.RequestAsync(MessageTypes.Subscribe, "\"metrics\":[\"" + Counter + "\"]");
        await patient.RequestAsync(MessageTypes.Subscribe, "\"metrics\":[\"" + Counter + "\"],\"intervals\":{\"" + Counter + "\":3600}");
        await other.RequestAsync(MessageTypes.Subscribe, "\"metrics\":[\"" + Toggle + "\"]");

        // The eager client sees the counter move; the patient one sees it once; the third never sees it.
        await eager.WaitForAsync(
            frame => LinkTestClient.TypeOf(frame) == MessageTypes.State,
            "the snapshot of the eager client");
        await eager.WaitForAsync(
            frame => LinkTestClient.TypeOf(frame) == MessageTypes.State,
            "a second state frame of the eager client",
            Array.FindIndex(eager.Frames, frame => LinkTestClient.TypeOf(frame) == MessageTypes.State) + 1);
        await patient.WaitForTypeAsync(MessageTypes.State);
        await other.WaitForTypeAsync(MessageTypes.State);
        await Task.Delay(500);

        Assert.True(eager.FramesOf(MessageTypes.State).Length >= 2);
        Assert.Single(patient.FramesOf(MessageTypes.State));
        Assert.All(eager.FramesOf(MessageTypes.State), frame => Assert.Equal(new[] { Counter }, LinkTestClient.MetricsOf(frame)));
        Assert.All(patient.FramesOf(MessageTypes.State), frame => Assert.Equal(new[] { Counter }, LinkTestClient.MetricsOf(frame)));
        Assert.All(other.FramesOf(MessageTypes.State), frame => Assert.Equal(new[] { Toggle }, LinkTestClient.MetricsOf(frame)));
        Assert.Equal(3, host.Server.ConnectionCount);
    }

    [Fact]
    public async Task Sampling_stops_when_the_last_client_has_disconnected()
    {
        await using var host = new LinkTestHost();
        SimulatedMetricProvider sampler = host.Platform.Metrics;

        // Nobody is connected: nothing is sampled.
        await Task.Delay(1200);
        Assert.Equal(0, sampler.SampleCallCount);

        await using LinkTestClient first = await host.ConnectAsync();
        await using LinkTestClient second = await host.ConnectAsync();
        await first.RequestAsync(MessageTypes.Subscribe, "\"metrics\":[\"" + Counter + "\"]");
        await second.RequestAsync(MessageTypes.Subscribe, "\"metrics\":[\"" + Cpu + "\"]");
        await TestWait.UntilAsync(() => sampler.SampleCallCount >= 2, "the metrics to be sampled periodically");

        // One client leaves: the other still wants its metric, so sampling goes on.
        await first.CloseAsync();
        await TestWait.UntilAsync(() => host.Server.ConnectionCount == 1, "the first client to be removed");
        await Task.Delay(300);
        int whileOneIsLeft = sampler.SampleCallCount;
        await TestWait.UntilAsync(() => sampler.SampleCallCount >= whileOneIsLeft + 2, "sampling to go on for the second client");

        // The last client leaves: the engine stops.
        await second.CloseAsync();
        await TestWait.UntilAsync(() => host.Server.ConnectionCount == 0, "the second client to be removed");
        await Task.Delay(500);
        int calls = sampler.SampleCallCount;
        int ids = sampler.SampledIdCount;
        await Task.Delay(2500);

        Assert.Equal(calls, sampler.SampleCallCount);
        Assert.Equal(ids, sampler.SampledIdCount);
    }
}
