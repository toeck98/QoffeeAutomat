using System.Net;
using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Config.Rollout;
using QoffeeAutomat.Hub.Metrics;
using QoffeeAutomat.Hub.Ui.Services;
using QoffeeAutomat.Hub.Ui.Tests.Support;
using QoffeeAutomat.Ui.Components.Api;

namespace QoffeeAutomat.Hub.Ui.Tests.Config;

/// <summary>
/// The calls of the hub API client that T916 added (PLAN.md S20): library, profiles, publish and roll-back, assignments and the metric query. They
/// are tested against a stub handler: the address, the method, the anti-forgery header on what changes state, the camel-case JSON that the hub
/// reads, and the problem details that the hub answers with.
/// </summary>
[Trait("Category", "Config")]
public sealed class HubApiClientConfigTests : IDisposable
{
    private const string LibraryInfoJson = "{\"kind\":\"command\",\"id\":\"lock_screen\",\"name\":\"Lock screen\",\"revision\":2,\"hash\":\"h2\",\"createdAt\":\"2026-11-19T12:00:00+00:00\",\"createdBy\":\"alice\",\"body\":{\"id\":\"lock_screen\"},\"changed\":true}";

    private const string ProfileJson = "{\"id\":\"office\",\"draft\":{\"name\":\"Office\",\"priority\":5,\"items\":[{\"kind\":\"command\",\"id\":\"lock_screen\",\"revision\":2}],\"variables\":{\"SITE\":\"berlin\"},\"policy\":{\"localConfig\":\"locked\",\"directLink\":\"allow\"}},\"activeRevision\":2,\"latestRevision\":2,\"hasUnpublishedChanges\":false,\"createdAt\":\"2026-11-17T12:00:00+00:00\",\"updatedAt\":\"2026-11-18T12:00:00+00:00\"}";

    private readonly List<HttpClient> _clients = [];

    public void Dispose()
    {
        foreach (HttpClient client in _clients)
        {
            client.Dispose();
        }
    }

    private HubApiClient Client(StubHandler handler, string baseAddress = "https://hub.example/")
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };
        _clients.Add(http);
        return new HubApiClient(http);
    }

    [Fact]
    public async Task The_library_is_listed_with_an_optional_kind_filter()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, "{\"documents\":[{\"kind\":\"command\",\"id\":\"lock_screen\",\"name\":\"Lock screen\",\"revision\":2,\"hash\":\"h2\",\"createdAt\":\"2026-11-19T12:00:00+00:00\",\"updatedAt\":\"2026-11-19T12:00:00+00:00\"}]}"));
        HubApiClient client = Client(handler);

        LibraryDocumentList all = await client.GetLibraryAsync();
        LibraryDocumentList commands = await client.GetLibraryAsync("command");

        Assert.Equal("lock_screen", Assert.Single(all.Documents).Id);
        Assert.Single(commands.Documents);
        Assert.Equal("/api/v1/library", handler.Requests[0].Uri.PathAndQuery);
        Assert.Equal("/api/v1/library?kind=command", handler.Requests[1].Uri.PathAndQuery);
        Assert.All(handler.Requests, request => Assert.False(request.Headers.ContainsKey(ApiHeaders.CsrfName)));
    }

    [Fact]
    public async Task A_library_document_is_read_with_its_body_and_optionally_at_a_revision()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, LibraryInfoJson));
        HubApiClient client = Client(handler);

        LibraryDocumentInfo current = await client.GetLibraryDocumentAsync("command", "lock screen");
        await client.GetLibraryDocumentAsync("command", "lock_screen", 1);

        Assert.Equal(2, current.Revision);
        Assert.Equal("lock_screen", current.Body.GetProperty("id").GetString());
        Assert.True(current.Changed);
        Assert.Equal("/api/v1/library/command/lock%20screen", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("/api/v1/library/command/lock_screen?revision=1", handler.Requests[1].Uri.PathAndQuery);
    }

    [Fact]
    public async Task A_library_document_is_saved_with_a_put_that_wraps_the_body()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.Created, LibraryInfoJson));
        HubApiClient client = Client(handler);

        LibraryDocumentInfo saved = await client.SaveLibraryDocumentAsync("command", "lock_screen", Samples.Json("{\"id\":\"lock_screen\"}"));

        Assert.Equal(2, saved.Revision);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/api/v1/library/command/lock_screen", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
        Assert.Equal("{\"body\":{\"id\":\"lock_screen\"}}", request.Body);
    }

    [Fact]
    public async Task The_text_of_a_script_travels_as_a_json_string()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, LibraryInfoJson));
        HubApiClient client = Client(handler);

        await client.SaveLibraryDocumentAsync("script", "backup.ps1", Samples.Text("Get-Date"));

        Assert.Equal("{\"body\":\"Get-Date\"}", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task A_library_document_is_deleted_with_a_delete()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        HubApiClient client = Client(handler);

        await client.DeleteLibraryDocumentAsync("command", "lock_screen");

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/api/v1/library/command/lock_screen", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
    }

    [Fact]
    public async Task The_revisions_of_a_library_document_are_read_from_their_own_address()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, "{\"kind\":\"command\",\"id\":\"lock_screen\",\"revisions\":[{\"revision\":2,\"hash\":\"h2\",\"createdAt\":\"2026-11-19T12:00:00+00:00\",\"createdBy\":\"alice\"}]}"));
        HubApiClient client = Client(handler);

        LibraryRevisionList list = await client.GetLibraryRevisionsAsync("command", "lock_screen");

        Assert.Equal(2, Assert.Single(list.Revisions).Revision);
        Assert.Equal("/api/v1/library/command/lock_screen/revisions", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task A_profile_is_created_and_its_draft_replaced_with_camel_case_json_without_the_members_that_are_not_set()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, ProfileJson));
        HubApiClient client = Client(handler);

        ProfileInfo created = await client.CreateProfileAsync(new ProfileDraftRequest("office", "Office", 0, null, [], new Dictionary<string, string>(), new ProfilePolicyRequest("inherit", "inherit")));
        await client.UpdateProfileAsync(
            "office",
            new ProfileDraftRequest(null, "Office", 5, "windows", [new ProfileItemRef("command", "lock_screen", null)], new Dictionary<string, string> { ["SITE"] = "berlin" }, new ProfilePolicyRequest("locked", "allow")));

        Assert.Equal(5, created.Draft.Priority);
        Assert.Equal("locked", created.Draft.Policy.LocalConfig);
        Assert.Equal(2, created.ActiveRevision);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/v1/profiles", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("{\"id\":\"office\",\"name\":\"Office\",\"priority\":0,\"items\":[],\"variables\":{},\"policy\":{\"localConfig\":\"inherit\",\"directLink\":\"inherit\"}}", handler.Requests[0].Body);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Equal("/api/v1/profiles/office", handler.Requests[1].Uri.AbsolutePath);
        Assert.Equal("{\"name\":\"Office\",\"priority\":5,\"osFilter\":\"windows\",\"items\":[{\"kind\":\"command\",\"id\":\"lock_screen\"}],\"variables\":{\"SITE\":\"berlin\"},\"policy\":{\"localConfig\":\"locked\",\"directLink\":\"allow\"}}", handler.Requests[1].Body);
    }

    [Fact]
    public async Task Profiles_are_listed_read_and_deleted()
    {
        var handler = new StubHandler(request => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : StubHandler.Json(HttpStatusCode.OK, request.Uri.AbsolutePath.EndsWith("profiles", StringComparison.Ordinal) ? "{\"profiles\":[" + ProfileJson + "]}" : ProfileJson));
        HubApiClient client = Client(handler);

        ProfileList list = await client.GetProfilesAsync();
        ProfileInfo one = await client.GetProfileAsync("office");
        await client.DeleteProfileAsync("office");

        Assert.Equal("office", Assert.Single(list.Profiles).Id);
        Assert.Equal("Office", one.Draft.Name);
        Assert.Equal("SITE", Assert.Single(one.Draft.Variables).Key);
        Assert.Equal(HttpMethod.Delete, handler.Requests[2].Method);
        Assert.Equal("/api/v1/profiles/office", handler.Requests[2].Uri.AbsolutePath);
    }

    [Fact]
    public async Task A_preview_names_the_revision_to_republish_when_there_is_one()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, "{\"profileId\":\"office\",\"revision\":3,\"newRevision\":true,\"unchanged\":false,\"containsSystemScripts\":true,\"systemScripts\":[\"lock_screen\"],\"requiresAdmin\":true,\"agents\":[{\"agentId\":\"pc-1\",\"agentName\":\"PC 1\",\"currentGeneration\":1,\"newGeneration\":2,\"documents\":[{\"kind\":\"command\",\"id\":\"lock_screen\",\"change\":\"added\",\"newHash\":\"h\"}],\"policy\":[{\"name\":\"localConfig\",\"from\":\"allow\",\"to\":\"locked\"}],\"warnings\":[]}]}"));
        HubApiClient client = Client(handler);

        PublishPreview draft = await client.PreviewProfileAsync("office");
        await client.PreviewProfileAsync("office", 2);

        Assert.True(draft.RequiresAdmin);
        AgentChange change = Assert.Single(draft.Agents);
        Assert.Equal("added", Assert.Single(change.Documents).Change);
        Assert.Equal("localConfig", Assert.Single(change.Policy).Name);
        Assert.Equal("/api/v1/profiles/office/preview", handler.Requests[0].Uri.PathAndQuery);
        Assert.Equal("/api/v1/profiles/office/preview?revision=2", handler.Requests[1].Uri.PathAndQuery);
    }

    [Fact]
    public async Task Publishing_and_rolling_back_are_posts_with_a_note_and_the_revision()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, "{\"profileId\":\"office\",\"revision\":3,\"newRevision\":true,\"unchanged\":false,\"agents\":[]}"));
        HubApiClient client = Client(handler);

        PublishResult published = await client.PublishProfileAsync("office", "first");
        await client.PublishProfileAsync("office");
        await client.RollbackProfileAsync("office", 2, "back");

        Assert.Equal(3, published.Revision);
        Assert.Equal("/api/v1/profiles/office/publish", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("{\"note\":\"first\"}", handler.Requests[0].Body);
        Assert.Equal("{}", handler.Requests[1].Body);
        Assert.Equal("/api/v1/profiles/office/rollback", handler.Requests[2].Uri.AbsolutePath);
        Assert.Equal("{\"revision\":2,\"note\":\"back\"}", handler.Requests[2].Body);
        Assert.All(handler.Requests, request => Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]));
    }

    [Fact]
    public async Task The_admin_required_refusal_of_a_publish_keeps_its_code()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.Forbidden, "{\"type\":\"about:blank\",\"title\":\"Forbidden\",\"status\":403,\"detail\":\"Only an administrator may publish system scripts.\",\"code\":\"admin_required\"}", "application/problem+json"));
        HubApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.PublishProfileAsync("office"));

        Assert.Equal(403, exception.StatusCode);
        Assert.Equal("admin_required", exception.Code);
    }

    [Fact]
    public async Task A_document_that_does_not_validate_comes_back_with_the_places_that_are_wrong()
    {
        var handler = new StubHandler(_ => StubHandler.Json(
            HttpStatusCode.UnprocessableEntity,
            "{\"type\":\"about:blank\",\"title\":\"Validation failed\",\"status\":422,\"code\":\"validation_failed\",\"errors\":[{\"path\":\"/action/code\",\"code\":\"required\",\"message\":\"The code is required.\"}]}",
            "application/problem+json"));
        HubApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.SaveLibraryDocumentAsync("command", "x", Samples.Json("{}")));

        Assert.Equal(422, exception.StatusCode);
        ApiValidationError error = Assert.Single(exception.ValidationErrors);
        Assert.Equal("/action/code", error.Path);
    }

    [Fact]
    public async Task Assignments_are_listed_created_and_deleted()
    {
        var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return request.Method == HttpMethod.Post
                ? StubHandler.Json(HttpStatusCode.Created, "{\"assignment\":{\"id\":\"a1\",\"profileId\":\"office\",\"target\":\"tag:lab\",\"createdAt\":\"2026-11-19T12:00:00+00:00\",\"createdBy\":\"alice\"},\"agents\":[{\"agentId\":\"pc-1\",\"agentName\":\"PC 1\",\"currentGeneration\":1,\"newGeneration\":2,\"documents\":[],\"policy\":[],\"warnings\":[]}]}")
                : StubHandler.Json(HttpStatusCode.OK, "{\"assignments\":[{\"id\":\"a1\",\"profileId\":\"office\",\"target\":\"all\",\"createdAt\":\"2026-11-19T12:00:00+00:00\",\"createdBy\":\"alice\"}]}");
        });
        HubApiClient client = Client(handler);

        AssignmentList list = await client.GetAssignmentsAsync();
        CreatedAssignment created = await client.CreateAssignmentAsync(new CreateAssignmentRequest("office", "tag:lab"));
        await client.DeleteAssignmentAsync("a1");

        Assert.Equal("all", Assert.Single(list.Assignments).Target);
        Assert.Equal("PC 1", Assert.Single(created.Agents).AgentName);
        Assert.Equal("{\"profileId\":\"office\",\"target\":\"tag:lab\"}", handler.Requests[1].Body);
        Assert.Equal("/api/v1/assignments/a1", handler.Requests[2].Uri.AbsolutePath);
    }

    [Fact]
    public async Task The_rollout_of_a_profile_and_the_desired_state_of_an_agent_have_their_own_addresses()
    {
        var handler = new StubHandler(request => request.Uri.AbsolutePath.EndsWith("rollout", StringComparison.Ordinal)
            ? StubHandler.Json(HttpStatusCode.OK, "{\"profileId\":\"office\",\"revision\":2,\"counts\":{\"total\":1,\"upToDate\":1,\"pending\":0,\"failed\":0,\"noDesiredState\":0,\"offline\":0},\"agents\":[]}")
            : StubHandler.Json(HttpStatusCode.OK, "{\"agentId\":\"pc-1\",\"generation\":3,\"upToDate\":true,\"policy\":{\"localConfig\":\"allow\",\"directLink\":\"allow\"},\"profiles\":[\"office\"],\"items\":[],\"warnings\":[]}"));
        HubApiClient client = Client(handler);

        ProfileRolloutInfo rollout = await client.GetProfileRolloutAsync("office", 2);
        AgentDesiredInfo desired = await client.GetAgentDesiredStateAsync("pc-1");

        Assert.Equal(1, rollout.Counts.UpToDate);
        Assert.Equal(3, desired.Generation);
        Assert.Equal("/api/v1/profiles/office/rollout?revision=2", handler.Requests[0].Uri.PathAndQuery);
        Assert.Equal("/api/v1/agents/pc-1/desired-state", handler.Requests[1].Uri.AbsolutePath);
    }

    [Fact]
    public async Task The_latest_metrics_of_an_agent_are_read_from_the_agent_address()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, "{\"agentId\":\"pc-1\",\"metrics\":[{\"agentId\":\"pc-1\",\"metricId\":\"cpu.total\",\"valueType\":\"number\",\"unit\":\"%\",\"ts\":\"2026-11-19T12:00:00+00:00\",\"value\":12.5}]}"));
        HubApiClient client = Client(handler);

        AgentMetricList list = await client.GetAgentMetricsAsync("pc-1");

        LatestMetric metric = Assert.Single(list.Metrics);
        Assert.Equal("cpu.total", metric.MetricId);
        Assert.Equal(12.5, metric.Value);
        Assert.Equal("/api/v1/agents/pc-1/metrics", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task A_metric_query_is_sent_with_the_agents_metrics_range_and_resolution_and_the_points_are_read()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, "{\"resolution\":\"raw\",\"from\":\"2026-11-19T06:00:00+00:00\",\"to\":\"2026-11-19T12:00:00+00:00\",\"series\":[{\"agentId\":\"pc-1\",\"metricId\":\"cpu.total\",\"valueType\":\"number\",\"unit\":\"%\",\"resolution\":\"raw\",\"points\":[{\"ts\":\"2026-11-19T07:00:00+00:00\",\"value\":10},{\"ts\":\"2026-11-19T08:00:00+00:00\",\"value\":20}],\"truncated\":false}]}"));
        HubApiClient client = Client(handler);
        DateTimeOffset to = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

        MetricQueryResult result = await client.QueryMetricsAsync(new MetricQueryParameters(["pc-1"], ["cpu.total"], to.AddHours(-6), to, "raw", 500));

        MetricSeriesData series = Assert.Single(result.Series);
        Assert.Equal(2, series.Points.Count);
        Assert.Equal(20, series.Points[1].Value);
        Assert.Equal("/api/v1/metrics/query", handler.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public void The_metric_query_string_lists_agents_and_metrics_with_commas_and_writes_the_times_in_utc()
    {
        DateTimeOffset to = new(2026, 11, 19, 14, 0, 0, TimeSpan.FromHours(2));

        string query = HubApiClient.MetricQueryString(new MetricQueryParameters(["pc-1", "pc-2"], ["cpu.total", "memory.*"], to.AddHours(-6), to, "5m", 500));

        Assert.Equal("?agent=pc-1%2Cpc-2&metric=cpu.total%2Cmemory.%2A&from=2026-11-19T06%3A00%3A00Z&to=2026-11-19T12%3A00%3A00Z&resolution=5m&maxPoints=500", query);
    }

    [Fact]
    public void The_metric_query_string_leaves_out_what_is_not_set()
    {
        string query = HubApiClient.MetricQueryString(new MetricQueryParameters(["pc 1"], []));

        Assert.Equal("?agent=pc%201", query);
    }

    [Fact]
    public async Task The_addresses_stay_below_a_path_prefix_such_as_home_assistant_ingress()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, "{\"profiles\":[]}"));
        HubApiClient client = Client(handler, "https://ha.local/api/hassio_ingress/token/");

        await client.GetProfilesAsync();

        Assert.Equal("/api/hassio_ingress/token/api/v1/profiles", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public void The_json_context_knows_the_body_of_a_library_document_without_reflection()
    {
        LibraryDocumentInfo? info = JsonSerializer.Deserialize(LibraryInfoJson, HubUiJsonContext.Default.LibraryDocumentInfo);

        Assert.NotNull(info);
        Assert.Equal(JsonValueKind.Object, info.Body.ValueKind);
    }
}
