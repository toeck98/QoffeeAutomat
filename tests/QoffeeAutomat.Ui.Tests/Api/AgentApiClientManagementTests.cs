using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Api;

/// <summary>
/// The calls of the settings, history, log, backup and diagnostics pages (T407) over HTTP: the URLs and queries, the
/// anti-forgery header on what changes state, the archive as <c>application/zip</c>, the file name of a download, and the
/// validation errors of a refused import.
/// </summary>
[Trait("Category", "Settings")]
public sealed class AgentApiClientManagementTests : IDisposable
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
    public async Task Runs_are_listed_with_filters_and_a_page()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(Samples.Runs(), ApiJsonContext.Default.RunList)));
        AgentApiClient client = Client(handler);

        RunList list = await client.QueryRunsAsync(new RunQuery(25, 50, "back up", "ui", true));

        Assert.Equal(2, list.Runs.Count);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v1/runs", request.Uri.AbsolutePath);
        Assert.Equal("?limit=25&offset=50&command=back%20up&source=ui&failed=true", request.Uri.Query);
        Assert.False(request.Headers.ContainsKey(ApiHeaders.CsrfName));
    }

    [Fact]
    public async Task Runs_without_filters_send_only_the_limit()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(Samples.Runs(), ApiJsonContext.Default.RunList)));
        AgentApiClient client = Client(handler);

        await client.QueryRunsAsync(new RunQuery(10));

        Assert.Equal("?limit=10", Assert.Single(handler.Requests).Uri.Query);
    }

    [Fact]
    public async Task One_run_is_read_with_its_variables_and_output()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(Samples.RunDetail("r1"), ApiJsonContext.Default.RunDetail)));
        AgentApiClient client = Client(handler);

        RunDetail detail = await client.GetRunAsync("r1");

        Assert.Equal("backup", detail.Command);
        Assert.Equal("disk full", detail.StandardError);
        Assert.Equal(5, detail.Variables!.Value.GetProperty("minutes").GetInt32());
        Assert.Equal("/api/v1/runs/r1", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task An_unknown_run_is_a_404_exception()
    {
        var handler = new StubHandler(_ => Problem(HttpStatusCode.NotFound, Samples.Problem(404, "not_found", "There is no run with this id.")));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.GetRunAsync("nope"));

        Assert.Equal(404, exception.StatusCode);
    }

    [Fact]
    public async Task Events_are_listed_with_an_optional_type()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(Samples.Events(), ApiJsonContext.Default.EventList)));
        AgentApiClient client = Client(handler);

        await client.QueryEventsAsync(100, "session.locked");
        await client.QueryEventsAsync(100, null);

        Assert.Equal("?limit=100&type=session.locked", handler.Requests[0].Uri.Query);
        Assert.Equal("?limit=100", handler.Requests[1].Uri.Query);
        Assert.Equal("/api/v1/events", handler.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task The_log_is_read_with_level_and_limit()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(new LogList(Samples.Logs()), ApiJsonContext.Default.LogList)));
        AgentApiClient client = Client(handler);

        LogList list = await client.GetLogsAsync("warning", 250);

        Assert.Equal(4, list.Entries.Count);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/logs", request.Uri.AbsolutePath);
        Assert.Equal("?level=warning&limit=250", request.Uri.Query);
    }

    [Fact]
    public async Task The_password_is_changed_with_a_post_and_the_anti_forgery_header()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        AgentApiClient client = Client(handler);

        await client.ChangePasswordAsync(new ChangePasswordRequest("old", "new"));

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/auth/password", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
        Assert.Equal("{\"currentPassword\":\"old\",\"newPassword\":\"new\"}", request.Body);
    }

    [Fact]
    public async Task A_wrong_current_password_is_an_exception_with_the_code()
    {
        var handler = new StubHandler(_ => Problem(HttpStatusCode.Forbidden, Samples.Problem(403, "invalid_credentials", "The password is wrong.")));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.ChangePasswordAsync(new ChangePasswordRequest("x", "y")));

        Assert.Equal(403, exception.StatusCode);
        Assert.Equal("invalid_credentials", exception.Code);
    }

    [Fact]
    public async Task A_restart_is_a_post_to_the_agent()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted));
        AgentApiClient client = Client(handler);

        await client.RestartAgentAsync();

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/agent/restart", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
    }

    [Fact]
    public async Task The_export_is_downloaded_with_the_file_name_of_the_agent()
    {
        var handler = new StubHandler(_ => Zip([1, 2, 3], "qoffeeautomat-config-20260101-120000.zip"));
        AgentApiClient client = Client(handler);

        DownloadedFile file = await client.ExportConfigAsync();

        Assert.Equal("qoffeeautomat-config-20260101-120000.zip", file.FileName);
        Assert.Equal("application/zip", file.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3 }, file.Content);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v1/config/export", request.Uri.AbsolutePath);
        Assert.False(request.Headers.ContainsKey(ApiHeaders.CsrfName));
    }

    [Fact]
    public async Task The_diagnostics_get_a_default_name_when_the_agent_names_none()
    {
        var handler = new StubHandler(_ => Zip([4, 5], null));
        AgentApiClient client = Client(handler);

        DownloadedFile file = await client.DownloadDiagnosticsAsync();

        Assert.Equal("qoffeeautomat-diagnostics.zip", file.FileName);
        Assert.Equal(new byte[] { 4, 5 }, file.Content);
        Assert.Equal("/api/v1/diagnostics", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task A_preview_posts_the_archive_as_zip_and_reads_what_will_change()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(Samples.ImportPreview(), ApiJsonContext.Default.ConfigImportPreview)));
        AgentApiClient client = Client(handler);

        ConfigImportPreview preview = await client.PreviewImportAsync([80, 75], includeSettings: true);

        Assert.True(preview.Valid);
        Assert.Equal(3, preview.Items.Count);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/config/import/preview", request.Uri.AbsolutePath);
        Assert.Equal("?settings=true", request.Uri.Query);
        Assert.Equal("application/zip", request.ContentType);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
    }

    [Fact]
    public async Task A_preview_without_the_settings_has_no_query()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(Samples.ImportPreview(), ApiJsonContext.Default.ConfigImportPreview)));
        AgentApiClient client = Client(handler);

        await client.PreviewImportAsync([1], includeSettings: false);

        Assert.Equal(string.Empty, Assert.Single(handler.Requests).Uri.Query);
    }

    [Fact]
    public async Task An_archive_that_does_not_validate_is_a_preview_with_errors()
    {
        var handler = new StubHandler(_ => Problem(
            HttpStatusCode.UnprocessableEntity,
            Samples.Problem(422, "validation_failed", "Invalid.", new ApiValidationError("/config/commands/x.json/id", "required", "The id is required."))));
        AgentApiClient client = Client(handler);

        ConfigImportPreview preview = await client.PreviewImportAsync([1], includeSettings: false);

        Assert.False(preview.Valid);
        Assert.Empty(preview.Items);
        ApiValidationError error = Assert.Single(preview.Errors);
        Assert.Equal("/config/commands/x.json/id", error.Path);
    }

    [Fact]
    public async Task An_import_posts_the_archive_and_reads_what_was_applied()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(new ConfigImportResult(2, 1, 3, true, false), ApiJsonContext.Default.ConfigImportResult)));
        AgentApiClient client = Client(handler);

        ConfigImportResult result = await client.ImportConfigAsync([1, 2], includeSettings: false);

        Assert.Equal(2, result.Commands);
        Assert.True(result.Metrics);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/config/import", request.Uri.AbsolutePath);
        Assert.Equal(string.Empty, request.Uri.Query);
        Assert.Equal("application/zip", request.ContentType);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
    }

    [Fact]
    public async Task A_refused_import_throws_with_the_validation_errors()
    {
        var handler = new StubHandler(_ => Problem(
            HttpStatusCode.UnprocessableEntity,
            Samples.Problem(422, "validation_failed", "Invalid.", new ApiValidationError("/config/sensors/bad.json/source", "required", "The sensor needs a source."))));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.ImportConfigAsync([1], includeSettings: true));

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal("/config/sensors/bad.json/source", Assert.Single(exception.ValidationErrors).Path);
        Assert.Equal("?settings=true", Assert.Single(handler.Requests).Uri.Query);
    }

    [Fact]
    public async Task The_revisions_of_a_document_are_read_by_kind_and_id()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(Samples.RevisionsOf("command", "lock screen", 3), ApiJsonContext.Default.RevisionList)));
        AgentApiClient client = Client(handler);

        RevisionList list = await client.GetRevisionsAsync("command", "lock screen");

        Assert.Equal(3, list.Current);
        Assert.Equal([3, 2, 1], list.Revisions.Select(entry => entry.Revision));
        Assert.Equal("/api/v1/config/revisions/command/lock%20screen", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task A_restore_posts_the_revision_with_the_current_one_as_if_match()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        AgentApiClient client = Client(handler);

        await client.RestoreRevisionAsync("sensor", "disk_temp", 1, expectedRevision: 3);

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/config/revisions/sensor/disk_temp/1/restore", request.Uri.AbsolutePath);
        Assert.Equal("\"3\"", request.Headers["If-Match"]);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
    }

    [Fact]
    public async Task A_restore_without_an_expected_revision_sends_no_if_match()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        AgentApiClient client = Client(handler);

        await client.RestoreRevisionAsync("settings", "settings", 2, expectedRevision: null);

        Assert.False(Assert.Single(handler.Requests).Headers.ContainsKey("If-Match"));
    }

    [Fact]
    public async Task A_stale_restore_is_a_412_exception()
    {
        var handler = new StubHandler(_ => Problem(HttpStatusCode.PreconditionFailed, Samples.Problem(412, "revision_mismatch", "The document was changed meanwhile.")));
        AgentApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.RestoreRevisionAsync("command", "backup", 1, 5));

        Assert.Equal(412, exception.StatusCode);
    }

    private static HttpResponseMessage Zip(byte[] bytes, string? fileName)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        if (fileName is not null)
        {
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = fileName };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
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
