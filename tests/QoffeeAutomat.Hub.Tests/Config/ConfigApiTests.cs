using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Security;
using QoffeeAutomat.Hub.Tests.Security;

namespace QoffeeAutomat.Hub.Tests.Config;

/// <summary>
/// The configuration endpoints of <c>/api/v1</c> in the whole hub (T907): the flow from library to publish, validation answers, the roles, the
/// rule for system scripts, the audit log and the published OpenAPI document.
/// </summary>
[Trait("Category", "Config")]
public sealed class ConfigApiTests
{
    private static Task<HubTestHost> StartAsync(FakeAgentSource agents)
    {
        return HubTestHost.StartAsync(
            withUsers: true,
            configure: services =>
            {
                services.RemoveAll<IConfigAgentSource>();
                services.AddSingleton<IConfigAgentSource>(agents);
            });
    }

    private static FakeAgentSource Agents()
    {
        var agents = new FakeAgentSource();
        agents.Set(Docs.Agent("pc1", "windows", "enrolled", "office"), Docs.Agent("pc2", "linux"));
        return agents;
    }

    private static string Body(JsonElement document)
    {
        return new JsonObject { ["body"] = JsonNode.Parse(document.GetRawText()) }.ToJsonString();
    }

    private static string[] Strings(JsonNode? array)
    {
        return [.. array!.AsArray().Select(item => item!.GetValue<string>())];
    }

    private static async Task<JsonNode> PutAsync(HubApiClient client, string path, string json, HttpStatusCode expected)
    {
        using HttpResponseMessage response = await client.SendAsync(HttpMethod.Put, path, json);
        return await HubAssert.JsonAsync(response, expected);
    }

    private static async Task<JsonNode> PostAsync(HubApiClient client, string path, string? json, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using HttpResponseMessage response = await client.PostAsync(path, json);
        return await HubAssert.JsonAsync(response, expected);
    }

    private static async Task<JsonNode> GetAsync(HubApiClient client, string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using HttpResponseMessage response = await client.GetAsync(path);
        return await HubAssert.JsonAsync(response, expected);
    }

    private static async Task<JsonNode> ProblemAsync(Task<HttpResponseMessage> request, HttpStatusCode status, string code)
    {
        using HttpResponseMessage response = await request;
        return await HubAssert.ProblemAsync(response, status, code);
    }

    private static async Task CreateProfileAsync(HubApiClient client, string id, string itemsJson = "[]", string? extra = null)
    {
        await PostAsync(
            client,
            "/api/v1/profiles",
            "{\"id\":\"" + id + "\",\"name\":\"Profile " + id + "\",\"items\":" + itemsJson + (extra is null ? string.Empty : "," + extra) + "}",
            HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_configuration_goes_from_the_library_through_a_profile_to_the_agents_over_the_API()
    {
        FakeAgentSource agents = Agents();
        await using HubTestHost host = await StartAsync(agents);
        HubApiClient admin = host.Admin;

        // Library.
        JsonNode created = await PutAsync(admin, "/api/v1/library/command/hello", Body(Docs.Command("hello")), HttpStatusCode.Created);
        Assert.Equal(1, created["revision"]!.GetValue<int>());
        Assert.True(created["changed"]!.GetValue<bool>());
        Assert.Equal("hub", created["body"]!["origin"]!.GetValue<string>());
        JsonNode unchanged = await PutAsync(admin, "/api/v1/library/command/hello", Body(Docs.Command("hello")), HttpStatusCode.OK);
        Assert.Equal(1, unchanged["revision"]!.GetValue<int>());
        Assert.False(unchanged["changed"]!.GetValue<bool>());

        Assert.Single((await GetAsync(admin, "/api/v1/library"))["documents"]!.AsArray());
        Assert.Empty((await GetAsync(admin, "/api/v1/library?kind=sensor"))["documents"]!.AsArray());
        await ProblemAsync(admin.GetAsync("/api/v1/library?kind=widget"), (HttpStatusCode)422, "validation_failed");
        Assert.Equal("hello", (await GetAsync(admin, "/api/v1/library/command/hello?revision=1"))["id"]!.GetValue<string>());
        Assert.Single((await GetAsync(admin, "/api/v1/library/command/hello/revisions"))["revisions"]!.AsArray());
        await ProblemAsync(admin.GetAsync("/api/v1/library/command/nothing"), HttpStatusCode.NotFound, "unknown_document");
        await ProblemAsync(admin.GetAsync("/api/v1/library/command/hello?revision=abc"), (HttpStatusCode)422, "validation_failed");

        // Profile with draft, assignment, preview.
        await CreateProfileAsync(
            admin,
            "office",
            """[{"kind":"command","id":"hello"}]""",
            "\"priority\":1,\"variables\":{\"SITE\":\"Berlin\"},\"policy\":{\"localConfig\":\"locked\"}");
        JsonNode profile = await GetAsync(admin, "/api/v1/profiles/office");
        Assert.Equal(1, profile["draft"]!["items"]![0]!["revision"]!.GetValue<int>());
        Assert.Null(profile["activeRevision"]);
        Assert.True(profile["hasUnpublishedChanges"]!.GetValue<bool>());
        JsonNode assignment = await PostAsync(admin, "/api/v1/assignments", """{"profileId":"office","target":"tag:office"}""", HttpStatusCode.Created);
        Assert.Empty(assignment["agents"]!.AsArray());

        JsonNode preview = await GetAsync(admin, "/api/v1/profiles/office/preview");
        Assert.Equal(["pc1"], preview["agents"]!.AsArray().Select(agent => agent!["agentId"]!.GetValue<string>()).ToArray());
        Assert.True(preview["newRevision"]!.GetValue<bool>());
        Assert.False(preview["unchanged"]!.GetValue<bool>());
        Assert.False(preview["requiresAdmin"]!.GetValue<bool>());
        Assert.Equal(0, await ConfigGenerationAsync(host, "pc1"));

        // Publish.
        JsonNode published = await PostAsync(admin, "/api/v1/profiles/office/publish", """{"note":"first"}""");
        Assert.Equal(1, published["revision"]!.GetValue<int>());
        Assert.True(published["newRevision"]!.GetValue<bool>());
        JsonNode change = Assert.Single(published["agents"]!.AsArray())!;
        Assert.Equal("pc1", change["agentId"]!.GetValue<string>());
        Assert.Equal(1, change["newGeneration"]!.GetValue<int>());

        JsonNode desired = await GetAsync(admin, "/api/v1/agents/pc1/desired-state");
        Assert.Equal(1, desired["generation"]!.GetValue<int>());
        Assert.True(desired["upToDate"]!.GetValue<bool>());
        Assert.Equal("locked", desired["policy"]!["localConfig"]!.GetValue<string>());
        Assert.Equal("allow", desired["policy"]!["directLink"]!.GetValue<string>());
        Assert.Equal(["command/hello", "variables/variables"], desired["items"]!.AsArray().Select(item => item!["kind"]!.GetValue<string>() + "/" + item["id"]!.GetValue<string>()).ToArray());
        await ProblemAsync(admin.GetAsync("/api/v1/agents/nobody/desired-state"), HttpStatusCode.NotFound, "unknown_agent");

        // Publishing again changes nothing.
        JsonNode again = await PostAsync(admin, "/api/v1/profiles/office/publish", null);
        Assert.True(again["unchanged"]!.GetValue<bool>());
        Assert.Empty(again["agents"]!.AsArray());
        Assert.Equal(1, await ConfigGenerationAsync(host, "pc1"));
        Assert.Single((await GetAsync(admin, "/api/v1/profiles/office/revisions"))["revisions"]!.AsArray());
        Assert.Equal("first", (await GetAsync(admin, "/api/v1/profiles/office/revisions/1"))["revision"]!["note"]!.GetValue<string>());
        await ProblemAsync(admin.GetAsync("/api/v1/profiles/office/revisions/9"), HttpStatusCode.NotFound, "unknown_revision");

        // Clean up: the agent loses the documents.
        string assignmentId = (await GetAsync(admin, "/api/v1/assignments"))["assignments"]![0]!["id"]!.GetValue<string>();
        using (HttpResponseMessage removedAssignment = await admin.DeleteAsync("/api/v1/assignments/" + assignmentId))
        {
            HubAssert.Status(removedAssignment, HttpStatusCode.NoContent);
        }

        Assert.Equal(2, await ConfigGenerationAsync(host, "pc1"));
        using (HttpResponseMessage removedProfile = await admin.DeleteAsync("/api/v1/profiles/office"))
        {
            HubAssert.Status(removedProfile, HttpStatusCode.NoContent);
        }

        await ProblemAsync(admin.GetAsync("/api/v1/profiles/office"), HttpStatusCode.NotFound, "unknown_profile");
    }

    [Fact]
    public async Task A_rollback_through_the_API_republishes_the_earlier_revision()
    {
        FakeAgentSource agents = Agents();
        await using HubTestHost host = await StartAsync(agents);
        HubApiClient admin = host.Admin;
        await PutAsync(admin, "/api/v1/library/command/hello", Body(Docs.Command("hello", code: "Write-Output one")), HttpStatusCode.Created);
        await CreateProfileAsync(admin, "all", """[{"kind":"command","id":"hello"}]""");
        await PostAsync(admin, "/api/v1/assignments", """{"profileId":"all","target":"all"}""", HttpStatusCode.Created);
        await PostAsync(admin, "/api/v1/profiles/all/publish", null);
        await PutAsync(admin, "/api/v1/library/command/hello", Body(Docs.Command("hello", code: "Write-Output two")), HttpStatusCode.OK);
        await PutAsync(admin, "/api/v1/profiles/all", """{"name":"Profile all","items":[{"kind":"command","id":"hello","revision":2}]}""", HttpStatusCode.OK);
        await PostAsync(admin, "/api/v1/profiles/all/publish", null);
        Assert.Equal(2, await ConfigGenerationAsync(host, "pc1"));

        JsonNode preview = await GetAsync(admin, "/api/v1/profiles/all/preview?revision=1");
        Assert.False(preview["newRevision"]!.GetValue<bool>());
        Assert.Equal(1, preview["revision"]!.GetValue<int>());
        Assert.Equal(2, preview["agents"]!.AsArray().Count);

        JsonNode rolledBack = await PostAsync(admin, "/api/v1/profiles/all/rollback", """{"revision":1,"note":"back"}""");
        Assert.Equal(1, rolledBack["revision"]!.GetValue<int>());
        Assert.False(rolledBack["newRevision"]!.GetValue<bool>());
        Assert.Equal(3, await ConfigGenerationAsync(host, "pc1"));
        Assert.Equal(1, (await GetAsync(admin, "/api/v1/profiles/all"))["activeRevision"]!.GetValue<int>());

        await ProblemAsync(admin.PostAsync("/api/v1/profiles/all/rollback", "{}"), (HttpStatusCode)422, "validation_failed");
        await ProblemAsync(admin.PostAsync("/api/v1/profiles/all/rollback", """{"revision":7}"""), HttpStatusCode.NotFound, "unknown_revision");
    }

    [Fact]
    public async Task Requests_with_content_that_is_not_acceptable_are_answered_with_422_and_pointers_or_404()
    {
        FakeAgentSource agents = Agents();
        await using HubTestHost host = await StartAsync(agents);
        HubApiClient admin = host.Admin;

        // The validators of the agent speak: the same pointer as the agent's API would give.
        JsonNode problem = await ProblemAsync(
            admin.SendAsync(HttpMethod.Put, "/api/v1/library/command/broken", Body(Docs.Command("broken", runAs: "root"))),
            (HttpStatusCode)422,
            "validation_failed");
        Assert.Contains(problem["errors"]!.AsArray(), error => error!["path"]!.GetValue<string>() == "/runAs");
        await ProblemAsync(admin.SendAsync(HttpMethod.Put, "/api/v1/library/command/hello", "{}"), (HttpStatusCode)422, "validation_failed");
        await ProblemAsync(admin.SendAsync(HttpMethod.Put, "/api/v1/library/command/hello", "not json"), HttpStatusCode.BadRequest, "bad_request");
        await ProblemAsync(admin.SendAsync(HttpMethod.Put, "/api/v1/library/widget/x", Body(Docs.Command("x"))), HttpStatusCode.NotFound, "unknown_kind");
        await ProblemAsync(admin.DeleteAsync("/api/v1/library/command/nothing"), HttpStatusCode.NotFound, "unknown_document");

        JsonNode badProfile = await ProblemAsync(
            admin.PostAsync("/api/v1/profiles", """{"id":"p_bad","name":"Bad","items":[{"kind":"command","id":"missing"}],"osFilter":"macos"}"""),
            (HttpStatusCode)422,
            "validation_failed");
        string[] paths = [.. badProfile["errors"]!.AsArray().Select(error => error!["path"]!.GetValue<string>())];
        Assert.Contains("/items/0", paths);
        Assert.Contains("/osFilter", paths);

        await CreateProfileAsync(admin, "p_good");
        await ProblemAsync(admin.PostAsync("/api/v1/profiles", """{"id":"p_good","name":"Again"}"""), HttpStatusCode.Conflict, "duplicate");
        await ProblemAsync(admin.PostAsync("/api/v1/assignments", """{"profileId":"p_good","target":"group:x"}"""), (HttpStatusCode)422, "validation_failed");
        await ProblemAsync(admin.PostAsync("/api/v1/assignments", """{"profileId":"p_missing","target":"all"}"""), (HttpStatusCode)422, "validation_failed");
        await PostAsync(admin, "/api/v1/assignments", """{"profileId":"p_good","target":"all"}""", HttpStatusCode.Created);
        await ProblemAsync(admin.PostAsync("/api/v1/assignments", """{"profileId":"p_good","target":"all"}"""), HttpStatusCode.Conflict, "duplicate");
        await ProblemAsync(admin.DeleteAsync("/api/v1/assignments/nothing"), HttpStatusCode.NotFound, "unknown_assignment");
        await ProblemAsync(admin.DeleteAsync("/api/v1/profiles/nothing"), HttpStatusCode.NotFound, "unknown_profile");
        await ProblemAsync(admin.PostAsync("/api/v1/profiles/nothing/publish", null), HttpStatusCode.NotFound, "unknown_profile");
        await ProblemAsync(admin.GetAsync("/api/v1/profiles/p_good/preview?revision=abc"), (HttpStatusCode)422, "validation_failed");
    }

    [Fact]
    public async Task Reading_is_open_to_every_role_and_changing_the_library_drafts_and_assignments_needs_an_administrator()
    {
        FakeAgentSource agents = Agents();
        await using HubTestHost host = await StartAsync(agents);
        await PutAsync(host.Admin, "/api/v1/library/command/hello", Body(Docs.Command("hello")), HttpStatusCode.Created);
        await CreateProfileAsync(host.Admin, "p_one", """[{"kind":"command","id":"hello"}]""");

        foreach (HubApiClient reader in new[] { host.Viewer, host.Operator })
        {
            await GetAsync(reader, "/api/v1/library");
            await GetAsync(reader, "/api/v1/profiles");
            await GetAsync(reader, "/api/v1/profiles/p_one/preview");
            await GetAsync(reader, "/api/v1/assignments");
            await GetAsync(reader, "/api/v1/agents/pc1/desired-state");
        }

        foreach (HubApiClient writer in new[] { host.Viewer, host.Operator })
        {
            await ProblemAsync(writer.SendAsync(HttpMethod.Put, "/api/v1/library/command/hello", Body(Docs.Command("hello"))), HttpStatusCode.Forbidden, "forbidden");
            await ProblemAsync(writer.DeleteAsync("/api/v1/library/command/hello"), HttpStatusCode.Forbidden, "forbidden");
            await ProblemAsync(writer.PostAsync("/api/v1/profiles", """{"id":"p_two","name":"Two"}"""), HttpStatusCode.Forbidden, "forbidden");
            await ProblemAsync(writer.SendAsync(HttpMethod.Put, "/api/v1/profiles/p_one", """{"name":"Changed"}"""), HttpStatusCode.Forbidden, "forbidden");
            await ProblemAsync(writer.DeleteAsync("/api/v1/profiles/p_one"), HttpStatusCode.Forbidden, "forbidden");
            await ProblemAsync(writer.PostAsync("/api/v1/assignments", """{"profileId":"p_one","target":"all"}"""), HttpStatusCode.Forbidden, "forbidden");
        }

        // An operator may publish, a viewer may not.
        await ProblemAsync(host.Viewer.PostAsync("/api/v1/profiles/p_one/publish", null), HttpStatusCode.Forbidden, "forbidden");
        JsonNode published = await PostAsync(host.Operator, "/api/v1/profiles/p_one/publish", null);
        Assert.Equal(1, published["revision"]!.GetValue<int>());
        await ProblemAsync(host.Viewer.PostAsync("/api/v1/profiles/p_one/rollback", """{"revision":1}"""), HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task A_profile_with_system_scripts_can_only_be_published_by_an_administrator_and_the_preview_says_so()
    {
        FakeAgentSource agents = Agents();
        await using HubTestHost host = await StartAsync(agents);
        await PutAsync(host.Admin, "/api/v1/library/command/dangerous", Body(Docs.Command("dangerous", runAs: "system")), HttpStatusCode.Created);
        await PutAsync(host.Admin, "/api/v1/library/command/harmless", Body(Docs.Command("harmless")), HttpStatusCode.Created);
        await CreateProfileAsync(host.Admin, "p_system", """[{"kind":"command","id":"dangerous"}]""");
        await CreateProfileAsync(host.Admin, "p_user", """[{"kind":"command","id":"harmless"}]""");
        await PostAsync(host.Admin, "/api/v1/assignments", """{"profileId":"p_system","target":"all"}""", HttpStatusCode.Created);

        JsonNode preview = await GetAsync(host.Operator, "/api/v1/profiles/p_system/preview");
        Assert.True(preview["containsSystemScripts"]!.GetValue<bool>());
        Assert.True(preview["requiresAdmin"]!.GetValue<bool>());
        Assert.Equal(["command:dangerous"], Strings(preview["systemScripts"]));
        Assert.Equal(2, preview["agents"]!.AsArray().Count);

        JsonNode refused = await ProblemAsync(host.Operator.PostAsync("/api/v1/profiles/p_system/publish", null), HttpStatusCode.Forbidden, "admin_required");
        Assert.Contains("dangerous", refused["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null((await GetAsync(host.Admin, "/api/v1/profiles/p_system"))["activeRevision"]);
        Assert.Equal(0, await ConfigGenerationAsync(host, "pc1"));

        JsonNode asAdmin = await PostAsync(host.Admin, "/api/v1/profiles/p_system/publish", null);
        Assert.Equal(2, asAdmin["agents"]!.AsArray().Count);
        Assert.Equal(1, await ConfigGenerationAsync(host, "pc1"));
        await ProblemAsync(host.Operator.PostAsync("/api/v1/profiles/p_system/rollback", """{"revision":1}"""), HttpStatusCode.Forbidden, "admin_required");

        // Without system scripts the operator may publish.
        JsonNode harmless = await PostAsync(host.Operator, "/api/v1/profiles/p_user/publish", null);
        Assert.Equal(1, harmless["revision"]!.GetValue<int>());
    }

    [Fact]
    public async Task Every_change_is_audited_with_the_profile_or_document_and_the_agents_that_got_a_new_generation()
    {
        FakeAgentSource agents = Agents();
        await using HubTestHost host = await StartAsync(agents);
        await PutAsync(host.Admin, "/api/v1/library/command/dangerous", Body(Docs.Command("dangerous", runAs: "system")), HttpStatusCode.Created);
        await CreateProfileAsync(host.Admin, "office", """[{"kind":"command","id":"dangerous"}]""");
        await PostAsync(host.Admin, "/api/v1/assignments", """{"profileId":"office","target":"tag:office"}""", HttpStatusCode.Created);
        await ProblemAsync(host.Operator.PostAsync("/api/v1/profiles/office/publish", null), HttpStatusCode.Forbidden, "admin_required");
        await PostAsync(host.Admin, "/api/v1/profiles/office/publish", null);

        JsonArray entries = (await GetAsync(host.Admin, "/api/v1/audit"))["entries"]!.AsArray();

        JsonNode saved = Assert.Single(entries, entry => entry!["action"]!.GetValue<string>() == "saveLibraryDocument")!;
        Assert.Equal("library:command/dangerous", saved["target"]!.GetValue<string>());
        Assert.Equal("success", saved["result"]!.GetValue<string>());
        JsonNode createdProfile = Assert.Single(entries, entry => entry!["action"]!.GetValue<string>() == "createProfile")!;
        Assert.Equal("profile:office", createdProfile["target"]!.GetValue<string>());
        JsonNode assigned = Assert.Single(entries, entry => entry!["action"]!.GetValue<string>() == "createAssignment")!;
        Assert.Equal("assignment:office@tag:office", assigned["target"]!.GetValue<string>());
        Assert.Empty(assigned["agents"]!.AsArray());

        JsonNode[] publishes = [.. entries.Where(entry => entry!["action"]!.GetValue<string>() == "publishProfile").Select(entry => entry!)];
        Assert.Equal(2, publishes.Length);
        JsonNode denied = Assert.Single(publishes, entry => entry["status"]!.GetValue<int>() == 403);
        Assert.Equal("denied", denied["result"]!.GetValue<string>());
        Assert.Equal("olivia", denied["user"]!.GetValue<string>());
        Assert.Equal("profile:office", denied["target"]!.GetValue<string>());
        JsonNode accepted = Assert.Single(publishes, entry => entry["status"]!.GetValue<int>() == 200);
        Assert.Equal("success", accepted["result"]!.GetValue<string>());
        Assert.Equal("admin", accepted["user"]!.GetValue<string>());
        Assert.Equal(["pc1"], Strings(accepted["agents"]));
    }

    [Fact]
    public async Task The_published_API_document_lists_the_operations_with_the_role_each_needs()
    {
        FakeAgentSource agents = Agents();
        await using HubTestHost host = await StartAsync(agents);

        JsonNode document = await GetAsync(host.Admin, "/openapi/v1.json");

        (string Path, string Method, string OperationId, string Role)[] expected =
        [
            ("/api/v1/library", "get", "listLibraryDocuments", HubRoles.Viewer),
            ("/api/v1/library/{kind}/{id}", "put", "saveLibraryDocument", HubRoles.Admin),
            ("/api/v1/profiles", "post", "createProfile", HubRoles.Admin),
            ("/api/v1/profiles/{id}/preview", "get", "previewProfilePublish", HubRoles.Viewer),
            ("/api/v1/profiles/{id}/publish", "post", "publishProfile", HubRoles.Operator),
            ("/api/v1/profiles/{id}/rollback", "post", "rollbackProfile", HubRoles.Operator),
            ("/api/v1/assignments", "post", "createAssignment", HubRoles.Admin),
            ("/api/v1/agents/{id}/desired-state", "get", "getAgentDesiredState", HubRoles.Viewer),
        ];
        foreach ((string path, string method, string operationId, string role) in expected)
        {
            JsonNode operation = document["paths"]![path]![method]!;
            Assert.Equal(operationId, operation["operationId"]!.GetValue<string>());
            Assert.Equal(role, operation["x-required-role"]!.GetValue<string>());
        }
    }

    private static async Task<long> ConfigGenerationAsync(HubTestHost host, string agentId)
    {
        AgentGeneration? generation = await host.Service<ConfigService>().GetGenerationAsync(agentId, CancellationToken.None);
        return generation?.Generation ?? 0;
    }
}
