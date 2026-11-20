using System.Net;
using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Ui.Library;
using QoffeeAutomat.Hub.Ui.Services;
using QoffeeAutomat.Hub.Ui.Tests.Support;
using QoffeeAutomat.Ui.Components.Api;

namespace QoffeeAutomat.Hub.Ui.Tests.Config;

/// <summary>
/// <c>LibraryAgentApi</c> (PLAN.md S20, D-033, T916): the <c>IAgentApi</c> of the component library over the library of the hub, so that the command
/// and sensor editors that were written for an agent edit hub documents. It reads and writes library documents through the hub API, hides the origin
/// that would make the editors refuse to save, lets the hub validate, and sends only the test of a draft (and what looks at a real PC) through the
/// tunnel to the PC that the user chose.
/// </summary>
[Trait("Category", "Config")]
public sealed class LibraryAgentApiTests : IDisposable
{
    private readonly List<HttpClient> _clients = [];

    public void Dispose()
    {
        foreach (HttpClient client in _clients)
        {
            client.Dispose();
        }
    }

    private (LibraryAgentApi Api, FakeHubApi Hub, StubHandler Handler, AgentSelection Selection) Create(Func<RecordedRequest, HttpResponseMessage>? respond = null, string role = HubRoleNames.Admin)
    {
        var handler = new StubHandler(respond ?? (_ => StubHandler.Json(HttpStatusCode.NotFound, "{\"type\":\"about:blank\",\"title\":\"Not Found\",\"status\":404,\"code\":\"not_found\"}", "application/problem+json")));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://hub.example/") };
        _clients.Add(http);
        var selection = new AgentSelection();
        selection.UseLibrary();
        FakeHubApi hub = new FakeHubApi().SignedIn(role);
        return (new LibraryAgentApi(hub, http, selection), hub, handler, selection);
    }

    [Fact]
    public async Task A_library_command_is_read_as_an_editable_document_with_the_revision_of_the_library()
    {
        (LibraryAgentApi api, FakeHubApi hub, _, _) = Create();
        hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Command, "lock_screen", "Lock screen", Samples.CommandBody("lock_screen"), revision: 3));

        CommandDocument command = await api.GetCommandAsync("lock_screen");

        Assert.Equal("lock_screen", command.Id);
        Assert.Equal("Lock screen", command.Name);
        Assert.Equal(3, command.Revision);

        // The hub stores its documents with the origin "hub", which an editor shows as read-only; in the library they are the thing to edit.
        Assert.Null(command.Origin);
    }

    [Fact]
    public async Task A_command_that_the_library_does_not_have_is_a_404_so_the_editor_shows_not_found()
    {
        (LibraryAgentApi api, _, _, _) = Create();

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => api.GetCommandAsync("missing"));

        Assert.Equal(404, exception.StatusCode);
    }

    [Fact]
    public async Task The_commands_are_the_library_commands_and_only_those()
    {
        (LibraryAgentApi api, FakeHubApi hub, _, _) = Create();
        hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Command, "a", "A", Samples.CommandBody("a", "A")));
        hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Command, "b", "B", Samples.CommandBody("b", "B")));
        hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Sensor, "s", "S", Samples.SensorBody("s", "S")));

        CommandList list = await api.GetCommandsAsync();

        string[] ids = [.. list.Commands.Select(command => command.Id).Order(StringComparer.Ordinal)];
        Assert.Equal(["a", "b"], ids);
    }

    [Fact]
    public async Task Creating_a_command_saves_it_in_the_library_and_returns_the_saved_revision()
    {
        (LibraryAgentApi api, FakeHubApi hub, _, _) = Create();

        CommandDocument saved = await api.CreateCommandAsync(Samples.Command("lock_screen"));

        (string kind, string id, JsonElement body) = Assert.Single(hub.SavedDocuments);
        Assert.Equal(DocumentKinds.Command, kind);
        Assert.Equal("lock_screen", id);
        Assert.Equal("lock_screen", body.GetProperty("id").GetString());
        Assert.Equal(1, saved.Revision);
        Assert.Null(saved.Origin);
    }

    [Fact]
    public async Task Creating_a_command_does_not_replace_one_with_the_same_id()
    {
        (LibraryAgentApi api, FakeHubApi hub, _, _) = Create();
        hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Command, "lock_screen", "Lock screen", Samples.CommandBody("lock_screen")));

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => api.CreateCommandAsync(Samples.Command("lock_screen")));

        Assert.Equal(409, exception.StatusCode);
        Assert.Equal("already_exists", exception.Code);
        Assert.Empty(hub.SavedDocuments);
    }

    [Fact]
    public async Task Replacing_a_command_adds_a_revision()
    {
        (LibraryAgentApi api, FakeHubApi hub, _, _) = Create();
        hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Command, "lock_screen", "Lock screen", Samples.CommandBody("lock_screen")));

        CommandDocument saved = await api.ReplaceCommandAsync(Samples.Command("lock_screen", "Lock the screen"));

        Assert.Equal(2, saved.Revision);
        Assert.Equal("Lock the screen", saved.Name);
        Assert.Equal(DocumentKinds.Command, Assert.Single(hub.SavedDocuments).Kind);
    }

    [Fact]
    public async Task The_validation_errors_of_the_hub_reach_the_editor_with_their_paths()
    {
        (LibraryAgentApi api, FakeHubApi hub, _, _) = Create();
        hub.SaveFailure = new ApiException(
            422,
            new ApiProblem("about:blank", "Validation failed", 422, "The request has a validation error.", "validation_failed", [new ApiValidationError("/timeoutSeconds", "range", "Out of range.")]));

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => api.ReplaceCommandAsync(Samples.Command("lock_screen")));

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal("/timeoutSeconds", Assert.Single(exception.ValidationErrors).Path);
    }

    [Fact]
    public async Task A_viewer_cannot_save_and_the_refusal_of_the_hub_comes_through()
    {
        (LibraryAgentApi api, FakeHubApi hub, _, _) = Create(role: HubRoleNames.Viewer);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => api.CreateCommandAsync(Samples.Command("lock_screen")));

        Assert.Equal(403, exception.StatusCode);
        Assert.Contains("refused:admin", hub.Calls);
    }

    [Fact]
    public async Task Deleting_a_command_deletes_the_library_document()
    {
        (LibraryAgentApi api, FakeHubApi hub, _, _) = Create();

        await api.DeleteCommandAsync("lock_screen");

        Assert.Equal((DocumentKinds.Command, "lock_screen"), Assert.Single(hub.DeletedDocuments));
    }

    [Fact]
    public async Task The_scripts_are_the_script_documents_of_the_library()
    {
        (LibraryAgentApi api, FakeHubApi hub, _, _) = Create();
        hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Script, "backup.ps1", null, Samples.Text("Get-Date"), revision: 2));
        hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Command, "a", "A", Samples.CommandBody("a")));

        ScriptSummaryList scripts = await api.GetScriptsAsync();

        ScriptSummary script = Assert.Single(scripts.Scripts);
        Assert.Equal("backup.ps1", script.Name);
        Assert.Equal("hash-2", script.ETag);
    }

    [Fact]
    public async Task Sensors_are_read_created_replaced_and_deleted_in_the_library()
    {
        (LibraryAgentApi api, FakeHubApi hub, _, _) = Create();
        hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Sensor, "explorer", "Explorer", Samples.SensorBody("explorer")));

        SensorList list = await api.GetSensorsAsync();
        SensorDocument created = await api.CreateSensorAsync(Samples.Sensor("notepad", "Notepad"));
        SensorDocument replaced = await api.ReplaceSensorAsync(Samples.Sensor("explorer", "Explorer 2"));
        await api.DeleteSensorAsync("notepad");

        Assert.Equal("explorer", Assert.Single(list.Sensors).Id);
        Assert.Null(list.Sensors[0].Origin);
        Assert.Equal(1, created.Revision);
        Assert.Equal(2, replaced.Revision);
        Assert.Equal((DocumentKinds.Sensor, "notepad"), Assert.Single(hub.DeletedDocuments));
    }

    [Fact]
    public async Task The_live_check_of_the_editor_needs_no_call_and_the_hub_validates_on_save()
    {
        (LibraryAgentApi api, FakeHubApi hub, StubHandler handler, _) = Create();

        ValidationReport report = await api.ValidateCommandAsync(Samples.Command("lock_screen"));

        Assert.True(report.Valid);
        Assert.Empty(report.Errors);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain(hub.Calls, call => call.StartsWith(nameof(IHubApi.SaveLibraryDocumentAsync), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_test_without_a_chosen_pc_fails_with_a_code_that_says_so_and_asks_nobody()
    {
        (LibraryAgentApi api, _, StubHandler handler, _) = Create();

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => api.TestCommandAsync(Samples.Command("lock_screen"), new Dictionary<string, JsonElement>()));

        Assert.Equal(LibraryAgentApi.NoTestTargetCode, exception.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_test_runs_the_draft_on_the_chosen_pc_through_the_tunnel()
    {
        (LibraryAgentApi api, _, StubHandler handler, AgentSelection selection) = Create(
            _ => StubHandler.Json(HttpStatusCode.Accepted, "{\"runId\":\"run-1\",\"command\":\"lock_screen\",\"source\":\"hub\"}"));
        selection.SelectTestTarget("pc-1");

        RunStarted started = await api.TestCommandAsync(Samples.Command("lock_screen"), new Dictionary<string, JsonElement>());

        Assert.Equal("run-1", started.RunId);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/agents/pc-1/agent-api/commands/test", request.Uri.AbsolutePath);
        Assert.True(request.Headers.ContainsKey(ApiHeaders.CsrfName));
        Assert.Contains("lock_screen", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_test_target_can_change_between_two_tests_and_does_not_touch_the_selection_of_the_agent_pages()
    {
        (LibraryAgentApi api, _, StubHandler handler, AgentSelection selection) = Create(
            _ => StubHandler.Json(HttpStatusCode.Accepted, "{\"runId\":\"run-1\",\"command\":\"lock_screen\",\"source\":\"hub\"}"));

        selection.SelectTestTarget("pc-1");
        await api.TestCommandAsync(Samples.Command("lock_screen"), new Dictionary<string, JsonElement>());
        selection.SelectTestTarget("pc-2");
        await api.TestCommandAsync(Samples.Command("lock_screen"), new Dictionary<string, JsonElement>());

        Assert.Equal("/api/v1/agents/pc-1/agent-api/commands/test", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("/api/v1/agents/pc-2/agent-api/commands/test", handler.Requests[1].Uri.AbsolutePath);
        Assert.Null(selection.AgentId);
        Assert.True(selection.LibraryMode);
    }

    [Fact]
    public async Task A_sensor_is_tested_on_the_chosen_pc_too()
    {
        (LibraryAgentApi api, _, StubHandler handler, AgentSelection selection) = Create(
            _ => StubHandler.Json(HttpStatusCode.OK, "{\"succeeded\":true,\"value\":1,\"durationMs\":5}"));
        selection.SelectTestTarget("pc-1");

        SensorTestResult result = await api.TestSensorAsync(Samples.Sensor("explorer"));

        Assert.True(result.Succeeded);
        Assert.Equal("/api/v1/agents/pc-1/agent-api/sensors/test", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task What_looks_at_a_pc_needs_the_chosen_pc_and_otherwise_the_editor_works_without_it()
    {
        (LibraryAgentApi api, _, StubHandler handler, _) = Create();

        ApiException status = await Assert.ThrowsAsync<ApiException>(() => api.GetStatusAsync());
        ApiException metrics = await Assert.ThrowsAsync<ApiException>(() => api.GetMetricsAsync());

        Assert.Equal(LibraryAgentApi.NoTestTargetCode, status.Code);
        Assert.Equal(LibraryAgentApi.NoTestTargetCode, metrics.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task What_the_library_does_not_offer_answers_404_like_an_agent_without_the_feature()
    {
        (LibraryAgentApi api, _, StubHandler handler, _) = Create();

        ApiException[] failures =
        [
            await Assert.ThrowsAsync<ApiException>(() => api.GetHubConfigAsync()),
            await Assert.ThrowsAsync<ApiException>(() => api.GetSecretsAsync()),
            await Assert.ThrowsAsync<ApiException>(() => api.GetRunsAsync(10)),
            await Assert.ThrowsAsync<ApiException>(() => api.StartRunAsync("lock_screen", new Dictionary<string, JsonElement>())),
            await Assert.ThrowsAsync<ApiException>(() => api.GetModeGroupsAsync()),
            await Assert.ThrowsAsync<ApiException>(() => api.GetSettingsAsync()),
            await Assert.ThrowsAsync<ApiException>(() => api.GetSensorValueAsync("explorer")),
        ];

        Assert.All(failures, failure =>
        {
            Assert.Equal(404, failure.StatusCode);
            Assert.Equal(LibraryAgentApi.UnsupportedCode, failure.Code);
        });
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_sign_in_is_the_one_of_the_hub()
    {
        (LibraryAgentApi api, FakeHubApi hub, StubHandler handler, _) = Create();

        AuthMeResponse? me = await api.GetMeAsync();
        SetupState? setup = await api.GetSetupStateAsync();
        await api.LogOutAsync();

        Assert.NotNull(me);
        Assert.Equal("alice", me.Name);
        Assert.Null(setup);
        Assert.Equal(1, hub.LogOutCalls);
        Assert.Empty(handler.Requests);
        await Assert.ThrowsAsync<NotSupportedException>(() => api.LogInAsync(new LoginRequest("x")));
    }

    [Fact]
    public void The_selection_is_in_library_mode_until_an_agent_is_selected()
    {
        var selection = new AgentSelection();
        selection.Select("pc-1", "PC 1");
        selection.SelectTestTarget("pc-2");

        selection.UseLibrary();

        Assert.True(selection.LibraryMode);
        Assert.Null(selection.AgentId);
        Assert.Equal("pc-2", selection.TestTargetId);

        selection.Select("pc-3");

        Assert.False(selection.LibraryMode);
        Assert.Equal("pc-3", selection.AgentId);
        Assert.Equal("pc-2", selection.TestTargetId);
    }

    [Theory]
    [InlineData("commands", "library")]
    [InlineData("sensors", "library")]
    [InlineData("secrets", "library")]
    [InlineData("commands/lock%20screen", "library/command/lock%20screen")]
    [InlineData("sensors/explorer", "library/sensor/explorer")]
    public void The_links_of_the_editors_lead_to_the_library_pages_while_a_library_page_is_open(string requested, string expected)
    {
        var selection = new AgentSelection();
        selection.UseLibrary();
        var links = new AgentScopedLinks(selection);

        Assert.Equal(expected, links.Resolve("/" + requested));
    }
}
