using System.Net.WebSockets;
using System.Text.Json;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Gateway;

namespace QoffeeAutomat.Hub.Tests.Gateway;

/// <summary>
/// The agent link as the hub serves it (T906, PLAN.md S07, S20), with an agent that the test plays by hand: the handshake, the caches, and what the hub does with an
/// agent that misbehaves. Every rule that closes a connection has a test with the close code that the agent sees.
/// </summary>
[Trait("Category", "Gateway")]
public sealed class AgentLinkProtocolTests
{
    private const string AgentId = "scripted-agent";
    private const string FirstRev = "aaaaaaaaaaaa";
    private const string SecondRev = "bbbbbbbbbbbb";

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private static GatewayOptions Options(
        int maxFrameBytes = 1024 * 1024,
        int maxFramesPerSecond = 200,
        int maxUnsolicitedResults = 16,
        int maxStateMetrics = 5000,
        int sendQueueCapacity = 128,
        TimeSpan? sendTimeout = null,
        IReadOnlyList<string>? features = null)
    {
        return new GatewayOptions
        {
            HandshakeTimeout = TimeSpan.FromMilliseconds(500),
            CloseTimeout = TimeSpan.FromMilliseconds(300),
            SendTimeout = sendTimeout ?? TimeSpan.FromSeconds(10),
            RequestTimeout = TimeSpan.FromSeconds(10),
            MaxFrameBytes = maxFrameBytes,
            MaxFramesPerSecond = maxFramesPerSecond,
            MaxUnsolicitedResults = maxUnsolicitedResults,
            MaxStateMetrics = maxStateMetrics,
            SendQueueCapacity = sendQueueCapacity,
            Features = features ?? [],
        };
    }

    private static Task<GatewayRig> StartAsync(GatewayOptions? options = null) => GatewayRig.StartAsync(options ?? Options(), withUsers: false);

    /// <summary>Starts to serve a scripted agent; nothing has been sent by the agent yet.</summary>
    private static (ScriptedAgentSocket Socket, Task Serving) Serve(GatewayRig rig, string id = AgentId, bool stall = false)
    {
        var socket = new ScriptedAgentSocket { StallSends = stall };
        return (socket, rig.Gateway.ServeAsync(socket, ScriptedAgent.Record(id), CancellationToken.None));
    }

    /// <summary>Serves a scripted agent, sends its <c>hello</c> and waits for the <c>client_hello</c> of the hub.</summary>
    private static async Task<(ScriptedAgentSocket Socket, Task Serving)> ConnectAsync(GatewayRig rig, string id = AgentId, string rev = FirstRev, params string[] features)
    {
        (ScriptedAgentSocket socket, Task serving) = Serve(rig, id);
        socket.SendText(ScriptedAgent.Hello(id, rev, 1, 1, features));
        _ = await socket.NextFrameOfAsync(MessageTypes.ClientHello);
        return (socket, serving);
    }

    /// <summary>Answers the <c>get_manifest</c> request that the hub makes for a revision it does not know.</summary>
    private static async Task AnswerManifestAsync(ScriptedAgentSocket socket, string rev = FirstRev, params string[] metrics)
    {
        JsonElement request = await socket.NextFrameOfAsync(MessageTypes.GetManifest);
        socket.SendText(ScriptedAgent.Ok(ScriptedAgent.IdOf(request), ScriptedAgent.ManifestOf(AgentId, rev, metrics)));
    }

    private static async Task AssertClosedWithAsync(ScriptedAgentSocket socket, Task serving, int expected)
    {
        Assert.Equal(expected, await socket.Closed.WaitAsync(Limit));
        await serving.WaitAsync(Limit);
    }

    // ------------------------------------------------------------------
    // Handshake
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_hub_answers_hello_with_client_hello_as_a_client_of_the_kind_hub()
    {
        await using GatewayRig rig = await StartAsync();
        (ScriptedAgentSocket socket, Task serving) = Serve(rig);

        socket.SendText(ScriptedAgent.Hello(AgentId, FirstRev));
        JsonElement hello = await socket.NextFrameOfAsync(MessageTypes.ClientHello);

        Assert.Equal(1, hello.GetProperty("protocol").GetInt32());
        Assert.Equal("hub", hello.GetProperty("client").GetProperty("kind").GetString());
        Assert.False(string.IsNullOrEmpty(hello.GetProperty("client").GetProperty("name").GetString()));
        Assert.False(string.IsNullOrEmpty(hello.GetProperty("client").GetProperty("version").GetString()));
        Assert.False(hello.TryGetProperty("features", out _));
        await TestWait.UntilAsync(() => rig.Gateway.GetPresence(AgentId).Online, "the agent to be online");
        socket.SendClose();
        await serving.WaitAsync(Limit);
    }

    [Fact]
    public async Task Features_are_negotiated_and_only_those_that_both_sides_announced_are_in_use()
    {
        GatewayOptions options = Options(features: [HubFeatures.ConfigSync, HubFeatures.MgmtTunnel]);
        await using GatewayRig rig = await StartAsync(options);
        (ScriptedAgentSocket socket, Task serving) = Serve(rig);

        socket.SendText(ScriptedAgent.Hello(AgentId, FirstRev, 1, 1, HubFeatures.MgmtTunnel, "from_the_future"));
        JsonElement hello = await socket.NextFrameOfAsync(MessageTypes.ClientHello);

        // The hub announces everything it supports; the intersection is what the presence reports.
        Assert.Equal(
            [HubFeatures.ConfigSync, HubFeatures.MgmtTunnel],
            hello.GetProperty("features").EnumerateArray().Select(feature => feature.GetString()));
        await TestWait.UntilAsync(() => rig.Gateway.GetPresence(AgentId).Online, "the agent to be online");
        Assert.Equal([HubFeatures.MgmtTunnel], rig.Gateway.GetPresence(AgentId).Features);
        socket.SendClose();
        await serving.WaitAsync(Limit);
    }

    [Fact]
    public async Task An_agent_that_does_not_speak_first_is_closed_with_4400()
    {
        await using GatewayRig rig = await StartAsync();
        (ScriptedAgentSocket socket, Task serving) = Serve(rig);

        await AssertClosedWithAsync(socket, serving, CloseCodes.BadRequest);

        Assert.False(rig.Gateway.GetPresence(AgentId).Online);
        Assert.Contains(rig.Hub.Logs.Messages, message => message.Contains("no hello in time", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("[]")]
    [InlineData("{\"x\":1}")]
    [InlineData("{\"t\":\"state\",\"ts\":1,\"v\":{}}")]
    [InlineData("{\"t\":\"hello\"}")]
    public async Task A_first_frame_that_is_not_a_valid_hello_is_closed_with_4400(string frame)
    {
        await using GatewayRig rig = await StartAsync();
        (ScriptedAgentSocket socket, Task serving) = Serve(rig);

        socket.SendText(frame);

        await AssertClosedWithAsync(socket, serving, CloseCodes.BadRequest);
        Assert.False(rig.Gateway.GetPresence(AgentId).Online);
    }

    [Fact]
    public async Task A_binary_first_frame_is_closed_with_4400()
    {
        await using GatewayRig rig = await StartAsync();
        (ScriptedAgentSocket socket, Task serving) = Serve(rig);

        socket.SendBinary([1, 2, 3]);

        await AssertClosedWithAsync(socket, serving, CloseCodes.BadRequest);
    }

    [Fact]
    public async Task A_hello_for_another_agent_than_the_credential_belongs_to_is_refused()
    {
        await using GatewayRig rig = await StartAsync();
        (ScriptedAgentSocket socket, Task serving) = Serve(rig, id: AgentId);

        socket.SendText(ScriptedAgent.Hello("somebody-else", FirstRev));

        await AssertClosedWithAsync(socket, serving, CloseCodes.BadRequest);
        Assert.False(rig.Gateway.GetPresence(AgentId).Online);
        Assert.False(rig.Gateway.GetPresence("somebody-else").Online);
        Assert.Empty(rig.Gateway.GetAllPresence());
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(0, 0)]
    public async Task A_protocol_range_without_version_1_is_closed_with_4406(int min, int max)
    {
        await using GatewayRig rig = await StartAsync();
        (ScriptedAgentSocket socket, Task serving) = Serve(rig);

        socket.SendText(ScriptedAgent.Hello(AgentId, FirstRev, min, max));

        await AssertClosedWithAsync(socket, serving, CloseCodes.UnsupportedVersion);
        Assert.False(rig.Gateway.GetPresence(AgentId).Online);
    }

    [Fact]
    public async Task A_range_that_includes_version_1_among_others_is_accepted()
    {
        await using GatewayRig rig = await StartAsync();
        (ScriptedAgentSocket socket, Task serving) = Serve(rig);

        socket.SendText(ScriptedAgent.Hello(AgentId, FirstRev, 1, 4));

        JsonElement hello = await socket.NextFrameOfAsync(MessageTypes.ClientHello);
        Assert.Equal(1, hello.GetProperty("protocol").GetInt32());
        socket.SendClose();
        await serving.WaitAsync(Limit);
    }

    // ------------------------------------------------------------------
    // An agent that misbehaves after the handshake
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("this is not json")]
    [InlineData("{\"t\":\"state\"}")]
    [InlineData("{\"t\":\"event\",\"type\":5}")]
    public async Task A_frame_that_is_not_valid_closes_the_connection_with_4400(string frame)
    {
        await using GatewayRig rig = await StartAsync();
        (ScriptedAgentSocket socket, Task serving) = await ConnectAsync(rig);

        socket.SendText(frame);

        await AssertClosedWithAsync(socket, serving, CloseCodes.BadRequest);
        Assert.False(rig.Gateway.GetPresence(AgentId).Online);
        Assert.Contains(rig.Hub.Logs.Messages, message => message.Contains("not valid QoffeeLink", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_binary_frame_closes_the_connection_with_4400()
    {
        await using GatewayRig rig = await StartAsync();
        (ScriptedAgentSocket socket, Task serving) = await ConnectAsync(rig);

        socket.SendBinary([1, 2, 3]);

        await AssertClosedWithAsync(socket, serving, CloseCodes.BadRequest);
    }

    [Fact]
    public async Task A_frame_that_is_too_large_closes_the_connection_with_1009()
    {
        await using GatewayRig rig = await StartAsync(Options(maxFrameBytes: 2048));
        (ScriptedAgentSocket socket, Task serving) = await ConnectAsync(rig);

        socket.SendText("{\"t\":\"event\",\"type\":\"big\",\"ts\":1,\"data\":{\"text\":\"" + new string('x', 5000) + "\"}}");

        await AssertClosedWithAsync(socket, serving, CloseCodes.MessageTooBig);
    }

    [Fact]
    public async Task An_agent_that_sends_too_many_frames_is_disconnected()
    {
        await using GatewayRig rig = await StartAsync(Options(maxFramesPerSecond: 10));
        (ScriptedAgentSocket socket, Task serving) = await ConnectAsync(rig);

        for (int count = 0; count < 40; count++)
        {
            socket.SendText("{\"t\":\"state\",\"ts\":1,\"v\":{}}");
        }

        await AssertClosedWithAsync(socket, serving, (int)WebSocketCloseStatus.PolicyViolation);
        Assert.Contains(rig.Hub.Logs.Messages, message => message.Contains("frames per second", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_agent_that_answers_requests_that_were_never_made_is_disconnected()
    {
        await using GatewayRig rig = await StartAsync(Options(maxUnsolicitedResults: 2));
        (ScriptedAgentSocket socket, Task serving) = await ConnectAsync(rig);

        for (long id = 900; id < 904; id++)
        {
            socket.SendText(ScriptedAgent.Ok(id));
        }

        await AssertClosedWithAsync(socket, serving, CloseCodes.BadRequest);
    }

    [Fact]
    public async Task An_agent_that_reports_more_metrics_than_the_hub_keeps_is_disconnected()
    {
        await using GatewayRig rig = await StartAsync(Options(maxStateMetrics: 3));
        (ScriptedAgentSocket socket, Task serving) = await ConnectAsync(rig);

        socket.SendText("{\"t\":\"state\",\"ts\":1,\"v\":{\"a\":1,\"b\":2,\"c\":3,\"d\":4}}");

        await AssertClosedWithAsync(socket, serving, (int)WebSocketCloseStatus.PolicyViolation);

        // The frame was refused before it reached the cache.
        Assert.Null(rig.Gateway.GetState(AgentId));
    }

    [Fact]
    public async Task An_agent_that_does_not_read_its_frames_is_disconnected_and_waiting_requests_fail()
    {
        GatewayOptions options = Options(sendQueueCapacity: 2, sendTimeout: TimeSpan.FromSeconds(1));
        await using GatewayRig rig = await StartAsync(options);
        (ScriptedAgentSocket socket, Task serving) = Serve(rig, stall: true);
        socket.SendText(ScriptedAgent.Hello(AgentId, FirstRev));
        await TestWait.UntilAsync(() => rig.Gateway.GetPresence(AgentId).Online, "the agent to be online");

        // The first frame, client_hello, is stuck in the socket; two more fit in the queue; the next one finds it full.
        Task<AgentRunOutcome>[] runs =
        [
            .. Enumerable.Range(0, 6).Select(_ => rig.Gateway.RunAsync(new AgentRunRequest(AgentId, "anything"), CancellationToken.None)),
        ];
        AgentRunOutcome[] outcomes = await Task.WhenAll(runs).WaitAsync(Limit);

        Assert.All(outcomes, outcome => Assert.Equal(HubErrorCodes.AgentOffline, outcome.ErrorCode));
        await serving.WaitAsync(Limit);
        Assert.False(rig.Gateway.GetPresence(AgentId).Online);
        Assert.Contains(rig.Hub.Logs.Messages, message => message.Contains("send queue is full", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_run_request_that_is_larger_than_a_frame_is_refused_without_closing_the_connection()
    {
        await using GatewayRig rig = await StartAsync(Options(maxFrameBytes: 2048));
        (ScriptedAgentSocket socket, Task serving) = await ConnectAsync(rig);
        var vars = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement(new string('x', 5000)) };

        AgentRunOutcome outcome = await rig.Gateway.RunAsync(new AgentRunRequest(AgentId, "command", vars), CancellationToken.None);

        Assert.Equal(ErrorCodes.BadRequest, outcome.ErrorCode);
        Assert.True(rig.Gateway.GetPresence(AgentId).Online);
        socket.SendClose();
        await serving.WaitAsync(Limit);
    }

    [Fact]
    public async Task A_request_that_the_agent_does_not_answer_fails_with_a_timeout_and_the_connection_stays()
    {
        GatewayOptions options = new()
        {
            HandshakeTimeout = TimeSpan.FromSeconds(5),
            CloseTimeout = TimeSpan.FromSeconds(5),
            RequestTimeout = TimeSpan.FromMilliseconds(300),
            RunTimeout = TimeSpan.FromMilliseconds(300),
        };
        await using GatewayRig rig = await StartAsync(options);
        (ScriptedAgentSocket socket, Task serving) = await ConnectAsync(rig);
        await TestWait.UntilAsync(() => rig.Gateway.GetPresence(AgentId).Online, "the agent to be online");

        AgentRunOutcome outcome = await rig.Gateway.RunAsync(new AgentRunRequest(AgentId, "command"), CancellationToken.None);

        Assert.Equal(ErrorCodes.Timeout, outcome.ErrorCode);
        Assert.True(rig.Gateway.GetPresence(AgentId).Online);
        socket.SendClose();
        await serving.WaitAsync(Limit);
    }

    [Fact]
    public async Task Too_many_requests_to_one_agent_fail_with_busy()
    {
        GatewayOptions options = new()
        {
            HandshakeTimeout = TimeSpan.FromMilliseconds(500),
            CloseTimeout = TimeSpan.FromMilliseconds(300),
            RequestTimeout = TimeSpan.FromSeconds(10),
            RunTimeout = TimeSpan.FromSeconds(10),
            MaxPendingRequests = 2,
        };
        await using GatewayRig rig = await StartAsync(options);
        (ScriptedAgentSocket socket, Task serving) = await ConnectAsync(rig);

        // The agent never answers; the third run finds two requests (the manifest fetch counts) in progress.
        Task<AgentRunOutcome> first = rig.Gateway.RunAsync(new AgentRunRequest(AgentId, "a"), CancellationToken.None);
        await TestWait.UntilAsync(() => socket.Pending >= 2, "the manifest request and the first run to be sent");
        AgentRunOutcome busy = await rig.Gateway.RunAsync(new AgentRunRequest(AgentId, "b"), CancellationToken.None);

        Assert.Equal(ErrorCodes.Busy, busy.ErrorCode);
        Assert.False(first.IsCompleted);
        socket.SendClose();
        await serving.WaitAsync(Limit);
        Assert.Equal(HubErrorCodes.AgentOffline, (await first.WaitAsync(Limit)).ErrorCode);
    }

    // ------------------------------------------------------------------
    // Closing
    // ------------------------------------------------------------------

    [Fact]
    public async Task When_the_agent_closes_the_connection_the_hub_answers_and_the_agent_is_offline()
    {
        await using GatewayRig rig = await StartAsync();
        var observer = new RecordingObserver();
        using IDisposable observation = rig.Gateway.Observe(observer);
        (ScriptedAgentSocket socket, Task serving) = await ConnectAsync(rig);
        await TestWait.UntilAsync(() => rig.Gateway.GetPresence(AgentId).Online, "the agent to be online");

        socket.SendClose();

        await AssertClosedWithAsync(socket, serving, CloseCodes.Normal);
        Assert.False(rig.Gateway.GetPresence(AgentId).Online);
        Assert.Equal(PowerStates.Unknown, rig.Gateway.GetPresence(AgentId).PowerState);
        Assert.Equal([true, false], observer.Presence.Select(presence => presence.Online));
    }

    [Theory]
    [InlineData(ByeReasons.Sleep, ByeReasons.Sleep)]
    [InlineData(ByeReasons.Hibernate, ByeReasons.Hibernate)]
    [InlineData(ByeReasons.Shutdown, ByeReasons.Shutdown)]
    [InlineData(ByeReasons.Restart, ByeReasons.Restart)]
    [InlineData(ByeReasons.ServiceStop, ByeReasons.ServiceStop)]
    [InlineData(ByeReasons.Update, ByeReasons.Update)]
    [InlineData("a reason that does not exist", PowerStates.Unknown)]
    public async Task The_power_state_is_the_reason_of_the_last_bye_or_unknown(string reason, string expected)
    {
        await using GatewayRig rig = await StartAsync();
        (ScriptedAgentSocket socket, Task serving) = await ConnectAsync(rig);

        socket.SendText(LinkCodec.Serialize(new ByeMessage(reason)));
        socket.SendClose(WebSocketCloseStatus.EndpointUnavailable);

        await serving.WaitAsync(Limit);
        AgentPresence presence = rig.Gateway.GetPresence(AgentId);
        Assert.False(presence.Online);
        Assert.Equal(expected, presence.PowerState);
    }

    // ------------------------------------------------------------------
    // Caches
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_manifest_that_cannot_be_fetched_or_is_not_valid_is_not_cached_and_the_link_stays_up()
    {
        await using GatewayRig rig = await StartAsync();
        (ScriptedAgentSocket socket, Task serving) = await ConnectAsync(rig);

        // The agent refuses to give its manifest.
        JsonElement first = await socket.NextFrameOfAsync(MessageTypes.GetManifest);
        socket.SendText(ScriptedAgent.Fail(ScriptedAgent.IdOf(first), ErrorCodes.Internal));
        await TestWait.UntilAsync(() => rig.Hub.Logs.Messages.Any(message => message.Contains("could not be fetched", StringComparison.Ordinal)), "the failure to be logged");
        Assert.Null(rig.Gateway.GetManifest(AgentId));

        // It announces another revision and answers with something that is not a manifest.
        socket.SendText(LinkCodec.Serialize(new ManifestChangedMessage(SecondRev)));
        JsonElement second = await socket.NextFrameOfAsync(MessageTypes.GetManifest);
        socket.SendText("{\"t\":\"result\",\"id\":" + ScriptedAgent.IdOf(second).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"ok\":true,\"data\":{\"nothing\":true}}");
        await Task.Delay(300);
        Assert.Null(rig.Gateway.GetManifest(AgentId));
        Assert.True(rig.Gateway.GetPresence(AgentId).Online);

        // A valid manifest at last.
        socket.SendText(LinkCodec.Serialize(new ManifestChangedMessage(SecondRev)));
        JsonElement third = await socket.NextFrameOfAsync(MessageTypes.GetManifest);
        socket.SendText(ScriptedAgent.Ok(ScriptedAgent.IdOf(third), ScriptedAgent.ManifestOf(AgentId, SecondRev, "m.a")));
        await TestWait.UntilAsync(() => rig.Gateway.GetManifest(AgentId)?.Rev == SecondRev, "the manifest to be cached");
        Assert.Equal(SecondRev, rig.Gateway.GetPresence(AgentId).ManifestRev);
        socket.SendClose();
        await serving.WaitAsync(Limit);
    }

    [Fact]
    public async Task A_revision_that_the_hub_has_cached_is_not_fetched_again_after_a_reconnect_or_a_repeated_announcement()
    {
        await using GatewayRig rig = await StartAsync();
        (ScriptedAgentSocket first, Task firstServing) = await ConnectAsync(rig);
        await AnswerManifestAsync(first, FirstRev, "m.a");
        await TestWait.UntilAsync(() => rig.Gateway.GetManifest(AgentId) is not null, "the manifest to be cached");

        // The same revision again: nothing is requested.
        first.SendText(LinkCodec.Serialize(new ManifestChangedMessage(FirstRev)));
        first.SendClose();
        await firstServing.WaitAsync(Limit);
        (ScriptedAgentSocket second, Task secondServing) = await ConnectAsync(rig, rev: FirstRev);
        await Task.Delay(200);

        Assert.Equal(0, second.Pending);
        Assert.Equal(FirstRev, rig.Gateway.GetManifest(AgentId)!.Rev);
        Assert.Contains(
            rig.Gateway.GetManifest(AgentId)!.Manifest.GetProperty("metrics").EnumerateArray(),
            metric => metric.GetProperty("id").GetString() == "m.a");
        second.SendClose();
        await secondServing.WaitAsync(Limit);
    }

    [Fact]
    public async Task The_live_state_follows_the_snapshots_and_the_changes_of_the_agent()
    {
        await using GatewayRig rig = await StartAsync();
        var observer = new RecordingObserver();
        using IDisposable observation = rig.Gateway.Observe(observer);
        (ScriptedAgentSocket socket, Task serving) = await ConnectAsync(rig);
        await AnswerManifestAsync(socket, FirstRev, "m.a", "m.b");
        await TestWait.UntilAsync(() => rig.Gateway.GetManifest(AgentId) is not null, "the manifest to be cached");

        // The consumer wants two metrics; the agent confirms and sends a full snapshot.
        using IMetricSubscription subscription = rig.Gateway.SubscribeMetrics(AgentId, MetricDemand.Live(["m.a", "m.b"]));
        JsonElement subscribe = await socket.NextFrameOfAsync(MessageTypes.Subscribe);
        Assert.Equal(["m.a", "m.b"], subscribe.GetProperty("metrics").EnumerateArray().Select(metric => metric.GetString()));
        socket.SendText(ScriptedAgent.Ok(ScriptedAgent.IdOf(subscribe)));
        socket.SendText("{\"t\":\"state\",\"ts\":1000,\"v\":{\"m.a\":1,\"m.b\":2}}");
        await TestWait.UntilAsync(() => rig.Gateway.GetState(AgentId) is { Ts: 1000 }, "the snapshot");
        Assert.Equal(2, rig.Gateway.GetState(AgentId)!.Values.Count);

        // A change is merged into what the hub knows.
        socket.SendText("{\"t\":\"state\",\"ts\":2000,\"v\":{\"m.a\":5}}");
        await TestWait.UntilAsync(() => rig.Gateway.GetState(AgentId) is { Ts: 2000 }, "the change");
        AgentStateSnapshot merged = rig.Gateway.GetState(AgentId)!;
        Assert.Equal(5, merged.Values["m.a"].GetInt32());
        Assert.Equal(2, merged.Values["m.b"].GetInt32());

        // A new subscription replaces the cache with its snapshot: the metric that is no longer subscribed is gone.
        subscription.Update(MetricDemand.Live(["m.a"]));
        JsonElement narrower = await socket.NextFrameOfAsync(MessageTypes.Subscribe);
        socket.SendText(ScriptedAgent.Ok(ScriptedAgent.IdOf(narrower)));
        socket.SendText("{\"t\":\"state\",\"ts\":3000,\"v\":{\"m.a\":6}}");
        await TestWait.UntilAsync(() => rig.Gateway.GetState(AgentId) is { Ts: 3000 }, "the second snapshot");
        AgentStateSnapshot replaced = rig.Gateway.GetState(AgentId)!;
        Assert.Equal(["m.a"], replaced.Values.Keys);
        Assert.Equal(6, replaced.Values["m.a"].GetInt32());

        // The observers saw the three frames in order, the first and the last as snapshots.
        Assert.Equal([true, false, true], observer.States.Select(update => update.IsSnapshot));
        Assert.Equal([1000L, 2000L, 3000L], observer.States.Select(update => update.Ts));

        // Without a consumer the subscription ends and the live state is dropped.
        subscription.Dispose();
        JsonElement none = await socket.NextFrameOfAsync(MessageTypes.Subscribe);
        Assert.Equal(0, none.GetProperty("metrics").GetArrayLength());
        socket.SendText(ScriptedAgent.Ok(ScriptedAgent.IdOf(none)));
        await TestWait.UntilAsync(() => rig.Gateway.GetState(AgentId) is null, "the live state to be dropped");
        socket.SendClose();
        await serving.WaitAsync(Limit);
    }

    [Fact]
    public async Task A_subscribe_that_the_agent_refuses_is_not_repeated_in_a_loop_and_the_next_change_tries_again()
    {
        await using GatewayRig rig = await StartAsync();
        (ScriptedAgentSocket socket, Task serving) = await ConnectAsync(rig);
        await AnswerManifestAsync(socket, FirstRev, "m.a");
        using IMetricSubscription subscription = rig.Gateway.SubscribeMetrics(AgentId, MetricDemand.Live(["m.a"]));

        JsonElement first = await socket.NextFrameOfAsync(MessageTypes.Subscribe);
        socket.SendText(ScriptedAgent.Fail(ScriptedAgent.IdOf(first), ErrorCodes.BadRequest));
        await Task.Delay(300);
        Assert.Equal(0, socket.Pending);

        subscription.Update(MetricDemand.Live(["m.a", "m.b"]));
        JsonElement second = await socket.NextFrameOfAsync(MessageTypes.Subscribe);
        Assert.Equal(["m.a", "m.b"], second.GetProperty("metrics").EnumerateArray().Select(metric => metric.GetString()));
        socket.SendText(ScriptedAgent.Ok(ScriptedAgent.IdOf(second)));
        socket.SendClose();
        await serving.WaitAsync(Limit);
    }

    [Fact]
    public async Task Events_are_not_stored_for_an_agent_that_is_not_in_the_registry_and_the_link_stays_up()
    {
        await using GatewayRig rig = await StartAsync();
        (ScriptedAgentSocket socket, Task serving) = await ConnectAsync(rig);

        // The scripted agent has no row in the registry, so storing fails; that is logged and does not end the connection.
        socket.SendText("{\"t\":\"event\",\"type\":\"custom\",\"ts\":1700000000000,\"data\":{\"name\":\"x\"}}");
        await TestWait.UntilAsync(() => rig.Hub.Logs.Messages.Any(message => message.Contains("could not be stored", StringComparison.Ordinal)), "the failure to be logged");

        Assert.True(rig.Gateway.GetPresence(AgentId).Online);
        socket.SendClose();
        await serving.WaitAsync(Limit);
    }
}
