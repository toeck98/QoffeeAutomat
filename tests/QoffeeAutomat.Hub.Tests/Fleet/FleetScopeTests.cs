using System.Text.Json;
using System.Text.Json.Nodes;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Hub.Tests.Fleet;

/// <summary>
/// The scope of a fleet token (T913, PLAN.md S20 "Fleet link"): a client scoped to tags never receives data of an agent without one of them, gets <c>forbidden</c> when it addresses
/// such an agent, and sees at once what a change of an agent's tags means for it. The hub never reveals more than <c>forbidden</c> about an agent that exists; an agent that does not
/// exist is <c>unknown_agent</c> for everyone.
/// </summary>
[Trait("Category", "Fleet")]
public sealed class FleetScopeTests
{
    private const string Gaming = "agent-gaming";
    private const string Office = "agent-office";
    private const string Both = "agent-both";

    private static async Task<(ScriptedAgentLink GamingLink, ScriptedAgentLink OfficeLink, ScriptedAgentLink BothLink)> AddAgentsAsync(FleetRig rig)
    {
        ScriptedAgentLink gaming = await rig.AddAgentAsync(Gaming, "Gaming PC", ["gaming"], ["cpu.usage"]);
        ScriptedAgentLink office = await rig.AddAgentAsync(Office, "Office PC", ["office"], ["cpu.usage"]);
        ScriptedAgentLink both = await rig.AddAgentAsync(Both, "Workstation", ["gaming", "office"], ["cpu.usage"]);
        return (gaming, office, both);
    }

    [Fact]
    public async Task Get_agents_lists_only_the_agents_that_carry_a_tag_of_the_scope()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        _ = await AddAgentsAsync(rig);
        await rig.EnrollAsync("agent-untagged", "Untagged PC");
        FleetClient gamers = await rig.ConnectClientAsync("tags", ["gaming"]);
        FleetClient everything = await rig.ConnectClientAsync();

        Dictionary<string, JsonElement> scoped = FleetClient.AgentsOf(await gamers.RequestAsync(FleetClient.Request(HubMessageTypes.GetAgents)));
        Dictionary<string, JsonElement> all = FleetClient.AgentsOf(await everything.RequestAsync(FleetClient.Request(HubMessageTypes.GetAgents)));

        Assert.Equal([Both, Gaming], scoped.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public async Task A_scope_of_several_tags_covers_an_agent_with_any_one_of_them()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        _ = await AddAgentsAsync(rig);
        FleetClient client = await rig.ConnectClientAsync("tags", ["gaming", "office"]);

        Dictionary<string, JsonElement> agents = FleetClient.AgentsOf(await client.RequestAsync(FleetClient.Request(HubMessageTypes.GetAgents)));

        Assert.Equal([Both, Gaming, Office], agents.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Addressing_an_agent_outside_the_scope_is_forbidden_for_every_agent_scoped_request()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        (_, ScriptedAgentLink office, _) = await AddAgentsAsync(rig);
        FleetClient client = await rig.ConnectClientAsync("tags", ["gaming"]);

        JsonElement manifest = await client.RequestAsync(FleetClient.Request(MessageTypes.GetManifest, Office));
        JsonElement subscribe = await client.RequestAsync(FleetClient.Subscribe(Office, "cpu.usage"));
        JsonElement run = await client.RequestAsync(FleetClient.Run(Office, "lock", "ha-user-1"));

        Assert.Equal(HubErrorCodes.Forbidden, FleetClient.ErrorCodeOf(manifest));
        Assert.Equal(HubErrorCodes.Forbidden, FleetClient.ErrorCodeOf(subscribe));
        Assert.Equal(HubErrorCodes.Forbidden, FleetClient.ErrorCodeOf(run));
        Assert.Empty(office.RequestsOf(MessageTypes.Run));
    }

    [Fact]
    public async Task A_client_scoped_to_a_tag_never_receives_state_or_events_of_other_agents()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        (ScriptedAgentLink gaming, ScriptedAgentLink office, _) = await AddAgentsAsync(rig);
        FleetClient client = await rig.ConnectClientAsync("tags", ["gaming"]);
        _ = FleetClient.DataOf(await client.RequestAsync(FleetClient.Subscribe(Gaming, "cpu.usage")));

        office.SendState(("cpu.usage", ScriptedAgentLink.Number(99)));
        office.SendEvent("session_locked");
        gaming.SendState(("cpu.usage", ScriptedAgentLink.Number(10)));
        gaming.SendEvent("session_locked");

        bool sawState = false;
        bool sawEvent = false;
        while (!sawState || !sawEvent)
        {
            JsonElement frame = await client.NextWhereAsync(candidate => FleetClient.AgentOf(candidate) == Gaming);
            string type = FleetClient.TypeOf(frame);
            if (type == MessageTypes.State)
            {
                sawState = true;
            }
            else if (type == MessageTypes.Event)
            {
                sawEvent = true;
            }
        }

        await client.ExpectNoFrameForAsync(Office);
        Assert.All(client.Frames.Where(frame => FleetClient.AgentOf(frame).Length > 0), frame => Assert.Equal(Gaming, FleetClient.AgentOf(frame)));
        Assert.DoesNotContain(client.Frames, frame => frame.GetRawText().Contains(Office, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Presence_changes_of_agents_outside_the_scope_are_not_announced()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        (_, ScriptedAgentLink office, _) = await AddAgentsAsync(rig);
        FleetClient client = await rig.ConnectClientAsync("tags", ["gaming"]);

        await office.GoOfflineAsync();

        await client.ExpectNoFrameForAsync(Office);
    }

    [Fact]
    public async Task An_agent_that_enrolls_with_a_foreign_tag_is_not_announced_to_a_scoped_client()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        FleetClient client = await rig.ConnectClientAsync("tags", ["gaming"]);

        _ = await rig.AddAgentAsync(Office, "Office PC", ["office"]);
        _ = await rig.AddAgentAsync(Gaming, "Gaming PC", ["gaming"]);

        JsonElement added = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == HubMessageTypes.AgentAdded);
        Assert.Equal(Gaming, FleetClient.AgentOf(added));
        Assert.DoesNotContain(client.Frames, frame => FleetClient.AgentOf(frame) == Office);
    }

    [Fact]
    public async Task Giving_an_agent_a_tag_of_the_scope_announces_it_and_removing_the_tag_withdraws_it()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink office = await rig.AddAgentAsync(Office, "Office PC", ["office"], ["cpu.usage"]);
        FleetClient client = await rig.ConnectClientAsync("tags", ["gaming"]);
        Assert.Empty(FleetClient.AgentsOf(await client.RequestAsync(FleetClient.Request(HubMessageTypes.GetAgents))));

        await rig.SetTagsAsync(Office, "office", "gaming");

        JsonElement added = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == HubMessageTypes.AgentAdded);
        Assert.Equal(Office, FleetClient.AgentOf(added));
        Assert.Equal("Office PC", added.GetProperty("name").GetString());
        Assert.True(added.GetProperty("online").GetBoolean());
        _ = FleetClient.DataOf(await client.RequestAsync(FleetClient.Subscribe(Office, "cpu.usage")));
        office.SendState(("cpu.usage", ScriptedAgentLink.Number(3)));
        _ = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == MessageTypes.State && FleetClient.AgentOf(frame) == Office);

        await rig.SetTagsAsync(Office, "office");

        JsonElement removed = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == HubMessageTypes.AgentRemoved);
        Assert.Equal(Office, FleetClient.AgentOf(removed));
        office.SendState(("cpu.usage", ScriptedAgentLink.Number(4)));
        office.SendEvent("session_locked");
        await client.ExpectNoFrameForAsync(Office);
        JsonElement forbidden = await client.RequestAsync(FleetClient.Request(MessageTypes.GetManifest, Office));
        Assert.Equal(HubErrorCodes.Forbidden, FleetClient.ErrorCodeOf(forbidden));
    }

    [Fact]
    public async Task A_change_of_tags_updates_each_client_by_its_own_scope()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        _ = await rig.AddAgentAsync(Office, "Office PC", ["office"]);
        FleetClient gamers = await rig.ConnectClientAsync("tags", ["gaming"]);
        FleetClient officers = await rig.ConnectClientAsync("tags", ["office"]);
        FleetClient everything = await rig.ConnectClientAsync();
        foreach (FleetClient client in new[] { gamers, officers, everything })
        {
            _ = FleetClient.AgentsOf(await client.RequestAsync(FleetClient.Request(HubMessageTypes.GetAgents)));
        }

        await rig.SetTagsAsync(Office, "gaming");

        Assert.Equal(Office, FleetClient.AgentOf(await gamers.NextWhereAsync(frame => FleetClient.TypeOf(frame) == HubMessageTypes.AgentAdded)));
        Assert.Equal(Office, FleetClient.AgentOf(await officers.NextWhereAsync(frame => FleetClient.TypeOf(frame) == HubMessageTypes.AgentRemoved)));
        await everything.ExpectNoFrameAsync();
    }

    [Fact]
    public async Task An_agent_removed_from_the_scope_stops_the_subscription_of_the_client()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        (ScriptedAgentLink gaming, _, _) = await AddAgentsAsync(rig);
        FleetClient client = await rig.ConnectClientAsync("tags", ["gaming"]);
        _ = FleetClient.DataOf(await client.RequestAsync(FleetClient.Subscribe(Gaming, "cpu.usage")));

        await rig.SetTagsAsync(Gaming, "office");
        _ = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == HubMessageTypes.AgentRemoved);
        await rig.SetTagsAsync(Gaming, "gaming");
        _ = await client.NextWhereAsync(frame => FleetClient.TypeOf(frame) == HubMessageTypes.AgentAdded);
        gaming.SendState(("cpu.usage", ScriptedAgentLink.Number(8)));

        // The old subscription went with the agent: nothing arrives until the client subscribes again.
        await client.ExpectNoFrameForAsync(Office);
    }

    [Fact]
    public async Task An_unknown_agent_is_unknown_agent_and_not_forbidden_for_a_scoped_client()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        _ = await AddAgentsAsync(rig);
        FleetClient client = await rig.ConnectClientAsync("tags", ["gaming"]);

        JsonElement result = await client.RequestAsync(FleetClient.Request(MessageTypes.GetManifest, "no-such-agent"));

        Assert.Equal(HubErrorCodes.UnknownAgent, FleetClient.ErrorCodeOf(result));
    }

    [Fact]
    public async Task A_denied_run_is_audited_as_denied_and_nothing_reaches_the_agent()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        (_, ScriptedAgentLink office, _) = await AddAgentsAsync(rig);
        FleetClient client = await rig.ConnectClientAsync("tags", ["gaming"]);

        JsonElement run = await client.RequestAsync(FleetClient.Run(Office, "lock", "ha-user-1"));

        Assert.Equal(HubErrorCodes.Forbidden, FleetClient.ErrorCodeOf(run));
        Assert.Empty(office.RequestsOf(MessageTypes.Run));
        JsonArray entries = await rig.AuditAsync("fleetRun");
        JsonNode entry = Assert.Single(entries)!;
        Assert.Equal("denied", entry["result"]!.GetValue<string>());
        Assert.Equal("ha-user-1", entry["user"]!.GetValue<string>());
        Assert.Equal("agent:" + Office + ":command:lock", entry["target"]!.GetValue<string>());
    }
}
