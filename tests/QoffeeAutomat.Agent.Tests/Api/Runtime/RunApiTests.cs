using System.Net;
using System.Text.Json.Nodes;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Execution;

namespace QoffeeAutomat.Agent.Tests.Api.Runtime;

/// <summary>The run endpoints of the management API (T214): start, live output, history and the test run of a draft.</summary>
[Trait("Category", "ApiRuntime")]
public sealed class RunApiTests
{
    private const string Id = "say_hello";

    [Fact]
    public async Task A_run_started_through_the_api_streams_its_output_while_it_is_running()
    {
        var runner = new ControlledProcessRunner();
        await using ApiTestHost host = await RuntimeHost.StartAsync(runner);
        await host.CreateCommandAsync(ApiSamples.Command(Id));

        using HttpResponseMessage started = await host.PostAsync("/api/v1/commands/" + Id + "/run", "{}");
        JsonNode accepted = await ApiAssert.JsonAsync(started, HttpStatusCode.Accepted);
        string runId = accepted["runId"]!.GetValue<string>();
        Assert.Equal("ui", accepted["source"]!.GetValue<string>());
        Assert.Equal("/api/v1/runs/" + runId, started.Headers.Location?.OriginalString);
        ControlledProcessRun process = await runner.WaitForRunAsync();

        await using ServerEventReader stream = await ServerEventReader.OpenAsync(host, "/api/v1/runs/" + runId + "/stream");
        Assert.Equal("text/event-stream", stream.Response.Content.Headers.ContentType?.MediaType);

        // The first piece arrives while the process is still running.
        process.Emit(ProcessOutputSource.StandardOutput, "first line\n");
        ServerEvent first = await stream.ReadAsync();
        Assert.Equal("output", first.Name);
        Assert.Equal("stdout", first.Data["stream"]!.GetValue<string>());
        Assert.Equal("first line\n", first.Data["text"]!.GetValue<string>());
        using (HttpResponseMessage whileRunning = await host.GetAsync("/api/v1/runs/" + runId))
        {
            JsonNode running = await ApiAssert.JsonAsync(whileRunning);
            Assert.Equal("running", running["state"]!.GetValue<string>());
        }

        Assert.Single(host.Service<CommandEngine>().ActiveRuns);

        process.Emit(ProcessOutputSource.StandardError, "warning\n");
        ServerEvent second = await stream.ReadAsync();
        Assert.Equal("stderr", second.Data["stream"]!.GetValue<string>());
        Assert.Equal("warning\n", second.Data["text"]!.GetValue<string>());

        process.Finish(0, "first line\n", "warning\n");
        ServerEvent end = await stream.ReadAsync();
        Assert.Equal("end", end.Name);
        Assert.Equal(runId, end.Data["runId"]!.GetValue<string>());
        Assert.True(end.Data["succeeded"]!.GetValue<bool>());
        Assert.Equal(0, end.Data["exitCode"]!.GetValue<int>());
        Assert.True(await stream.HasEndedAsync());

        // The finished run is in the history with its output.
        await WaitUntilFinishedAsync(host, runId);
        using HttpResponseMessage finished = await host.GetAsync("/api/v1/runs/" + runId);
        JsonNode detail = await ApiAssert.JsonAsync(finished);
        Assert.Equal("first line\n", detail["standardOutput"]!.GetValue<string>());
        Assert.Equal("warning\n", detail["standardError"]!.GetValue<string>());
        Assert.Equal(0, detail["exitCode"]!.GetValue<int>());
        Assert.Equal(Id, detail["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_subscriber_that_arrives_late_gets_the_output_from_the_start()
    {
        var runner = new ControlledProcessRunner();
        await using ApiTestHost host = await RuntimeHost.StartAsync(runner);
        await host.CreateCommandAsync(ApiSamples.Command(Id));
        using HttpResponseMessage started = await host.PostAsync("/api/v1/commands/" + Id + "/run");
        string runId = (await ApiAssert.JsonAsync(started, HttpStatusCode.Accepted))["runId"]!.GetValue<string>();
        ControlledProcessRun process = await runner.WaitForRunAsync();
        process.Emit(ProcessOutputSource.StandardOutput, "early\n");
        process.Emit(ProcessOutputSource.StandardOutput, "earlier still\n");
        await Task.Delay(50);

        await using ServerEventReader stream = await ServerEventReader.OpenAsync(host, "/api/v1/runs/" + runId + "/stream");
        ServerEvent first = await stream.ReadAsync();
        ServerEvent second = await stream.ReadAsync();
        process.Emit(ProcessOutputSource.StandardOutput, "late\n");
        ServerEvent third = await stream.ReadAsync();
        process.Finish(0, "early\nearlier still\nlate\n", string.Empty);
        ServerEvent end = await stream.ReadAsync();

        Assert.Equal("early\n", first.Data["text"]!.GetValue<string>());
        Assert.Equal("earlier still\n", second.Data["text"]!.GetValue<string>());
        Assert.Equal("late\n", third.Data["text"]!.GetValue<string>());
        Assert.Equal("end", end.Name);
    }

    [Fact]
    public async Task The_stream_of_a_finished_run_replays_the_history_and_an_unknown_run_is_a_404()
    {
        var runner = new ControlledProcessRunner(autoFinish: true);
        await using ApiTestHost host = await RuntimeHost.StartAsync(runner);
        await host.CreateCommandAsync(ApiSamples.Command(Id));
        string runId = await RunAndWaitAsync(host, Id);

        await using ServerEventReader stream = await ServerEventReader.OpenAsync(host, "/api/v1/runs/" + runId + "/stream");
        ServerEvent output = await stream.ReadAsync();
        ServerEvent end = await stream.ReadAsync();
        using HttpResponseMessage unknown = await host.GetAsync("/api/v1/runs/0123456789abcdef/stream");
        using HttpResponseMessage unknownRun = await host.GetAsync("/api/v1/runs/0123456789abcdef");

        Assert.Equal("output", output.Name);
        Assert.Equal("done\n", output.Data["text"]!.GetValue<string>());
        Assert.Equal("end", end.Name);
        Assert.True(end.Data["succeeded"]!.GetValue<bool>());
        await ApiAssert.ProblemAsync(unknown, HttpStatusCode.NotFound, "unknown_run");
        await ApiAssert.ProblemAsync(unknownRun, HttpStatusCode.NotFound, "unknown_run");
    }

    [Fact]
    public async Task The_history_is_paged_and_can_be_filtered()
    {
        var runner = new ControlledProcessRunner(autoFinish: true);
        await using ApiTestHost host = await RuntimeHost.StartAsync(runner);
        await host.CreateCommandAsync(ApiSamples.Command(Id));
        await host.CreateCommandAsync(ApiSamples.Command("other_one"));
        await RunAndWaitAsync(host, Id);
        await RunAndWaitAsync(host, "other_one");
        runner.ExitCode = 3;
        string failedId = await RunAndWaitAsync(host, Id);

        JsonNode all = await ListAsync(host, string.Empty);
        JsonNode firstPage = await ListAsync(host, "?limit=2");
        JsonNode secondPage = await ListAsync(host, "?limit=2&offset=2");
        JsonNode failed = await ListAsync(host, "?failed=true");
        JsonNode other = await ListAsync(host, "?command=other_one");
        JsonNode none = await ListAsync(host, "?command=nothing_here");
        JsonNode ui = await ListAsync(host, "?source=ui");

        Assert.Equal(3, all["runs"]!.AsArray().Count);
        Assert.Equal(failedId, all["runs"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(2, firstPage["runs"]!.AsArray().Count);
        Assert.True(firstPage["hasMore"]!.GetValue<bool>());
        Assert.Equal(2, firstPage["limit"]!.GetValue<int>());
        Assert.Single(secondPage["runs"]!.AsArray());
        Assert.False(secondPage["hasMore"]!.GetValue<bool>());
        Assert.Equal(2, secondPage["offset"]!.GetValue<int>());
        JsonNode failedRun = Assert.Single(failed["runs"]!.AsArray())!;
        Assert.Equal(failedId, failedRun["id"]!.GetValue<string>());
        Assert.Equal(3, failedRun["exitCode"]!.GetValue<int>());
        Assert.False(failedRun["succeeded"]!.GetValue<bool>());
        Assert.Equal("other_one", Assert.Single(other["runs"]!.AsArray())!["command"]!.GetValue<string>());
        Assert.Empty(none["runs"]!.AsArray());
        Assert.Equal(3, ui["runs"]!.AsArray().Count);

        string[] invalidQueries = ["?limit=0", "?limit=500", "?offset=-1"];
        foreach (string query in invalidQueries)
        {
            using HttpResponseMessage invalid = await host.GetAsync("/api/v1/runs" + query);
            await ApiAssert.ProblemAsync(invalid, HttpStatusCode.BadRequest, "invalid_query");
        }
    }

    [Fact]
    public async Task A_test_run_of_a_draft_runs_without_saving_is_recorded_as_a_test_run_and_publishes_no_events()
    {
        var runner = new ControlledProcessRunner(autoFinish: true);
        await using ApiTestHost host = await RuntimeHost.StartAsync(runner);
        await host.CreateCommandAsync(ApiSamples.Command(Id));

        // The draft is disabled and unsaved: it still runs.
        using HttpResponseMessage test = await host.PostAsync("/api/v1/commands/test", "{\"command\":" + ApiSamples.Command("draft_cmd", enabled: false) + "}");
        JsonNode accepted = await ApiAssert.JsonAsync(test, HttpStatusCode.Accepted);
        string testRunId = accepted["runId"]!.GetValue<string>();
        Assert.Equal("test", accepted["source"]!.GetValue<string>());
        Assert.Equal("draft_cmd", accepted["command"]!.GetValue<string>());
        await WaitUntilFinishedAsync(host, testRunId);
        string normalRunId = await RunAndWaitAsync(host, Id);

        using HttpResponseMessage record = await host.GetAsync("/api/v1/runs/" + testRunId);
        JsonNode detail = await ApiAssert.JsonAsync(record);
        Assert.Equal("test", detail["source"]!.GetValue<string>());
        Assert.Equal("draft_cmd", detail["command"]!.GetValue<string>());
        Assert.Equal("done\n", detail["standardOutput"]!.GetValue<string>());
        JsonNode tests = await ListAsync(host, "?source=test");
        Assert.Equal(testRunId, Assert.Single(tests["runs"]!.AsArray())!["id"]!.GetValue<string>());

        // Nothing was saved ...
        using HttpResponseMessage notSaved = await host.GetAsync("/api/v1/commands/draft_cmd");
        Assert.Equal(HttpStatusCode.NotFound, notSaved.StatusCode);
        Assert.Null(host.Service<CommandEngine>().Registry.TryGet("draft_cmd"));

        // ... and the saved command's run, but not the test run, was announced.
        string[] finished = await FinishedEventCommandsAsync(host);
        Assert.Contains(Id, finished);
        Assert.DoesNotContain("draft_cmd", finished);
        Assert.NotEqual(testRunId, normalRunId);
    }

    [Fact]
    public async Task A_draft_that_is_not_valid_is_refused_with_paths_into_the_request_body()
    {
        await using ApiTestHost host = await RuntimeHost.StartAsync(new ControlledProcessRunner(autoFinish: true));
        const string draft = """
            {"command":{"id":"draft_cmd","name":"Draft","enabled":true,"exposeToHa":false,"runAs":"root","action":{"type":"process","file":"x"}}}
            """;

        using HttpResponseMessage invalid = await host.PostAsync("/api/v1/commands/test", draft);
        using HttpResponseMessage missing = await host.PostAsync("/api/v1/commands/test", "{}");

        JsonNode problem = await ApiAssert.ProblemAsync(invalid, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Contains("/command/runAs", ApiAssert.ErrorPaths(problem));
        JsonNode noDraft = await ApiAssert.ProblemAsync(missing, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Contains("/command", ApiAssert.ErrorPaths(noDraft));
        using HttpResponseMessage runs = await host.GetAsync("/api/v1/runs");
        Assert.Empty((await ApiAssert.JsonAsync(runs))["runs"]!.AsArray());
    }

    [Fact]
    public async Task Invalid_variables_are_refused_with_422_and_the_path_of_the_variable_and_the_rejection_is_recorded()
    {
        var runner = new ControlledProcessRunner(autoFinish: true);
        await using ApiTestHost host = await RuntimeHost.StartAsync(runner);
        await host.CreateCommandAsync(ApiSamples.CommandWithLevel("set_level"));

        using HttpResponseMessage tooHigh = await host.PostAsync("/api/v1/commands/set_level/run", "{\"vars\":{\"Level\":99}}");
        using HttpResponseMessage missing = await host.PostAsync("/api/v1/commands/set_level/run", "{}");
        using HttpResponseMessage unknownVariable = await host.PostAsync("/api/v1/commands/set_level/run", "{\"vars\":{\"Level\":3,\"Nope\":1}}");
        using HttpResponseMessage good = await host.PostAsync("/api/v1/commands/set_level/run", "{\"vars\":{\"Level\":3}}");

        JsonNode problem = await ApiAssert.ProblemAsync(tooHigh, HttpStatusCode.UnprocessableEntity, "invalid_vars");
        Assert.Contains("/vars/Level", ApiAssert.ErrorPaths(problem));
        JsonNode missingProblem = await ApiAssert.ProblemAsync(missing, HttpStatusCode.UnprocessableEntity, "invalid_vars");
        Assert.Contains("/vars/Level", ApiAssert.ErrorPaths(missingProblem));
        JsonNode unknownProblem = await ApiAssert.ProblemAsync(unknownVariable, HttpStatusCode.UnprocessableEntity, "invalid_vars");
        Assert.Contains("/vars/Nope", ApiAssert.ErrorPaths(unknownProblem));
        string goodId = (await ApiAssert.JsonAsync(good, HttpStatusCode.Accepted))["runId"]!.GetValue<string>();
        await WaitUntilFinishedAsync(host, goodId);

        // The rejected request is in the history, and the process of the accepted one got the validated value.
        string rejectedId = problem["runId"]!.GetValue<string>();
        using HttpResponseMessage rejected = await host.GetAsync("/api/v1/runs/" + rejectedId);
        JsonNode detail = await ApiAssert.JsonAsync(rejected);
        Assert.Equal("invalid_vars", detail["errorCode"]!.GetValue<string>());
        Assert.False(detail["succeeded"]!.GetValue<bool>());
        ControlledProcessRun process = Assert.Single(runner.Runs);
        string[] expectedArguments = ["--level", "3"];
        Assert.Equal(expectedArguments, process.Request.Arguments);
    }

    [Fact]
    public async Task A_run_that_the_engine_rejects_is_answered_with_the_status_of_the_reason()
    {
        var runner = new ControlledProcessRunner(autoFinish: true);
        await using ApiTestHost host = await RuntimeHost.StartAsync(runner);
        await host.CreateCommandAsync(ApiSamples.Command("off_one", enabled: false));
        await host.CreateCommandAsync(ApiSamples.Command("busy_one").Replace("\"captureOutput\":true", "\"cooldownSeconds\":60,\"captureOutput\":true", StringComparison.Ordinal));

        using HttpResponseMessage unknown = await host.PostAsync("/api/v1/commands/nothing_here/run");
        using HttpResponseMessage disabled = await host.PostAsync("/api/v1/commands/off_one/run");
        using HttpResponseMessage first = await host.PostAsync("/api/v1/commands/busy_one/run");
        string firstId = (await ApiAssert.JsonAsync(first, HttpStatusCode.Accepted))["runId"]!.GetValue<string>();
        await WaitUntilFinishedAsync(host, firstId);
        using HttpResponseMessage cooling = await host.PostAsync("/api/v1/commands/busy_one/run");
        host.Service<AgentPause>().SetPaused(true);
        using HttpResponseMessage paused = await host.PostAsync("/api/v1/commands/off_one/run");
        using HttpResponseMessage pausedEnabled = await host.PostAsync("/api/v1/commands/busy_one/run");

        await ApiAssert.ProblemAsync(unknown, HttpStatusCode.NotFound, "unknown_command");
        JsonNode disabledProblem = await ApiAssert.ProblemAsync(disabled, HttpStatusCode.Conflict, "command_disabled");
        Assert.False(string.IsNullOrEmpty(disabledProblem["runId"]?.GetValue<string>()));
        await ApiAssert.ProblemAsync(cooling, HttpStatusCode.TooManyRequests, "cooldown");
        await ApiAssert.ProblemAsync(paused, HttpStatusCode.Conflict, "command_disabled");
        await ApiAssert.ProblemAsync(pausedEnabled, HttpStatusCode.Conflict, "paused");
    }

    [Fact]
    public async Task A_run_with_a_secret_variable_shows_its_output_only_when_it_ends_and_redacted()
    {
        var runner = new ControlledProcessRunner();
        await using ApiTestHost host = await RuntimeHost.StartAsync(runner);
        const string command = """
            {"id":"login_tool","name":"Login","enabled":true,"exposeToHa":false,"runAs":"system","action":{"type":"process","file":"login-tool","args":["{{Pass}}"]},"parameters":[{"name":"Pass","type":"string","required":true,"secret":true}],"captureOutput":true}
            """;
        await host.CreateCommandAsync(command);
        using HttpResponseMessage started = await host.PostAsync("/api/v1/commands/login_tool/run", "{\"vars\":{\"Pass\":\"hunter2-secret\"}}");
        string runId = (await ApiAssert.JsonAsync(started, HttpStatusCode.Accepted))["runId"]!.GetValue<string>();
        ControlledProcessRun process = await runner.WaitForRunAsync();

        await using ServerEventReader stream = await ServerEventReader.OpenAsync(host, "/api/v1/runs/" + runId + "/stream");
        process.Finish(0, "logged in with hunter2-secret\n", string.Empty);
        ServerEvent output = await stream.ReadAsync();
        ServerEvent end = await stream.ReadAsync();

        Assert.Equal("output", output.Name);
        Assert.DoesNotContain("hunter2-secret", output.Data["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("***", output.Data["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("end", end.Name);
    }

    private static async Task<JsonNode> ListAsync(ApiTestHost host, string query)
    {
        using HttpResponseMessage response = await host.GetAsync("/api/v1/runs" + query);
        return await ApiAssert.JsonAsync(response);
    }

    /// <summary>Starts a run and waits until it is in the history.</summary>
    private static async Task<string> RunAndWaitAsync(ApiTestHost host, string commandId)
    {
        using HttpResponseMessage started = await host.PostAsync("/api/v1/commands/" + commandId + "/run");
        string runId = (await ApiAssert.JsonAsync(started, HttpStatusCode.Accepted))["runId"]!.GetValue<string>();
        await WaitUntilFinishedAsync(host, runId);
        return runId;
    }

    private static async Task WaitUntilFinishedAsync(ApiTestHost host, string runId)
    {
        DateTime deadline = DateTime.UtcNow + TestWait.DefaultTimeout;
        while (true)
        {
            using HttpResponseMessage response = await host.GetAsync("/api/v1/runs/" + runId);
            JsonNode run = await ApiAssert.JsonAsync(response);
            if (run["state"]!.GetValue<string>() == "finished")
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, "The run " + runId + " did not finish.");
            await Task.Delay(20);
        }
    }

    /// <summary>The commands of the <c>command_finished</c> events; the event is stored right after the run, so this waits a moment for the saved command's.</summary>
    private static async Task<string[]> FinishedEventCommandsAsync(ApiTestHost host)
    {
        DateTime deadline = DateTime.UtcNow + TestWait.DefaultTimeout;
        while (true)
        {
            using HttpResponseMessage response = await host.GetAsync("/api/v1/events?type=command_finished&limit=100");
            JsonNode body = await ApiAssert.JsonAsync(response);
            string[] commands = [.. body["events"]!.AsArray().Select(item => item!["data"]!["command"]!.GetValue<string>())];
            if (commands.Contains(Id) || DateTime.UtcNow > deadline)
            {
                return commands;
            }

            await Task.Delay(20);
        }
    }
}
