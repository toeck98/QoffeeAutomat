using System.Text.Json;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Tests.Gateway;
using QoffeeAutomat.Hub.Tunnel;

namespace QoffeeAutomat.Hub.Tests.Tunnel;

/// <summary>
/// The hub's side of a tunnelled event stream (T912, PLAN.md S20): opening with <c>mgmt_stream_open</c>, delivering <c>mgmt_stream_data</c> in order, ending with
/// <c>mgmt_stream_close</c> from either side, and the limits: a consumer that does not read is cut off, a lost agent ends its streams, a feature that was not announced is
/// not served. The agent is played by hand.
/// </summary>
[Trait("Category", "Tunnel")]
public sealed class TunnelStreamTests
{
    private const string AgentId = "scripted-agent";
    private const string Rev = "aaaaaaaaaaaa";
    private const string RunStream = "/api/v1/runs/01JD8Z3Q4V7N2K9M5X6C8B0A1E/stream";

    private static readonly TunnelPrincipal Alice = new("alice", TunnelRoles.Viewer);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private static async Task<(GatewayRig Rig, ScriptedAgentSocket Socket, Task Serving)> ConnectAsync(GatewayOptions options, bool announce = true)
    {
        GatewayRig rig = await GatewayRig.StartAsync(options, withUsers: false);
        var socket = new ScriptedAgentSocket();
        Task serving = rig.Gateway.ServeAsync(socket, ScriptedAgent.Record(AgentId), CancellationToken.None);
        socket.SendText(announce ? ScriptedAgent.Hello(AgentId, Rev, 1, 1, HubFeatures.MgmtTunnel) : ScriptedAgent.Hello(AgentId, Rev));
        _ = await socket.NextFrameOfAsync(MessageTypes.ClientHello);
        await TestWait.UntilAsync(() => rig.Gateway.GetPresence(AgentId).Online, "the agent to be online");

        // The hub asks for the manifest of a revision it does not know; the scripted agent answers so that nothing waits for it.
        JsonElement manifest = await socket.NextFrameOfAsync(MessageTypes.GetManifest);
        socket.SendText(ScriptedAgent.Ok(ScriptedAgent.IdOf(manifest), ScriptedAgent.ManifestOf(AgentId, Rev, "cpu.load")));
        return (rig, socket, serving);
    }

    /// <summary>Opens a stream and plays the agent's answer.</summary>
    private static async Task<AgentStreamRelay> OpenAsync(GatewayRig rig, ScriptedAgentSocket socket, string sid = "s1", IReadOnlyDictionary<string, string>? query = null)
    {
        Task<AgentStreamOutcome> opening = rig.Gateway.OpenStreamAsync(AgentId, 0, RunStream, query, Alice, CancellationToken.None);
        JsonElement open = await socket.NextFrameOfAsync(HubMessageTypes.MgmtStreamOpen);
        socket.SendText(ScriptedAgent.Ok(ScriptedAgent.IdOf(open), TunnelKit.Json("{\"sid\":\"" + sid + "\"}")));
        AgentStreamOutcome outcome = await opening.WaitAsync(Limit);
        Assert.True(outcome.Ok, outcome.ErrorCode + " " + outcome.ErrorMessage);
        return outcome.Stream!;
    }

    private static string Event(string sid, string name, string data)
    {
        return JsonSerializer.Serialize(new Dictionary<string, string> { ["t"] = HubMessageTypes.MgmtStreamData, ["sid"] = sid, ["event"] = name, ["data"] = data });
    }

    private static string Close(string sid) => "{\"t\":\"" + HubMessageTypes.MgmtStreamClose + "\",\"sid\":\"" + sid + "\"}";

    [Fact]
    public async Task Opening_a_stream_sends_the_path_the_user_and_the_query_and_registers_the_stream_of_the_agent()
    {
        (GatewayRig rig, ScriptedAgentSocket socket, Task serving) = await ConnectAsync(TunnelKit.Options());
        await using GatewayRig owner = rig;

        Task<AgentStreamOutcome> opening = rig.Gateway.OpenStreamAsync(
            AgentId,
            0,
            "/api/v1/metrics/stream",
            new Dictionary<string, string> { ["metrics"] = "cpu.load", ["interval"] = "2" },
            Alice,
            CancellationToken.None);
        JsonElement open = await socket.NextFrameOfAsync(HubMessageTypes.MgmtStreamOpen);

        Assert.Equal("/api/v1/metrics/stream", open.GetProperty("path").GetString());
        Assert.Equal("alice", open.GetProperty("principal").GetProperty("user").GetString());
        Assert.Equal("viewer", open.GetProperty("principal").GetProperty("role").GetString());
        Assert.Equal("cpu.load", open.GetProperty("query").GetProperty("metrics").GetString());
        Assert.Equal("2", open.GetProperty("query").GetProperty("interval").GetString());

        socket.SendText(ScriptedAgent.Ok(ScriptedAgent.IdOf(open), TunnelKit.Json("{\"sid\":\"s7\"}")));
        AgentStreamOutcome outcome = await opening.WaitAsync(Limit);

        Assert.True(outcome.Ok);
        Assert.Equal("s7", outcome.Stream!.Sid);
        Assert.Equal(AgentId, outcome.Stream.AgentId);
        Assert.Null(outcome.Stream.EndReason);
        socket.SendClose();
        await serving.WaitAsync(Limit);
    }

    [Fact]
    public async Task Events_arrive_in_order_and_the_close_of_the_agent_ends_the_stream_normally()
    {
        (GatewayRig rig, ScriptedAgentSocket socket, Task serving) = await ConnectAsync(TunnelKit.Options());
        await using GatewayRig owner = rig;
        await using AgentStreamRelay stream = await OpenAsync(rig, socket);

        socket.SendText(Event("s1", "output", "{\"stream\":\"stdout\",\"text\":\"one\"}"));
        socket.SendText(Event("s1", "output", "{\"stream\":\"stdout\",\"text\":\"two\"}"));
        socket.SendText(Event("s1", "end", "{\"exit_code\":0}"));
        socket.SendText(Close("s1"));

        var received = new List<AgentStreamEvent>();
        using var timeout = new CancellationTokenSource(Limit);
        await foreach (AgentStreamEvent item in stream.Events.ReadAllAsync(timeout.Token))
        {
            received.Add(item);
        }

        Assert.Equal(["output", "output", "end"], received.Select(item => item.Event));
        Assert.Contains("one", received[0].Data, StringComparison.Ordinal);
        Assert.Contains("two", received[1].Data, StringComparison.Ordinal);
        Assert.Equal(StreamEndReasons.Completed, stream.EndReason);
        socket.SendClose();
        await serving.WaitAsync(Limit);
    }

    [Fact]
    public async Task Closing_the_stream_on_the_hub_tells_the_agent()
    {
        (GatewayRig rig, ScriptedAgentSocket socket, Task serving) = await ConnectAsync(TunnelKit.Options());
        await using GatewayRig owner = rig;
        AgentStreamRelay stream = await OpenAsync(rig, socket, "s3");

        await stream.DisposeAsync();

        JsonElement close = await socket.NextFrameOfAsync(HubMessageTypes.MgmtStreamClose);
        Assert.Equal("s3", close.GetProperty("sid").GetString());
        Assert.Equal(StreamEndReasons.Closed, stream.EndReason);
        socket.SendClose();
        await serving.WaitAsync(Limit);
    }

    [Fact]
    public async Task A_consumer_that_does_not_read_is_cut_off_and_the_agent_is_told_to_stop()
    {
        GatewayOptions options = TunnelKit.Options(streamQueue: 2, backpressure: TimeSpan.FromMilliseconds(100));
        (GatewayRig rig, ScriptedAgentSocket socket, Task serving) = await ConnectAsync(options);
        await using GatewayRig owner = rig;
        await using AgentStreamRelay stream = await OpenAsync(rig, socket);

        for (int i = 0; i < 6; i++)
        {
            socket.SendText(Event("s1", "output", "{\"n\":" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}"));
        }

        JsonElement close = await socket.NextFrameOfAsync(HubMessageTypes.MgmtStreamClose);
        Assert.Equal("s1", close.GetProperty("sid").GetString());
        await TestWait.UntilAsync(() => stream.EndReason is not null, "the stream to end");
        Assert.Equal(StreamEndReasons.SlowConsumer, stream.EndReason);

        // What fitted into the queue is still delivered; the stream was cut off, not the agent link.
        var received = new List<AgentStreamEvent>();
        using var timeout = new CancellationTokenSource(Limit);
        await foreach (AgentStreamEvent item in stream.Events.ReadAllAsync(timeout.Token))
        {
            received.Add(item);
        }

        Assert.Equal(2, received.Count);
        Assert.True(rig.Gateway.GetPresence(AgentId).Online);
        socket.SendClose();
        await serving.WaitAsync(Limit);
    }

    [Fact]
    public async Task A_short_stall_of_the_consumer_holds_the_agent_back_instead_of_cutting_the_stream_off()
    {
        GatewayOptions options = TunnelKit.Options(streamQueue: 2, backpressure: TimeSpan.FromSeconds(5));
        (GatewayRig rig, ScriptedAgentSocket socket, Task serving) = await ConnectAsync(options);
        await using GatewayRig owner = rig;
        await using AgentStreamRelay stream = await OpenAsync(rig, socket);

        for (int i = 0; i < 5; i++)
        {
            socket.SendText(Event("s1", "output", "{\"n\":" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}"));
        }

        // The consumer wakes up late and reads everything: the receive loop waited for room, so no event was lost.
        await Task.Delay(300);
        var received = new List<string>();
        using var timeout = new CancellationTokenSource(Limit);
        while (received.Count < 5)
        {
            received.Add((await stream.Events.ReadAsync(timeout.Token)).Data);
        }

        Assert.Equal(["{\"n\":0}", "{\"n\":1}", "{\"n\":2}", "{\"n\":3}", "{\"n\":4}"], received);
        Assert.Null(stream.EndReason);
        socket.SendClose();
        await serving.WaitAsync(Limit);
    }

    [Fact]
    public async Task When_the_agent_goes_away_its_streams_end_with_agent_offline()
    {
        (GatewayRig rig, ScriptedAgentSocket socket, Task serving) = await ConnectAsync(TunnelKit.Options());
        await using GatewayRig owner = rig;
        await using AgentStreamRelay stream = await OpenAsync(rig, socket);

        socket.SendClose();
        await serving.WaitAsync(Limit);

        using var timeout = new CancellationTokenSource(Limit);
        while (await stream.Events.WaitToReadAsync(timeout.Token))
        {
            while (stream.Events.TryRead(out _))
            {
            }
        }

        Assert.Equal(StreamEndReasons.AgentOffline, stream.EndReason);
        Assert.False(rig.Gateway.GetPresence(AgentId).Online);
    }

    [Fact]
    public async Task A_refusal_of_the_agent_is_returned_with_its_code_and_opens_no_stream()
    {
        (GatewayRig rig, ScriptedAgentSocket socket, Task serving) = await ConnectAsync(TunnelKit.Options());
        await using GatewayRig owner = rig;

        Task<AgentStreamOutcome> opening = rig.Gateway.OpenStreamAsync(AgentId, 0, RunStream, null, Alice, CancellationToken.None);
        JsonElement open = await socket.NextFrameOfAsync(HubMessageTypes.MgmtStreamOpen);
        socket.SendText(ScriptedAgent.Fail(ScriptedAgent.IdOf(open), HubErrorCodes.Forbidden));
        AgentStreamOutcome outcome = await opening.WaitAsync(Limit);

        Assert.False(outcome.Ok);
        Assert.Equal(HubErrorCodes.Forbidden, outcome.ErrorCode);
        Assert.Null(outcome.Stream);
        socket.SendClose();
        await serving.WaitAsync(Limit);
    }

    [Fact]
    public async Task An_agent_that_is_not_connected_fails_with_agent_offline()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync(TunnelKit.Options(), withUsers: false);

        AgentStreamOutcome outcome = await rig.Gateway.OpenStreamAsync("nobody", 0, RunStream, null, Alice, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal(HubErrorCodes.AgentOffline, outcome.ErrorCode);
    }

    [Fact]
    public async Task Too_many_open_streams_of_one_agent_fail_with_busy()
    {
        (GatewayRig rig, ScriptedAgentSocket socket, Task serving) = await ConnectAsync(TunnelKit.Options(maxStreams: 1));
        await using GatewayRig owner = rig;
        await using AgentStreamRelay first = await OpenAsync(rig, socket);

        AgentStreamOutcome second = await rig.Gateway.OpenStreamAsync(AgentId, 0, RunStream, null, Alice, CancellationToken.None);

        Assert.False(second.Ok);
        Assert.Equal(ErrorCodes.Busy, second.ErrorCode);
        Assert.Null(first.EndReason);
        socket.SendClose();
        await serving.WaitAsync(Limit);
    }

    [Fact]
    public async Task Events_of_a_stream_that_is_not_open_are_ignored_and_do_not_close_the_link()
    {
        (GatewayRig rig, ScriptedAgentSocket socket, Task serving) = await ConnectAsync(TunnelKit.Options());
        await using GatewayRig owner = rig;
        await using AgentStreamRelay stream = await OpenAsync(rig, socket);

        socket.SendText(Event("unknown", "output", "{}"));
        socket.SendText(Close("unknown"));
        socket.SendText("{\"t\":\"mgmt_stream_data\",\"sid\":42}");
        socket.SendText(Event("s1", "output", "{\"still\":\"works\"}"));

        AgentStreamEvent item = await stream.Events.ReadAsync(new CancellationTokenSource(Limit).Token);
        Assert.Contains("works", item.Data, StringComparison.Ordinal);
        Assert.True(rig.Gateway.GetPresence(AgentId).Online);
        socket.SendClose();
        await serving.WaitAsync(Limit);
    }

    [Fact]
    public async Task Stream_frames_of_an_agent_that_did_not_announce_the_feature_are_ignored()
    {
        (GatewayRig rig, ScriptedAgentSocket socket, Task serving) = await ConnectAsync(TunnelKit.Options(), announce: false);
        await using GatewayRig owner = rig;
        Assert.DoesNotContain(HubFeatures.MgmtTunnel, rig.Gateway.GetPresence(AgentId).Features);
        await using AgentStreamRelay stream = await OpenAsync(rig, socket);

        socket.SendText(Event("s1", "output", "{}"));
        socket.SendText(Close("s1"));
        await Task.Delay(200);

        Assert.False(stream.Events.TryRead(out _));
        Assert.Null(stream.EndReason);
        Assert.True(rig.Gateway.GetPresence(AgentId).Online);
        socket.SendClose();
        await serving.WaitAsync(Limit);
    }
}
