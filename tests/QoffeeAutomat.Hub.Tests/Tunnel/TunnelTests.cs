using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.History;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Tests.Gateway;
using QoffeeAutomat.Hub.Tests.Security;
using QoffeeAutomat.Hub.Tunnel;

namespace QoffeeAutomat.Hub.Tests.Tunnel;

/// <summary>
/// The management tunnel end to end (T912, PLAN.md S20, S08, S09): a hub in process and a whole agent on the simulated platform that dials it. A request of a hub user
/// goes through the proxy, the agent link and the agent's own request pipeline and back; the role rules, the local switch, the event streams and the offline cases are
/// checked with the real agent, so the agent's routing, authorization and endpoints are the ones that answer.
/// </summary>
[Trait("Category", "Tunnel")]
public sealed class TunnelTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private static async Task<(GatewayRig Rig, SimulatedAgent Agent)> StartAsync()
    {
        GatewayRig rig = await GatewayRig.StartAsync(TunnelKit.Options(), withUsers: true);
        SimulatedAgent agent = await rig.AddAgentAsync();
        Assert.Contains(HubFeatures.MgmtTunnel, rig.Gateway.GetPresence(agent.Id).Features);
        return (rig, agent);
    }

    private static async Task<string> RunIdOfAsync(HttpResponseMessage response)
    {
        JsonNode started = await HubAssert.JsonAsync(response, HttpStatusCode.Accepted);
        return started["runId"]!.GetValue<string>();
    }

    // ------------------------------------------------------------------
    // Roles
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_hub_viewer_reads_the_command_list_of_an_agent_but_gets_403_for_run_and_for_every_write()
    {
        (GatewayRig rig, SimulatedAgent agent) = await StartAsync();
        await using GatewayRig owner = rig;
        HubApiClient viewer = rig.Hub.Viewer;

        using (HttpResponseMessage read = await viewer.GetAsync(TunnelKit.AgentApi(agent.Id, "commands")))
        {
            JsonNode list = await HubAssert.JsonAsync(read);
            Assert.Contains(SimulatedAgent.CommandId, list.ToJsonString(), StringComparison.Ordinal);
        }

        // The hub already refuses: a viewer does not even reach the operator policy of the write routes.
        foreach (HttpMethod method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            using HttpResponseMessage refused = await viewer.SendAsync(method, TunnelKit.AgentApi(agent.Id, "commands/" + SimulatedAgent.CommandId + "/run"), method == HttpMethod.Delete ? null : "{}");
            _ = await HubAssert.ProblemAsync(refused, HttpStatusCode.Forbidden, "forbidden");
        }

        // The agent decides for itself: the same requests, sent to it directly with the role viewer, are refused with 403 as well, and nothing changed.
        foreach ((string method, string path) in new[]
        {
            ("POST", "/api/v1/commands/" + SimulatedAgent.CommandId + "/run"),
            ("POST", "/api/v1/commands/test"),
            ("PUT", "/api/v1/commands/" + SimulatedAgent.CommandId),
            ("PATCH", "/api/v1/settings"),
            ("DELETE", "/api/v1/commands/" + SimulatedAgent.CommandId),
        })
        {
            AgentRequestOutcome outcome = await rig.Gateway.RequestAsync(agent.Id, 0, TunnelKit.RawRequest(method, path, "victor", TunnelRoles.Viewer, new JsonObject()), CancellationToken.None);
            Assert.Equal(403, TunnelKit.StatusOf(outcome));
            Assert.Equal("forbidden", TunnelKit.ProblemCodeOf(outcome));
        }

        Assert.Empty(agent.Launcher.Launches);
        Assert.NotNull(agent.Config.Effective.GetDocument<QoffeeAutomat.Contracts.Config.CommandDocument>(SimulatedAgent.CommandId));
    }

    [Fact]
    public async Task An_operator_runs_a_command_through_the_tunnel_and_the_agent_records_the_hub_and_the_user()
    {
        (GatewayRig rig, SimulatedAgent agent) = await StartAsync();
        await using GatewayRig owner = rig;

        using HttpResponseMessage response = await rig.Hub.Operator.PostAsync(
            TunnelKit.AgentApi(agent.Id, "commands/" + SimulatedAgent.CommandId + "/run"),
            "{\"vars\":{\"Level\":3}}");
        string runId = await RunIdOfAsync(response);

        await TestWait.UntilAsync(() => !agent.Launcher.Launches.IsEmpty, "the command to be launched");
        IRunHistory history = agent.Services.GetRequiredService<IRunHistory>();
        RunRecord? record = null;
        DateTime deadline = DateTime.UtcNow + Limit;
        while (record is null && DateTime.UtcNow < deadline)
        {
            record = await history.GetAsync(runId, CancellationToken.None);
            if (record is null)
            {
                await Task.Delay(20);
            }
        }

        Assert.NotNull(record);
        Assert.Equal("hub", record.Source);
        Assert.Equal("hub-link", record.ClientId);
        Assert.Equal(HubTestHost.OperatorName, record.UserId);
        Assert.Equal(SimulatedAgent.CommandId, record.Command);
    }

    [Fact]
    public async Task An_operator_may_run_but_the_agent_refuses_every_other_write_and_passes_its_own_problems_back()
    {
        (GatewayRig rig, SimulatedAgent agent) = await StartAsync();
        await using GatewayRig owner = rig;
        HubApiClient operatorClient = rig.Hub.Operator;

        // The hub lets operators call the write routes; the agent narrows that down to the run and test endpoints.
        using (HttpResponseMessage replace = await operatorClient.SendAsync(HttpMethod.Put, TunnelKit.AgentApi(agent.Id, "commands/" + SimulatedAgent.CommandId), "{}"))
        {
            JsonNode problem = await HubAssert.ProblemAsync(replace, HttpStatusCode.Forbidden, "forbidden");
            Assert.Contains("operator", problem["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        using (HttpResponseMessage delete = await operatorClient.SendAsync(HttpMethod.Delete, TunnelKit.AgentApi(agent.Id, "commands/" + SimulatedAgent.CommandId)))
        {
            _ = await HubAssert.ProblemAsync(delete, HttpStatusCode.Forbidden, "forbidden");
        }

        Assert.NotNull(agent.Config.Effective.GetDocument<QoffeeAutomat.Contracts.Config.CommandDocument>(SimulatedAgent.CommandId));

        // The endpoint itself answers what it would answer the browser, here 404 for a command that does not exist.
        using HttpResponseMessage unknown = await operatorClient.PostAsync(TunnelKit.AgentApi(agent.Id, "commands/no_such_command/run"), "{}");
        _ = await HubAssert.ProblemAsync(unknown, HttpStatusCode.NotFound, "unknown_command");
    }

    [Fact]
    public async Task An_administrator_may_change_the_agent_through_the_tunnel()
    {
        (GatewayRig rig, SimulatedAgent agent) = await StartAsync();
        await using GatewayRig owner = rig;

        using HttpResponseMessage delete = await rig.Hub.Admin.SendAsync(HttpMethod.Delete, TunnelKit.AgentApi(agent.Id, "commands/" + SimulatedAgent.CommandId));

        HubAssert.Status(delete, HttpStatusCode.NoContent);
        Assert.Null(agent.Config.Effective.GetDocument<QoffeeAutomat.Contracts.Config.CommandDocument>(SimulatedAgent.CommandId));
    }

    [Fact]
    public async Task The_agent_keeps_its_account_and_its_hub_link_to_itself_whatever_the_hub_asks()
    {
        (GatewayRig rig, SimulatedAgent agent) = await StartAsync();
        await using GatewayRig owner = rig;

        foreach ((string method, string path) in new[]
        {
            ("POST", "/api/v1/auth/login"),
            ("POST", "/api/v1/auth/password"),
            ("POST", "/api/v1/auth/setup"),
            ("PATCH", "/api/v1/hub"),
            ("POST", "/api/v1/hub/leave"),
        })
        {
            AgentRequestOutcome outcome = await rig.Gateway.RequestAsync(agent.Id, 0, TunnelKit.RawRequest(method, path, "mallory", TunnelRoles.Admin, new JsonObject()), CancellationToken.None);
            Assert.Equal(403, TunnelKit.StatusOf(outcome));
        }

        Assert.True(agent.Manager.ManagementTunnelEnabled);

        // Reading the hub link stays possible.
        AgentRequestOutcome read = await rig.Gateway.RequestAsync(agent.Id, 0, TunnelKit.RawRequest("GET", "/api/v1/hub", "mallory", TunnelRoles.Viewer), CancellationToken.None);
        Assert.Equal(200, TunnelKit.StatusOf(read));
    }

    // ------------------------------------------------------------------
    // Paths
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/local/emit")]
    [InlineData("/link")]
    [InlineData("/api/v2/commands")]
    [InlineData("/api/v1")]
    [InlineData("/api/v1/../healthz")]
    [InlineData("/api/v1/commands/../../healthz")]
    [InlineData("/api/v1/commands?x=1")]
    [InlineData("/api/v1/commands%2F..%2F..%2Fhealthz")]
    [InlineData("/API/V1/commands")]
    [InlineData("api/v1/commands")]
    [InlineData("")]
    public async Task Paths_outside_api_v1_are_rejected_by_the_agent(string path)
    {
        (GatewayRig rig, SimulatedAgent agent) = await StartAsync();
        await using GatewayRig owner = rig;

        AgentRequestOutcome outcome = await rig.Gateway.RequestAsync(agent.Id, 0, TunnelKit.RawRequest("GET", path, "mallory", TunnelRoles.Admin), CancellationToken.None);

        Assert.False(outcome.Ok, "The path '" + path + "' was served.");
        Assert.Equal(ErrorCodes.BadRequest, outcome.ErrorCode);
    }

    [Fact]
    public async Task A_stream_of_a_path_outside_api_v1_is_rejected_by_the_agent_too()
    {
        (GatewayRig rig, SimulatedAgent agent) = await StartAsync();
        await using GatewayRig owner = rig;

        AgentStreamOutcome outcome = await rig.Gateway.OpenStreamAsync(agent.Id, 0, "/healthz", null, new TunnelPrincipal("mallory", TunnelRoles.Admin), CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal(ErrorCodes.BadRequest, outcome.ErrorCode);
    }

    [Fact]
    public async Task A_role_that_the_agent_does_not_know_gets_nothing()
    {
        (GatewayRig rig, SimulatedAgent agent) = await StartAsync();
        await using GatewayRig owner = rig;

        AgentRequestOutcome outcome = await rig.Gateway.RequestAsync(agent.Id, 0, TunnelKit.RawRequest("GET", "/api/v1/commands", "mallory", "root"), CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal(ErrorCodes.BadRequest, outcome.ErrorCode);
    }

    [Fact]
    public async Task An_endpoint_that_does_not_answer_with_json_is_reported_instead_of_being_garbled()
    {
        (GatewayRig rig, SimulatedAgent agent) = await StartAsync();
        await using GatewayRig owner = rig;

        // The configuration export of the agent is a zip file; the tunnel carries JSON only.
        AgentRequestOutcome outcome = await rig.Gateway.RequestAsync(agent.Id, 0, TunnelKit.RawRequest("GET", "/api/v1/config/export", "alice", TunnelRoles.Viewer), CancellationToken.None);

        Assert.Equal(415, TunnelKit.StatusOf(outcome));
        Assert.Equal("tunnel_unsupported_media", TunnelKit.ProblemCodeOf(outcome));
    }

    // ------------------------------------------------------------------
    // The local switch
    // ------------------------------------------------------------------

    [Fact]
    public async Task With_the_tunnel_switched_off_on_the_agent_every_tunnelled_request_fails_with_a_specific_error_and_the_reason()
    {
        (GatewayRig rig, SimulatedAgent agent) = await StartAsync();
        await using GatewayRig owner = rig;

        await agent.Manager.SetManagementTunnelAsync(false, CancellationToken.None);
        await TestWait.UntilAsync(
            () => rig.Gateway.GetPresence(agent.Id) is { Online: true } presence && !presence.Features.Contains(HubFeatures.MgmtTunnel),
            "the agent to reconnect without the tunnel",
            15000);

        foreach (HubApiClient client in new[] { rig.Hub.Viewer, rig.Hub.Operator, rig.Hub.Admin })
        {
            using HttpResponseMessage read = await client.GetAsync(TunnelKit.AgentApi(agent.Id, "commands"));
            JsonNode problem = await HubAssert.ProblemAsync(read, HttpStatusCode.Forbidden, TunnelProblemCodes.TunnelDisabled);
            Assert.Contains("switched off", problem["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        using (HttpResponseMessage run = await rig.Hub.Operator.PostAsync(TunnelKit.AgentApi(agent.Id, "commands/" + SimulatedAgent.CommandId + "/run"), "{}"))
        {
            _ = await HubAssert.ProblemAsync(run, HttpStatusCode.Forbidden, TunnelProblemCodes.TunnelDisabled);
        }

        using (HttpResponseMessage stream = await rig.Hub.Viewer.OpenStreamAsync(TunnelKit.AgentApi(agent.Id, "metrics/stream?metrics=*")))
        {
            _ = await HubAssert.ProblemAsync(stream, HttpStatusCode.Forbidden, TunnelProblemCodes.TunnelDisabled);
        }

        Assert.Empty(agent.Launcher.Launches);

        // Switched on again, the tunnel works at once.
        await agent.Manager.SetManagementTunnelAsync(true, CancellationToken.None);
        await TestWait.UntilAsync(
            () => rig.Gateway.GetPresence(agent.Id) is { Online: true } presence && presence.Features.Contains(HubFeatures.MgmtTunnel),
            "the agent to reconnect with the tunnel",
            15000);
        using HttpResponseMessage again = await rig.Hub.Viewer.GetAsync(TunnelKit.AgentApi(agent.Id, "commands"));
        HubAssert.Status(again, HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------
    // Offline agents
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_agent_that_went_offline_answers_503_with_agent_offline()
    {
        (GatewayRig rig, SimulatedAgent agent) = await StartAsync();
        await using GatewayRig owner = rig;

        await TunnelKit.GoOfflineAsync(rig, agent);

        using HttpResponseMessage response = await rig.Hub.Viewer.GetAsync(TunnelKit.AgentApi(agent.Id, "commands"));
        _ = await HubAssert.ProblemAsync(response, HttpStatusCode.ServiceUnavailable, HubErrorCodes.AgentOffline);
    }

    // ------------------------------------------------------------------
    // Live output
    // ------------------------------------------------------------------

    [Fact]
    public async Task Live_output_of_a_run_started_through_the_tunnel_is_streamed_to_the_hub_client_while_the_command_runs()
    {
        (GatewayRig rig, SimulatedAgent agent) = await StartAsync();
        await using GatewayRig owner = rig;

        // The launcher holds the command: it is running, and not finished, for as long as the test wants.
        agent.Launcher.Hold();
        try
        {
            using HttpResponseMessage started = await rig.Hub.Operator.PostAsync(
                TunnelKit.AgentApi(agent.Id, "commands/" + SimulatedAgent.CommandId + "/run"),
                "{\"vars\":{\"Level\":2}}");
            string runId = await RunIdOfAsync(started);
            await TestWait.UntilAsync(() => !agent.Launcher.Launches.IsEmpty, "the command to be started");

            // The stream opens while the command is running: the browser has its headers and waits for events.
            await using SseClient browser = await SseClient.OpenAsync(rig.Hub.Viewer, TunnelKit.AgentApi(agent.Id, "runs/" + runId + "/stream"));
            Assert.Equal("text/event-stream", browser.MediaType);
            Task<ReceivedEvent> end = browser.UntilAsync("end", Limit);
            await Task.Delay(200);
            Assert.False(end.IsCompleted, "The end event arrived while the command was still running.");

            agent.Launcher.Release();

            ReceivedEvent finished = await end.WaitAsync(Limit);
            Assert.Contains("0", finished.Data, StringComparison.Ordinal);
            ReceivedEvent closed = await browser.UntilAsync("tunnel_closed", Limit);
            Assert.Contains("completed", closed.Data, StringComparison.Ordinal);
        }
        finally
        {
            agent.Launcher.Release();
        }
    }

    [Fact]
    public async Task A_stream_of_a_run_that_is_over_replays_its_output_and_ends()
    {
        (GatewayRig rig, SimulatedAgent agent) = await StartAsync();
        await using GatewayRig owner = rig;
        using HttpResponseMessage started = await rig.Hub.Operator.PostAsync(
            TunnelKit.AgentApi(agent.Id, "commands/" + SimulatedAgent.CommandId + "/run"),
            "{\"vars\":{\"Level\":4}}");
        string runId = await RunIdOfAsync(started);
        IRunHistory history = agent.Services.GetRequiredService<IRunHistory>();
        await TestWait.UntilAsync(() => history.GetAsync(runId, CancellationToken.None).GetAwaiter().GetResult() is not null, "the run to be in the history", 15000);

        await using SseClient browser = await SseClient.OpenAsync(rig.Hub.Viewer, TunnelKit.AgentApi(agent.Id, "runs/" + runId + "/stream"));

        ReceivedEvent output = await browser.UntilAsync("output", Limit);
        Assert.Contains("ran", output.Data, StringComparison.Ordinal);
        _ = await browser.UntilAsync("end", Limit);
        _ = await browser.UntilAsync("tunnel_closed", Limit);
        Assert.Null(await browser.NextAsync());
    }

    [Fact]
    public async Task A_stream_that_the_endpoint_refuses_returns_the_status_of_the_endpoint()
    {
        (GatewayRig rig, SimulatedAgent agent) = await StartAsync();
        await using GatewayRig owner = rig;

        using HttpResponseMessage response = await rig.Hub.Viewer.OpenStreamAsync(TunnelKit.AgentApi(agent.Id, "runs/no-such-run/stream"));

        JsonNode problem = await HubAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not_found");
        Assert.Contains("no run", problem["detail"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task When_the_agent_goes_offline_during_a_stream_the_hub_client_is_told()
    {
        (GatewayRig rig, SimulatedAgent agent) = await StartAsync();
        await using GatewayRig owner = rig;
        agent.Launcher.Hold();
        try
        {
            using HttpResponseMessage started = await rig.Hub.Operator.PostAsync(
                TunnelKit.AgentApi(agent.Id, "commands/" + SimulatedAgent.CommandId + "/run"),
                "{\"vars\":{\"Level\":1}}");
            string runId = await RunIdOfAsync(started);
            await TestWait.UntilAsync(() => !agent.Launcher.Launches.IsEmpty, "the command to be started");
            await using SseClient browser = await SseClient.OpenAsync(rig.Hub.Viewer, TunnelKit.AgentApi(agent.Id, "runs/" + runId + "/stream"));

            await TunnelKit.GoOfflineAsync(rig, agent);

            ReceivedEvent closed = await browser.UntilAsync("tunnel_closed", Limit);
            Assert.Contains(StreamEndReasons.AgentOffline, closed.Data, StringComparison.Ordinal);
        }
        finally
        {
            agent.Launcher.Release();
        }
    }
}
