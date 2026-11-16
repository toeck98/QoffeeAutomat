using System.Net;
using System.Text.Json.Nodes;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Manifest;

namespace QoffeeAutomat.Agent.Tests.Api;

/// <summary>The command endpoints of the management API (T213).</summary>
[Trait("Category", "Api")]
public sealed class CommandApiTests
{
    private const string Id = "say_hello";

    [Fact]
    public async Task Creating_updating_disabling_and_deleting_a_command_is_reflected_in_the_command_engine_and_the_manifest()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        CommandEngine engine = host.Service<CommandEngine>();
        ManifestService manifest = host.Service<ManifestService>();
        string emptyRev = manifest.Current.Rev;

        // Create.
        using HttpResponseMessage created = await host.PostAsync("/api/v1/commands", ApiSamples.Command(Id));
        await ApiAssert.JsonAsync(created, HttpStatusCode.Created);
        Assert.Equal("1", ApiAssert.ETag(created));
        Assert.Equal("/api/v1/commands/" + Id, created.Headers.Location?.OriginalString);
        Assert.NotNull(engine.Registry.TryGet(Id));
        Assert.Contains(manifest.Current.Commands, command => command.Id == Id);
        string createdRev = manifest.Current.Rev;
        Assert.NotEqual(emptyRev, createdRev);

        // Update: the name is part of the manifest.
        using HttpResponseMessage updated = await host.PutAsync("/api/v1/commands/" + Id, ApiSamples.Command(Id, name: "Say hello twice"));
        await ApiAssert.JsonAsync(updated);
        Assert.Equal("2", ApiAssert.ETag(updated));
        Assert.Equal("Say hello twice", engine.Registry.TryGet(Id)!.Name);
        Assert.Equal("Say hello twice", manifest.Current.Commands.Single(command => command.Id == Id).Name);
        string updatedRev = manifest.Current.Rev;
        Assert.NotEqual(createdRev, updatedRev);

        // Disable: the engine refuses to run it and the manifest drops it.
        using HttpResponseMessage disabled = await host.PatchAsync("/api/v1/commands/" + Id, "{\"enabled\":false}");
        JsonNode disabledDocument = await ApiAssert.JsonAsync(disabled);
        Assert.False(disabledDocument["enabled"]!.GetValue<bool>());
        Assert.Equal("3", ApiAssert.ETag(disabled));
        Assert.False(engine.Registry.TryGet(Id)!.Enabled);
        Assert.DoesNotContain(manifest.Current.Commands, command => command.Id == Id);
        Assert.NotEqual(updatedRev, manifest.Current.Rev);
        RunResult refused = await engine.RunAsync(new RunRequest(Id));
        Assert.Equal(ErrorCodes.CommandDisabled, refused.ErrorCode);

        // Delete.
        using HttpResponseMessage deleted = await host.DeleteAsync("/api/v1/commands/" + Id);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null(engine.Registry.TryGet(Id));
        Assert.Equal(emptyRev, manifest.Current.Rev);
        using HttpResponseMessage gone = await host.GetAsync("/api/v1/commands/" + Id);
        await ApiAssert.ProblemAsync(gone, HttpStatusCode.NotFound, "unknown_command");
    }

    [Fact]
    public async Task Reading_a_command_returns_the_document_and_its_revision_as_etag_and_the_list_has_it()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await host.CreateCommandAsync(ApiSamples.Command(Id));

        using HttpResponseMessage single = await host.GetAsync("/api/v1/commands/" + Id);
        JsonNode document = await ApiAssert.JsonAsync(single);
        using HttpResponseMessage list = await host.GetAsync("/api/v1/commands");
        JsonNode all = await ApiAssert.JsonAsync(list);

        Assert.Equal(Id, document["id"]!.GetValue<string>());
        Assert.Equal("process", document["action"]!["type"]!.GetValue<string>());
        Assert.Equal(1, document["revision"]!.GetValue<int>());
        Assert.Equal("1", ApiAssert.ETag(single));
        Assert.Contains(all["commands"]!.AsArray(), command => command!["id"]!.GetValue<string>() == Id);
    }

    [Fact]
    public async Task Creating_a_command_whose_id_exists_is_a_conflict_and_changes_nothing()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await host.CreateCommandAsync(ApiSamples.Command(Id));

        using HttpResponseMessage again = await host.PostAsync("/api/v1/commands", ApiSamples.Command(Id, name: "Overwritten"));
        JsonNode problem = await ApiAssert.ProblemAsync(again, HttpStatusCode.Conflict, "already_exists");

        Assert.Equal(1, problem["currentRevision"]!.GetValue<int>());
        Assert.Equal("Say hello", host.Service<CommandEngine>().Registry.TryGet(Id)!.Name);
    }

    [Fact]
    public async Task Validation_errors_return_422_with_json_pointer_paths_and_nothing_is_saved()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        const string invalid = """
            {"id":"bad_one","name":"Bad one","enabled":true,"exposeToHa":false,"runAs":"root","timeoutSeconds":0,"action":{"type":"process","file":"x"},"parameters":[{"name":"1st","type":"int"}]}
            """;

        using HttpResponseMessage response = await host.PostAsync("/api/v1/commands", invalid);
        JsonNode problem = await ApiAssert.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
        string[] paths = ApiAssert.ErrorPaths(problem);

        Assert.Contains("/runAs", paths);
        Assert.Contains("/timeoutSeconds", paths);
        Assert.Contains("/parameters/0/name", paths);
        Assert.All(problem["errors"]!.AsArray(), error =>
        {
            Assert.False(string.IsNullOrEmpty(error!["code"]!.GetValue<string>()));
            Assert.False(string.IsNullOrEmpty(error["message"]!.GetValue<string>()));
        });
        using HttpResponseMessage notSaved = await host.GetAsync("/api/v1/commands/bad_one");
        Assert.Equal(HttpStatusCode.NotFound, notSaved.StatusCode);
    }

    [Fact]
    public async Task A_document_with_missing_members_is_reported_by_the_validator_not_as_a_bad_request()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage response = await host.PostAsync("/api/v1/commands", "{}");
        JsonNode problem = await ApiAssert.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
        string[] paths = ApiAssert.ErrorPaths(problem);

        Assert.Contains("/id", paths);
        Assert.Contains("/name", paths);
        Assert.Contains("/action", paths);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_a_bad_request()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage response = await host.PostAsync("/api/v1/commands", "{ not json");

        await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "bad_request");
    }

    [Fact]
    public async Task Validate_reports_the_errors_of_a_draft_without_saving_it()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage good = await host.PostAsync("/api/v1/commands/validate", ApiSamples.Command("draft_one"));
        JsonNode goodReport = await ApiAssert.JsonAsync(good);
        using HttpResponseMessage bad = await host.PostAsync("/api/v1/commands/validate", "{\"id\":\"Draft\",\"name\":\"\"}");
        JsonNode badReport = await ApiAssert.JsonAsync(bad);

        Assert.True(goodReport["valid"]!.GetValue<bool>());
        Assert.Empty(goodReport["errors"]!.AsArray());
        Assert.False(badReport["valid"]!.GetValue<bool>());
        string[] paths = [.. badReport["errors"]!.AsArray().Select(error => error!["path"]!.GetValue<string>())];
        Assert.Contains("/id", paths);
        Assert.Contains("/name", paths);
        Assert.Null(host.Service<CommandEngine>().Registry.TryGet("draft_one"));
    }

    [Fact]
    public async Task A_stale_if_match_is_refused_with_412_and_the_current_revision()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await host.CreateCommandAsync(ApiSamples.Command(Id));
        using HttpResponseMessage first = await host.PutAsync("/api/v1/commands/" + Id, ApiSamples.Command(Id, name: "Second"), ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using HttpResponseMessage stalePut = await host.PutAsync("/api/v1/commands/" + Id, ApiSamples.Command(Id, name: "Third"), ifMatch: "\"1\"");
        JsonNode problem = await ApiAssert.ProblemAsync(stalePut, HttpStatusCode.PreconditionFailed, "revision_mismatch");
        using HttpResponseMessage stalePatch = await host.PatchAsync("/api/v1/commands/" + Id, "{\"enabled\":false}", ifMatch: "\"1\"");
        using HttpResponseMessage staleDelete = await host.DeleteAsync("/api/v1/commands/" + Id, ifMatch: "\"1\"");

        Assert.Equal(2, problem["currentRevision"]!.GetValue<int>());
        await ApiAssert.ProblemAsync(stalePatch, HttpStatusCode.PreconditionFailed, "revision_mismatch");
        await ApiAssert.ProblemAsync(staleDelete, HttpStatusCode.PreconditionFailed, "revision_mismatch");
        Assert.Equal("Second", host.Service<CommandEngine>().Registry.TryGet(Id)!.Name);

        // The current revision works, with or without quotes, and a bare * only needs the command to exist.
        using HttpResponseMessage current = await host.PutAsync("/api/v1/commands/" + Id, ApiSamples.Command(Id, name: "Third"), ifMatch: "2");
        using HttpResponseMessage any = await host.PutAsync("/api/v1/commands/" + Id, ApiSamples.Command(Id, name: "Fourth"), ifMatch: "*");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(HttpStatusCode.OK, any.StatusCode);
        Assert.Equal("4", ApiAssert.ETag(any));
    }

    [Fact]
    public async Task An_if_match_that_is_not_a_revision_is_a_bad_request()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await host.CreateCommandAsync(ApiSamples.Command(Id));

        using HttpResponseMessage response = await host.PutAsync("/api/v1/commands/" + Id, ApiSamples.Command(Id), ifMatch: "\"abc\"");

        await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "invalid_if_match");
    }

    [Fact]
    public async Task Patch_changes_only_the_members_that_are_given_and_needs_at_least_one()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await host.CreateCommandAsync(ApiSamples.Command(Id));
        ManifestService manifest = host.Service<ManifestService>();
        Assert.Contains(manifest.Current.Commands, command => command.Id == Id);

        using HttpResponseMessage hide = await host.PatchAsync("/api/v1/commands/" + Id, "{\"exposeToHa\":false}");
        JsonNode hidden = await ApiAssert.JsonAsync(hide);
        using HttpResponseMessage nothing = await host.PatchAsync("/api/v1/commands/" + Id, "{}");

        Assert.True(hidden["enabled"]!.GetValue<bool>());
        Assert.False(hidden["exposeToHa"]!.GetValue<bool>());
        Assert.DoesNotContain(manifest.Current.Commands, command => command.Id == Id);
        JsonNode problem = await ApiAssert.ProblemAsync(nothing, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Contains("/", ApiAssert.ErrorPaths(problem));
    }

    [Fact]
    public async Task The_id_in_the_body_must_be_the_id_in_the_url()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await host.CreateCommandAsync(ApiSamples.Command(Id));

        using HttpResponseMessage response = await host.PutAsync("/api/v1/commands/" + Id, ApiSamples.Command("other_id"));

        JsonNode problem = await ApiAssert.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Contains("/id", ApiAssert.ErrorPaths(problem));
    }

    [Fact]
    public async Task Changing_deleting_or_patching_a_command_that_does_not_exist_is_a_404()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage put = await host.PutAsync("/api/v1/commands/nothing_here", ApiSamples.Command("nothing_here"));
        using HttpResponseMessage patch = await host.PatchAsync("/api/v1/commands/nothing_here", "{\"enabled\":true}");
        using HttpResponseMessage delete = await host.DeleteAsync("/api/v1/commands/nothing_here");

        await ApiAssert.ProblemAsync(put, HttpStatusCode.NotFound, "unknown_command");
        await ApiAssert.ProblemAsync(patch, HttpStatusCode.NotFound, "unknown_command");
        await ApiAssert.ProblemAsync(delete, HttpStatusCode.NotFound, "unknown_command");
    }

    [Fact]
    public async Task A_sequence_that_calls_itself_is_refused_with_the_path_of_the_step()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        const string cycle = """
            {"id":"loop_one","name":"Loop","enabled":true,"exposeToHa":false,"runAs":"system","action":{"type":"sequence","steps":[{"command":"loop_one"}]}}
            """;

        using HttpResponseMessage response = await host.PostAsync("/api/v1/commands", cycle);

        JsonNode problem = await ApiAssert.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Contains("/action/steps/0/command", ApiAssert.ErrorPaths(problem));
    }

    [Fact]
    public async Task A_saved_command_document_keeps_its_polymorphic_action_through_the_api()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        const string sequence = """
            {"id":"morning","name":"Morning","enabled":true,"exposeToHa":false,"runAs":"system","action":{"type":"sequence","steps":[{"command":"say_hello"},{"delaySeconds":1}],"onError":"continue"}}
            """;

        await host.CreateCommandAsync(sequence);

        CommandDocument stored = host.Service<CommandEngine>().Registry.TryGet("morning")!;
        SequenceAction action = Assert.IsType<SequenceAction>(stored.Action);
        Assert.Equal(2, action.Steps.Count);
        Assert.Equal(SequenceErrorModes.Continue, action.OnError);
    }
}
