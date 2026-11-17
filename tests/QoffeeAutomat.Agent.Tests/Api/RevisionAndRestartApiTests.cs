using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Agent.Hosting;

namespace QoffeeAutomat.Agent.Tests.Api;

/// <summary>The kept revisions of a document, their restore, and the restart of the agent (PLAN.md S08, T407).</summary>
[Trait("Category", "Api")]
public sealed class RevisionAndRestartApiTests
{
    private const string RestartPath = "/api/v1/agent/restart";

    [Fact]
    public async Task A_restart_is_requested_once_and_answered_with_202()
    {
        var restarter = new RecordingRestarter();
        await using ApiTestHost host = await ApiTestHost.StartAsync(services => services.AddSingleton<IAgentRestarter>(restarter));

        using HttpResponseMessage response = await host.PostAsync(RestartPath);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, restarter.Requests);
    }

    [Fact]
    public async Task A_restart_needs_a_session_and_the_anti_forgery_header()
    {
        var restarter = new RecordingRestarter();
        await using ApiTestHost host = await ApiTestHost.StartAsync(services => services.AddSingleton<IAgentRestarter>(restarter));

        using HttpResponseMessage anonymous = await host.SendAsync(HttpMethod.Post, RestartPath, anonymous: true);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using HttpResponseMessage noHeader = await TestAgent.SendAsync(host.Client, HttpMethod.Post, RestartPath, cookie: host.Cookie, csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);

        Assert.Equal(0, restarter.Requests);
    }

    [Fact]
    public async Task The_revisions_of_a_command_are_listed_newest_first()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await SaveThreeRevisionsAsync(host, "say_hello");

        using HttpResponseMessage response = await host.GetAsync("/api/v1/config/revisions/command/say_hello");
        JsonNode list = await ApiAssert.JsonAsync(response);

        Assert.Equal("command", list["kind"]!.GetValue<string>());
        Assert.Equal("say_hello", list["id"]!.GetValue<string>());
        Assert.Equal(3, list["current"]!.GetValue<int>());
        Assert.Equal([3, 2, 1], list["revisions"]!.AsArray().Select(entry => entry!["revision"]!.GetValue<int>()).ToArray());
    }

    [Fact]
    public async Task The_revisions_of_a_sensor_and_of_the_settings_are_listed()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        using (HttpResponseMessage created = await host.PostAsync("/api/v1/sensors", ApiSamples.Sensor("app_running", "notepad")))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using (HttpResponseMessage first = await host.PutAsync("/api/v1/settings", "{\"schemaVersion\":1,\"name\":\"Study PC\"}"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (HttpResponseMessage second = await host.PutAsync("/api/v1/settings", "{\"schemaVersion\":1,\"name\":\"Office PC\"}"))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        using HttpResponseMessage sensor = await host.GetAsync("/api/v1/config/revisions/sensor/app_running");
        JsonNode sensors = await ApiAssert.JsonAsync(sensor);
        Assert.Equal(1, sensors["current"]!.GetValue<int>());
        Assert.Single(sensors["revisions"]!.AsArray());

        using HttpResponseMessage settings = await host.GetAsync("/api/v1/config/revisions/settings/settings");
        JsonNode list = await ApiAssert.JsonAsync(settings);
        Assert.Equal(2, list["current"]!.GetValue<int>());
        Assert.Equal(2, list["revisions"]!.AsArray().Count);

        using HttpResponseMessage wrongId = await host.GetAsync("/api/v1/config/revisions/settings/other");
        await ApiAssert.ProblemAsync(wrongId, HttpStatusCode.NotFound, "unknown_document");
    }

    [Fact]
    public async Task Revisions_of_a_missing_document_or_an_unknown_kind_are_404()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage missing = await host.GetAsync("/api/v1/config/revisions/command/never_saved");
        await ApiAssert.ProblemAsync(missing, HttpStatusCode.NotFound, "unknown_document");

        using HttpResponseMessage kind = await host.GetAsync("/api/v1/config/revisions/script/hello.ps1");
        await ApiAssert.ProblemAsync(kind, HttpStatusCode.NotFound, "unknown_kind");

        using HttpResponseMessage restoreKind = await host.PostAsync("/api/v1/config/revisions/script/hello.ps1/1/restore");
        await ApiAssert.ProblemAsync(restoreKind, HttpStatusCode.NotFound, "unknown_kind");
    }

    [Fact]
    public async Task Restoring_a_revision_stores_it_as_a_new_revision()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await SaveThreeRevisionsAsync(host, "say_hello");

        using HttpResponseMessage restored = await host.SendAsync(HttpMethod.Post, "/api/v1/config/revisions/command/say_hello/1/restore", ifMatch: "\"3\"");

        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
        Assert.Equal("4", ApiAssert.ETag(restored));

        using HttpResponseMessage read = await host.GetAsync("/api/v1/commands/say_hello");
        JsonNode command = await ApiAssert.JsonAsync(read);
        Assert.Equal("First name", command["name"]!.GetValue<string>());
        Assert.Equal(4, command["revision"]!.GetValue<int>());

        using HttpResponseMessage list = await host.GetAsync("/api/v1/config/revisions/command/say_hello");
        JsonNode revisions = await ApiAssert.JsonAsync(list);
        Assert.Equal(4, revisions["current"]!.GetValue<int>());
        Assert.Equal(4, revisions["revisions"]!.AsArray().Count);
    }

    [Fact]
    public async Task Restoring_without_If_Match_is_allowed()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await SaveThreeRevisionsAsync(host, "say_hello");

        using HttpResponseMessage restored = await host.PostAsync("/api/v1/config/revisions/command/say_hello/2/restore");

        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
        using HttpResponseMessage read = await host.GetAsync("/api/v1/commands/say_hello");
        JsonNode command = await ApiAssert.JsonAsync(read);
        Assert.Equal("Second name", command["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Restoring_with_a_stale_If_Match_is_refused_and_changes_nothing()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await SaveThreeRevisionsAsync(host, "say_hello");

        using HttpResponseMessage stale = await host.SendAsync(HttpMethod.Post, "/api/v1/config/revisions/command/say_hello/1/restore", ifMatch: "\"2\"");

        JsonNode problem = await ApiAssert.ProblemAsync(stale, HttpStatusCode.PreconditionFailed, "revision_mismatch");
        Assert.Equal(3, problem["currentRevision"]!.GetValue<int>());
        using HttpResponseMessage read = await host.GetAsync("/api/v1/commands/say_hello");
        JsonNode command = await ApiAssert.JsonAsync(read);
        Assert.Equal(3, command["revision"]!.GetValue<int>());
        Assert.Equal("Third name", command["name"]!.GetValue<string>());

        using HttpResponseMessage malformed = await host.SendAsync(HttpMethod.Post, "/api/v1/config/revisions/command/say_hello/1/restore", ifMatch: "\"abc\"");
        await ApiAssert.ProblemAsync(malformed, HttpStatusCode.BadRequest, "invalid_if_match");
    }

    [Fact]
    public async Task Restoring_a_revision_that_is_not_kept_is_404()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await SaveThreeRevisionsAsync(host, "say_hello");

        using HttpResponseMessage unknownRevision = await host.PostAsync("/api/v1/config/revisions/command/say_hello/99/restore");
        await ApiAssert.ProblemAsync(unknownRevision, HttpStatusCode.NotFound, "unknown_revision");

        using HttpResponseMessage unknownCommand = await host.PostAsync("/api/v1/config/revisions/command/never_saved/1/restore");
        await ApiAssert.ProblemAsync(unknownCommand, HttpStatusCode.NotFound, "unknown_revision");
    }

    [Fact]
    public async Task A_deleted_command_keeps_its_revisions_and_can_be_restored()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await SaveThreeRevisionsAsync(host, "say_hello");
        using (HttpResponseMessage deleted = await host.DeleteAsync("/api/v1/commands/say_hello"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using HttpResponseMessage list = await host.GetAsync("/api/v1/config/revisions/command/say_hello");
        JsonNode revisions = await ApiAssert.JsonAsync(list);
        Assert.Null(revisions["current"]);
        Assert.Equal(3, revisions["revisions"]!.AsArray().Count);

        // Zero means "must not exist": true for a deleted document.
        using HttpResponseMessage restored = await host.SendAsync(HttpMethod.Post, "/api/v1/config/revisions/command/say_hello/3/restore", ifMatch: "\"0\"");
        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
        using HttpResponseMessage read = await host.GetAsync("/api/v1/commands/say_hello");
        JsonNode command = await ApiAssert.JsonAsync(read);
        Assert.Equal("Third name", command["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_settings_can_be_restored()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        using (HttpResponseMessage first = await host.PutAsync("/api/v1/settings", "{\"schemaVersion\":1,\"name\":\"Study PC\"}"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (HttpResponseMessage second = await host.PutAsync("/api/v1/settings", "{\"schemaVersion\":1,\"name\":\"Office PC\"}"))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        using HttpResponseMessage restored = await host.SendAsync(HttpMethod.Post, "/api/v1/config/revisions/settings/settings/1/restore", ifMatch: "\"2\"");

        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
        using HttpResponseMessage read = await host.GetAsync("/api/v1/settings");
        JsonNode settings = await ApiAssert.JsonAsync(read);
        Assert.Equal("Study PC", settings["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_revision_endpoints_need_a_session_and_a_restore_the_anti_forgery_header()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await SaveThreeRevisionsAsync(host, "say_hello");

        using HttpResponseMessage anonymousList = await host.SendAsync(HttpMethod.Get, "/api/v1/config/revisions/command/say_hello", anonymous: true);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousList.StatusCode);

        using HttpResponseMessage anonymousRestore = await host.SendAsync(HttpMethod.Post, "/api/v1/config/revisions/command/say_hello/1/restore", anonymous: true);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRestore.StatusCode);

        using HttpResponseMessage noHeader = await TestAgent.SendAsync(
            host.Client, HttpMethod.Post, "/api/v1/config/revisions/command/say_hello/1/restore", cookie: host.Cookie, csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);

        using HttpResponseMessage read = await host.GetAsync("/api/v1/commands/say_hello");
        JsonNode command = await ApiAssert.JsonAsync(read);
        Assert.Equal(3, command["revision"]!.GetValue<int>());
    }

    /// <summary>Saves a command three times; its name tells the revision.</summary>
    private static async Task SaveThreeRevisionsAsync(ApiTestHost host, string id)
    {
        await host.CreateCommandAsync(ApiSamples.Command(id, name: "First name"));
        foreach (string name in new[] { "Second name", "Third name" })
        {
            using HttpResponseMessage response = await host.PutAsync("/api/v1/commands/" + id, ApiSamples.Command(id, name: name));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    /// <summary>Counts the restarts instead of stopping the test host.</summary>
    private sealed class RecordingRestarter : IAgentRestarter
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public void RequestRestart() => Interlocked.Increment(ref _requests);
    }
}
