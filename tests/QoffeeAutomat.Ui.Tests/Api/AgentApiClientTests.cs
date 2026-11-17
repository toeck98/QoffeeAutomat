using System.Net;
using System.Text;
using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Api;

/// <summary>
/// The HTTP implementation of <c>IAgentApi</c> (T401, PLAN.md S08, S09 item 1): cookies and anti-forgery header, JSON
/// through the source-generated context, problem details as one kind of exception, and the live stream.
/// </summary>
[Trait("Category", "Ui")]
public sealed class AgentApiClientTests : IDisposable
{
    private const string ProblemJson = "application/problem+json";

    private readonly List<HttpClient> _httpClients = [];

    public void Dispose()
    {
        foreach (HttpClient http in _httpClients)
        {
            http.Dispose();
        }
    }

    [Fact]
    public async Task A_read_goes_to_the_versioned_api_without_the_anti_forgery_header()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(Samples.Status(), ApiJsonContext.Default.AgentStatus)));
        AgentApiClient client = Client(handler);

        AgentStatus status = await client.GetStatusAsync();

        Assert.Equal("1.2.3", status.Version);
        Assert.Equal("STUDY-PC", status.Platform.Hostname);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v1/status", request.Uri.AbsolutePath);
        Assert.False(request.Headers.ContainsKey(ApiHeaders.CsrfName));
    }

    [Fact]
    public async Task A_request_that_changes_state_carries_the_anti_forgery_header_and_camel_case_json()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"authenticated\":true,\"name\":\"administrator\"}"));
        AgentApiClient client = Client(handler);

        AuthMeResponse me = await client.LogInAsync(new LoginRequest("secret"));

        Assert.True(me.Authenticated);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/auth/login", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
        Assert.Equal("{\"password\":\"secret\"}", request.Body);
        Assert.StartsWith("application/json", request.ContentType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_sends_the_token_and_the_password()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"authenticated\":true,\"name\":\"administrator\"}"));
        AgentApiClient client = Client(handler);

        await client.SetUpAsync(new SetupRequest("tok", "pw"));

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/auth/setup", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
        Assert.Equal("{\"setupToken\":\"tok\",\"password\":\"pw\"}", request.Body);
    }

    [Fact]
    public async Task Logging_out_is_a_post_with_the_anti_forgery_header()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        AgentApiClient client = Client(handler);

        await client.LogOutAsync();

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/auth/logout", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
    }

    [Fact]
    public async Task Problem_details_become_an_exception_with_the_status_the_code_and_the_detail()
    {
        var handler = new StubHandler(_ => Problem(HttpStatusCode.Unauthorized, Samples.Problem(401, "invalid_credentials", "The password is wrong.")));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.LogInAsync(new LoginRequest("x")));

        Assert.Equal(401, exception.StatusCode);
        Assert.Equal("invalid_credentials", exception.Code);
        Assert.Equal("The password is wrong.", exception.Message);
        Assert.True(exception.IsUnauthorized);
        Assert.False(exception.IsNetworkError);
    }

    [Fact]
    public async Task The_retry_after_header_of_a_lockout_is_kept()
    {
        var handler = new StubHandler(_ =>
        {
            HttpResponseMessage response = Problem(HttpStatusCode.TooManyRequests, Samples.Problem(429, "locked_out", "Too many failed attempts."));
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(900));
            return response;
        });
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.LogInAsync(new LoginRequest("x")));

        Assert.Equal("locked_out", exception.Code);
        Assert.Equal(900, exception.RetryAfterSeconds);
    }

    [Fact]
    public async Task Validation_errors_keep_their_json_pointers()
    {
        var handler = new StubHandler(_ => Problem(
            (HttpStatusCode)422,
            Samples.Problem(422, "validation_failed", "The request has a validation error.", new ApiValidationError("/parameters/0/min", "invalid", "min must be a number."))));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.GetStatusAsync());

        Assert.Equal(422, exception.StatusCode);
        ApiValidationError error = Assert.Single(exception.ValidationErrors);
        Assert.Equal("/parameters/0/min", error.Path);
        Assert.Equal("invalid", error.Code);
    }

    [Fact]
    public async Task An_error_without_problem_details_is_described_by_its_status()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadGateway, "<html>proxy error</html>", "text/html"));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.GetStatusAsync());

        Assert.Equal(502, exception.StatusCode);
        Assert.Equal("http_502", exception.Code);
        Assert.Null(exception.Problem!.Code);
    }

    [Fact]
    public async Task A_connection_failure_becomes_a_network_error()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.GetStatusAsync());

        Assert.True(exception.IsNetworkError);
        Assert.Equal(ApiException.NetworkErrorCode, exception.Code);
        Assert.IsType<HttpRequestException>(exception.InnerException);
    }

    [Fact]
    public async Task Cancelling_a_call_is_not_reported_as_a_network_error()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException());
        AgentApiClient client = Client(handler);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetStatusAsync(cancellation.Token));
    }

    [Fact]
    public async Task An_answer_that_is_not_json_is_an_invalid_answer()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "this is not json"));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.GetStatusAsync());

        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task The_session_check_answers_null_for_401_and_the_account_otherwise()
    {
        int calls = 0;
        var handler = new StubHandler(_ => ++calls == 1
            ? Problem(HttpStatusCode.Unauthorized, Samples.Problem(401, "unauthorized", "Sign in."))
            : Json(HttpStatusCode.OK, "{\"authenticated\":true,\"name\":\"administrator\"}"));
        AgentApiClient client = Client(handler);

        Assert.Null(await client.GetMeAsync());
        AuthMeResponse? me = await client.GetMeAsync();

        Assert.Equal("administrator", me!.Name);
    }

    [Fact]
    public async Task The_setup_check_answers_null_when_setup_is_not_available()
    {
        int calls = 0;
        var handler = new StubHandler(_ => ++calls == 1
            ? Problem(HttpStatusCode.NotFound, Samples.Problem(404, "setup_unavailable", "A password is already set."))
            : Json(HttpStatusCode.OK, "{\"available\":true}"));
        AgentApiClient client = Client(handler);

        Assert.Null(await client.GetSetupStateAsync());
        SetupState? state = await client.GetSetupStateAsync();

        Assert.True(state!.Available);
        Assert.Equal("/api/v1/auth/setup", handler.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task The_history_and_list_calls_put_their_arguments_in_the_query()
    {
        var handler = new StubHandler(request => request.Uri.AbsolutePath switch
        {
            "/api/v1/metrics/history" => Json(HttpStatusCode.OK, JsonSerializer.Serialize(Samples.History(), ApiJsonContext.Default.MetricHistoryResponse)),
            "/api/v1/runs" => Json(HttpStatusCode.OK, JsonSerializer.Serialize(Samples.Runs(), ApiJsonContext.Default.RunList)),
            "/api/v1/events" => Json(HttpStatusCode.OK, JsonSerializer.Serialize(Samples.Events(), ApiJsonContext.Default.EventList)),
            "/api/v1/link/clients" => Json(HttpStatusCode.OK, JsonSerializer.Serialize(Samples.Clients(), ApiJsonContext.Default.LinkClientList)),
            "/api/v1/metrics" => Json(HttpStatusCode.OK, JsonSerializer.Serialize(Samples.Catalog(), ApiJsonContext.Default.MetricList)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        AgentApiClient client = Client(handler);

        MetricHistoryResponse history = await client.GetMetricHistoryAsync(["cpu.usage", "memory.used_percent"], 900);
        RunList runs = await client.GetRunsAsync(8);
        EventList events = await client.GetEventsAsync(5);
        LinkClientList clients = await client.GetLinkClientsAsync();
        MetricList catalog = await client.GetMetricsAsync();

        Assert.Equal(2, history.Series.Count);
        Assert.Equal(2, runs.Runs.Count);
        Assert.Equal(2, events.Events.Count);
        Assert.Equal(2, clients.Clients.Count);
        Assert.Equal(4, catalog.Metrics.Count);
        Assert.Equal("?metrics=cpu.usage,memory.used_percent&seconds=900", Uri.UnescapeDataString(handler.Requests[0].Uri.Query));
        Assert.Equal("?limit=8", handler.Requests[1].Uri.Query);
        Assert.Equal("?limit=5", handler.Requests[2].Uri.Query);
    }

    [Fact]
    public async Task The_metric_stream_yields_the_snapshot_and_the_updates_and_skips_what_it_does_not_know()
    {
        const string body = ": heartbeat\n\n"
            + "event: snapshot\ndata: {\"timestamp\":\"2026-11-16T12:00:00+00:00\",\"snapshot\":true,\"values\":{\"cpu.usage\":12.5,\"memory.used_percent\":null}}\n\n"
            + "event: ping\ndata: x\n\n"
            + "event: update\ndata: {not json\n\n"
            + "event: update\ndata: {\"timestamp\":\"2026-11-16T12:00:02+00:00\",\"snapshot\":false,\"values\":{\"cpu.usage\":13}}\n\n";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
        AgentApiClient client = Client(handler);

        var updates = new List<MetricStreamUpdate>();
        await foreach (MetricStreamUpdate update in client.StreamMetricsAsync(["cpu.usage", "memory.used_percent"], 2))
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count);
        Assert.True(updates[0].Snapshot);
        Assert.Equal(12.5, updates[0].Values["cpu.usage"].GetDouble());
        Assert.Equal(JsonValueKind.Null, updates[0].Values["memory.used_percent"].ValueKind);
        Assert.False(updates[1].Snapshot);
        Assert.Equal(13, updates[1].Values["cpu.usage"].GetDouble());

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/metrics/stream", request.Uri.AbsolutePath);
        Assert.Equal("?metrics=cpu.usage,memory.used_percent&interval=2", Uri.UnescapeDataString(request.Uri.Query));
        Assert.Contains("text/event-stream", request.Headers["Accept"], StringComparison.Ordinal);
        Assert.False(request.Headers.ContainsKey(ApiHeaders.CsrfName));
    }

    [Fact]
    public async Task A_refused_stream_throws_the_problem_when_it_is_opened()
    {
        var handler = new StubHandler(_ => Problem(HttpStatusCode.Unauthorized, Samples.Problem(401, "unauthorized", "Sign in.")));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(async () =>
        {
            await foreach (MetricStreamUpdate unused in client.StreamMetricsAsync(["cpu.usage"], 2))
            {
                Assert.Fail("No update was expected.");
            }
        });

        Assert.True(exception.IsUnauthorized);
    }

    [Fact]
    public async Task Cancelling_the_enumeration_closes_the_connection()
    {
        var body = new EndlessStream();
        var handler = new StubHandler(_ =>
        {
            var content = new StreamContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        AgentApiClient client = Client(handler);
        using var cancellation = new CancellationTokenSource();

        Task<int> reading = Task.Run(async () =>
        {
            int count = 0;
            await foreach (MetricStreamUpdate unused in client.StreamMetricsAsync(["cpu.usage"], 2, cancellation.Token))
            {
                count++;
            }

            return count;
        });
        await body.WaitUntilReadAsync();

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.True(body.IsDisposed, "The response body was not disposed, so the connection stays open.");
    }

    [Fact]
    public async Task The_command_list_is_read_with_its_actions()
    {
        var list = new CommandList(Samples.Commands());
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(list, ApiDocumentJsonContext.Default.CommandList)));
        AgentApiClient client = Client(handler);

        CommandList commands = await client.GetCommandsAsync();

        Assert.Equal(["backup", "lock_screen", "night_mode", "open_notes"], commands.Commands.Select(command => command.Id));
        Assert.IsType<PowerShellAction>(commands.Commands[0].Action);
        Assert.IsType<SequenceAction>(commands.Commands[2].Action);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v1/commands", request.Uri.AbsolutePath);
        Assert.False(request.Headers.ContainsKey(ApiHeaders.CsrfName));
    }

    [Fact]
    public async Task Patching_a_command_sends_only_the_members_that_change()
    {
        CommandDocument changed = Samples.Command("backup", enabled: true);
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(changed, ApiDocumentJsonContext.Default.CommandDocument)));
        AgentApiClient client = Client(handler);

        CommandDocument saved = await client.PatchCommandAsync("backup", new CommandPatch(Enabled: true));

        Assert.True(saved.Enabled);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/api/v1/commands/backup", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
        Assert.Equal("{\"enabled\":true}", request.Body);
        Assert.StartsWith("application/json", request.ContentType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_id_of_a_command_is_escaped_in_the_path()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(Samples.Command("a b"), ApiDocumentJsonContext.Default.CommandDocument)));
        AgentApiClient client = Client(handler);

        await client.PatchCommandAsync("a b", new CommandPatch(ExposeToHa: false));

        Assert.Equal("/api/v1/commands/a%20b", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task A_refused_patch_throws_the_problem()
    {
        var handler = new StubHandler(_ => Problem(HttpStatusCode.NotFound, Samples.Problem(404, "unknown_command", "There is no command with this id.")));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.PatchCommandAsync("gone", new CommandPatch(Enabled: false)));

        Assert.Equal(404, exception.StatusCode);
        Assert.Equal("unknown_command", exception.Code);
    }

    [Fact]
    public async Task Creating_a_command_posts_the_document_and_returns_the_saved_one()
    {
        CommandDocument command = Samples.Command("new_one", "New one", action: new ProcessAction("notepad.exe"));
        var handler = new StubHandler(_ => Json(HttpStatusCode.Created, JsonSerializer.Serialize(command with { Revision = 1 }, ApiDocumentJsonContext.Default.CommandDocument)));
        AgentApiClient client = Client(handler);

        CommandDocument saved = await client.CreateCommandAsync(command);

        Assert.Equal("new_one", saved.Id);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/commands", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
        Assert.Contains("\"id\":\"new_one\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"process\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_a_command_that_exists_throws_the_conflict()
    {
        var handler = new StubHandler(_ => Problem(HttpStatusCode.Conflict, Samples.Problem(409, "already_exists", "A command with this id exists.")));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.CreateCommandAsync(Samples.Command("backup")));

        Assert.Equal(409, exception.StatusCode);
    }

    [Fact]
    public async Task Reading_a_command_gets_it_by_id_with_its_revision()
    {
        CommandDocument stored = Samples.Command("set profile") with { Revision = 4 };
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(stored, ApiDocumentJsonContext.Default.CommandDocument)));
        AgentApiClient client = Client(handler);

        CommandDocument command = await client.GetCommandAsync("set profile");

        Assert.Equal(4, command.Revision);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v1/commands/set%20profile", request.Uri.AbsolutePath);
        Assert.False(request.Headers.ContainsKey(ApiHeaders.CsrfName));
    }

    [Fact]
    public async Task Reading_an_unknown_command_throws_the_not_found_problem()
    {
        var handler = new StubHandler(_ => Problem(HttpStatusCode.NotFound, Samples.Problem(404, "unknown_command", "There is no command with this id.")));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.GetCommandAsync("gone"));

        Assert.Equal(404, exception.StatusCode);
        Assert.Equal("unknown_command", exception.Code);
    }

    [Fact]
    public async Task Replacing_a_command_puts_the_document_against_its_revision()
    {
        CommandDocument command = Samples.Command("backup", "Back up") with { Revision = 3 };
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(command with { Revision = 4 }, ApiDocumentJsonContext.Default.CommandDocument)));
        AgentApiClient client = Client(handler);

        CommandDocument saved = await client.ReplaceCommandAsync(command);

        Assert.Equal(4, saved.Revision);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/api/v1/commands/backup", request.Uri.AbsolutePath);
        Assert.Equal("\"3\"", request.Headers["If-Match"]);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
        Assert.Contains("\"id\":\"backup\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replacing_a_command_without_a_revision_sends_no_precondition()
    {
        CommandDocument command = Samples.Command("backup") with { Revision = null };
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(command with { Revision = 1 }, ApiDocumentJsonContext.Default.CommandDocument)));
        AgentApiClient client = Client(handler);

        await client.ReplaceCommandAsync(command);

        Assert.False(Assert.Single(handler.Requests).Headers.ContainsKey("If-Match"));
    }

    [Fact]
    public async Task Replacing_a_command_that_changed_meanwhile_throws_the_stale_revision()
    {
        var handler = new StubHandler(_ => Problem(HttpStatusCode.PreconditionFailed, new ApiProblem("about:blank", "Precondition Failed", 412, "The command was changed.", "revision_mismatch", CurrentRevision: 5)));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.ReplaceCommandAsync(Samples.Command("backup")));

        Assert.Equal(412, exception.StatusCode);
        Assert.Equal(5, exception.Problem?.CurrentRevision);
    }

    [Fact]
    public async Task Validating_a_draft_posts_it_and_returns_the_errors_with_their_paths()
    {
        var report = new ValidationReport(false, [new ApiValidationError("/parameters/0/min", "invalid", "min must be a number.")]);
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(report, ApiJsonContext.Default.ValidationReport)));
        AgentApiClient client = Client(handler);

        ValidationReport result = await client.ValidateCommandAsync(Samples.Command("draft", action: new BuiltinAction("power.shutdown")));

        Assert.False(result.Valid);
        ApiValidationError error = Assert.Single(result.Errors);
        Assert.Equal("/parameters/0/min", error.Path);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/commands/validate", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
        Assert.Contains("\"id\":\"draft\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"builtin\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Testing_a_draft_posts_the_draft_with_its_variables_and_returns_the_run()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Accepted, JsonSerializer.Serialize(new RunStarted("t1", "draft", "test"), ApiJsonContext.Default.RunStarted)));
        AgentApiClient client = Client(handler);
        var variables = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["Profile"] = Samples.Text("TV"),
            ["Count"] = Samples.Number(3),
        };

        RunStarted started = await client.TestCommandAsync(Samples.Command("draft"), variables);

        Assert.Equal("t1", started.RunId);
        Assert.Equal("test", started.Source);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/commands/test", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
        Assert.Contains("{\"command\":{", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"vars\":{\"Profile\":\"TV\",\"Count\":3}", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_draft_that_is_refused_for_a_test_keeps_the_errors_below_the_command_and_the_variables()
    {
        var handler = new StubHandler(_ => Problem(
            HttpStatusCode.UnprocessableEntity,
            Samples.Problem(
                422,
                "validation_failed",
                "Not valid.",
                new ApiValidationError("/command/action/code", "required", "Inline PowerShell needs code."),
                new ApiValidationError("/vars/Count", "range", "Too big."))));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => client.TestCommandAsync(Samples.Command("draft"), new Dictionary<string, JsonElement>(StringComparer.Ordinal)));

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal(["/command/action/code", "/vars/Count"], exception.ValidationErrors.Select(error => error.Path));
    }

    [Fact]
    public async Task Listing_the_scripts_reads_their_names()
    {
        var list = new ScriptSummaryList([new ScriptSummary("set_profile.ps1", "abc123", Samples.Now)]);
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(list, ApiJsonContext.Default.ScriptSummaryList)));
        AgentApiClient client = Client(handler);

        ScriptSummaryList scripts = await client.GetScriptsAsync();

        Assert.Equal("set_profile.ps1", Assert.Single(scripts.Scripts).Name);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v1/scripts", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task Deleting_a_command_is_a_delete_with_the_anti_forgery_header()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        AgentApiClient client = Client(handler);

        await client.DeleteCommandAsync("backup");

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/api/v1/commands/backup", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task A_refused_delete_throws_the_problem()
    {
        var handler = new StubHandler(_ => Problem(HttpStatusCode.NotFound, Samples.Problem(404, "unknown_command", "There is no command with this id.")));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.DeleteCommandAsync("gone"));

        Assert.Equal(404, exception.StatusCode);
    }

    [Fact]
    public async Task Starting_a_run_posts_the_variables_and_returns_the_accepted_run()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Accepted, "{\"runId\":\"r9\",\"command\":\"backup\",\"source\":\"ui\"}"));
        AgentApiClient client = Client(handler);
        var variables = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["target"] = Samples.Text("D:\\Backups"),
            ["count"] = Samples.Number(3),
        };

        RunStarted started = await client.StartRunAsync("backup", variables);

        Assert.Equal("r9", started.RunId);
        Assert.Equal("backup", started.Command);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/commands/backup/run", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
        Assert.Equal("{\"vars\":{\"target\":\"D:\\\\Backups\",\"count\":3}}", request.Body);
    }

    [Fact]
    public async Task A_run_that_the_agent_rejects_keeps_the_error_of_every_variable()
    {
        var handler = new StubHandler(_ => Problem(
            HttpStatusCode.UnprocessableEntity,
            Samples.Problem(422, "validation_failed", "The variables are not valid.", new ApiValidationError("/vars/count", "range", "Too big."))));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => client.StartRunAsync("backup", new Dictionary<string, JsonElement>(StringComparer.Ordinal)));

        Assert.Equal(422, exception.StatusCode);
        ApiValidationError error = Assert.Single(exception.ValidationErrors);
        Assert.Equal("/vars/count", error.Path);
        Assert.Equal("range", error.Code);
    }

    [Fact]
    public async Task The_run_stream_yields_the_output_and_ends_with_the_end_item()
    {
        const string body = ": heartbeat\n\n"
            + "event: output\ndata: {\"stream\":\"stdout\",\"text\":\"hello\\n\",\"timestamp\":\"2026-11-16T12:00:00+00:00\"}\n\n"
            + "event: ping\ndata: x\n\n"
            + "event: output\ndata: {not json\n\n"
            + "event: output\ndata: {\"stream\":\"stderr\",\"text\":\"oops\",\"timestamp\":\"2026-11-16T12:00:01+00:00\"}\n\n"
            + "event: end\ndata: {\"runId\":\"r9\",\"succeeded\":false,\"exitCode\":1,\"errorCode\":\"exit_nonzero\",\"durationMs\":42,\"outputTruncated\":false}\n\n"
            + "event: output\ndata: {\"stream\":\"stdout\",\"text\":\"after the end\",\"timestamp\":\"2026-11-16T12:00:02+00:00\"}\n\n";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
        AgentApiClient client = Client(handler);

        var items = new List<RunStreamItem>();
        await foreach (RunStreamItem item in client.StreamRunAsync("r9"))
        {
            items.Add(item);
        }

        Assert.Equal(3, items.Count);
        Assert.Equal("stdout", items[0].Output?.Stream);
        Assert.Equal("hello\n", items[0].Output?.Text);
        Assert.Equal("stderr", items[1].Output?.Stream);
        Assert.Null(items[2].Output);
        RunEndEvent end = Assert.IsType<RunEndEvent>(items[2].End);
        Assert.False(end.Succeeded);
        Assert.Equal(1, end.ExitCode);
        Assert.Equal("exit_nonzero", end.ErrorCode);
        Assert.Equal(42, end.DurationMs);

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/runs/r9/stream", request.Uri.AbsolutePath);
        Assert.Contains("text/event-stream", request.Headers["Accept"], StringComparison.Ordinal);
        Assert.False(request.Headers.ContainsKey(ApiHeaders.CsrfName));
    }

    [Fact]
    public async Task A_refused_run_stream_throws_the_problem_when_it_is_opened()
    {
        var handler = new StubHandler(_ => Problem(HttpStatusCode.NotFound, Samples.Problem(404, "unknown_run", "There is no run with this id.")));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(async () =>
        {
            await foreach (RunStreamItem unused in client.StreamRunAsync("gone"))
            {
                Assert.Fail("No item was expected.");
            }
        });

        Assert.Equal(404, exception.StatusCode);
    }

    [Fact]
    public async Task Cancelling_the_run_stream_closes_the_connection()
    {
        var body = new EndlessStream();
        var handler = new StubHandler(_ =>
        {
            var content = new StreamContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        AgentApiClient client = Client(handler);
        using var cancellation = new CancellationTokenSource();

        Task<int> reading = Task.Run(async () =>
        {
            int count = 0;
            await foreach (RunStreamItem unused in client.StreamRunAsync("r9", cancellation.Token))
            {
                count++;
            }

            return count;
        });
        await body.WaitUntilReadAsync();

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.True(body.IsDisposed, "The response body was not disposed, so the connection stays open.");
    }

    [Fact]
    public async Task A_metric_override_is_put_to_the_metric_with_the_anti_forgery_header()
    {
        var saved = new MetricOverridesDocument(
            DocumentSchema.CurrentVersion,
            new Dictionary<string, MetricOverride> { ["cpu.usage"] = new(Enabled: false, IntervalSeconds: 5) },
            Revision: 2);
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(saved, ApiDocumentJsonContext.Default.MetricOverridesDocument)));
        AgentApiClient client = Client(handler);

        MetricOverridesDocument result = await client.PutMetricOverrideAsync("cpu.usage", new MetricOverride(Enabled: false, ExposeToHa: true, IntervalSeconds: 5));

        Assert.Equal(2, result.Revision);
        Assert.False(result.Overrides["cpu.usage"].Enabled);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/api/v1/metrics/cpu.usage", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
        Assert.Equal("{\"enabled\":false,\"exposeToHa\":true,\"intervalSeconds\":5}", request.Body);
    }

    [Fact]
    public async Task A_refused_metric_override_throws_the_problem()
    {
        var handler = new StubHandler(_ => Problem(HttpStatusCode.NotFound, Samples.Problem(404, "unknown_metric", "There is no metric with this id.")));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.PutMetricOverrideAsync("nope", new MetricOverride(Enabled: true)));

        Assert.Equal("unknown_metric", exception.Code);
    }

    [Fact]
    public async Task The_sensors_are_read_with_their_sources()
    {
        var list = new SensorList(
        [
            Samples.Sensor("disk_temp", source: new PowerShellSensorSource(PowerShellSources.Inline, Code: "1")),
            Samples.Sensor("browser", source: new ProcessRunningSensorSource("firefox"), valueType: "bool", kind: "binary_sensor"),
        ]);
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(list, ApiDocumentJsonContext.Default.SensorList)));
        AgentApiClient client = Client(handler);

        SensorList result = await client.GetSensorsAsync();

        Assert.Equal(2, result.Sensors.Count);
        Assert.IsType<PowerShellSensorSource>(result.Sensors[0].Source);
        Assert.Equal("firefox", Assert.IsType<ProcessRunningSensorSource>(result.Sensors[1].Source).Process);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v1/sensors", request.Uri.AbsolutePath);
        Assert.False(request.Headers.ContainsKey(ApiHeaders.CsrfName));
    }

    [Fact]
    public async Task Creating_a_sensor_posts_the_document_and_returns_the_saved_one()
    {
        SensorDocument sensor = Samples.Sensor("disk_temp", "Disk temperature", unit: "°C", deviceClass: "temperature", stateClass: "measurement");
        var handler = new StubHandler(_ => Json(HttpStatusCode.Created, JsonSerializer.Serialize(sensor with { Revision = 1 }, ApiDocumentJsonContext.Default.SensorDocument)));
        AgentApiClient client = Client(handler);

        SensorDocument saved = await client.CreateSensorAsync(sensor);

        Assert.Equal("disk_temp", saved.Id);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/sensors", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
        Assert.Contains("\"id\":\"disk_temp\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"powershell\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"deviceClass\":\"temperature\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_a_sensor_that_exists_fails_with_status_409()
    {
        var handler = new StubHandler(_ => Problem(HttpStatusCode.Conflict, Samples.Problem(409, "already_exists", "A sensor with this id exists.")));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.CreateSensorAsync(Samples.Sensor("disk_temp")));

        Assert.Equal(409, exception.StatusCode);
    }

    [Fact]
    public async Task Replacing_a_sensor_sends_the_revision_as_if_match()
    {
        SensorDocument sensor = Samples.Sensor("disk_temp") with { Revision = 3 };
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(sensor with { Revision = 4 }, ApiDocumentJsonContext.Default.SensorDocument)));
        AgentApiClient client = Client(handler);

        SensorDocument saved = await client.ReplaceSensorAsync(sensor);

        Assert.Equal(4, saved.Revision);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/api/v1/sensors/disk_temp", request.Uri.AbsolutePath);
        Assert.Equal("\"3\"", request.Headers["If-Match"]);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
    }

    [Fact]
    public async Task A_stale_sensor_revision_fails_with_status_412()
    {
        var handler = new StubHandler(_ => Problem(HttpStatusCode.PreconditionFailed, Samples.Problem(412, "revision_mismatch", "Changed meanwhile.")));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.ReplaceSensorAsync(Samples.Sensor("disk_temp")));

        Assert.Equal(412, exception.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_sensor_is_a_delete_with_the_escaped_id()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        AgentApiClient client = Client(handler);

        await client.DeleteSensorAsync("a b");

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/api/v1/sensors/a%20b", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
    }

    [Fact]
    public async Task Testing_a_sensor_posts_the_draft_and_reads_the_parsed_value()
    {
        var result = new SensorTestResult(true, Samples.Number(42), null, 12);
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(result, ApiJsonContext.Default.SensorTestResult)));
        AgentApiClient client = Client(handler);

        SensorTestResult answer = await client.TestSensorAsync(Samples.Sensor("disk_temp"));

        Assert.True(answer.Succeeded);
        Assert.Equal(42, answer.Value!.Value.GetDouble());
        Assert.Equal(12, answer.DurationMs);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/sensors/test", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
        Assert.Contains("\"id\":\"disk_temp\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sensor_test_that_fails_returns_the_error()
    {
        var result = new SensorTestResult(false, null, "The output is not a number.", 5);
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(result, ApiJsonContext.Default.SensorTestResult)));
        AgentApiClient client = Client(handler);

        SensorTestResult answer = await client.TestSensorAsync(Samples.Sensor("disk_temp"));

        Assert.False(answer.Succeeded);
        Assert.Null(answer.Value);
        Assert.Equal("The output is not a number.", answer.Error);
    }

    [Fact]
    public async Task A_sensor_draft_that_is_not_valid_fails_with_the_errors_at_their_paths()
    {
        var handler = new StubHandler(_ => Problem(
            HttpStatusCode.UnprocessableContent,
            Samples.Problem(422, "validation_failed", "The sensor is not valid.", new ApiValidationError("/source/type", "unsupported", "Windows only."))));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.TestSensorAsync(Samples.Sensor("disk_temp")));

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal("/source/type", Assert.Single(exception.ValidationErrors).Path);
    }

    [Fact]
    public async Task Reading_a_state_value_gets_the_value_resource_of_the_escaped_id()
    {
        var value = new SensorValue("a b", Samples.Text("Eco"), Samples.Now);
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(value, ApiJsonContext.Default.SensorValue)));
        AgentApiClient client = Client(handler);

        SensorValue answer = await client.GetSensorValueAsync("a b");

        Assert.Equal("Eco", answer.Value!.Value.GetString());
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v1/sensors/a%20b/value", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task Setting_a_state_value_puts_the_text_as_a_json_string()
    {
        var value = new SensorValue("level", Samples.Number(42), Samples.Now);
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(value, ApiJsonContext.Default.SensorValue)));
        AgentApiClient client = Client(handler);

        SensorValue answer = await client.SetSensorValueAsync("level", "42");

        Assert.Equal(42, answer.Value!.Value.GetDouble());
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/api/v1/sensors/level/value", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
        Assert.Equal("{\"value\":\"42\"}", request.Body);
    }

    [Fact]
    public async Task A_state_value_that_does_not_fit_fails_with_the_error_at_value()
    {
        var handler = new StubHandler(_ => Problem(
            HttpStatusCode.UnprocessableContent,
            Samples.Problem(422, "validation_failed", "The request has a validation error.", new ApiValidationError("/value", "invalid", "'warm' is not a number."))));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.SetSensorValueAsync("level", "warm"));

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal("/value", Assert.Single(exception.ValidationErrors).Path);
    }

    [Fact]
    public async Task Starting_pairing_posts_to_the_pairing_endpoint_and_reads_the_code()
    {
        var info = new PairingInfo("123456", Samples.Now.AddMinutes(5), "AB:CD");
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(info, ApiJsonContext.Default.PairingInfo)));
        AgentApiClient client = Client(handler);

        PairingInfo answer = await client.StartPairingAsync();

        Assert.Equal("123456", answer.Code);
        Assert.Equal("AB:CD", answer.Fingerprint);
        Assert.Equal(Samples.Now.AddMinutes(5), answer.ExpiresAt);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/link/pairing", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
    }

    [Fact]
    public async Task Starting_pairing_without_a_certificate_fails_with_status_503()
    {
        var handler = new StubHandler(_ => Problem(HttpStatusCode.ServiceUnavailable, Samples.Problem(503, "no_certificate", "The agent has no TLS certificate.")));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.StartPairingAsync());

        Assert.Equal("no_certificate", exception.Code);
    }

    [Fact]
    public async Task The_active_pairing_code_is_read_and_no_content_means_there_is_none()
    {
        var info = new PairingInfo("654321", Samples.Now.AddMinutes(1), "AB:CD");
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(info, ApiJsonContext.Default.PairingInfo)));
        AgentApiClient client = Client(handler);

        PairingInfo? active = await client.GetPairingAsync();

        Assert.Equal("654321", active!.Code);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v1/link/pairing", request.Uri.AbsolutePath);
        Assert.False(request.Headers.ContainsKey(ApiHeaders.CsrfName));

        var none = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.Null(await Client(none).GetPairingAsync());
    }

    [Fact]
    public async Task Cancelling_pairing_is_a_delete_of_the_pairing_endpoint()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        AgentApiClient client = Client(handler);

        await client.CancelPairingAsync();

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/api/v1/link/pairing", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
    }

    [Fact]
    public async Task Revoking_a_client_deletes_it_by_its_escaped_id()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        AgentApiClient client = Client(handler);

        await client.RevokeLinkClientAsync("c 1");

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/api/v1/link/clients/c%201", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
    }

    [Fact]
    public async Task Revoking_an_unknown_client_fails_with_status_404()
    {
        var handler = new StubHandler(_ => Problem(HttpStatusCode.NotFound, Samples.Problem(404, "unknown_client", "There is no paired client with this id.")));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.RevokeLinkClientAsync("gone"));

        Assert.Equal(404, exception.StatusCode);
        Assert.Equal("unknown_client", exception.Code);
    }

    [Fact]
    public async Task The_settings_are_read_and_put_with_their_revision_as_if_match()
    {
        var settings = new AgentSettingsDocument(DocumentSchema.CurrentVersion, "Study PC", Zeroconf: false, Revision: 5);
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(settings, ApiDocumentJsonContext.Default.AgentSettingsDocument)));
        AgentApiClient client = Client(handler);

        AgentSettingsDocument read = await client.GetSettingsAsync();
        AgentSettingsDocument saved = await client.PutSettingsAsync(read with { Zeroconf = true });

        Assert.Equal(5, read.Revision);
        Assert.False(read.Zeroconf);
        Assert.Equal("Study PC", saved.Name);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/api/v1/settings", handler.Requests[0].Uri.AbsolutePath);
        Assert.False(handler.Requests[0].Headers.ContainsKey(ApiHeaders.CsrfName));
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Equal("/api/v1/settings", handler.Requests[1].Uri.AbsolutePath);
        Assert.Equal("\"5\"", handler.Requests[1].Headers["If-Match"]);
        Assert.Equal("1", handler.Requests[1].Headers[ApiHeaders.CsrfName]);
        Assert.Contains("\"zeroconf\":true", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    private AgentApiClient Client(StubHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        _httpClients.Add(http);
        return new AgentApiClient(http);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string mediaType = "application/json")
    {
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, ApiProblem problem)
    {
        return Json(status, JsonSerializer.Serialize(problem, ApiJsonContext.Default.ApiProblem), ProblemJson);
    }
}
