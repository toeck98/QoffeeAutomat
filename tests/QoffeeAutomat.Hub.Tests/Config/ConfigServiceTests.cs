using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Core.Validation;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Security;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Tests.Config;

/// <summary>
/// The configuration service on a real database (T907): the library with its revisions and the validators of the agent, profiles with a draft and
/// immutable published revisions, generation bookkeeping, signing, preview, publish and roll-back, and the role that system scripts need.
/// </summary>
[Trait("Category", "Config")]
public sealed class ConfigServiceTests
{
    // Library -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_saved_document_gets_revision_one_and_an_unchanged_save_adds_no_revision()
    {
        using var fixture = new ConfigFixture();

        ConfigResult<LibraryDocumentInfo> first = await fixture.Service.SaveLibraryDocumentAsync("command", "hello", Docs.Command("hello"), "admin", CancellationToken.None);
        ConfigResult<LibraryDocumentInfo> same = await fixture.Service.SaveLibraryDocumentAsync("command", "hello", Docs.Command("hello"), "admin", CancellationToken.None);
        ConfigResult<LibraryDocumentInfo> edited = await fixture.Service.SaveLibraryDocumentAsync("command", "hello", Docs.Command("hello", name: "Renamed"), "someone", CancellationToken.None);

        Assert.Equal(ConfigStatus.Created, first.Status);
        Assert.Equal(1, first.Value!.Revision);
        Assert.True(first.Value.Changed);
        Assert.Equal("hub", first.Value.Body.GetProperty("origin").GetString());
        Assert.Equal(1, first.Value.Body.GetProperty("revision").GetInt32());
        Assert.True(first.Value.Body.TryGetProperty("updatedAt", out _));
        Assert.Equal(DocumentHasher.Hash("command", first.Value.Body), first.Value.Hash);

        Assert.Equal(ConfigStatus.Ok, same.Status);
        Assert.Equal(1, same.Value!.Revision);
        Assert.False(same.Value.Changed);
        Assert.Equal(first.Value.Hash, same.Value.Hash);

        Assert.Equal(ConfigStatus.Ok, edited.Status);
        Assert.Equal(2, edited.Value!.Revision);
        Assert.Equal("someone", edited.Value.CreatedBy);
        Assert.NotEqual(first.Value.Hash, edited.Value.Hash);

        LibraryRevisionList history = (await fixture.Service.ListLibraryRevisionsAsync("command", "hello", CancellationToken.None))!;
        Assert.Equal([2, 1], history.Revisions.Select(revision => revision.Revision).ToArray());
        LibraryDocumentInfo old = (await fixture.Service.GetLibraryDocumentAsync("command", "hello", 1, CancellationToken.None))!;
        Assert.Equal("A command", old.Body.GetProperty("name").GetString());
        LibraryDocumentInfo current = (await fixture.Service.GetLibraryDocumentAsync("command", "hello", null, CancellationToken.None))!;
        Assert.Equal("Renamed", current.Body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Documents_are_checked_by_the_validators_of_the_agent_and_the_errors_are_the_same()
    {
        using var fixture = new ConfigFixture();
        JsonElement body = Docs.Command("broken", name: string.Empty, runAs: "root");
        CommandDocument parsed = JsonSerializer.Deserialize(body, ConfigJsonContext.Default.CommandDocument)!;
        (string Path, string Code)[] expected = [.. DocumentValidator.ValidateCommand(parsed).Errors.Select(error => (error.Path, error.Code)).Order()];

        ConfigResult<LibraryDocumentInfo> result = await fixture.Service.SaveLibraryDocumentAsync("command", "broken", body, "admin", CancellationToken.None);

        Assert.Equal(ConfigStatus.Invalid, result.Status);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, result.Errors.Select(error => (error.Path, error.Code)).Order().ToArray());
        Assert.Contains(result.Errors, error => error.Path == "/runAs");
        Assert.Contains(result.Errors, error => error.Path == "/name");
        Assert.Null(await fixture.Service.GetLibraryDocumentAsync("command", "broken", null, CancellationToken.None));
    }

    [Fact]
    public async Task The_id_in_the_document_must_equal_the_id_in_the_path()
    {
        using var fixture = new ConfigFixture();

        ConfigResult<LibraryDocumentInfo> result = await fixture.Service.SaveLibraryDocumentAsync("command", "one", Docs.Command("two"), "admin", CancellationToken.None);

        Assert.Equal(ConfigStatus.Invalid, result.Status);
        ValidationError error = Assert.Single(result.Errors);
        Assert.Equal("/id", error.Path);
        Assert.Equal(ValidationCodes.Conflict, error.Code);
    }

    [Fact]
    public async Task Members_that_the_format_does_not_know_are_dropped_and_the_origin_is_hub()
    {
        using var fixture = new ConfigFixture();
        JsonElement withExtra = ConfigJson.Parse(Docs.Command("extra").GetRawText().Replace("\"schemaVersion\"", "\"surprise\": 1, \"origin\": \"local\", \"schemaVersion\"", StringComparison.Ordinal));

        LibraryDocumentInfo saved = await fixture.SaveAsync("command", "extra", withExtra);

        Assert.False(saved.Body.TryGetProperty("surprise", out _));
        Assert.Equal("hub", saved.Body.GetProperty("origin").GetString());
    }

    [Fact]
    public async Task Every_kind_of_document_is_accepted_when_valid_and_rejected_when_not()
    {
        using var fixture = new ConfigFixture();
        await fixture.SaveAsync("command", "cmd_a", Docs.Command("cmd_a"));

        LibraryDocumentInfo sensor = await fixture.SaveAsync("sensor", "notepad", Docs.Sensor("notepad"));
        LibraryDocumentInfo mode = await fixture.SaveAsync("mode", "pc_mode", Docs.Mode("pc_mode", "cmd_a"));
        LibraryDocumentInfo overrides = await fixture.SaveAsync("metric_overrides", "base", Docs.MetricOverrides());
        LibraryDocumentInfo script = await fixture.SaveAsync("script", "run.ps1", Docs.Script("exit 0\n"));
        LibraryDocumentInfo variables = await fixture.SaveAsync("variables", "site", Docs.Variables(("SITE", "Home")));

        Assert.Equal("Notepad", sensor.Name);
        Assert.Equal("Mode group", mode.Name);
        Assert.Null(overrides.Name);
        Assert.Equal(DocumentHasher.HashScript("exit 0\n"), script.Hash);
        Assert.Equal("exit 0\n", script.Body.GetString());
        Assert.Equal("Home", variables.Body.GetProperty("SITE").GetString());

        (string Kind, string Id, JsonElement Body)[] invalid =
        [
            ("sensor", "notepad", ConfigJson.Parse("""{ "schemaVersion": 1, "id": "notepad", "name": "N", "enabled": true, "exposeToHa": true, "kind": "weird", "valueType": "bool", "source": { "type": "process_running", "process": "x" } }""")),
            ("mode", "bad_mode", ConfigJson.Parse("""{ "schemaVersion": 1, "id": "bad_mode", "name": "M", "enabled": true, "exposeToHa": true, "defaultMode": "missing", "modes": [ { "id": "a", "name": "A" } ] }""")),
            ("metric_overrides", "base", ConfigJson.Parse("""{ "schemaVersion": 1, "overrides": { "cpu.usage": { "intervalSeconds": -1 } } }""")),
            ("script", "../escape.ps1", Docs.Script("x")),
            ("script", "plain.ps1", ConfigJson.Parse("""{ "not": "text" }""")),
            ("variables", "site", ConfigJson.Parse("""{ "1bad": "x" }""")),
            ("variables", "site", ConfigJson.Parse("""{ "GOOD": 1 }""")),
            ("command", "cmd_a", ConfigJson.Parse("[]")),
        ];
        foreach ((string kind, string id, JsonElement body) in invalid)
        {
            ConfigResult<LibraryDocumentInfo> result = await fixture.Service.SaveLibraryDocumentAsync(kind, id, body, "admin", CancellationToken.None);
            Assert.True(result.Status == ConfigStatus.Invalid, kind + "/" + id + " must be invalid, but: " + ConfigFixture.Describe(result));
        }

        ConfigResult<LibraryDocumentInfo> unknown = await fixture.Service.SaveLibraryDocumentAsync("widget", "x", Docs.Script("x"), "admin", CancellationToken.None);
        Assert.Equal(ConfigStatus.NotFound, unknown.Status);
        Assert.Equal("unknown_kind", unknown.Code);
    }

    [Fact]
    public async Task Sequences_that_call_each_other_in_a_cycle_are_rejected_using_the_library()
    {
        using var fixture = new ConfigFixture();
        await fixture.SaveAsync("command", "seq_a", Docs.SequenceCommand("seq_a", "seq_b"));

        ConfigResult<LibraryDocumentInfo> cycle = await fixture.Service.SaveLibraryDocumentAsync("command", "seq_b", Docs.SequenceCommand("seq_b", "seq_a"), "admin", CancellationToken.None);

        Assert.Equal(ConfigStatus.Invalid, cycle.Status);
        Assert.Contains(cycle.Errors, error => error.Code == ValidationCodes.Cycle);
    }

    [Fact]
    public async Task A_mode_group_can_only_name_commands_of_the_library()
    {
        using var fixture = new ConfigFixture();

        ConfigResult<LibraryDocumentInfo> before = await fixture.Service.SaveLibraryDocumentAsync("mode", "pc_mode", Docs.Mode("pc_mode", "not_there"), "admin", CancellationToken.None);
        await fixture.SaveAsync("command", "not_there", Docs.Command("not_there"));
        ConfigResult<LibraryDocumentInfo> after = await fixture.Service.SaveLibraryDocumentAsync("mode", "pc_mode", Docs.Mode("pc_mode", "not_there"), "admin", CancellationToken.None);

        Assert.Equal(ConfigStatus.Invalid, before.Status);
        Assert.Contains(before.Errors, error => error.Code == ValidationCodes.UnknownReference);
        Assert.True(after.IsSuccess, ConfigFixture.Describe(after));
    }

    [Fact]
    public async Task A_deleted_document_disappears_but_a_pinned_revision_stays_readable()
    {
        using var fixture = new ConfigFixture();
        await fixture.SaveAsync("command", "doomed", Docs.Command("doomed"));
        await fixture.CreateProfileAsync("p_use", [Docs.Item("command", "doomed")]);

        ConfigResult<bool> inUse = await fixture.Service.DeleteLibraryDocumentAsync("command", "doomed", CancellationToken.None);
        await fixture.PublishAsync("p_use");
        await fixture.UpdateProfileAsync("p_use");
        ConfigResult<bool> deleted = await fixture.Service.DeleteLibraryDocumentAsync("command", "doomed", CancellationToken.None);
        ConfigResult<bool> again = await fixture.Service.DeleteLibraryDocumentAsync("command", "doomed", CancellationToken.None);

        Assert.Equal(ConfigStatus.Conflict, inUse.Status);
        Assert.Equal("in_use", inUse.Code);
        Assert.Contains("p_use", inUse.Message, StringComparison.Ordinal);
        Assert.True(deleted.IsSuccess);
        Assert.Equal(ConfigStatus.NotFound, again.Status);
        Assert.Empty(await fixture.Service.ListLibraryAsync("command", CancellationToken.None));
        Assert.Null(await fixture.Service.GetLibraryDocumentAsync("command", "doomed", null, CancellationToken.None));
        Assert.NotNull(await fixture.Service.GetLibraryDocumentAsync("command", "doomed", 1, CancellationToken.None));

        // Saving the id again continues the numbering and the old pins stay valid.
        LibraryDocumentInfo recreated = await fixture.SaveAsync("command", "doomed", Docs.Command("doomed"));
        Assert.Equal(2, recreated.Revision);
        Assert.Single(await fixture.Service.ListLibraryAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task A_revision_cannot_be_changed_afterwards()
    {
        using var fixture = new ConfigFixture();
        await fixture.SaveAsync("command", "frozen", Docs.Command("frozen"));
        await fixture.CreateProfileAsync("p_frozen", [Docs.Item("command", "frozen")]);
        await fixture.PublishAsync("p_frozen");

        SqliteException library = await Assert.ThrowsAsync<SqliteException>(() => fixture.Database.ExecuteAsync(
            connection => HubSql.Execute(connection, null, "UPDATE library_revisions SET body = '{}' WHERE id = 'frozen';"),
            CancellationToken.None));
        SqliteException profile = await Assert.ThrowsAsync<SqliteException>(() => fixture.Database.ExecuteAsync(
            connection => HubSql.Execute(connection, null, "UPDATE profile_revisions SET content = '{}' WHERE profile_id = 'p_frozen';"),
            CancellationToken.None));

        Assert.Contains("immutable", library.Message, StringComparison.Ordinal);
        Assert.Contains("immutable", profile.Message, StringComparison.Ordinal);
    }

    // Profiles ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_draft_pins_the_current_revision_of_a_document_and_is_sorted()
    {
        using var fixture = new ConfigFixture();
        await fixture.SaveAsync("command", "zeta", Docs.Command("zeta"));
        await fixture.SaveAsync("command", "alpha", Docs.Command("alpha"));
        await fixture.SaveAsync("command", "alpha", Docs.Command("alpha", name: "Alpha 2"));
        await fixture.SaveAsync("script", "run.ps1", Docs.Script("exit 0"));

        ProfileInfo profile = await fixture.CreateProfileAsync(
            "p_pins",
            [Docs.Item("script", "run.ps1"), Docs.Item("command", "zeta"), Docs.Item("command", "alpha")]);

        string[] expected = ["command/alpha@2", "command/zeta@1", "script/run.ps1@1"];
        Assert.Equal(expected, profile.Draft.Items.Select(item => item.Kind + "/" + item.Id + "@" + item.Revision).ToArray());
        Assert.Null(profile.ActiveRevision);
        Assert.Equal(0, profile.LatestRevision);
        Assert.True(profile.HasUnpublishedChanges);
    }

    [Fact]
    public async Task A_draft_that_is_not_valid_is_rejected_with_pointers_to_the_problems()
    {
        using var fixture = new ConfigFixture();
        await fixture.SaveAsync("command", "known", Docs.Command("known"));
        await fixture.SaveAsync("command", "other", Docs.Command("other"));
        await fixture.SaveAsync("metric_overrides", "base", Docs.MetricOverrides());
        await fixture.SaveAsync("metric_overrides", "extra", Docs.MetricOverrides(20));

        ProfileDraftRequest request = new(
            "p_bad",
            string.Empty,
            20_000,
            "macos",
            [
                Docs.Item("command", "missing"),
                Docs.Item("command", "known"),
                Docs.Item("command", "known"),
                Docs.Item("command", "other", 9),
                Docs.Item("widget", "x"),
                Docs.Item("metric_overrides", "base"),
                Docs.Item("metric_overrides", "extra"),
            ],
            new Dictionary<string, string> { ["1bad"] = "x", ["GOOD"] = new string('x', 5000) },
            new ProfilePolicyRequest("sometimes", "never"));
        ConfigResult<ProfileInfo> result = await fixture.Service.CreateProfileAsync(request, CancellationToken.None);

        Assert.Equal(ConfigStatus.Invalid, result.Status);
        string[] paths = [.. result.Errors.Select(error => error.Path)];
        foreach (string expected in new[]
        {
            "/name", "/priority", "/osFilter", "/items/0", "/items/2", "/items/3/revision", "/items/4/kind", "/items/6",
            "/variables/1bad", "/variables/GOOD", "/policy/localConfig", "/policy/directLink",
        })
        {
            Assert.Contains(expected, paths);
        }

        Assert.Null(await fixture.Service.GetProfileAsync("p_bad", CancellationToken.None));
    }

    [Fact]
    public async Task Profile_ids_are_checked_and_a_profile_id_is_used_once()
    {
        using var fixture = new ConfigFixture();
        await fixture.CreateProfileAsync("p_one");

        ConfigResult<ProfileInfo> duplicate = await fixture.Service.CreateProfileAsync(new ProfileDraftRequest("p_one", "Again", null, null, null, null, null), CancellationToken.None);
        ConfigResult<ProfileInfo> badId = await fixture.Service.CreateProfileAsync(new ProfileDraftRequest("Not Valid", "Name", null, null, null, null, null), CancellationToken.None);
        ConfigResult<ProfileInfo> rename = await fixture.Service.UpdateProfileAsync("p_one", new ProfileDraftRequest("p_two", "Name", null, null, null, null, null), CancellationToken.None);
        ConfigResult<ProfileInfo> missing = await fixture.Service.UpdateProfileAsync("p_missing", new ProfileDraftRequest(null, "Name", null, null, null, null, null), CancellationToken.None);

        Assert.Equal(ConfigStatus.Conflict, duplicate.Status);
        Assert.Equal(ConfigStatus.Invalid, badId.Status);
        Assert.Equal(ConfigStatus.Invalid, rename.Status);
        Assert.Equal(ConfigStatus.NotFound, missing.Status);
    }

    [Fact]
    public async Task A_published_revision_is_frozen_while_the_draft_moves_on()
    {
        using var fixture = new ConfigFixture();
        await fixture.SaveAsync("command", "one", Docs.Command("one"));
        await fixture.SaveAsync("command", "two", Docs.Command("two"));
        await fixture.CreateProfileAsync("p_draft", [Docs.Item("command", "one")], variables: new Dictionary<string, string> { ["A"] = "1" });
        await fixture.PublishAsync("p_draft");

        ProfileInfo afterPublish = (await fixture.Service.GetProfileAsync("p_draft", CancellationToken.None))!;
        await fixture.UpdateProfileAsync("p_draft", [Docs.Item("command", "two")], priority: 5, variables: new Dictionary<string, string> { ["A"] = "2" });
        ProfileInfo afterEdit = (await fixture.Service.GetProfileAsync("p_draft", CancellationToken.None))!;
        ProfileRevisionInfo frozen = (await fixture.Service.GetProfileRevisionAsync("p_draft", 1, CancellationToken.None))!;

        Assert.False(afterPublish.HasUnpublishedChanges);
        Assert.Equal(1, afterPublish.ActiveRevision);
        Assert.True(afterEdit.HasUnpublishedChanges);
        Assert.Equal(1, afterEdit.ActiveRevision);
        Assert.Equal(["one"], frozen.Content.Items.Select(item => item.Id).ToArray());
        Assert.Equal(0, frozen.Content.Priority);
        Assert.Equal("1", frozen.Content.Variables["A"]);
        Assert.True(frozen.Revision.Active);
        Assert.Equal("tester", frozen.Revision.PublishedBy);
        Assert.Null(await fixture.Service.GetProfileRevisionAsync("p_draft", 2, CancellationToken.None));
    }

    // Generations -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Publishing_without_a_change_of_content_creates_no_new_generation_for_any_agent()
    {
        using var fixture = new ConfigFixture();
        fixture.Agents.Set(Docs.Agent("pc1", "windows", AgentStatuses.Enrolled, "office"), Docs.Agent("pc2", "linux"), Docs.Agent("pc3"));
        await fixture.SaveAsync("command", "hello", Docs.Command("hello"));
        await fixture.CreateProfileAsync("p_all", [Docs.Item("command", "hello")]);
        await fixture.PublishAsync("p_all");
        IReadOnlyList<AgentChange> assigned = await fixture.AssignAsync("p_all", "all");
        AgentGeneration[] before = await GenerationsAsync(fixture, "pc1", "pc2", "pc3");
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));

        PublishResult again = await fixture.PublishAsync("p_all");
        await fixture.SaveAsync("command", "hello", Docs.Command("hello"));
        PublishResult afterSameSave = await fixture.PublishAsync("p_all");
        AgentGeneration[] after = await GenerationsAsync(fixture, "pc1", "pc2", "pc3");

        Assert.Equal(["pc1", "pc2", "pc3"], assigned.Select(change => change.AgentId).Order().ToArray());
        Assert.All(before, generation => Assert.Equal(1, generation.Generation));
        foreach (PublishResult result in new[] { again, afterSameSave })
        {
            Assert.True(result.Unchanged);
            Assert.False(result.NewRevision);
            Assert.Equal(1, result.Revision);
            Assert.Empty(result.Agents);
        }

        Assert.Equal(before.Select(Fingerprint).ToArray(), after.Select(Fingerprint).ToArray());
        Assert.Single((await fixture.Service.ListProfileRevisionsAsync("p_all", CancellationToken.None))!.Revisions);
    }

    [Fact]
    public async Task A_new_revision_that_changes_nothing_an_agent_receives_creates_no_generation()
    {
        using var fixture = new ConfigFixture();
        fixture.Agents.Set(Docs.Agent("pc1"));
        await fixture.PublishedCommandProfileAsync("p_all", "hello", "all");
        AgentGeneration before = (await fixture.Service.GetGenerationAsync("pc1", CancellationToken.None))!;

        // The name of the profile is not part of the desired state, so the revision is new and the agents stay as they are.
        await fixture.Service.UpdateProfileAsync(
            "p_all",
            new ProfileDraftRequest(null, "A new name", 0, null, [Docs.Item("command", "hello")], null, null),
            CancellationToken.None);
        PublishResult result = await fixture.PublishAsync("p_all");

        Assert.False(result.Unchanged);
        Assert.True(result.NewRevision);
        Assert.Equal(2, result.Revision);
        Assert.Empty(result.Agents);
        Assert.Equal(Fingerprint(before), Fingerprint((await fixture.Service.GetGenerationAsync("pc1", CancellationToken.None))!));
    }

    [Fact]
    public async Task The_preview_lists_exactly_the_agents_whose_desired_state_would_change()
    {
        using var fixture = new ConfigFixture();
        fixture.Agents.Set(
            Docs.Agent("pc1", "windows", AgentStatuses.Enrolled, "office"),
            Docs.Agent("pc2", "windows", AgentStatuses.Enrolled, "lab"),
            Docs.Agent("pc3", "windows"),
            Docs.Agent("pc4", "linux", AgentStatuses.Enrolled, "office"),
            Docs.Agent("pc5", "windows", AgentStatuses.Pending, "office"));
        await fixture.SaveAsync("command", "hello", Docs.Command("hello"));
        await fixture.CreateProfileAsync("p_office", [Docs.Item("command", "hello")], osFilter: "windows");
        await fixture.AssignAsync("p_office", "tag:office");

        ConfigResult<PublishPreview> preview = await fixture.Service.PreviewAsync("p_office", null, CancellationToken.None);

        PublishPreview value = preview.Value!;
        Assert.Equal(["pc1"], value.Agents.Select(change => change.AgentId).ToArray());
        Assert.True(value.NewRevision);
        Assert.False(value.Unchanged);
        Assert.False(value.RequiresAdmin);
        AgentChange change = Assert.Single(value.Agents);
        Assert.Equal(0, change.CurrentGeneration);
        Assert.Equal(1, change.NewGeneration);
        DocumentChange added = Assert.Single(change.Documents);
        Assert.Equal(("command", "hello", "added"), (added.Kind, added.Id, added.Change));
        Assert.Null(added.OldHash);
        Assert.NotNull(added.NewHash);

        // Previewing changes nothing.
        Assert.Null((await fixture.Service.GetProfileAsync("p_office", CancellationToken.None))!.ActiveRevision);
        Assert.Equal(0, await fixture.GenerationOfAsync("pc1"));

        // Publishing does exactly what the preview said.
        PublishResult published = await fixture.PublishAsync("p_office");
        Assert.Equal(value.Agents.Select(item => (item.AgentId, item.NewGeneration)).ToArray(), published.Agents.Select(item => (item.AgentId, item.NewGeneration)).ToArray());
        Assert.Equal(1, await fixture.GenerationOfAsync("pc1"));
        foreach (string untouched in new[] { "pc2", "pc3", "pc4", "pc5" })
        {
            Assert.Equal(0, await fixture.GenerationOfAsync(untouched));
        }
    }

    [Fact]
    public async Task The_preview_shows_changed_added_and_removed_documents_and_policy_per_agent()
    {
        using var fixture = new ConfigFixture();
        fixture.Agents.Set(Docs.Agent("pc1", "windows", AgentStatuses.Enrolled, "office"), Docs.Agent("pc2", "windows", AgentStatuses.Enrolled, "lab"));
        await fixture.SaveAsync("command", "keep", Docs.Command("keep"));
        await fixture.SaveAsync("command", "edit", Docs.Command("edit"));
        await fixture.SaveAsync("command", "drop", Docs.Command("drop"));
        await fixture.SaveAsync("sensor", "added", Docs.Sensor("added"));
        await fixture.CreateProfileAsync("p_office", [Docs.Item("command", "keep"), Docs.Item("command", "edit"), Docs.Item("command", "drop")]);
        await fixture.PublishAsync("p_office");
        await fixture.AssignAsync("p_office", "tag:office");
        LibraryDocumentInfo edited = await fixture.SaveAsync("command", "edit", Docs.Command("edit", name: "Edited"));
        await fixture.UpdateProfileAsync(
            "p_office",
            [Docs.Item("command", "keep"), Docs.Item("command", "edit", edited.Revision), Docs.Item("sensor", "added")],
            policy: new ProfilePolicyRequest("locked", "deny"));

        PublishPreview preview = (await fixture.Service.PreviewAsync("p_office", null, CancellationToken.None)).Value!;

        AgentChange change = Assert.Single(preview.Agents);
        Assert.Equal("pc1", change.AgentId);
        Assert.Equal(1, change.CurrentGeneration);
        Assert.Equal(2, change.NewGeneration);
        Assert.Equal(
            ["command/drop:removed", "command/edit:changed", "sensor/added:added"],
            change.Documents.Select(document => document.Kind + "/" + document.Id + ":" + document.Change).ToArray());
        DocumentChange changed = change.Documents.Single(document => document.Id == "edit");
        Assert.NotNull(changed.OldHash);
        Assert.NotNull(changed.NewHash);
        Assert.NotEqual(changed.OldHash, changed.NewHash);
        Assert.Equal(
            [("local_config", "allow", "locked"), ("direct_link", "allow", "deny")],
            change.Policy.Select(difference => (difference.Name, difference.From, difference.To)).ToArray());
    }

    [Fact]
    public async Task Generations_increase_only_when_the_content_changes_and_each_is_signed_for_its_agent()
    {
        using var fixture = new ConfigFixture();
        fixture.Agents.Set(Docs.Agent("pc1"), Docs.Agent("pc2"));
        await fixture.PublishedCommandProfileAsync("p_all", "hello", "all");
        AgentGeneration first = (await fixture.Service.GetGenerationAsync("pc1", CancellationToken.None))!;

        await fixture.SaveAsync("command", "hello", Docs.Command("hello", code: "Write-Output changed"));
        await fixture.UpdateProfileAsync("p_all", [Docs.Item("command", "hello")]);
        await fixture.PublishAsync("p_all");
        AgentGeneration second = (await fixture.Service.GetGenerationAsync("pc1", CancellationToken.None))!;

        Assert.Equal(1, first.Generation);
        Assert.Equal(2, second.Generation);
        Assert.NotEqual(first.ContentHash, second.ContentHash);
        Assert.NotEqual(first.Signature, second.Signature);

        Assert.True(HubSigningKey.TryImportPublicKey(fixture.Signer.PublicKey, out ECDsa? publicKey));
        using (publicKey)
        {
            Assert.True(DesiredStateSigner.Verify("pc1", second.ToMessage(7), publicKey!));
            Assert.False(DesiredStateSigner.Verify("pc2", second.ToMessage(7), publicKey!), "The signature is bound to the agent.");
            Assert.False(DesiredStateSigner.Verify("pc1", second.ToMessage(7) with { Generation = 3 }, publicKey!));
            Assert.False(DesiredStateSigner.Verify("pc1", second.ToMessage(7) with { Policy = new PolicyInfo("locked", "allow") }, publicKey!));
        }

        // The documents of a generation are kept and match the signed hashes.
        IReadOnlyList<HubDocument> documents = await fixture.Service.LoadDocumentsAsync(second.Items, CancellationToken.None);
        Assert.Equal(second.Items.Count, documents.Count);
        Assert.All(documents, document => Assert.True(DocumentHasher.Matches(document)));
        Assert.Equal(second.Items.Select(item => item.Hash).ToArray(), documents.Select(document => document.Hash).ToArray());
        Assert.Equal(second.Items.ToArray(), DesiredState.SortItems(second.Items).ToArray());

        // Older documents stay available for an agent that is still on them.
        Assert.Single(await fixture.Service.LoadDocumentsAsync(first.Items, CancellationToken.None));
    }

    [Fact]
    public async Task Changing_an_assignment_gives_a_new_generation_exactly_to_the_agents_it_reaches()
    {
        using var fixture = new ConfigFixture();
        fixture.Agents.Set(Docs.Agent("pc1", "windows", AgentStatuses.Enrolled, "office"), Docs.Agent("pc2", "windows", AgentStatuses.Enrolled, "lab"));
        await fixture.SaveAsync("command", "hello", Docs.Command("hello"));
        await fixture.CreateProfileAsync("p_office", [Docs.Item("command", "hello")]);
        await fixture.PublishAsync("p_office");

        IReadOnlyList<AgentChange> toOffice = await fixture.AssignAsync("p_office", "tag:office");
        IReadOnlyList<AgentChange> toPc2 = await fixture.AssignAsync("p_office", "agent:pc2");
        ConfigResult<CreatedAssignment> duplicate = await fixture.Service.CreateAssignmentAsync("p_office", "agent:pc2", "tester", CancellationToken.None);
        AssignmentInfo assignment = (await fixture.Service.ListAssignmentsAsync(CancellationToken.None)).Single(item => item.Target == "agent:pc2");
        ConfigResult<IReadOnlyList<AgentChange>> removed = await fixture.Service.DeleteAssignmentAsync(assignment.Id, CancellationToken.None);

        Assert.Equal(["pc1"], toOffice.Select(change => change.AgentId).ToArray());
        Assert.Equal(["pc2"], toPc2.Select(change => change.AgentId).ToArray());
        Assert.Equal(ConfigStatus.Conflict, duplicate.Status);
        Assert.Equal(["pc2"], removed.Value!.Select(change => change.AgentId).ToArray());
        Assert.Equal(2, await fixture.GenerationOfAsync("pc2"));
        Assert.Empty((await fixture.Service.GetGenerationAsync("pc2", CancellationToken.None))!.Items);
        Assert.Equal(1, await fixture.GenerationOfAsync("pc1"));
    }

    [Fact]
    public async Task Assignments_are_checked()
    {
        using var fixture = new ConfigFixture();
        await fixture.CreateProfileAsync("p_one");

        ConfigResult<CreatedAssignment> badTarget = await fixture.Service.CreateAssignmentAsync("p_one", "group:x", "tester", CancellationToken.None);
        ConfigResult<CreatedAssignment> noProfile = await fixture.Service.CreateAssignmentAsync("p_missing", "all", "tester", CancellationToken.None);
        ConfigResult<CreatedAssignment> nothing = await fixture.Service.CreateAssignmentAsync(null, null, "tester", CancellationToken.None);
        ConfigResult<IReadOnlyList<AgentChange>> missing = await fixture.Service.DeleteAssignmentAsync("nope", CancellationToken.None);

        Assert.Equal(ConfigStatus.Invalid, badTarget.Status);
        Assert.Contains(badTarget.Errors, error => error.Path == "/target");
        Assert.Equal(ConfigStatus.Invalid, noProfile.Status);
        Assert.Contains(noProfile.Errors, error => error.Path == "/profileId");
        Assert.Equal(2, nothing.Errors.Count);
        Assert.Equal(ConfigStatus.NotFound, missing.Status);
    }

    [Fact]
    public async Task Deleting_a_profile_removes_its_documents_from_the_agents_it_reached()
    {
        using var fixture = new ConfigFixture();
        fixture.Agents.Set(Docs.Agent("pc1"));
        await fixture.PublishedCommandProfileAsync("p_all", "hello", "all");

        ConfigResult<IReadOnlyList<AgentChange>> deleted = await fixture.Service.DeleteProfileAsync("p_all", CancellationToken.None);
        ConfigResult<IReadOnlyList<AgentChange>> again = await fixture.Service.DeleteProfileAsync("p_all", CancellationToken.None);

        AgentChange change = Assert.Single(deleted.Value!);
        Assert.Equal("removed", Assert.Single(change.Documents).Change);
        Assert.Equal(2, change.NewGeneration);
        Assert.Equal(ConfigStatus.NotFound, again.Status);
        Assert.Empty(await fixture.Service.ListAssignmentsAsync(CancellationToken.None));
        Assert.Null(await fixture.Service.GetProfileAsync("p_all", CancellationToken.None));
    }

    [Fact]
    public async Task A_change_of_tags_is_picked_up_by_recomputing_and_a_second_recompute_changes_nothing()
    {
        using var fixture = new ConfigFixture();
        fixture.Agents.Set(Docs.Agent("pc1"));
        await fixture.SaveAsync("command", "hello", Docs.Command("hello"));
        await fixture.CreateProfileAsync("p_office", [Docs.Item("command", "hello")]);
        await fixture.PublishAsync("p_office");
        await fixture.AssignAsync("p_office", "tag:office");
        Assert.Equal(0, await fixture.GenerationOfAsync("pc1"));
        AgentDesiredInfo stale = (await fixture.Service.GetDesiredStateAsync("pc1", CancellationToken.None))!;
        Assert.True(stale.UpToDate);
        Assert.Empty(stale.Items);

        fixture.Agents.SetTags("pc1", "office");
        AgentDesiredInfo drifted = (await fixture.Service.GetDesiredStateAsync("pc1", CancellationToken.None))!;
        AgentChange? change = await fixture.Service.RecomputeAgentAsync("pc1", CancellationToken.None);
        AgentChange? nothing = await fixture.Service.RecomputeAgentAsync("pc1", CancellationToken.None);
        IReadOnlyList<AgentChange> all = await fixture.Service.RecomputeAsync(CancellationToken.None);
        AgentGeneration? ensured = await fixture.Service.EnsureGenerationAsync("pc1", CancellationToken.None);

        Assert.False(drifted.UpToDate);
        Assert.Equal("hello", Assert.Single(drifted.Items).Id);
        Assert.Equal(1, change!.NewGeneration);
        Assert.Null(nothing);
        Assert.Empty(all);
        Assert.Equal(1, ensured!.Generation);
        Assert.Null(await fixture.Service.RecomputeAgentAsync("unknown", CancellationToken.None));
    }

    [Fact]
    public async Task Only_enrolled_agents_get_a_desired_state_and_the_state_of_a_removed_agent_is_forgotten()
    {
        using var fixture = new ConfigFixture();
        fixture.Agents.Set(Docs.Agent("pc1"), Docs.Agent("waiting", "windows", AgentStatuses.Pending), Docs.Agent("refused", "windows", AgentStatuses.Rejected));
        await fixture.PublishedCommandProfileAsync("p_all", "hello", "all");

        Assert.Equal(1, await fixture.GenerationOfAsync("pc1"));
        Assert.Equal(0, await fixture.GenerationOfAsync("waiting"));
        Assert.Equal(0, await fixture.GenerationOfAsync("refused"));

        fixture.Agents.Remove("pc1");
        await fixture.Service.RecomputeAsync(CancellationToken.None);

        Assert.Null(await fixture.Service.GetGenerationAsync("pc1", CancellationToken.None));
        Assert.Null(await fixture.Service.GetDesiredStateAsync("pc1", CancellationToken.None));
    }

    [Fact]
    public async Task The_OS_filter_decides_which_agents_receive_a_profile()
    {
        using var fixture = new ConfigFixture();
        fixture.Agents.Set(Docs.Agent("win", "windows"), Docs.Agent("lin", "linux"));
        await fixture.SaveAsync("command", "win_only", Docs.Command("win_only"));
        await fixture.SaveAsync("command", "lin_only", Docs.Command("lin_only"));
        await fixture.CreateProfileAsync("p_win", [Docs.Item("command", "win_only")], osFilter: "windows");
        await fixture.CreateProfileAsync("p_lin", [Docs.Item("command", "lin_only")], osFilter: "linux");
        await fixture.PublishAsync("p_win");
        await fixture.PublishAsync("p_lin");
        await fixture.AssignAsync("p_win", "all");
        await fixture.AssignAsync("p_lin", "all");

        AgentGeneration win = (await fixture.Service.GetGenerationAsync("win", CancellationToken.None))!;
        AgentGeneration lin = (await fixture.Service.GetGenerationAsync("lin", CancellationToken.None))!;

        Assert.Equal(["win_only"], win.Items.Select(item => item.Id).ToArray());
        Assert.Equal(["lin_only"], lin.Items.Select(item => item.Id).ToArray());
        Assert.Equal(1, win.Generation);
        Assert.Equal(1, lin.Generation);
    }

    [Fact]
    public async Task Variables_and_policy_of_several_profiles_are_merged_and_overrides_are_shown_as_warnings()
    {
        using var fixture = new ConfigFixture();
        fixture.Agents.Set(Docs.Agent("pc1", "windows", AgentStatuses.Enrolled, "office"));
        await fixture.SaveAsync("variables", "site", Docs.Variables(("SITE", "Berlin"), ("SHARED", "library")));
        await fixture.CreateProfileAsync(
            "p_base",
            [Docs.Item("variables", "site")],
            variables: new Dictionary<string, string> { ["LEVEL"] = "base" },
            policy: new ProfilePolicyRequest("locked", null));
        await fixture.CreateProfileAsync(
            "p_office",
            variables: new Dictionary<string, string> { ["LEVEL"] = "office", ["SHARED"] = "office" },
            policy: new ProfilePolicyRequest("allow", "deny"));
        await fixture.PublishAsync("p_base");
        await fixture.PublishAsync("p_office");
        await fixture.AssignAsync("p_base", "all");
        await fixture.AssignAsync("p_office", "tag:office");

        AgentDesiredInfo desired = (await fixture.Service.GetDesiredStateAsync("pc1", CancellationToken.None))!;
        AgentGeneration generation = (await fixture.Service.GetGenerationAsync("pc1", CancellationToken.None))!;
        HubDocument variables = Assert.Single(await fixture.Service.LoadDocumentsAsync(generation.Items, CancellationToken.None));

        Assert.True(desired.UpToDate);
        Assert.Equal(["p_base", "p_office"], desired.Profiles.ToArray());
        Assert.Equal(new PolicyInfo("allow", "deny"), desired.Policy);
        Assert.Equal(generation.Policy, desired.Policy);
        Assert.Equal(DocumentKinds.Variables, variables.Kind);
        Assert.Equal("office", variables.Body.GetProperty("LEVEL").GetString());
        Assert.Equal("office", variables.Body.GetProperty("SHARED").GetString());
        Assert.Equal("Berlin", variables.Body.GetProperty("SITE").GetString());
        string[] expectedWarnings = ["LEVEL:variable_overridden", "SHARED:variable_overridden", "local_config:policy_overridden"];
        Assert.Equal(expectedWarnings, desired.Warnings.Select(warning => warning.Id + ":" + warning.Code).Order(StringComparer.Ordinal).ToArray());
    }

    // Roll-back ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rolling_back_republishes_an_earlier_revision_and_agents_get_its_content_in_a_new_generation()
    {
        using var fixture = new ConfigFixture();
        fixture.Agents.Set(Docs.Agent("pc1"));
        await fixture.SaveAsync("command", "hello", Docs.Command("hello", code: "Write-Output one"));
        await fixture.CreateProfileAsync("p_all", [Docs.Item("command", "hello")]);
        await fixture.PublishAsync("p_all");
        await fixture.AssignAsync("p_all", "all");
        AgentGeneration one = (await fixture.Service.GetGenerationAsync("pc1", CancellationToken.None))!;

        LibraryDocumentInfo second = await fixture.SaveAsync("command", "hello", Docs.Command("hello", code: "Write-Output two"));
        await fixture.UpdateProfileAsync("p_all", [Docs.Item("command", "hello", second.Revision)]);
        PublishResult published = await fixture.PublishAsync("p_all");
        AgentGeneration two = (await fixture.Service.GetGenerationAsync("pc1", CancellationToken.None))!;

        PublishPreview preview = (await fixture.Service.PreviewAsync("p_all", 1, CancellationToken.None)).Value!;
        PublishResult rolledBack = await fixture.RollbackAsync("p_all", 1);
        AgentGeneration three = (await fixture.Service.GetGenerationAsync("pc1", CancellationToken.None))!;
        PublishResult again = await fixture.RollbackAsync("p_all", 1);
        long generationAfterAgain = await fixture.GenerationOfAsync("pc1");
        PublishResult forward = await fixture.RollbackAsync("p_all", 2);
        AgentGeneration four = (await fixture.Service.GetGenerationAsync("pc1", CancellationToken.None))!;
        ProfileRevisionList revisions = (await fixture.Service.ListProfileRevisionsAsync("p_all", CancellationToken.None))!;

        Assert.Equal(2, published.Revision);
        Assert.Equal(2, two.Generation);
        Assert.NotEqual(one.ContentHash, two.ContentHash);

        Assert.Equal(["pc1"], preview.Agents.Select(change => change.AgentId).ToArray());
        Assert.False(preview.NewRevision);
        Assert.Equal(1, preview.Revision);

        Assert.Equal(1, rolledBack.Revision);
        Assert.False(rolledBack.NewRevision);
        Assert.False(rolledBack.Unchanged);
        Assert.Equal(["pc1"], rolledBack.Agents.Select(change => change.AgentId).ToArray());
        Assert.Equal(3, three.Generation);
        Assert.Equal(one.ContentHash, three.ContentHash);
        Assert.Equal(one.Items.ToArray(), three.Items.ToArray());

        Assert.True(again.Unchanged);
        Assert.Empty(again.Agents);
        Assert.Equal(3, generationAfterAgain);

        Assert.Equal(2, forward.Revision);
        Assert.Equal(4, four.Generation);
        Assert.Equal(two.ContentHash, four.ContentHash);

        // A roll-back creates no revision and leaves the draft alone.
        Assert.Equal([2, 1], revisions.Revisions.Select(revision => revision.Revision).ToArray());
        Assert.Equal([true, false], revisions.Revisions.Select(revision => revision.Active).ToArray());
        Assert.Equal(second.Revision, (await fixture.Service.GetProfileAsync("p_all", CancellationToken.None))!.Draft.Items[0].Revision);
    }

    [Fact]
    public async Task Rolling_back_to_a_revision_that_does_not_exist_is_refused()
    {
        using var fixture = new ConfigFixture();
        await fixture.CreateProfileAsync("p_one");

        ConfigResult<PublishResult> missingRevision = await fixture.Service.PublishAsync("p_one", 4, null, "tester", HubRoles.Admin, CancellationToken.None);
        ConfigResult<PublishResult> missingProfile = await fixture.Service.PublishAsync("p_none", null, null, "tester", HubRoles.Admin, CancellationToken.None);
        ConfigResult<PublishResult> longNote = await fixture.Service.PublishAsync("p_one", null, new string('n', 501), "tester", HubRoles.Admin, CancellationToken.None);

        Assert.Equal(ConfigStatus.NotFound, missingRevision.Status);
        Assert.Equal("unknown_revision", missingRevision.Code);
        Assert.Equal(ConfigStatus.NotFound, missingProfile.Status);
        Assert.Equal("unknown_profile", missingProfile.Code);
        Assert.Equal(ConfigStatus.Invalid, longNote.Status);
    }

    // System scripts ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_profile_with_a_command_that_runs_code_as_system_can_only_be_published_by_an_administrator()
    {
        using var fixture = new ConfigFixture();
        fixture.Agents.Set(Docs.Agent("pc1"));
        await fixture.SaveAsync("command", "dangerous", Docs.Command("dangerous", runAs: "system"));
        await fixture.CreateProfileAsync("p_system", [Docs.Item("command", "dangerous")]);
        await fixture.AssignAsync("p_system", "all");

        PublishPreview preview = (await fixture.Service.PreviewAsync("p_system", null, CancellationToken.None)).Value!;
        ConfigResult<PublishResult> asOperator = await fixture.Service.PublishAsync("p_system", null, null, "olivia", HubRoles.Operator, CancellationToken.None);
        ConfigResult<PublishResult> asViewer = await fixture.Service.PublishAsync("p_system", null, null, "victor", HubRoles.Viewer, CancellationToken.None);

        Assert.True(preview.ContainsSystemScripts);
        Assert.True(preview.RequiresAdmin);
        Assert.Equal(["command:dangerous"], preview.SystemScripts.ToArray());
        Assert.Equal(["pc1"], preview.Agents.Select(change => change.AgentId).ToArray());
        foreach (ConfigResult<PublishResult> refused in new[] { asOperator, asViewer })
        {
            Assert.Equal(ConfigStatus.Forbidden, refused.Status);
            Assert.Equal("admin_required", refused.Code);
        }

        // A refused publish changes nothing.
        Assert.Null((await fixture.Service.GetProfileAsync("p_system", CancellationToken.None))!.ActiveRevision);
        Assert.Empty((await fixture.Service.ListProfileRevisionsAsync("p_system", CancellationToken.None))!.Revisions);
        Assert.Equal(0, await fixture.GenerationOfAsync("pc1"));

        PublishResult asAdmin = await fixture.PublishAsync("p_system", HubRoles.Admin);
        Assert.Equal(["pc1"], asAdmin.Agents.Select(change => change.AgentId).ToArray());
        Assert.Equal(1, await fixture.GenerationOfAsync("pc1"));

        // Publishing the same content again and rolling back to it need the role as well.
        ConfigResult<PublishResult> sameAsOperator = await fixture.Service.PublishAsync("p_system", null, null, "olivia", HubRoles.Operator, CancellationToken.None);
        ConfigResult<PublishResult> rollbackAsOperator = await fixture.Service.PublishAsync("p_system", 1, null, "olivia", HubRoles.Operator, CancellationToken.None);
        Assert.Equal(ConfigStatus.Forbidden, sameAsOperator.Status);
        Assert.Equal(ConfigStatus.Forbidden, rollbackAsOperator.Status);
    }

    [Theory]
    [InlineData("system", "powershell", true)]
    [InlineData("system", "bash", true)]
    [InlineData("user", "powershell", false)]
    [InlineData("user_elevated", "powershell", false)]
    [InlineData("system", "builtin", false)]
    [InlineData("system", "sequence", false)]
    public async Task Only_commands_that_run_code_as_system_make_a_profile_need_the_administrator(string runAs, string actionType, bool needsAdmin)
    {
        using var fixture = new ConfigFixture();
        JsonElement command = actionType switch
        {
            "powershell" => Docs.Command("cmd_x", runAs: runAs),
            "bash" => Docs.BashCommand("cmd_x", runAs),
            "builtin" => Docs.BuiltinCommand("cmd_x", runAs),
            _ => Docs.SequenceCommand("cmd_x", "cmd_y", runAs),
        };

        await fixture.SaveAsync("command", "cmd_x", command);
        await fixture.CreateProfileAsync("p_x", [Docs.Item("command", "cmd_x")]);

        PublishPreview preview = (await fixture.Service.PreviewAsync("p_x", null, CancellationToken.None)).Value!;
        ConfigResult<PublishResult> asOperator = await fixture.Service.PublishAsync("p_x", null, null, "olivia", HubRoles.Operator, CancellationToken.None);

        Assert.Equal(needsAdmin, preview.RequiresAdmin);
        Assert.Equal(needsAdmin, asOperator.Status == ConfigStatus.Forbidden);
        Assert.Equal(!needsAdmin, asOperator.IsSuccess);
    }

    [Fact]
    public async Task A_script_that_a_system_command_of_another_profile_runs_cannot_be_replaced_without_the_administrator()
    {
        using var fixture = new ConfigFixture();
        await fixture.SaveAsync("command", "run_it", Docs.FileCommand("run_it", "run.ps1", runAs: "system"));
        await fixture.SaveAsync("script", "run.ps1", Docs.Script("exit 0"));
        await fixture.SaveAsync("script", "other.ps1", Docs.Script("exit 1"));
        await fixture.CreateProfileAsync("p_runner", [Docs.Item("command", "run_it"), Docs.Item("script", "run.ps1")]);
        await fixture.CreateProfileAsync("p_scripts", [Docs.Item("script", "run.ps1")]);
        await fixture.CreateProfileAsync("p_harmless", [Docs.Item("script", "other.ps1")]);

        PublishPreview runner = (await fixture.Service.PreviewAsync("p_runner", null, CancellationToken.None)).Value!;
        PublishPreview scripts = (await fixture.Service.PreviewAsync("p_scripts", null, CancellationToken.None)).Value!;
        PublishPreview harmless = (await fixture.Service.PreviewAsync("p_harmless", null, CancellationToken.None)).Value!;
        ConfigResult<PublishResult> viaOperator = await fixture.Service.PublishAsync("p_scripts", null, null, "olivia", HubRoles.Operator, CancellationToken.None);
        ConfigResult<PublishResult> harmlessViaOperator = await fixture.Service.PublishAsync("p_harmless", null, null, "olivia", HubRoles.Operator, CancellationToken.None);

        Assert.Equal(["command:run_it", "script:run.ps1"], runner.SystemScripts.ToArray());
        Assert.Equal(["script:run.ps1"], scripts.SystemScripts.ToArray());
        Assert.Empty(harmless.SystemScripts);
        Assert.Equal(ConfigStatus.Forbidden, viaOperator.Status);
        Assert.True(harmlessViaOperator.IsSuccess, ConfigFixture.Describe(harmlessViaOperator));
    }

    /// <summary>Everything of a generation as text: the record itself compares its list of items by reference.</summary>
    private static string Fingerprint(AgentGeneration generation)
    {
        return string.Join(
            "|",
            generation.AgentId,
            generation.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            generation.ContentHash,
            generation.Signature,
            generation.UpdatedAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(",", generation.Items.Select(item => item.Kind + "/" + item.Id + "/" + item.Hash)));
    }

    private static async Task<AgentGeneration[]> GenerationsAsync(ConfigFixture fixture, params string[] agentIds)
    {
        var generations = new List<AgentGeneration>();
        foreach (string agentId in agentIds)
        {
            generations.Add((await fixture.Service.GetGenerationAsync(agentId, CancellationToken.None))!);
        }

        return [.. generations];
    }
}
