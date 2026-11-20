using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Hub.Tests.Fleet;

/// <summary>
/// Commands over the fleet link (T913, PLAN.md S20 "Fleet link"): a <c>run</c> with the agent id in <c>a</c> is forwarded to that agent with the Home Assistant user id, the result of
/// the agent comes back as the result of the request, the agent's errors are passed on, an offline agent is <c>agent_offline</c>, and every run is written to the audit log of the hub.
/// A revoked token cannot run anything any more.
/// </summary>
[Trait("Category", "Fleet")]
public sealed class FleetRunTests
{
    private const string AgentA = "agent-a";
    private const string AgentB = "agent-b";

    [Fact]
    public async Task A_run_is_forwarded_to_the_agent_with_the_user_id_and_the_result_comes_back()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink agent = await rig.AddAgentAsync(AgentA, "Office PC");
        FleetClient client = await rig.ConnectClientAsync();
        var vars = new JsonObject { ["volume"] = 30 };

        JsonElement result = await client.RequestAsync(FleetClient.Run(AgentA, "set_volume", "ha-user-1", vars: vars));

        JsonElement data = FleetClient.DataOf(result);
        Assert.Equal(0, data.GetProperty("exit_code").GetInt32());
        Assert.Equal("done", data.GetProperty("stdout").GetString());
        JsonElement forwarded = Assert.Single(agent.RequestsOf(MessageTypes.Run));
        Assert.Equal("set_volume", forwarded.GetProperty("command").GetString());
        Assert.Equal("ha-user-1", forwarded.GetProperty("context").GetProperty("user_id").GetString());
        Assert.Equal(30, forwarded.GetProperty("vars").GetProperty("volume").GetInt32());
        Assert.False(forwarded.TryGetProperty("a", out _));
    }

    [Fact]
    public async Task A_run_without_a_user_id_carries_the_token_as_the_user()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink agent = await rig.AddAgentAsync(AgentA, "Office PC");
        PairedToken token = await rig.PairAsync();
        FleetClient client = await rig.ConnectClientAsync(token);

        _ = FleetClient.DataOf(await client.RequestAsync(FleetClient.Run(AgentA, "lock")));

        JsonElement forwarded = Assert.Single(agent.RequestsOf(MessageTypes.Run));
        Assert.Equal("fleet:" + token.Id, forwarded.GetProperty("context").GetProperty("user_id").GetString());
    }

    [Fact]
    public async Task A_run_that_does_not_wait_returns_when_the_agent_started_it()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink agent = await rig.AddAgentAsync(AgentA, "Office PC");
        FleetClient client = await rig.ConnectClientAsync();

        JsonElement data = FleetClient.DataOf(await client.RequestAsync(FleetClient.Run(AgentA, "backup", "ha-user-1", wait: false)));

        Assert.Equal("run-1", data.GetProperty("run_id").GetString());
        Assert.False(data.TryGetProperty("exit_code", out _));
        Assert.False(Assert.Single(agent.RequestsOf(MessageTypes.Run)).GetProperty("wait").GetBoolean());
    }

    [Fact]
    public async Task A_run_on_each_of_two_agents_reaches_only_that_agent()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink first = await rig.AddAgentAsync(AgentA, "Office PC");
        ScriptedAgentLink second = await rig.AddAgentAsync(AgentB, "Lab PC");
        FleetClient client = await rig.ConnectClientAsync();

        _ = FleetClient.DataOf(await client.RequestAsync(FleetClient.Run(AgentA, "lock")));
        _ = FleetClient.DataOf(await client.RequestAsync(FleetClient.Run(AgentB, "sleep")));

        Assert.Equal("lock", Assert.Single(first.RequestsOf(MessageTypes.Run)).GetProperty("command").GetString());
        Assert.Equal("sleep", Assert.Single(second.RequestsOf(MessageTypes.Run)).GetProperty("command").GetString());
    }

    [Fact]
    public async Task A_run_that_takes_long_does_not_block_other_requests_of_the_connection()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink agent = await rig.AddAgentAsync(AgentA, "Office PC", metrics: ["cpu.usage"]);
        FleetClient client = await rig.ConnectClientAsync();
        agent.HoldRuns();

        long running = await client.SendRequestAsync(FleetClient.Run(AgentA, "backup", "ha-user-1"));
        JsonElement manifest = await client.RequestAsync(FleetClient.Request(MessageTypes.GetManifest, AgentA));
        agent.ReleaseRuns();
        JsonElement result = await client.AwaitResultAsync(running);

        _ = FleetClient.DataOf(manifest);
        _ = FleetClient.DataOf(result);
    }

    [Fact]
    public async Task The_error_of_the_agent_is_passed_on_with_its_code()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink agent = await rig.AddAgentAsync(AgentA, "Office PC");
        agent.RunError = ErrorCodes.UnknownCommand;
        FleetClient client = await rig.ConnectClientAsync();

        JsonElement result = await client.RequestAsync(FleetClient.Run(AgentA, "nothing", "ha-user-1"));

        Assert.Equal(ErrorCodes.UnknownCommand, FleetClient.ErrorCodeOf(result));
        JsonNode entry = Assert.Single(await rig.AuditAsync("fleetRun"))!;
        Assert.Equal("failure", entry["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_run_on_an_agent_that_is_offline_is_agent_offline()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink agent = await rig.AddAgentAsync(AgentA, "Office PC");
        FleetClient client = await rig.ConnectClientAsync();
        await agent.GoOfflineAsync();

        JsonElement result = await client.RequestAsync(FleetClient.Run(AgentA, "lock", "ha-user-1"));

        Assert.Equal(HubErrorCodes.AgentOffline, FleetClient.ErrorCodeOf(result));
        Assert.Empty(agent.RequestsOf(MessageTypes.Run));
    }

    [Fact]
    public async Task A_run_on_an_agent_that_never_connected_is_agent_offline()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        await rig.EnrollAsync(AgentA, "Office PC");
        FleetClient client = await rig.ConnectClientAsync();

        JsonElement result = await client.RequestAsync(FleetClient.Run(AgentA, "lock"));

        Assert.Equal(HubErrorCodes.AgentOffline, FleetClient.ErrorCodeOf(result));
    }

    [Fact]
    public async Task A_run_with_an_invalid_command_or_user_is_a_bad_request_and_nothing_is_forwarded()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink agent = await rig.AddAgentAsync(AgentA, "Office PC");
        FleetClient client = await rig.ConnectClientAsync();

        JsonElement blank = await client.RequestAsync(FleetClient.Run(AgentA, "   ", "ha-user-1"));
        JsonElement longCommand = await client.RequestAsync(FleetClient.Run(AgentA, new string('x', 257), "ha-user-1"));
        JsonElement longUser = await client.RequestAsync(FleetClient.Run(AgentA, "lock", new string('u', 129)));
        JsonElement controlUser = await client.RequestAsync(FleetClient.Run(AgentA, "lock", "ha\nuser"));

        foreach (JsonElement result in new[] { blank, longCommand, longUser, controlUser })
        {
            Assert.Equal(ErrorCodes.BadRequest, FleetClient.ErrorCodeOf(result));
        }

        Assert.Empty(agent.RequestsOf(MessageTypes.Run));
    }

    [Fact]
    public async Task A_run_is_written_to_the_audit_log_with_the_user_the_agent_and_the_command()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        _ = await rig.AddAgentAsync(AgentA, "Office PC");
        FleetClient client = await rig.ConnectClientAsync();

        _ = FleetClient.DataOf(await client.RequestAsync(FleetClient.Run(AgentA, "lock", "ha-user-1")));

        JsonNode entry = Assert.Single(await rig.AuditAsync("fleetRun"))!;
        Assert.Equal("success", entry["result"]!.GetValue<string>());
        Assert.Equal("ha-user-1", entry["user"]!.GetValue<string>());
        Assert.Equal("agent:" + AgentA + ":command:lock", entry["target"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------
    // Revoked tokens
    // ------------------------------------------------------------------

    [Fact]
    public async Task After_the_token_is_revoked_the_link_is_closed_and_a_new_link_is_refused_with_revoked()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink agent = await rig.AddAgentAsync(AgentA, "Office PC");
        PairedToken token = await rig.PairAsync();
        FleetClient client = await rig.ConnectClientAsync(token);
        _ = FleetClient.DataOf(await client.RequestAsync(FleetClient.Run(AgentA, "lock")));

        using HttpResponseMessage revoke = await rig.Hub.Admin.DeleteAsync("/api/v1/fleet-tokens/" + token.Id);

        Assert.True(revoke.IsSuccessStatusCode);
        Assert.Equal(CloseCodes.Unauthorized, await client.Closed.WaitAsync(TimeSpan.FromSeconds(10)));
        (HttpStatusCode status, string code) = await rig.OpenLinkWithoutUpgradeAsync(token.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(HubErrorCodes.Revoked, code);
        Assert.Single(agent.RequestsOf(MessageTypes.Run));
    }

    [Fact]
    public async Task Fleet_tokens_are_independent_the_other_token_keeps_running_commands()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        ScriptedAgentLink agent = await rig.AddAgentAsync(AgentA, "Office PC");
        PairedToken revoked = await rig.PairAsync();
        PairedToken kept = await rig.PairAsync();
        _ = await rig.ConnectClientAsync(revoked);
        FleetClient client = await rig.ConnectClientAsync(kept);

        using HttpResponseMessage revoke = await rig.Hub.Admin.DeleteAsync("/api/v1/fleet-tokens/" + revoked.Id);
        Assert.True(revoke.IsSuccessStatusCode);

        _ = FleetClient.DataOf(await client.RequestAsync(FleetClient.Run(AgentA, "lock", "ha-user-1")));
        Assert.Single(agent.RequestsOf(MessageTypes.Run));
    }
}
