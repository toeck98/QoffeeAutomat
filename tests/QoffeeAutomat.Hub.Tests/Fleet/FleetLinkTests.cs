using System.Text.Json;
using System.Text.Json.Nodes;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Fleet;

namespace QoffeeAutomat.Hub.Tests.Fleet;

/// <summary>
/// The fleet link of the hub (T913, PLAN.md S20 "Fleet link"): the <c>hello</c> of the hub, <c>get_agents</c>, the unsolicited <c>agent_added</c>, <c>agent_removed</c> and
/// <c>agent_status</c>, manifests from the cache, subscriptions that deliver the <c>state</c> of several agents over one connection with the right agent id on every frame, events,
/// and what a client is told when an agent goes offline. Every frame that the hub sends is validated against the protocol schemas by the <see cref="FleetClient"/>; disposing the
/// client fails the test when one does not validate.
/// </summary>
[Trait("Category", "Fleet")]
public sealed class FleetLinkTests
{
    private const string AgentA = "agent-a";
    private const string AgentB = "agent-b";

    // ------------------------------------------------------------------
    // Hello and the list of agents
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_hub_speaks_first_with_a_hello_that_names_the_hub_and_offers_the_fleet_feature()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        FleetClient client = await rig.ConnectClientAsync();

        JsonElement hub = client.Hello.GetProperty("hub");
        Assert.False(string.IsNullOrEmpty(hub.GetProperty("id").GetString()));
        Assert.False(string.IsNullOrEmpty(hub.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrEmpty(hub.GetProperty("version").GetString()));
        Assert.False(client.Hello.TryGetProperty("agent", out _));
        Assert.Contains(HubFeatures.Fleet, client.Hello.GetProperty("features").EnumerateArray().Select(feature => feature.GetString()));
        Assert.Equal(ProtocolInfo.Version, client.Hello.GetProperty("protocol").GetProperty("max").GetInt32());
    }

    [Fact]
    public async Task Get_agents_lists_every_enrolled_agent_with_presence_and_manifest_revision()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        _ = await rig.AddAgentAsync(AgentA, "Office PC", metrics: ["cpu.usage"], rev: "aaaaaaaaaaaa");
        await rig.EnrollAsync(AgentB, "Lab PC");
        FleetClient client = await rig.ConnectClientAsync();

        Dictionary<string, JsonElement> agents = FleetClient.AgentsOf(await client.RequestAsync(FleetClient.Request(HubMessageTypes.GetAgents)));

        Assert.Equal([AgentA, AgentB], agents.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Office PC", agents[AgentA].GetProperty("name").GetString());
        Assert.True(agents[AgentA].GetProperty("online").GetBoolean());
        Assert.Equal("aaaaaaaaaaaa", agents[AgentA].GetProperty("manifest_rev").GetString());
        Assert.Equal(PowerStates.Unknown, agents[AgentA].GetProperty("power_state").GetString());
        Assert.False(agents[AgentB].GetProperty("online").GetBoolean());
        Assert.Equal("Lab PC", agents[AgentB].GetProperty("name").GetString());
    }

    [Fact]
    public async Task An_agent_that_connects_after_the_client_is_announced_with_agent_added()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        FleetClient client = await rig.ConnectClientAsync();

        _ = await rig.AddAgentAsync(AgentA, "Office PC");

        JsonElement added = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == HubMessageTypes.AgentAdded);
        Assert.Equal(AgentA, FleetClient.AgentOf(added));
        Assert.Equal("Office PC", added.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Deleting_an_agent_is_announced_with_agent_removed()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        _ = await rig.AddAgentAsync(AgentA, "Office PC");
        FleetClient client = await rig.ConnectClientAsync();
        _ = FleetClient.AgentsOf(await client.RequestAsync(FleetClient.Request(HubMessageTypes.GetAgents)));

        using HttpResponseMessage delete = await rig.Hub.Admin.DeleteAsync("/api/v1/agents/" + AgentA);

        Assert.True(delete.IsSuccessStatusCode, "Deleting the agent failed: " + (int)delete.StatusCode);
        JsonElement removed = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == HubMessageTypes.AgentRemoved);
        Assert.Equal(AgentA, FleetClient.AgentOf(removed));
        JsonElement afterwards = await client.RequestAsync(FleetClient.Request(HubMessageTypes.GetAgents));
        Assert.Empty(FleetClient.AgentsOf(afterwards));
    }

    // ------------------------------------------------------------------
    // Manifests
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_manifest_is_served_from_the_cache_without_asking_the_agent()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink agent = await rig.AddAgentAsync(AgentA, "Office PC", metrics: ["cpu.usage", "mem.usage"], rev: "aaaaaaaaaaaa");
        FleetClient client = await rig.ConnectClientAsync();
        int asked = agent.RequestsOf(MessageTypes.GetManifest).Length;

        JsonElement manifest = FleetClient.DataOf(await client.RequestAsync(FleetClient.Request(MessageTypes.GetManifest, AgentA)));

        Assert.Equal("aaaaaaaaaaaa", manifest.GetProperty("rev").GetString());
        Assert.Equal(
            ["cpu.usage", "mem.usage"],
            manifest.GetProperty("metrics").EnumerateArray().Select(metric => metric.GetProperty("id").GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(asked, agent.RequestsOf(MessageTypes.GetManifest).Length);
    }

    [Fact]
    public async Task A_manifest_of_an_agent_that_never_connected_is_agent_offline()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        await rig.EnrollAsync(AgentA, "Office PC");
        FleetClient client = await rig.ConnectClientAsync();

        JsonElement result = await client.RequestAsync(FleetClient.Request(MessageTypes.GetManifest, AgentA));

        Assert.Equal(HubErrorCodes.AgentOffline, FleetClient.ErrorCodeOf(result));
    }

    [Fact]
    public async Task A_changed_manifest_is_announced_with_manifest_changed_and_the_cache_serves_the_new_one()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink agent = await rig.AddAgentAsync(AgentA, "Office PC", metrics: ["cpu.usage"], rev: "aaaaaaaaaaaa");
        FleetClient client = await rig.ConnectClientAsync();
        _ = FleetClient.AgentsOf(await client.RequestAsync(FleetClient.Request(HubMessageTypes.GetAgents)));

        await agent.ChangeManifestAsync("bbbbbbbbbbbb", "cpu.usage", "mem.usage");

        JsonElement changed = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == MessageTypes.ManifestChanged);
        Assert.Equal(AgentA, FleetClient.AgentOf(changed));
        Assert.Equal("bbbbbbbbbbbb", changed.GetProperty("rev").GetString());
        JsonElement manifest = FleetClient.DataOf(await client.RequestAsync(FleetClient.Request(MessageTypes.GetManifest, AgentA)));
        Assert.Equal("bbbbbbbbbbbb", manifest.GetProperty("rev").GetString());
        Assert.Equal(2, manifest.GetProperty("metrics").GetArrayLength());
    }

    // ------------------------------------------------------------------
    // Subscriptions, state and events
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_subscription_answers_with_a_result_and_then_a_snapshot_of_what_the_client_follows()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink agent = await rig.AddAgentAsync(AgentA, "Office PC", metrics: ["cpu.usage", "mem.usage"]);
        agent.Values["cpu.usage"] = ScriptedAgentLink.Number(12.5);
        agent.Values["mem.usage"] = ScriptedAgentLink.Number(40);
        FleetClient client = await rig.ConnectClientAsync();

        JsonElement result = await client.RequestAsync(FleetClient.Subscribe(AgentA, "cpu.usage"));
        _ = FleetClient.DataOf(result);

        JsonElement state = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == MessageTypes.State);
        Assert.Equal(AgentA, FleetClient.AgentOf(state));
        JsonElement values = state.GetProperty("v");
        Assert.Equal(12.5, values.GetProperty("cpu.usage").GetDouble());
        Assert.False(values.TryGetProperty("mem.usage", out _));
    }

    [Fact]
    public async Task The_state_of_two_agents_travels_over_one_connection_and_each_frame_carries_its_agent_id()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink first = await rig.AddAgentAsync(AgentA, "Office PC", metrics: ["cpu.usage"]);
        ScriptedAgentLink second = await rig.AddAgentAsync(AgentB, "Lab PC", metrics: ["cpu.usage"]);
        FleetClient client = await rig.ConnectClientAsync();
        _ = FleetClient.DataOf(await client.RequestAsync(FleetClient.Subscribe(AgentA, "cpu.usage")));
        _ = FleetClient.DataOf(await client.RequestAsync(FleetClient.Subscribe(AgentB, "cpu.usage")));

        first.SendState(("cpu.usage", ScriptedAgentLink.Number(11)));
        second.SendState(("cpu.usage", ScriptedAgentLink.Number(22)));

        JsonElement fromFirst = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == MessageTypes.State && FleetClient.AgentOf(frame) == AgentA && Reads(frame, 11));
        JsonElement fromSecond = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == MessageTypes.State && FleetClient.AgentOf(frame) == AgentB && Reads(frame, 22));
        Assert.Equal(AgentA, FleetClient.AgentOf(fromFirst));
        Assert.Equal(AgentB, FleetClient.AgentOf(fromSecond));

        // Nowhere does a value of one agent travel under the id of the other.
        foreach (JsonElement frame in client.Frames.Where(frame => FleetClient.TypeOf(frame) == MessageTypes.State))
        {
            double value = frame.GetProperty("v").GetProperty("cpu.usage").GetDouble();
            Assert.Equal(FleetClient.AgentOf(frame) == AgentA ? 11 : 22, value);
        }
    }

    [Fact]
    public async Task A_client_gets_no_state_for_agents_or_metrics_it_does_not_follow()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink first = await rig.AddAgentAsync(AgentA, "Office PC", metrics: ["cpu.usage", "mem.usage"]);
        ScriptedAgentLink second = await rig.AddAgentAsync(AgentB, "Lab PC", metrics: ["cpu.usage"]);
        FleetClient client = await rig.ConnectClientAsync();
        _ = FleetClient.DataOf(await client.RequestAsync(FleetClient.Subscribe(AgentA, "cpu.usage")));

        second.SendState(("cpu.usage", ScriptedAgentLink.Number(5)));
        first.SendState(("mem.usage", ScriptedAgentLink.Number(6)));
        first.SendState(("cpu.usage", ScriptedAgentLink.Number(7)));

        JsonElement state = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == MessageTypes.State && Reads(frame, 7));
        Assert.Equal(AgentA, FleetClient.AgentOf(state));
        Assert.DoesNotContain(client.Frames, frame => FleetClient.TypeOf(frame) == MessageTypes.State && (FleetClient.AgentOf(frame) == AgentB || frame.GetProperty("v").TryGetProperty("mem.usage", out _)));
    }

    [Fact]
    public async Task An_empty_subscription_ends_the_following_of_an_agent()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink agent = await rig.AddAgentAsync(AgentA, "Office PC", metrics: ["cpu.usage"]);
        FleetClient client = await rig.ConnectClientAsync();
        _ = FleetClient.DataOf(await client.RequestAsync(FleetClient.Subscribe(AgentA, "cpu.usage")));
        agent.SendState(("cpu.usage", ScriptedAgentLink.Number(1)));
        _ = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == MessageTypes.State && Reads(frame, 1));

        _ = FleetClient.DataOf(await client.RequestAsync(FleetClient.Subscribe(AgentA)));
        agent.SendState(("cpu.usage", ScriptedAgentLink.Number(2)));

        await client.ExpectNoFrameAsync();
    }

    [Fact]
    public async Task An_event_of_an_agent_reaches_the_client_with_the_agent_id()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink agent = await rig.AddAgentAsync(AgentA, "Office PC");
        _ = await rig.AddAgentAsync(AgentB, "Lab PC");
        FleetClient client = await rig.ConnectClientAsync();

        agent.SendEvent("session_locked", "{\"user\":\"tim\"}");

        JsonElement frame = await client.NextWhereAsync(candidate => FleetClient.TypeOf(candidate) == MessageTypes.Event);
        Assert.Equal(AgentA, FleetClient.AgentOf(frame));
        Assert.Equal("session_locked", frame.GetProperty("type").GetString());
        Assert.Equal("tim", frame.GetProperty("data").GetProperty("user").GetString());
    }

    [Fact]
    public async Task A_request_for_an_agent_that_does_not_exist_is_unknown_agent()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        FleetClient client = await rig.ConnectClientAsync();

        JsonElement manifest = await client.RequestAsync(FleetClient.Request(MessageTypes.GetManifest, "nobody"));
        JsonElement subscribe = await client.RequestAsync(FleetClient.Subscribe("nobody", "cpu.usage"));
        JsonElement run = await client.RequestAsync(FleetClient.Run("nobody", "lock"));

        Assert.Equal(HubErrorCodes.UnknownAgent, FleetClient.ErrorCodeOf(manifest));
        Assert.Equal(HubErrorCodes.UnknownAgent, FleetClient.ErrorCodeOf(subscribe));
        Assert.Equal(HubErrorCodes.UnknownAgent, FleetClient.ErrorCodeOf(run));
    }

    // ------------------------------------------------------------------
    // Agents that go offline
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_agent_going_offline_produces_agent_status_and_its_metrics_become_unavailable()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink agent = await rig.AddAgentAsync(AgentA, "Office PC", metrics: ["cpu.usage", "mem.usage"]);
        agent.Values["cpu.usage"] = ScriptedAgentLink.Number(30);
        agent.Values["mem.usage"] = ScriptedAgentLink.Number(50);
        FleetClient client = await rig.ConnectClientAsync();
        _ = FleetClient.AgentsOf(await client.RequestAsync(FleetClient.Request(HubMessageTypes.GetAgents)));
        _ = FleetClient.DataOf(await client.RequestAsync(FleetClient.Subscribe(AgentA, "cpu.usage", "mem.usage")));
        _ = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == MessageTypes.State && Reads(frame, 30));

        await agent.GoOfflineAsync(ByeReasons.Sleep);

        JsonElement status = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == HubMessageTypes.AgentStatus);
        Assert.Equal(AgentA, FleetClient.AgentOf(status));
        Assert.False(status.GetProperty("online").GetBoolean());
        Assert.Equal(ByeReasons.Sleep, status.GetProperty("power_state").GetString());
        JsonElement unavailable = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == MessageTypes.State && frame.GetProperty("v").GetProperty("cpu.usage").ValueKind == JsonValueKind.Null);
        Assert.Equal(AgentA, FleetClient.AgentOf(unavailable));
        Assert.Equal(JsonValueKind.Null, unavailable.GetProperty("v").GetProperty("mem.usage").ValueKind);
    }

    [Fact]
    public async Task An_agent_that_comes_back_is_announced_online_again()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink agent = await rig.AddAgentAsync(AgentA, "Office PC", metrics: ["cpu.usage"]);
        FleetClient client = await rig.ConnectClientAsync();
        _ = FleetClient.AgentsOf(await client.RequestAsync(FleetClient.Request(HubMessageTypes.GetAgents)));
        await agent.GoOfflineAsync(ByeReasons.Shutdown);
        _ = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == HubMessageTypes.AgentStatus && !frame.GetProperty("online").GetBoolean());

        await agent.ReconnectAsync();

        JsonElement online = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == HubMessageTypes.AgentStatus && frame.GetProperty("online").GetBoolean());
        Assert.Equal(AgentA, FleetClient.AgentOf(online));
    }

    [Fact]
    public async Task A_subscription_to_an_offline_agent_reports_its_metrics_as_unavailable()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink agent = await rig.AddAgentAsync(AgentA, "Office PC", metrics: ["cpu.usage"]);
        agent.Values["cpu.usage"] = ScriptedAgentLink.Number(30);
        await agent.GoOfflineAsync();
        FleetClient client = await rig.ConnectClientAsync();

        JsonElement result = await client.RequestAsync(FleetClient.Subscribe(AgentA, "cpu.usage"));

        // The hub can follow an agent that is away: the demand is held at the gateway and the client sees the metric without a value.
        _ = FleetClient.DataOf(result);
        JsonElement state = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == MessageTypes.State);
        Assert.Equal(AgentA, FleetClient.AgentOf(state));
        Assert.Equal(JsonValueKind.Null, state.GetProperty("v").GetProperty("cpu.usage").ValueKind);
    }

    // ------------------------------------------------------------------
    // The rules of the link
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_frame_that_is_not_json_closes_the_link_with_4400()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        FleetClient client = await rig.ConnectClientAsync();

        await client.SendTextAsync("this is not json");

        Assert.Equal(CloseCodes.BadRequest, await client.Closed.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_binary_frame_closes_the_link_with_4400()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        FleetClient client = await rig.ConnectClientAsync();

        await client.SendBinaryAsync([1, 2, 3]);

        Assert.Equal(CloseCodes.BadRequest, await client.Closed.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_request_with_an_agent_id_of_the_wrong_type_is_a_bad_request_and_unknown_types_are_ignored()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        FleetClient client = await rig.ConnectClientAsync();

        await client.SendTextAsync("{\"t\":\"from_a_later_version\",\"id\":1000}");
        JsonObject request = FleetClient.Request(MessageTypes.GetManifest);
        request["a"] = 5;
        JsonElement invalid = await client.RequestAsync(request);

        Assert.Equal(ErrorCodes.BadRequest, FleetClient.ErrorCodeOf(invalid));
        Assert.False(client.Closed.IsCompleted);
    }

    [Fact]
    public async Task The_hub_closes_the_connections_with_going_away_when_it_stops()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        FleetClient client = await rig.ConnectClientAsync();

        rig.Hub.Service<FleetService>().BeginStop();

        Assert.Equal(CloseCodes.GoingAway, await client.Closed.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static bool Reads(JsonElement frame, double value)
    {
        return frame.TryGetProperty("v", out JsonElement values)
            && values.TryGetProperty("cpu.usage", out JsonElement cpu)
            && cpu.ValueKind == JsonValueKind.Number
            && cpu.GetDouble() == value;
    }
}
