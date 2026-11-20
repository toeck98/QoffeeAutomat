using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Agent.Hub;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Tests.Security;

namespace QoffeeAutomat.Hub.Tests.Gateway;

/// <summary>
/// The agent gateway with real agents (T906, PLAN.md S07, S20): the hub in process, agents on the simulated platform that enroll, dial and speak QoffeeLink to it. The
/// agent sees exactly the frames that a real agent would; a recording socket in the agent's connector shows what the hub sent.
/// </summary>
[Trait("Category", "Gateway")]
public sealed class AgentGatewayTests
{
    private static KeyValuePair<string, TimeSpan?> Wants(string id, double? seconds = null)
    {
        return new KeyValuePair<string, TimeSpan?>(id, seconds is { } value ? TimeSpan.FromSeconds(value) : null);
    }

    private static string[] MetricIds(SimulatedAgent agent, int count)
    {
        string[] ids = [.. agent.ManifestSource.Current.Metrics.Select(metric => metric.Id).Take(count)];
        Assert.Equal(count, ids.Length);
        return ids;
    }

    private static string[] MetricsOf(JsonElement subscribe)
    {
        JsonElement metrics = subscribe.GetProperty("metrics");
        return metrics.ValueKind == JsonValueKind.String ? ["*"] : [.. metrics.EnumerateArray().Select(metric => metric.GetString()!)];
    }

    private static Dictionary<string, double> IntervalsOf(JsonElement subscribe)
    {
        return subscribe.TryGetProperty("intervals", out JsonElement intervals)
            ? intervals.EnumerateObject().ToDictionary(member => member.Name, member => member.Value.GetDouble(), StringComparer.Ordinal)
            : new Dictionary<string, double>(StringComparer.Ordinal);
    }

    private static int SubscribeCount(SimulatedAgent agent) => agent.Link.ReceivedOf(MessageTypes.Subscribe).Length;

    private static async Task UntilSubscribeCountAsync(SimulatedAgent agent, int count)
    {
        await TestWait.UntilAsync(() => SubscribeCount(agent) >= count, "the agent to receive " + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " subscribe request(s)");
    }

    /// <summary>Takes the computer to sleep without the agent being able to dial the hub again, so that the hub sees an agent that is offline.</summary>
    private static async Task SleepWithoutReturnAsync(SimulatedAgent agent)
    {
        agent.Link.Online = false;
        await Task.Run(() => agent.Platform.Power.Simulate(PowerEventKind.Sleep));
    }

    private static async Task WakeAsync(SimulatedAgent agent)
    {
        agent.Link.Online = true;
        await Task.Run(() => agent.Platform.Power.Simulate(PowerEventKind.Resume));
    }

    private static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    // ------------------------------------------------------------------
    // The union subscription
    // ------------------------------------------------------------------

    [Fact]
    public async Task Two_consumers_with_different_metrics_and_intervals_make_the_agent_receive_one_subscribe_with_the_union()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        SimulatedAgent agent = await rig.CreateAgentAsync();
        string[] metrics = MetricIds(agent, 3);

        // Both consumers are there before the agent connects: the gateway holds their demands and sends the union once, after the handshake.
        using IMetricSubscription store = rig.Gateway.SubscribeMetrics(agent.Id, MetricDemand.Of([Wants(metrics[0], 60), Wants(metrics[1])]));
        using IMetricSubscription view = rig.Gateway.SubscribeMetrics(agent.Id, MetricDemand.Of([Wants(metrics[0], 10), Wants(metrics[1], 5), Wants(metrics[2], 30)]));
        await rig.JoinAsync(agent);
        await rig.WaitOnlineAsync(agent);
        await UntilSubscribeCountAsync(agent, 1);
        await Task.Delay(300);

        JsonElement subscribe = Assert.Single(agent.Link.ReceivedOf(MessageTypes.Subscribe));
        Assert.Equal(metrics.Order(StringComparer.Ordinal), MetricsOf(subscribe));

        // The first metric: the shorter of 60 and 10 seconds. The second is live for the first consumer, so it has no interval. The third: 30 seconds.
        Dictionary<string, double> intervals = IntervalsOf(subscribe);
        Assert.Equal(2, intervals.Count);
        Assert.Equal(10, intervals[metrics[0]]);
        Assert.Equal(30, intervals[metrics[2]]);
        Assert.False(intervals.ContainsKey(metrics[1]));

        // The agent answered with a snapshot, which is the live state of the hub.
        await TestWait.UntilAsync(() => rig.Gateway.GetState(agent.Id) is not null, "the live state of the agent");
        Assert.All(rig.Gateway.GetState(agent.Id)!.Values.Keys, id => Assert.Contains(id, metrics));
    }

    [Fact]
    public async Task When_the_last_consumer_of_a_metric_goes_away_the_next_subscribe_no_longer_contains_it()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        SimulatedAgent agent = await rig.AddAgentAsync();
        string[] metrics = MetricIds(agent, 3);
        string[] all = [.. metrics.Order(StringComparer.Ordinal)];
        string[] firstTwo = [.. metrics.Take(2).Order(StringComparer.Ordinal)];

        using IMetricSubscription store = rig.Gateway.SubscribeMetrics(agent.Id, MetricDemand.Of([Wants(metrics[0], 60), Wants(metrics[1])]));
        using IMetricSubscription view = rig.Gateway.SubscribeMetrics(agent.Id, MetricDemand.Of([Wants(metrics[0], 10), Wants(metrics[2], 30)]));
        await TestWait.UntilAsync(
            () => SubscribeCount(agent) > 0 && MetricsOf(agent.Link.ReceivedOf(MessageTypes.Subscribe)[^1]).SequenceEqual(all),
            "the agent to be subscribed to the union of both consumers");
        Assert.Equal(10, IntervalsOf(agent.Link.ReceivedOf(MessageTypes.Subscribe)[^1])[metrics[0]]);
        await TestWait.UntilAsync(() => rig.Gateway.GetState(agent.Id) is not null, "the live state of the agent");

        // The view goes away: the third metric has no consumer any more, and the first is wanted every 60 seconds only.
        int before = SubscribeCount(agent);
        view.Dispose();
        await UntilSubscribeCountAsync(agent, before + 1);
        JsonElement next = agent.Link.ReceivedOf(MessageTypes.Subscribe)[^1];
        Assert.Equal(firstTwo, MetricsOf(next));
        Assert.DoesNotContain(metrics[2], MetricsOf(next));
        Assert.Equal(60, IntervalsOf(next)[metrics[0]]);
        Assert.False(IntervalsOf(next).ContainsKey(metrics[1]));
        await Task.Delay(200);
        Assert.Equal(before + 1, SubscribeCount(agent));

        // The last consumer goes away: the subscription is empty and the live state is gone.
        store.Dispose();
        await UntilSubscribeCountAsync(agent, before + 2);
        Assert.Empty(MetricsOf(agent.Link.ReceivedOf(MessageTypes.Subscribe)[^1]));
        await TestWait.UntilAsync(() => rig.Gateway.GetState(agent.Id) is null, "the live state to be cleared");
    }

    [Fact]
    public async Task A_consumer_that_wants_every_metric_makes_the_union_the_wildcard()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        SimulatedAgent agent = await rig.AddAgentAsync();
        string[] metrics = MetricIds(agent, 2);

        using IMetricSubscription some = rig.Gateway.SubscribeMetrics(agent.Id, MetricDemand.Of([Wants(metrics[0], 60)]));
        using IMetricSubscription everything = rig.Gateway.SubscribeMetrics(agent.Id, MetricDemand.Everything);

        await TestWait.UntilAsync(
            () => SubscribeCount(agent) > 0 && MetricsOf(agent.Link.ReceivedOf(MessageTypes.Subscribe)[^1]).SequenceEqual(["*"]),
            "the agent to be subscribed to every metric");
        Assert.Empty(IntervalsOf(agent.Link.ReceivedOf(MessageTypes.Subscribe)[^1]));
        await TestWait.UntilAsync(() => rig.Gateway.GetState(agent.Id) is { Values.Count: > 1 }, "a snapshot of all metrics");
    }

    [Fact]
    public async Task A_consumer_can_change_its_demand_and_the_agent_follows()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        SimulatedAgent agent = await rig.AddAgentAsync();
        string[] metrics = MetricIds(agent, 2);

        using IMetricSubscription subscription = rig.Gateway.SubscribeMetrics(agent.Id, MetricDemand.Live([metrics[0]]));
        await UntilSubscribeCountAsync(agent, 1);
        subscription.Update(MetricDemand.Of([Wants(metrics[1], 15)]));

        await TestWait.UntilAsync(
            () => MetricsOf(agent.Link.ReceivedOf(MessageTypes.Subscribe)[^1]).SequenceEqual([metrics[1]]),
            "the agent to follow the new demand");
        Assert.Equal(15, IntervalsOf(agent.Link.ReceivedOf(MessageTypes.Subscribe)[^1])[metrics[1]]);

        // A demand of a subscription that was disposed does nothing.
        subscription.Dispose();
        subscription.Update(MetricDemand.Live([metrics[0]]));
        int count = SubscribeCount(agent);
        await UntilSubscribeCountAsync(agent, count);
        await Task.Delay(200);
        Assert.Empty(MetricsOf(agent.Link.ReceivedOf(MessageTypes.Subscribe)[^1]));
    }

    [Fact]
    public async Task A_reconnecting_agent_receives_the_union_again_because_a_new_connection_has_no_subscription()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        SimulatedAgent agent = await rig.AddAgentAsync();
        string[] metrics = MetricIds(agent, 2);
        using IMetricSubscription subscription = rig.Gateway.SubscribeMetrics(agent.Id, MetricDemand.Live(metrics));
        await UntilSubscribeCountAsync(agent, 1);

        int dials = agent.Link.Dials;
        await Task.Run(() => agent.Platform.Power.Simulate(PowerEventKind.Sleep));

        await TestWait.UntilAsync(() => agent.Link.Dials > dials && rig.Gateway.GetPresence(agent.Id).Online, "the agent to dial again");
        await UntilSubscribeCountAsync(agent, 2);
        Assert.Equal(metrics.Order(StringComparer.Ordinal), MetricsOf(agent.Link.ReceivedOf(MessageTypes.Subscribe)[^1]));
        await TestWait.UntilAsync(() => rig.Gateway.GetState(agent.Id) is not null, "the live state after the reconnect");
    }

    [Fact]
    public async Task The_consumers_of_one_agent_do_not_change_what_another_agent_receives()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        SimulatedAgent first = await rig.AddAgentAsync();
        SimulatedAgent second = await rig.AddAgentAsync();
        string[] metrics = MetricIds(first, 1);

        using IMetricSubscription subscription = rig.Gateway.SubscribeMetrics(first.Id, MetricDemand.Live(metrics));

        await UntilSubscribeCountAsync(first, 1);
        await Task.Delay(200);
        Assert.Empty(second.Link.ReceivedOf(MessageTypes.Subscribe));
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, rig.Gateway.GetAllPresence().Count(presence => presence.Online));
    }

    [Fact]
    public async Task Too_many_subscriptions_or_metrics_are_refused()
    {
        var options = new GatewayOptions { MaxSubscriptionsPerAgent = 2, MaxMetricsPerDemand = 3, HandshakeTimeout = TimeSpan.FromMilliseconds(500) };
        await using GatewayRig rig = await GatewayRig.StartAsync(options);

        using IMetricSubscription first = rig.Gateway.SubscribeMetrics("agent-a", MetricDemand.Live(["a", "b"]));
        Assert.Throws<InvalidOperationException>(() => rig.Gateway.SubscribeMetrics("agent-a", MetricDemand.Live(["c", "d"])));
        using IMetricSubscription second = rig.Gateway.SubscribeMetrics("agent-a", MetricDemand.Live(["b", "c"]));
        Assert.Throws<InvalidOperationException>(() => rig.Gateway.SubscribeMetrics("agent-a", MetricDemand.Live(["a"])));
        Assert.Throws<InvalidOperationException>(() => first.Update(MetricDemand.Live(["x", "y", "z"])));
    }

    // ------------------------------------------------------------------
    // Presence, manifest, state, events
    // ------------------------------------------------------------------

    [Fact]
    public async Task Presence_follows_the_connection_and_the_power_state_comes_from_the_bye_reason()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        var observer = new RecordingObserver();
        using IDisposable observation = rig.Gateway.Observe(observer);
        SimulatedAgent agent = await rig.AddAgentAsync();

        AgentPresence online = rig.Gateway.GetPresence(agent.Id);
        Assert.True(online.Online);
        Assert.NotNull(online.ConnectedAt);
        Assert.NotNull(online.LastSeenAt);
        Assert.Equal(PowerStates.Unknown, online.PowerState);
        Assert.Equal(agent.ManifestSource.Current.Rev, online.ManifestRev);
        Assert.False(string.IsNullOrEmpty(online.BootId));
        Assert.Equal(agent.Id, online.Agent!.Id);
        Assert.Contains(observer.Presence, presence => presence.AgentId == agent.Id && presence.Online);

        await SleepWithoutReturnAsync(agent);

        await TestWait.UntilAsync(() => !rig.Gateway.GetPresence(agent.Id).Online, "the agent to be offline");
        AgentPresence offline = rig.Gateway.GetPresence(agent.Id);
        Assert.Equal(ByeReasons.Sleep, offline.PowerState);
        Assert.Null(offline.ConnectedAt);
        Assert.NotNull(offline.LastSeenAt);
        Assert.Contains(observer.Presence, presence => !presence.Online && presence.PowerState == ByeReasons.Sleep);
        Assert.NotNull((await rig.Hub.Service<AgentRegistry>().FindAsync(agent.Id, CancellationToken.None))!.LastSeenAt);

        // The cached manifest stays while the agent is away.
        Assert.NotNull(rig.Gateway.GetManifest(agent.Id));

        await WakeAsync(agent);

        await TestWait.UntilAsync(() => rig.Gateway.GetPresence(agent.Id).Online, "the agent to be back");
        Assert.Equal(PowerStates.Unknown, rig.Gateway.GetPresence(agent.Id).PowerState);
    }

    [Fact]
    public async Task An_agent_that_never_connected_is_offline_with_an_unknown_power_state()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();

        AgentPresence presence = rig.Gateway.GetPresence("nobody");

        Assert.False(presence.Online);
        Assert.Equal(PowerStates.Unknown, presence.PowerState);
        Assert.Null(presence.ConnectedAt);
        Assert.Null(rig.Gateway.GetManifest("nobody"));
        Assert.Null(rig.Gateway.GetState("nobody"));
        Assert.Empty(rig.Gateway.GetAllPresence());
    }

    [Fact]
    public async Task The_manifest_is_cached_by_revision_and_fetched_again_only_when_the_revision_changes()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        var observer = new RecordingObserver();
        using IDisposable observation = rig.Gateway.Observe(observer);
        SimulatedAgent agent = await rig.AddAgentAsync();
        string first = rig.Gateway.GetManifest(agent.Id)!.Rev;
        Assert.Equal(agent.ManifestSource.Current.Rev, first);
        Assert.Single(agent.Link.ReceivedOf(MessageTypes.GetManifest));
        Assert.Contains(
            rig.Gateway.GetManifest(agent.Id)!.Manifest.GetProperty("commands").EnumerateArray(),
            command => command.GetProperty("id").GetString() == SimulatedAgent.CommandId);

        // The same revision after a reconnect is a cache hit: no second get_manifest.
        int dials = agent.Link.Dials;
        await Task.Run(() => agent.Platform.Power.Simulate(PowerEventKind.Sleep));
        await TestWait.UntilAsync(() => agent.Link.Dials > dials && rig.Gateway.GetPresence(agent.Id).Online, "the agent to dial again");
        await Task.Delay(200);
        Assert.Single(agent.Link.ReceivedOf(MessageTypes.GetManifest));

        // A new command changes the revision: the agent says so with manifest_changed and the hub fetches the new manifest.
        string second = await agent.AddCommandAsync("another_command");
        Assert.NotEqual(first, second);
        await TestWait.UntilAsync(() => rig.Gateway.GetManifest(agent.Id)!.Rev == second, "the new manifest to be cached");
        await TestWait.UntilAsync(() => observer.Manifests.Count == 2, "the observer to be told of the new manifest");
        Assert.Equal(2, agent.Link.ReceivedOf(MessageTypes.GetManifest).Length);
        Assert.Contains(
            rig.Gateway.GetManifest(agent.Id)!.Manifest.GetProperty("commands").EnumerateArray(),
            command => command.GetProperty("id").GetString() == "another_command");
        Assert.Equal([first, second], observer.Manifests.Select(entry => entry.Manifest.Rev));
        Assert.Equal(second, rig.Gateway.GetPresence(agent.Id).ManifestRev);
    }

    [Fact]
    public async Task Events_of_an_agent_are_stored_newest_first_and_announced_to_observers_and_the_oldest_are_dropped()
    {
        var options = new GatewayOptions { MaxEventsPerAgent = 3, HandshakeTimeout = TimeSpan.FromMilliseconds(500), CloseTimeout = TimeSpan.FromMilliseconds(500) };
        await using GatewayRig rig = await GatewayRig.StartAsync(options);
        var observer = new RecordingObserver();
        using IDisposable observation = rig.Gateway.Observe(observer);
        SimulatedAgent agent = await rig.AddAgentAsync();

        for (int number = 1; number <= 5; number++)
        {
            _ = await agent.Events.PublishAsync(EventTypes.Custom, EventData.Create(("name", "bell-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        await TestWait.UntilAsync(() => observer.Events.Count >= 5, "five events to arrive at the hub");
        IReadOnlyList<StoredAgentEvent> stored = await rig.Gateway.GetEventsAsync(agent.Id, 10, CancellationToken.None);

        Assert.Equal(3, stored.Count);
        Assert.All(stored, entry => Assert.Equal(EventTypes.Custom, entry.Type));
        Assert.Equal(["bell-5", "bell-4", "bell-3"], stored.Select(entry => entry.Data!.Value.GetProperty("name").GetString()));
        Assert.Equal(5, observer.Events.Count);
        Assert.Single((await rig.Gateway.GetEventsAsync(agent.Id, 1, CancellationToken.None)));
        Assert.Empty(await rig.Gateway.GetEventsAsync(agent.Id, 0, CancellationToken.None));
    }

    [Fact]
    public async Task State_changes_of_subscribed_metrics_reach_the_cache_and_the_observers()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        var observer = new RecordingObserver();
        using IDisposable observation = rig.Gateway.Observe(observer);
        SimulatedAgent agent = await rig.AddAgentAsync();

        using IMetricSubscription subscription = rig.Gateway.SubscribeMetrics(agent.Id, MetricDemand.Everything);

        await TestWait.UntilAsync(() => observer.States.Any(update => update.IsSnapshot), "a snapshot");
        AgentStateUpdate snapshot = observer.States.First(update => update.IsSnapshot);
        Assert.Equal(agent.Id, snapshot.AgentId);
        Assert.NotEmpty(snapshot.Values);
        AgentStateSnapshot cached = rig.Gateway.GetState(agent.Id)!;
        Assert.True(cached.Values.Count >= snapshot.Values.Count);
        Assert.True(cached.Ts >= snapshot.Ts);
    }

    [Fact]
    public async Task An_observer_that_fails_does_not_end_the_connection_of_the_agent()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        var failing = new RecordingObserver { ThrowOnPresence = true };
        var healthy = new RecordingObserver();
        using IDisposable first = rig.Gateway.Observe(failing);
        using IDisposable second = rig.Gateway.Observe(healthy);

        SimulatedAgent agent = await rig.AddAgentAsync();

        Assert.True(rig.Gateway.GetPresence(agent.Id).Online);
        Assert.Contains(healthy.Presence, presence => presence.Online);
        Assert.Contains(rig.Hub.Logs.Messages, message => message.Contains("observer of the agent gateway failed", StringComparison.Ordinal));
        AgentRunOutcome outcome = await rig.Gateway.RunAsync(new AgentRunRequest(agent.Id, SimulatedAgent.CommandId, new Dictionary<string, JsonElement> { ["Level"] = Json("2") }), CancellationToken.None);
        Assert.True(outcome.Ok, outcome.ErrorMessage);
    }

    [Fact]
    public async Task An_observer_that_was_disposed_is_not_notified_any_more()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        var observer = new RecordingObserver();
        IDisposable observation = rig.Gateway.Observe(observer);
        observation.Dispose();
        observation.Dispose();

        _ = await rig.AddAgentAsync();

        Assert.Empty(observer.Presence);
    }

    // ------------------------------------------------------------------
    // Running commands
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_command_runs_on_the_agent_with_its_variables_bound_as_separate_arguments()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        SimulatedAgent agent = await rig.AddAgentAsync();

        AgentRunOutcome outcome = await rig.Gateway.RunAsync(
            new AgentRunRequest(agent.Id, SimulatedAgent.CommandId, new Dictionary<string, JsonElement> { ["Level"] = Json("3") }, Wait: true, UserId: "olivia"),
            CancellationToken.None);

        Assert.True(outcome.Ok, outcome.ErrorMessage);
        Assert.Equal(0, outcome.Result!.ExitCode);
        Assert.False(string.IsNullOrEmpty(outcome.Result.RunId));
        Assert.Equal(["--level", "3"], Assert.Single(agent.Launcher.Launches).Arguments.ToArray());

        // The run request names the hub user as the user of the run.
        JsonElement run = Assert.Single(agent.Link.Received, frame => HubTestLink.TypeOf(frame) == MessageTypes.Run);
        Assert.Equal("olivia", run.GetProperty("context").GetProperty("user_id").GetString());
        Assert.Equal(SimulatedAgent.CommandId, run.GetProperty("command").GetString());
    }

    [Fact]
    public async Task A_run_that_does_not_wait_returns_when_the_agent_has_started_it()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        SimulatedAgent agent = await rig.AddAgentAsync();

        AgentRunOutcome outcome = await rig.Gateway.RunAsync(
            new AgentRunRequest(agent.Id, SimulatedAgent.CommandId, new Dictionary<string, JsonElement> { ["Level"] = Json("1") }, Wait: false),
            CancellationToken.None);

        Assert.True(outcome.Ok, outcome.ErrorMessage);
        Assert.False(string.IsNullOrEmpty(outcome.Result!.RunId));
        Assert.Null(outcome.Result.ExitCode);
        Assert.False(Assert.Single(agent.Link.Received, frame => HubTestLink.TypeOf(frame) == MessageTypes.Run).GetProperty("wait").GetBoolean());
    }

    [Fact]
    public async Task The_errors_of_the_agent_and_of_the_gateway_come_back_as_codes()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        SimulatedAgent agent = await rig.AddAgentAsync();

        AgentRunOutcome unknown = await rig.Gateway.RunAsync(new AgentRunRequest(agent.Id, "no_such_command"), CancellationToken.None);
        AgentRunOutcome invalid = await rig.Gateway.RunAsync(
            new AgentRunRequest(agent.Id, SimulatedAgent.CommandId, new Dictionary<string, JsonElement> { ["Level"] = Json("9") }),
            CancellationToken.None);
        AgentRunOutcome nobody = await rig.Gateway.RunAsync(new AgentRunRequest("nobody", SimulatedAgent.CommandId), CancellationToken.None);

        Assert.False(unknown.Ok);
        Assert.Equal(ErrorCodes.UnknownCommand, unknown.ErrorCode);
        Assert.Equal(ErrorCodes.InvalidVars, invalid.ErrorCode);
        Assert.Equal(HubErrorCodes.AgentOffline, nobody.ErrorCode);
        Assert.Null(nobody.Result);
        Assert.NotEmpty(unknown.ErrorMessage!);
    }

    [Fact]
    public async Task A_run_on_an_agent_that_went_away_fails_with_agent_offline()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        SimulatedAgent agent = await rig.AddAgentAsync();
        await SleepWithoutReturnAsync(agent);
        await TestWait.UntilAsync(() => !rig.Gateway.GetPresence(agent.Id).Online, "the agent to be offline");

        AgentRunOutcome outcome = await rig.Gateway.RunAsync(
            new AgentRunRequest(agent.Id, SimulatedAgent.CommandId, new Dictionary<string, JsonElement> { ["Level"] = Json("2") }),
            CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal(HubErrorCodes.AgentOffline, outcome.ErrorCode);
    }

    [Fact]
    public async Task A_run_that_is_waiting_when_the_agent_disconnects_fails_with_agent_offline()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        SimulatedAgent agent = await rig.AddAgentAsync();
        agent.Launcher.Hold();

        Task<AgentRunOutcome> running = rig.Gateway.RunAsync(
            new AgentRunRequest(agent.Id, SimulatedAgent.CommandId, new Dictionary<string, JsonElement> { ["Level"] = Json("2") }),
            CancellationToken.None);
        await TestWait.UntilAsync(() => !agent.Launcher.Launches.IsEmpty, "the command to start");
        await SleepWithoutReturnAsync(agent);
        agent.Launcher.Release();

        AgentRunOutcome outcome = await running.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(HubErrorCodes.AgentOffline, outcome.ErrorCode);
    }

    // ------------------------------------------------------------------
    // Replacement of a session, rotation, removal
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_new_connection_of_the_same_agent_replaces_the_old_one_without_the_agent_going_offline()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        var observer = new RecordingObserver();
        using IDisposable observation = rig.Gateway.Observe(observer);
        SimulatedAgent agent = await rig.AddAgentAsync();
        AgentRecord record = (await rig.Hub.Service<AgentRegistry>().FindAsync(agent.Id, CancellationToken.None))!;
        string rev = rig.Gateway.GetManifest(agent.Id)!.Rev;
        int dials = agent.Link.Dials;

        // Somebody who holds the credential opens a second link for the agent.
        var second = new ScriptedAgentSocket();
        Task serving = rig.Gateway.ServeAsync(second, record, CancellationToken.None);
        second.SendText(ScriptedAgent.Hello(agent.Id, rev));
        _ = await second.NextFrameOfAsync(MessageTypes.ClientHello);

        // The real agent lost its link and dials again; that connection replaces the scripted one.
        await TestWait.UntilAsync(() => agent.Link.Dials > dials, "the real agent to dial again");
        Assert.Equal((int)System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, await second.Closed.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains("replaced", second.CloseStatusDescription, StringComparison.OrdinalIgnoreCase);
        await serving.WaitAsync(TimeSpan.FromSeconds(15));

        // One session per agent at any time, and the agent was never reported offline in between.
        await TestWait.UntilAsync(() => rig.Gateway.GetPresence(agent.Id).Online, "the agent to be online");
        Assert.DoesNotContain(observer.Presence, presence => !presence.Online);
        Assert.Contains("opened a new connection", string.Join('\n', rig.Hub.Logs.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Credential_rotate_reaches_the_connected_agent_and_the_new_credential_is_the_one_that_works()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        SimulatedAgent agent = await rig.AddAgentAsync();
        string old = agent.Credential!;

        using HttpResponseMessage response = await rig.Hub.Admin.PostAsync("/api/v1/agents/" + agent.Id + "/rotate-credential");

        await HubAssert.JsonAsync(response);
        string rotated = agent.Credential!;
        Assert.NotEqual(old, rotated);
        Assert.Single(agent.Link.ReceivedOf(HubMessageTypes.CredentialRotate));
        Assert.True(rig.Gateway.GetPresence(agent.Id).Online);

        // The link drops and the agent dials again with the new credential; the old one is revoked by now, so this only works with the new one.
        int dials = agent.Link.Dials;
        await Task.Run(() => agent.Platform.Power.Simulate(PowerEventKind.Sleep));
        await TestWait.UntilAsync(() => agent.Link.Dials > dials && rig.Gateway.GetPresence(agent.Id).Online, "the agent to be back with the new credential");
        Assert.Equal(HubLinkStates.Connected, (await agent.Services.GetRequiredService<HubLinkService>().GetStatusAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task Credential_rotate_to_an_agent_that_is_not_connected_is_refused_and_the_old_credential_stays()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        SimulatedAgent agent = await rig.AddAgentAsync();
        string old = agent.Credential!;
        await SleepWithoutReturnAsync(agent);
        await TestWait.UntilAsync(() => !rig.Gateway.GetPresence(agent.Id).Online, "the agent to be offline");

        using HttpResponseMessage response = await rig.Hub.Admin.PostAsync("/api/v1/agents/" + agent.Id + "/rotate-credential");

        await HubAssert.ProblemAsync(response, System.Net.HttpStatusCode.Conflict, HubErrorCodes.AgentOffline);
        Assert.Equal(old, agent.Credential);
    }

    [Fact]
    public async Task Removing_a_connected_agent_tells_it_to_leave_and_closes_its_link()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        var observer = new RecordingObserver();
        using IDisposable observation = rig.Gateway.Observe(observer);
        SimulatedAgent agent = await rig.AddAgentAsync();
        string id = agent.Id;

        using HttpResponseMessage response = await rig.Hub.Admin.DeleteAsync("/api/v1/agents/" + id);

        HubAssert.Status(response, System.Net.HttpStatusCode.NoContent);
        await TestWait.UntilAsync(() => agent.Manager.GetStateAsync(CancellationToken.None).GetAwaiter().GetResult() is null, "the agent to delete its hub state");
        JsonElement unenroll = Assert.Single(agent.Link.ReceivedOf(HubMessageTypes.Unenroll));
        Assert.Equal("removed", unenroll.GetProperty("reason").GetString());
        Assert.Equal(id, Assert.Single(observer.Removed));
        Assert.False(rig.Gateway.GetPresence(id).Online);
        Assert.Null(rig.Gateway.GetManifest(id));
        Assert.Empty(rig.Gateway.GetAllPresence());

        // The agent does not come back: it has no credential any more.
        int dials = agent.Link.Dials;
        await Task.Delay(300);
        Assert.Equal(dials, agent.Link.Dials);
        Assert.Equal(HubLinkStates.NotJoined, (await agent.Services.GetRequiredService<HubLinkService>().GetStatusAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task An_agent_that_was_removed_while_it_was_away_is_told_that_its_credential_is_revoked_when_it_comes_back()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        SimulatedAgent agent = await rig.AddAgentAsync();
        await SleepWithoutReturnAsync(agent);
        await TestWait.UntilAsync(() => !rig.Gateway.GetPresence(agent.Id).Online, "the agent to be offline");

        using (HttpResponseMessage response = await rig.Hub.Admin.DeleteAsync("/api/v1/agents/" + agent.Id))
        {
            HubAssert.Status(response, System.Net.HttpStatusCode.NoContent);
        }

        await WakeAsync(agent);

        // The hub answers the upgrade with 401 and the code revoked; the agent deletes its credential and stops.
        await TestWait.UntilAsync(
            () => agent.Services.GetRequiredService<HubLinkService>().GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult().State == HubLinkStates.Revoked,
            "the agent to learn that its credential is revoked",
            15000);
        HubStoredState state = (await agent.Manager.GetStateAsync(CancellationToken.None))!;
        Assert.Null(state.Credential);
        Assert.False(rig.Gateway.GetPresence(agent.Id).Online);
        int dials = agent.Link.Dials;
        await Task.Delay(300);
        Assert.Equal(dials, agent.Link.Dials);
    }

    [Fact]
    public async Task The_gateway_closes_the_links_with_going_away_when_the_hub_stops()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync();
        var script = new ScriptedAgentSocket();
        Task serving = rig.Gateway.ServeAsync(script, ScriptedAgent.Record("stopping-agent"), CancellationToken.None);
        script.SendText(ScriptedAgent.Hello("stopping-agent", "aaaaaaaaaaaa"));
        _ = await script.NextFrameOfAsync(MessageTypes.ClientHello);

        rig.Gateway.BeginStop();

        Assert.True(rig.Gateway.IsStopping);
        Assert.Equal((int)System.Net.WebSockets.WebSocketCloseStatus.EndpointUnavailable, await script.Closed.WaitAsync(TimeSpan.FromSeconds(15)));
        await serving.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
