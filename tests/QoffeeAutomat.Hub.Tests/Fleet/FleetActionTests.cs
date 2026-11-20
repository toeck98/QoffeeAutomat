using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Fleet.Actions;
using QoffeeAutomat.Hub.Security;
using QoffeeAutomat.Hub.Tests.Security;

namespace QoffeeAutomat.Hub.Tests.Fleet;

/// <summary>
/// Fleet actions (T917, PLAN.md S20 "Fleet actions"): a bulk run reports success, failure or offline for every targeted agent with bounded parallelism and is recorded in the audit
/// log; the update of agents goes one agent after another, waits for each to come back with the target version, and stops at the first that does not, reporting which agents were
/// updated. The agents are played by hand (<see cref="ScriptedAgentLink"/>), so the tests decide when an agent "comes back".
/// </summary>
[Trait("Category", "FleetActions")]
public sealed class FleetActionTests
{
    private const string BulkRuns = "/api/v1/fleet-actions/bulk-runs";
    private const string Updates = "/api/v1/fleet-actions/updates";

    private static Action<IServiceCollection> Fast(int maxRetained = 50, int maxRunning = 4)
    {
        return services =>
        {
            services.RemoveAll<FleetActionOptions>();
            services.AddSingleton(new FleetActionOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(20),
                MaxRetainedActions = maxRetained,
                MaxRunningActions = maxRunning,
            });
        };
    }

    private static JsonObject Bulk(string command, string[]? agents = null, string[]? tags = null, int? parallelism = null, JsonObject? vars = null)
    {
        var body = new JsonObject { ["command"] = command };
        if (agents is not null)
        {
            body["agents"] = Array(agents);
        }

        if (tags is not null)
        {
            body["tags"] = Array(tags);
        }

        if (parallelism is not null)
        {
            body["parallelism"] = parallelism;
        }

        if (vars is not null)
        {
            body["vars"] = vars;
        }

        return body;
    }

    private static JsonObject Update(string[]? agents = null, string[]? tags = null, string? target = "2.0.0", int? timeoutSeconds = null)
    {
        var body = new JsonObject();
        if (agents is not null)
        {
            body["agents"] = Array(agents);
        }

        if (tags is not null)
        {
            body["tags"] = Array(tags);
        }

        if (target is not null)
        {
            body["targetVersion"] = target;
        }

        if (timeoutSeconds is not null)
        {
            body["timeoutSeconds"] = timeoutSeconds;
        }

        return body;
    }

    private static JsonArray Array(string[] values) => new([.. values.Select(value => (JsonNode?)JsonValue.Create(value))]);

    private static async Task<JsonNode> PostAsync(HubApiClient client, string path, JsonObject body, HttpStatusCode expected = HttpStatusCode.Accepted)
    {
        using HttpResponseMessage response = await client.PostAsync(path, body.ToJsonString());
        return await HubAssert.JsonAsync(response, expected);
    }

    private static async Task<JsonNode> PostProblemAsync(HubApiClient client, string path, JsonObject body, HttpStatusCode status, string code)
    {
        using HttpResponseMessage response = await client.PostAsync(path, body.ToJsonString());
        return await HubAssert.ProblemAsync(response, status, code);
    }

    private static async Task<JsonNode> GetAsync(FleetRig rig, string path)
    {
        using HttpResponseMessage response = await rig.Hub.Admin.GetAsync(path);
        return await HubAssert.JsonAsync(response);
    }

    /// <summary>Waits until the action has ended and reads it through the API.</summary>
    private static async Task<JsonNode> EndedAsync(FleetRig rig, JsonNode started)
    {
        string id = started["id"]!.GetValue<string>();
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        FleetActionInfo? ended = await rig.Hub.Service<FleetActionService>().WaitForEndAsync(id, patience.Token);
        Assert.NotNull(ended);
        return await GetAsync(rig, "/api/v1/fleet-actions/" + id);
    }

    private static JsonNode Item(JsonNode action, string agentId)
    {
        return action["items"]!.AsArray().Single(item => item!["agentId"]!.GetValue<string>() == agentId)!;
    }

    private static string State(JsonNode action, string agentId) => Item(action, agentId)["state"]!.GetValue<string>();

    private static int RunRequests(ScriptedAgentLink agent) => agent.RequestsOf(MessageTypes.Run).Length;

    private static bool GotUpdate(ScriptedAgentLink agent)
    {
        return agent.RequestsOf(MessageTypes.Run).Any(frame => frame.GetProperty("command").GetString() == FleetActionService.UpdateCommand);
    }

    /// <summary>Plays an agent that installs its update: it receives the command, says bye, and comes back with the new version.</summary>
    private static async Task InstallAsync(ScriptedAgentLink agent, string version)
    {
        await TestWait.UntilAsync(() => GotUpdate(agent), "the agent " + agent.Id + " to receive the update command", 15000);
        await agent.GoOfflineAsync(ByeReasons.Update);
        agent.Version = version;
        await agent.ReconnectAsync();
    }

    // ------------------------------------------------------------------
    // Bulk runs
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_bulk_run_reports_success_failure_and_offline_for_each_targeted_agent()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        ScriptedAgentLink ok = await rig.AddAgentAsync("pc-ok", "A ok");
        ScriptedAgentLink broken = await rig.AddAgentAsync("pc-broken", "B broken");
        ScriptedAgentLink asleep = await rig.AddAgentAsync("pc-asleep", "C asleep");
        await rig.EnrollAsync("pc-never", "D never connected");
        broken.RunError = ErrorCodes.UnknownCommand;
        await asleep.GoOfflineAsync();

        JsonNode started = await PostAsync(rig.Hub.Admin, BulkRuns, Bulk("lock", agents: ["pc-ok", "pc-broken", "pc-asleep", "pc-never"]));
        JsonNode action = await EndedAsync(rig, started);

        Assert.Equal("bulk_run", action["kind"]!.GetValue<string>());
        Assert.Equal("completed", action["state"]!.GetValue<string>());
        Assert.Equal("lock", action["command"]!.GetValue<string>());
        Assert.Equal("success", State(action, "pc-ok"));
        Assert.Equal(0, Item(action, "pc-ok")["exitCode"]!.GetValue<int>());
        Assert.Equal("failure", State(action, "pc-broken"));
        Assert.Equal(ErrorCodes.UnknownCommand, Item(action, "pc-broken")["errorCode"]!.GetValue<string>());
        Assert.Equal("offline", State(action, "pc-asleep"));
        Assert.Equal(HubErrorCodes.AgentOffline, Item(action, "pc-asleep")["errorCode"]!.GetValue<string>());
        Assert.Equal("offline", State(action, "pc-never"));
        JsonNode counts = action["counts"]!;
        Assert.Equal(1, counts["success"]!.GetValue<int>());
        Assert.Equal(1, counts["failure"]!.GetValue<int>());
        Assert.Equal(2, counts["offline"]!.GetValue<int>());
        Assert.Equal(0, counts["pending"]!.GetValue<int>());
        Assert.Equal(1, RunRequests(ok));
        Assert.Equal(1, RunRequests(broken));
        Assert.Equal(0, RunRequests(asleep));
    }

    [Fact]
    public async Task A_bulk_run_that_waits_reports_the_exit_code_and_the_duration_that_the_agent_gave()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        _ = await rig.AddAgentAsync("pc-1", "PC 1");

        JsonNode started = await PostAsync(rig.Hub.Admin, BulkRuns, Bulk("lock", agents: ["pc-1"]));
        JsonNode action = await EndedAsync(rig, started);

        Assert.Equal("success", State(action, "pc-1"));
        Assert.Equal(12, Item(action, "pc-1")["durationMs"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_bulk_run_by_tag_reaches_the_agents_with_the_tag_and_nobody_else()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        ScriptedAgentLink lab1 = await rig.AddAgentAsync("lab-1", "Lab 1", tags: ["lab"]);
        ScriptedAgentLink lab2 = await rig.AddAgentAsync("lab-2", "Lab 2", tags: ["lab", "office"]);
        ScriptedAgentLink other = await rig.AddAgentAsync("other", "Other", tags: ["home"]);

        JsonNode started = await PostAsync(rig.Hub.Admin, BulkRuns, Bulk("lock", tags: ["lab"]));
        JsonNode action = await EndedAsync(rig, started);

        Assert.Equal(2, action["items"]!.AsArray().Count);
        Assert.Equal("success", State(action, "lab-1"));
        Assert.Equal("success", State(action, "lab-2"));
        Assert.Equal("lab", action["tags"]!.AsArray().Single()!.GetValue<string>());
        Assert.Equal(1, RunRequests(lab1));
        Assert.Equal(1, RunRequests(lab2));
        Assert.Equal(0, RunRequests(other));
    }

    [Fact]
    public async Task A_bulk_run_with_agents_and_tags_targets_the_union_once_each()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        _ = await rig.AddAgentAsync("lab-1", "Lab 1", tags: ["lab"]);
        _ = await rig.AddAgentAsync("solo", "Solo");

        JsonNode started = await PostAsync(rig.Hub.Admin, BulkRuns, Bulk("lock", agents: ["solo", "lab-1", "solo"], tags: ["lab"]));
        JsonNode action = await EndedAsync(rig, started);

        Assert.Equal(["Lab 1", "Solo"], action["items"]!.AsArray().Select(item => item!["agentName"]!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task A_bulk_run_forwards_the_variables_and_the_user_to_each_agent()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        ScriptedAgentLink agent = await rig.AddAgentAsync("pc-1", "PC 1");

        JsonNode started = await PostAsync(rig.Hub.Admin, BulkRuns, Bulk("set_volume", agents: ["pc-1"], vars: new JsonObject { ["volume"] = 30 }));
        _ = await EndedAsync(rig, started);

        JsonElement forwarded = Assert.Single(agent.RequestsOf(MessageTypes.Run));
        Assert.Equal("set_volume", forwarded.GetProperty("command").GetString());
        Assert.Equal(30, forwarded.GetProperty("vars").GetProperty("volume").GetInt32());
        Assert.Equal(HubTestHost.AdminName, forwarded.GetProperty("context").GetProperty("user_id").GetString());
    }

    [Fact]
    public async Task A_bulk_run_never_runs_more_agents_at_the_same_time_than_the_parallelism()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        ScriptedAgentLink[] agents =
        [
            await rig.AddAgentAsync("pc-1", "PC 1"),
            await rig.AddAgentAsync("pc-2", "PC 2"),
            await rig.AddAgentAsync("pc-3", "PC 3"),
        ];
        foreach (ScriptedAgentLink agent in agents)
        {
            agent.HoldRuns();
        }

        JsonNode started = await PostAsync(rig.Hub.Admin, BulkRuns, Bulk("backup", agents: ["pc-1", "pc-2", "pc-3"], parallelism: 1));

        await TestWait.UntilAsync(() => agents.Sum(RunRequests) == 1, "the first agent to get the command", 15000);
        await Task.Delay(300);
        Assert.Equal(1, agents.Sum(RunRequests));
        JsonNode running = await GetAsync(rig, "/api/v1/fleet-actions/" + started["id"]!.GetValue<string>());
        Assert.Equal("running", running["state"]!.GetValue<string>());
        Assert.Equal(1, running["counts"]!["running"]!.GetValue<int>());
        Assert.Equal(2, running["counts"]!["pending"]!.GetValue<int>());

        foreach (ScriptedAgentLink agent in agents)
        {
            agent.ReleaseRuns();
        }

        JsonNode action = await EndedAsync(rig, started);
        Assert.Equal(3, action["counts"]!["success"]!.GetValue<int>());
        Assert.Equal(3, agents.Sum(RunRequests));
    }

    [Fact]
    public async Task A_bulk_run_is_recorded_in_the_audit_log_when_it_starts_and_when_it_ends()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        _ = await rig.AddAgentAsync("pc-ok", "PC ok");
        ScriptedAgentLink broken = await rig.AddAgentAsync("pc-broken", "PC broken");
        broken.RunError = ErrorCodes.CommandDisabled;

        JsonNode started = await PostAsync(rig.Hub.Admin, BulkRuns, Bulk("lock", agents: ["pc-ok", "pc-broken"]));
        string id = started["id"]!.GetValue<string>();
        _ = await EndedAsync(rig, started);

        JsonNode request = Assert.Single(await rig.AuditAsync("startBulkRun"))!;
        Assert.Equal(HubTestHost.AdminName, request["user"]!.GetValue<string>());
        Assert.Equal("success", request["result"]!.GetValue<string>());
        Assert.Equal("fleet-action:" + id + ":command:lock", request["target"]!.GetValue<string>());
        Assert.Equal(["pc-broken", "pc-ok"], request["agents"]!.AsArray().Select(agent => agent!.GetValue<string>()).ToArray());

        JsonNode finished = Assert.Single(await rig.AuditAsync("bulkRunFinished"))!;
        Assert.Equal(HubTestHost.AdminName, finished["user"]!.GetValue<string>());
        Assert.Equal("failure", finished["result"]!.GetValue<string>());
        Assert.Equal("fleet-action:" + id + ":command:lock", finished["target"]!.GetValue<string>());
        Assert.Equal(["pc-broken", "pc-ok"], finished["agents"]!.AsArray().Select(agent => agent!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task A_bulk_run_without_failures_ends_with_a_successful_audit_entry()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        _ = await rig.AddAgentAsync("pc-1", "PC 1");

        _ = await EndedAsync(rig, await PostAsync(rig.Hub.Admin, BulkRuns, Bulk("lock", agents: ["pc-1"])));

        JsonNode finished = Assert.Single(await rig.AuditAsync("bulkRunFinished"))!;
        Assert.Equal("success", finished["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_bulk_run_that_is_cancelled_leaves_the_agents_that_were_not_reached_untouched()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        ScriptedAgentLink first = await rig.AddAgentAsync("pc-1", "PC 1");
        ScriptedAgentLink second = await rig.AddAgentAsync("pc-2", "PC 2");
        first.HoldRuns();
        second.HoldRuns();
        JsonNode started = await PostAsync(rig.Hub.Admin, BulkRuns, Bulk("backup", agents: ["pc-1", "pc-2"], parallelism: 1));
        string id = started["id"]!.GetValue<string>();
        await TestWait.UntilAsync(() => RunRequests(first) + RunRequests(second) == 1, "the first agent to get the command", 15000);

        using HttpResponseMessage cancel = await rig.Hub.Admin.PostAsync("/api/v1/fleet-actions/" + id + "/cancel");
        _ = await HubAssert.JsonAsync(cancel);
        JsonNode action = await EndedAsync(rig, started);

        Assert.Equal("cancelled", action["state"]!.GetValue<string>());
        Assert.Equal(2, action["counts"]!["cancelled"]!.GetValue<int>());
        Assert.Equal(1, RunRequests(first) + RunRequests(second));
        first.ReleaseRuns();
        second.ReleaseRuns();
        using HttpResponseMessage again = await rig.Hub.Admin.PostAsync("/api/v1/fleet-actions/" + id + "/cancel");
        _ = await HubAssert.ProblemAsync(again, HttpStatusCode.Conflict, "not_running");
    }

    [Fact]
    public async Task A_bulk_run_that_the_agent_does_not_answer_in_time_is_a_failure_with_the_code_timeout()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        ScriptedAgentLink slow = await rig.AddAgentAsync("pc-slow", "PC slow");
        slow.HoldRuns();
        var body = Bulk("backup", agents: ["pc-slow"]);
        body["timeoutSeconds"] = 1;

        JsonNode action = await EndedAsync(rig, await PostAsync(rig.Hub.Admin, BulkRuns, body));

        Assert.Equal("failure", State(action, "pc-slow"));
        Assert.Equal(ErrorCodes.Timeout, Item(action, "pc-slow")["errorCode"]!.GetValue<string>());
        slow.ReleaseRuns();
    }

    [Fact]
    public async Task A_bulk_run_with_invalid_input_is_refused_with_a_validation_error_and_nothing_is_sent()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        ScriptedAgentLink agent = await rig.AddAgentAsync("pc-1", "PC 1", tags: ["lab"]);
        (JsonObject Body, string Path, string Code)[] cases =
        [
            (Bulk("lock"), "/agents", "required"),
            (Bulk("   ", agents: ["pc-1"]), "/command", "invalid"),
            (Bulk(new string('x', 257), agents: ["pc-1"]), "/command", "invalid"),
            (Bulk("lock", agents: ["nobody"]), "/agents", "unknown_agent"),
            (Bulk("lock", tags: ["unused"]), "/tags", "no_targets"),
            (Bulk("lock", tags: ["Not A Tag"]), "/tags", "invalid"),
            (Bulk("lock", agents: ["pc-1"], parallelism: 0), "/parallelism", "invalid"),
            (Bulk("lock", agents: ["pc-1"], parallelism: 17), "/parallelism", "invalid"),
        ];

        foreach ((JsonObject body, string path, string code) in cases)
        {
            JsonNode problem = await PostProblemAsync(rig.Hub.Admin, BulkRuns, body, (HttpStatusCode)422, "validation_failed");
            JsonNode error = problem["errors"]!.AsArray().Single()!;
            Assert.Equal(path, error["path"]!.GetValue<string>());
            Assert.Equal(code, error["code"]!.GetValue<string>());
        }

        Assert.Equal(0, RunRequests(agent));
    }

    [Fact]
    public async Task A_bulk_run_is_refused_while_as_many_actions_run_as_the_hub_allows()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast(maxRunning: 1));
        ScriptedAgentLink agent = await rig.AddAgentAsync("pc-1", "PC 1");
        agent.HoldRuns();
        JsonNode first = await PostAsync(rig.Hub.Admin, BulkRuns, Bulk("backup", agents: ["pc-1"]));

        _ = await PostProblemAsync(rig.Hub.Admin, BulkRuns, Bulk("backup", agents: ["pc-1"]), HttpStatusCode.Conflict, "too_many_actions");

        agent.ReleaseRuns();
        _ = await EndedAsync(rig, first);
    }

    [Fact]
    public async Task The_hub_keeps_only_the_newest_finished_actions_in_the_list()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast(maxRetained: 2));
        _ = await rig.AddAgentAsync("pc-1", "PC 1");
        var ids = new List<string>();
        for (int round = 0; round < 4; round++)
        {
            JsonNode started = await PostAsync(rig.Hub.Admin, BulkRuns, Bulk("lock", agents: ["pc-1"]));
            ids.Add(started["id"]!.GetValue<string>());
            _ = await EndedAsync(rig, started);
        }

        JsonNode list = await GetAsync(rig, "/api/v1/fleet-actions");

        string[] listed = [.. list["actions"]!.AsArray().Select(action => action!["id"]!.GetValue<string>())];
        Assert.Equal(3, listed.Length);
        Assert.Equal(ids[3], listed[0]);
        Assert.DoesNotContain(ids[0], listed);
        using HttpResponseMessage gone = await rig.Hub.Admin.GetAsync("/api/v1/fleet-actions/" + ids[0]);
        _ = await HubAssert.ProblemAsync(gone, HttpStatusCode.NotFound, "unknown_action");
    }

    // ------------------------------------------------------------------
    // Update orchestration
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_update_goes_one_agent_after_another_and_waits_for_each_to_come_back_with_the_new_version()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        ScriptedAgentLink first = await rig.AddAgentAsync("pc-1", "A first");
        ScriptedAgentLink second = await rig.AddAgentAsync("pc-2", "B second");
        ScriptedAgentLink third = await rig.AddAgentAsync("pc-3", "C third");

        JsonNode started = await PostAsync(rig.Hub.Admin, Updates, Update(agents: ["pc-1", "pc-2", "pc-3"]));

        await TestWait.UntilAsync(() => GotUpdate(first), "the first agent to get the update command", 15000);
        await Task.Delay(200);
        Assert.False(GotUpdate(second), "the second agent must wait for the first to come back");
        Assert.False(GotUpdate(third));
        await InstallAsync(first, "2.0.0");
        await TestWait.UntilAsync(() => GotUpdate(second), "the second agent to get the update command", 15000);
        Assert.False(GotUpdate(third), "the third agent must wait for the second to come back");
        await InstallAsync(second, "2.0.0");
        await InstallAsync(third, "2.0.1");
        JsonNode action = await EndedAsync(rig, started);

        Assert.Equal("update", action["kind"]!.GetValue<string>());
        Assert.Equal("completed", action["state"]!.GetValue<string>());
        Assert.Equal("2.0.0", action["targetVersion"]!.GetValue<string>());
        Assert.Equal(["pc-1", "pc-2", "pc-3"], action["updated"]!.AsArray().Select(agent => agent!.GetValue<string>()).ToArray());
        Assert.Equal(3, action["counts"]!["success"]!.GetValue<int>());
        Assert.Equal("1.0.0", Item(action, "pc-1")["versionBefore"]!.GetValue<string>());
        Assert.Equal("2.0.0", Item(action, "pc-1")["versionAfter"]!.GetValue<string>());
        Assert.Equal("2.0.1", Item(action, "pc-3")["versionAfter"]!.GetValue<string>());
        Assert.Null(action["stoppedAt"]);
        JsonNode finished = Assert.Single(await rig.AuditAsync("fleetUpdateFinished"))!;
        Assert.Equal("success", finished["result"]!.GetValue<string>());
        Assert.Equal(["pc-1", "pc-2", "pc-3"], finished["agents"]!.AsArray().Select(agent => agent!.GetValue<string>()).ToArray());
        Assert.Single(await rig.AuditAsync("startFleetUpdate"));
    }

    [Fact]
    public async Task An_update_does_not_wait_for_the_end_of_the_command_because_the_agent_goes_away_while_it_installs()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        ScriptedAgentLink agent = await rig.AddAgentAsync("pc-1", "PC 1");

        JsonNode started = await PostAsync(rig.Hub.Admin, Updates, Update(agents: ["pc-1"]));
        await InstallAsync(agent, "2.0.0");
        _ = await EndedAsync(rig, started);

        JsonElement run = Assert.Single(agent.RequestsOf(MessageTypes.Run));
        Assert.Equal(FleetActionService.UpdateCommand, run.GetProperty("command").GetString());
        Assert.False(run.GetProperty("wait").GetBoolean());
    }

    [Fact]
    public async Task An_update_stops_after_the_first_agent_that_does_not_come_back_and_reports_which_agents_were_updated()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        ScriptedAgentLink first = await rig.AddAgentAsync("pc-1", "A first");
        ScriptedAgentLink second = await rig.AddAgentAsync("pc-2", "B second");
        ScriptedAgentLink third = await rig.AddAgentAsync("pc-3", "C third");

        // The first agent needs time to say bye and reconnect inside the timeout; the second one then waits it out.
        JsonNode started = await PostAsync(rig.Hub.Admin, Updates, Update(agents: ["pc-1", "pc-2", "pc-3"], timeoutSeconds: 3));
        await InstallAsync(first, "2.0.0");

        // The second agent receives the command and never comes back.
        await TestWait.UntilAsync(() => GotUpdate(second), "the second agent to get the update command", 15000);
        JsonNode action = await EndedAsync(rig, started);

        Assert.Equal("stopped", action["state"]!.GetValue<string>());
        Assert.Equal("pc-2", action["stoppedAt"]!.GetValue<string>());
        Assert.Equal(["pc-1"], action["updated"]!.AsArray().Select(agent => agent!.GetValue<string>()).ToArray());
        Assert.Equal("success", State(action, "pc-1"));
        Assert.Equal("failure", State(action, "pc-2"));
        Assert.Equal("version_not_reached", Item(action, "pc-2")["errorCode"]!.GetValue<string>());
        Assert.Contains("2.0.0", action["stopReason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("skipped", State(action, "pc-3"));
        Assert.Equal("not_reached", Item(action, "pc-3")["errorCode"]!.GetValue<string>());
        Assert.False(GotUpdate(third), "an agent behind the one that failed must not be touched");
        JsonNode finished = Assert.Single(await rig.AuditAsync("fleetUpdateFinished"))!;
        Assert.Equal("failure", finished["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_agent_that_comes_back_with_the_old_version_has_not_been_updated()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        ScriptedAgentLink first = await rig.AddAgentAsync("pc-1", "A first");
        ScriptedAgentLink second = await rig.AddAgentAsync("pc-2", "B second");

        JsonNode started = await PostAsync(rig.Hub.Admin, Updates, Update(agents: ["pc-1", "pc-2"], timeoutSeconds: 3));
        await InstallAsync(first, "1.0.0");
        JsonNode action = await EndedAsync(rig, started);

        Assert.Equal("stopped", action["state"]!.GetValue<string>());
        Assert.Equal("pc-1", action["stoppedAt"]!.GetValue<string>());
        Assert.Empty(action["updated"]!.AsArray());
        Assert.Equal("failure", State(action, "pc-1"));
        Assert.Equal("1.0.0", Item(action, "pc-1")["versionAfter"]!.GetValue<string>());
        Assert.False(GotUpdate(second));
    }

    [Fact]
    public async Task An_agent_that_refuses_the_update_command_stops_the_update()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        ScriptedAgentLink first = await rig.AddAgentAsync("pc-1", "A first");
        ScriptedAgentLink second = await rig.AddAgentAsync("pc-2", "B second");
        first.RunError = ErrorCodes.UnknownCommand;

        JsonNode action = await EndedAsync(rig, await PostAsync(rig.Hub.Admin, Updates, Update(agents: ["pc-1", "pc-2"])));

        Assert.Equal("stopped", action["state"]!.GetValue<string>());
        Assert.Equal("pc-1", action["stoppedAt"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.UnknownCommand, Item(action, "pc-1")["errorCode"]!.GetValue<string>());
        Assert.Equal("skipped", State(action, "pc-2"));
        Assert.False(GotUpdate(second));
    }

    [Fact]
    public async Task An_update_leaves_offline_agents_and_agents_that_have_the_version_already_and_goes_on()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        ScriptedAgentLink asleep = await rig.AddAgentAsync("pc-1", "A asleep");
        ScriptedAgentLink current = await rig.AddAgentAsync("pc-2", "B current", version: "2.0.0");
        ScriptedAgentLink old = await rig.AddAgentAsync("pc-3", "C old");
        await asleep.GoOfflineAsync();

        JsonNode started = await PostAsync(rig.Hub.Admin, Updates, Update(agents: ["pc-1", "pc-2", "pc-3"]));
        await InstallAsync(old, "2.0.0");
        JsonNode action = await EndedAsync(rig, started);

        Assert.Equal("completed", action["state"]!.GetValue<string>());
        Assert.Equal("offline", State(action, "pc-1"));
        Assert.Equal("skipped", State(action, "pc-2"));
        Assert.Equal("already_current", Item(action, "pc-2")["errorCode"]!.GetValue<string>());
        Assert.Equal("success", State(action, "pc-3"));
        Assert.Equal(["pc-3"], action["updated"]!.AsArray().Select(agent => agent!.GetValue<string>()).ToArray());
        Assert.Equal(0, RunRequests(asleep));
        Assert.Equal(0, RunRequests(current));
    }

    [Fact]
    public async Task An_update_by_tag_updates_the_agents_with_the_tag_in_the_order_of_their_names()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        ScriptedAgentLink zulu = await rig.AddAgentAsync("pc-z", "Zulu", tags: ["lab"]);
        ScriptedAgentLink alpha = await rig.AddAgentAsync("pc-a", "alpha", tags: ["lab"]);
        ScriptedAgentLink other = await rig.AddAgentAsync("pc-o", "Other", tags: ["home"]);

        JsonNode started = await PostAsync(rig.Hub.Admin, Updates, Update(tags: ["lab"]));
        await InstallAsync(alpha, "2.0.0");
        await InstallAsync(zulu, "2.0.0");
        JsonNode action = await EndedAsync(rig, started);

        Assert.Equal(["pc-a", "pc-z"], action["updated"]!.AsArray().Select(agent => agent!.GetValue<string>()).ToArray());
        Assert.Equal(0, RunRequests(other));
    }

    [Fact]
    public async Task Only_one_update_runs_at_a_time_and_an_administrator_can_cancel_it()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        ScriptedAgentLink first = await rig.AddAgentAsync("pc-1", "A first");
        ScriptedAgentLink second = await rig.AddAgentAsync("pc-2", "B second");
        JsonNode started = await PostAsync(rig.Hub.Admin, Updates, Update(agents: ["pc-1", "pc-2"], timeoutSeconds: 300));
        await TestWait.UntilAsync(() => GotUpdate(first), "the first agent to get the update command", 15000);

        _ = await PostProblemAsync(rig.Hub.Admin, Updates, Update(agents: ["pc-2"]), HttpStatusCode.Conflict, "update_in_progress");
        using HttpResponseMessage cancel = await rig.Hub.Admin.PostAsync("/api/v1/fleet-actions/" + started["id"]!.GetValue<string>() + "/cancel");
        _ = await HubAssert.JsonAsync(cancel);
        JsonNode action = await EndedAsync(rig, started);

        Assert.Equal("cancelled", action["state"]!.GetValue<string>());
        Assert.Equal("cancelled", State(action, "pc-1"));
        Assert.Equal("cancelled", State(action, "pc-2"));
        Assert.Empty(action["updated"]!.AsArray());
        Assert.False(GotUpdate(second));
    }

    [Fact]
    public async Task An_update_without_a_target_version_uses_the_newest_version_that_an_agent_reports()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        ScriptedAgentLink agent = await rig.AddAgentAsync("pc-1", "PC 1", metrics: [FleetActionService.LatestVersionMetric]);
        agent.SendState((FleetActionService.LatestVersionMetric, ScriptedAgentLink.Text("2.5.0")));
        await TestWait.UntilAsync(() => rig.Gateway.GetState("pc-1")?.Values.ContainsKey(FleetActionService.LatestVersionMetric) == true, "the hub to see the newest version", 15000);

        JsonNode started = await PostAsync(rig.Hub.Admin, Updates, Update(agents: ["pc-1"], target: null));
        await InstallAsync(agent, "2.5.0");
        JsonNode action = await EndedAsync(rig, started);

        Assert.Equal("2.5.0", action["targetVersion"]!.GetValue<string>());
        Assert.Equal("completed", action["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_update_needs_a_target_version_when_no_agent_reports_a_newest_one()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        _ = await rig.AddAgentAsync("pc-1", "PC 1");

        JsonNode problem = await PostProblemAsync(rig.Hub.Admin, Updates, Update(agents: ["pc-1"], target: null), (HttpStatusCode)422, "validation_failed");

        Assert.Equal("unknown_latest", problem["errors"]!.AsArray().Single()!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_update_with_invalid_input_is_refused_and_nothing_is_sent()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        ScriptedAgentLink agent = await rig.AddAgentAsync("pc-1", "PC 1");
        (JsonObject Body, string Path, string Code)[] cases =
        [
            (Update(), "/agents", "required"),
            (Update(agents: ["nobody"]), "/agents", "unknown_agent"),
            (Update(agents: ["pc-1"], target: "latest"), "/targetVersion", "invalid"),
            (Update(agents: ["pc-1"], timeoutSeconds: 0), "/timeoutSeconds", "invalid"),
            (Update(agents: ["pc-1"], timeoutSeconds: 4000), "/timeoutSeconds", "invalid"),
        ];

        foreach ((JsonObject body, string path, string code) in cases)
        {
            JsonNode problem = await PostProblemAsync(rig.Hub.Admin, Updates, body, (HttpStatusCode)422, "validation_failed");
            JsonNode error = problem["errors"]!.AsArray().Single()!;
            Assert.Equal(path, error["path"]!.GetValue<string>());
            Assert.Equal(code, error["code"]!.GetValue<string>());
        }

        Assert.Equal(0, RunRequests(agent));
    }

    // ------------------------------------------------------------------
    // Versions
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_versions_of_the_agents_are_shown_against_the_newest_release_that_an_agent_reports()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        ScriptedAgentLink old = await rig.AddAgentAsync("pc-1", "A old", metrics: [FleetActionService.LatestVersionMetric]);
        _ = await rig.AddAgentAsync("pc-2", "B current", version: "2.0.0");
        ScriptedAgentLink asleep = await rig.AddAgentAsync("pc-3", "C asleep");
        old.SendState((FleetActionService.LatestVersionMetric, ScriptedAgentLink.Text("2.0.0")));
        await TestWait.UntilAsync(() => rig.Gateway.GetState("pc-1")?.Values.ContainsKey(FleetActionService.LatestVersionMetric) == true, "the hub to see the newest version", 15000);
        await asleep.GoOfflineAsync();

        JsonNode overview = await GetAsync(rig, "/api/v1/fleet-versions");

        Assert.Equal("2.0.0", overview["latestVersion"]!.GetValue<string>());
        JsonArray agents = overview["agents"]!.AsArray();
        JsonNode Agent(string id) => agents.Single(agent => agent!["agentId"]!.GetValue<string>() == id)!;
        Assert.Equal("outdated", Agent("pc-1")["state"]!.GetValue<string>());
        Assert.Equal("1.0.0", Agent("pc-1")["version"]!.GetValue<string>());
        Assert.Equal("current", Agent("pc-2")["state"]!.GetValue<string>());
        Assert.False(Agent("pc-3")["online"]!.GetValue<bool>());
        Assert.False(Agent("pc-3")["canUpdate"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Without_a_reported_newest_release_every_agent_is_unknown()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());
        _ = await rig.AddAgentAsync("pc-1", "PC 1");

        JsonNode overview = await GetAsync(rig, "/api/v1/fleet-versions");

        Assert.Null(overview["latestVersion"]);
        Assert.Equal("unknown", overview["agents"]!.AsArray().Single()!["state"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------
    // Roles
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_operator_runs_and_cancels_bulk_runs_but_does_not_update_agents_and_a_viewer_only_reads()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast(), withUsers: true);
        ScriptedAgentLink agent = await rig.AddAgentAsync("pc-1", "PC 1");
        HubApiClient operatorClient = rig.Hub.ClientFor(HubRoles.Operator);
        HubApiClient viewer = rig.Hub.ClientFor(HubRoles.Viewer);

        agent.HoldRuns();
        JsonNode bulk = await PostAsync(operatorClient, BulkRuns, Bulk("backup", agents: ["pc-1"]));
        Assert.Equal(HubTestHost.OperatorName, bulk["user"]!.GetValue<string>());
        using (HttpResponseMessage viewerCancel = await viewer.PostAsync("/api/v1/fleet-actions/" + bulk["id"]!.GetValue<string>() + "/cancel"))
        {
            _ = await HubAssert.ProblemAsync(viewerCancel, HttpStatusCode.Forbidden, "forbidden");
        }

        using (HttpResponseMessage operatorCancel = await operatorClient.PostAsync("/api/v1/fleet-actions/" + bulk["id"]!.GetValue<string>() + "/cancel"))
        {
            _ = await HubAssert.JsonAsync(operatorCancel);
        }

        _ = await PostProblemAsync(operatorClient, Updates, Update(agents: ["pc-1"]), HttpStatusCode.Forbidden, "forbidden");
        _ = await PostProblemAsync(viewer, BulkRuns, Bulk("backup", agents: ["pc-1"]), HttpStatusCode.Forbidden, "forbidden");
        using HttpResponseMessage list = await viewer.GetAsync("/api/v1/fleet-actions");
        Assert.Single((await HubAssert.JsonAsync(list))["actions"]!.AsArray());
        using HttpResponseMessage versions = await viewer.GetAsync("/api/v1/fleet-versions");
        _ = await HubAssert.JsonAsync(versions);
        agent.ReleaseRuns();
    }

    [Fact]
    public async Task An_operator_cannot_cancel_an_update_of_agents()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast(), withUsers: true);
        ScriptedAgentLink agent = await rig.AddAgentAsync("pc-1", "PC 1");
        JsonNode started = await PostAsync(rig.Hub.Admin, Updates, Update(agents: ["pc-1"], timeoutSeconds: 300));
        string id = started["id"]!.GetValue<string>();
        await TestWait.UntilAsync(() => GotUpdate(agent), "the agent to get the update command", 15000);

        using (HttpResponseMessage refused = await rig.Hub.ClientFor(HubRoles.Operator).PostAsync("/api/v1/fleet-actions/" + id + "/cancel"))
        {
            _ = await HubAssert.ProblemAsync(refused, HttpStatusCode.Forbidden, "admin_required");
        }

        Assert.Equal("running", (await GetAsync(rig, "/api/v1/fleet-actions/" + id))["state"]!.GetValue<string>());
        using HttpResponseMessage cancel = await rig.Hub.Admin.PostAsync("/api/v1/fleet-actions/" + id + "/cancel");
        _ = await HubAssert.JsonAsync(cancel);
        _ = await EndedAsync(rig, started);
    }

    [Fact]
    public async Task An_unknown_action_is_404_to_read_and_to_cancel()
    {
        await using FleetRig rig = await FleetRig.StartAsync(Fast());

        using HttpResponseMessage read = await rig.Hub.Admin.GetAsync("/api/v1/fleet-actions/nothing");
        _ = await HubAssert.ProblemAsync(read, HttpStatusCode.NotFound, "unknown_action");
        using HttpResponseMessage cancel = await rig.Hub.Admin.PostAsync("/api/v1/fleet-actions/nothing/cancel");
        _ = await HubAssert.ProblemAsync(cancel, HttpStatusCode.NotFound, "unknown_action");
    }
}
