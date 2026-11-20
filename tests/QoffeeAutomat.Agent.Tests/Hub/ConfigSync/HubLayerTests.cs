using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Config.HubLayer;

namespace QoffeeAutomat.Agent.Tests.Hub.ConfigSync;

/// <summary>The hub layer of the config store (PLAN.md S20, D-028, T908): applying a generation, shadowing, the lock, atomic replacement and leaving.</summary>
[Trait("Category", "ConfigSync")]
public sealed class HubLayerTests
{
    [Fact]
    public async Task A_generation_becomes_the_hub_layer_and_its_documents_are_served_with_the_origin_hub()
    {
        using var rig = new StoreRig();
        await rig.LoadAsync();
        ConfigStore store = rig.Store;

        HubApplyResult result = await store.Hub.ApplyAsync(
            HubDocs.Generation(
                1,
                HubDocs.Open,
                HubDocs.Command("hub_cmd"),
                HubDocs.Sensor("hub_sensor"),
                HubDocs.Script("hub.ps1", "Write-Output 'hello'\n"),
                HubDocs.Variables("site", "SITE_NAME", "Home office"),
                HubDocs.MetricOverrides()),
            CancellationToken.None);

        Assert.True(result.Applied, result.Error);
        Assert.All(result.Results, item => Assert.Equal(ConfigItemStates.Applied, item.State));
        Assert.Equal(1, store.Hub.State.Generation);
        Assert.True(store.Hub.State.IsActive);

        CommandDocument command = store.Effective.GetDocument<CommandDocument>("hub_cmd")!;
        Assert.Equal(DocumentOrigins.Hub, command.Origin);
        Assert.Equal(DocumentOrigins.Hub, store.Effective.GetDocument<SensorDocument>("hub_sensor")!.Origin);
        Assert.Equal(DocumentOrigins.Hub, store.Effective.GetDocument<MetricOverridesDocument>(ConfigIds.MetricOverrides)!.Origin);
        ScriptFile? script = await store.Effective.GetScriptAsync("hub.ps1", CancellationToken.None);
        Assert.Equal("Write-Output 'hello'\n", script!.Content);
        Assert.Equal("Home office", store.Hub.Variables["SITE_NAME"]);
        Assert.True(File.Exists(Path.Combine(rig.HubDirectory, "state.json")));

        // The local layer is untouched: the hub documents are not local files.
        Assert.Null(store.Local.GetDocument<CommandDocument>("hub_cmd"));
    }

    [Fact]
    public async Task A_local_command_shadowed_by_a_hub_command_is_active_again_after_the_hub_command_is_withdrawn()
    {
        using var rig = new StoreRig();
        await rig.LoadAsync();
        ConfigStore store = rig.Store;
        CommandDocument local = await HubDocs.SaveLocalCommandAsync(store, "shared", "Local version");
        using ConfigSubscription changes = store.Subscribe();

        HubApplyResult first = await store.Hub.ApplyAsync(HubDocs.Generation(1, HubDocs.Open, HubDocs.Command("shared", "Hub version")), CancellationToken.None);

        Assert.True(first.Applied, first.Error);
        CommandDocument effective = store.Effective.GetDocument<CommandDocument>("shared")!;
        Assert.Equal("Hub version", effective.Name);
        Assert.Equal(DocumentOrigins.Hub, effective.Origin);
        Assert.Single(store.Effective.List<CommandDocument>());
        IReadOnlyList<HubShadowedItem> shadowed = await HubConfigOverview.GetShadowedAsync(store, CancellationToken.None);
        HubShadowedItem item = Assert.Single(shadowed);
        Assert.Equal(DocumentKinds.Command, item.Kind);
        Assert.Equal("shared", item.Id);
        Assert.Equal(HubShadowReasons.HubDocument, item.Reason);
        Assert.Equal(local.Revision, store.Local.GetDocument<CommandDocument>("shared")!.Revision);

        // The hub withdraws the command: the local one is used again, unchanged.
        HubApplyResult second = await store.Hub.ApplyAsync(HubDocs.Generation(2, HubDocs.Open), CancellationToken.None);

        Assert.True(second.Applied, second.Error);
        CommandDocument again = store.Effective.GetDocument<CommandDocument>("shared")!;
        Assert.Equal("Local version", again.Name);
        Assert.Equal(DocumentOrigins.Local, again.Origin);
        Assert.Empty(await HubConfigOverview.GetShadowedAsync(store, CancellationToken.None));

        // Consumers were told, so that the command registry and the manifest read the effective view again.
        var seen = new List<ConfigChange>();
        while (changes.Changes.TryRead(out ConfigChange? change))
        {
            seen.Add(change);
        }

        Assert.Contains(seen, change => change.Layer == "hub" && change.Kind == ConfigKind.Command && change.Id == "shared" && change.Type == ConfigChangeType.Added);
        Assert.Contains(seen, change => change.Layer == "hub" && change.Kind == ConfigKind.Command && change.Id == "shared" && change.Type == ConfigChangeType.Removed);
    }

    [Fact]
    public async Task While_the_local_configuration_is_locked_the_local_documents_are_inactive_and_come_back_with_the_unlock()
    {
        using var rig = new StoreRig();
        await rig.LoadAsync();
        ConfigStore store = rig.Store;
        await HubDocs.SaveLocalCommandAsync(store, "mine", "Local command");
        using ConfigSubscription changes = store.Subscribe();

        HubApplyResult locked = await store.Hub.ApplyAsync(HubDocs.Generation(1, HubDocs.Locked, HubDocs.Command("hub_cmd")), CancellationToken.None);

        Assert.True(locked.Applied, locked.Error);
        Assert.True(store.Hub.State.LocalConfigLocked);
        Assert.Null(store.Effective.GetDocument<CommandDocument>("mine"));
        string[] visible = [.. store.Effective.List<CommandDocument>().Select(command => command.Id)];
        string[] expectedVisible = ["hub_cmd"];
        Assert.Equal(expectedVisible, visible);
        HubShadowedItem shadowed = Assert.Single(await HubConfigOverview.GetShadowedAsync(store, CancellationToken.None));
        Assert.Equal("mine", shadowed.Id);
        Assert.Equal(HubShadowReasons.LocalConfigLocked, shadowed.Reason);

        // Engines that read the effective view are told that a local command left it.
        var seen = new List<ConfigChange>();
        while (changes.Changes.TryRead(out ConfigChange? change))
        {
            seen.Add(change);
        }

        Assert.Contains(seen, change => change.Layer == "local" && change.Id == "mine" && change.Type == ConfigChangeType.Removed);

        HubApplyResult unlocked = await store.Hub.ApplyAsync(HubDocs.Generation(2, HubDocs.Open, HubDocs.Command("hub_cmd")), CancellationToken.None);

        Assert.True(unlocked.Applied, unlocked.Error);
        Assert.False(store.Hub.State.LocalConfigLocked);
        Assert.Equal("Local command", store.Effective.GetDocument<CommandDocument>("mine")!.Name);
        Assert.Equal(2, store.Effective.List<CommandDocument>().Count);
    }

    [Fact]
    public async Task A_bad_document_fails_the_generation_and_the_previous_generation_stays_active()
    {
        using var rig = new StoreRig();
        await rig.LoadAsync();
        ConfigStore store = rig.Store;
        Assert.True((await store.Hub.ApplyAsync(HubDocs.Generation(1, HubDocs.Open, HubDocs.Command("good", "First")), CancellationToken.None)).Applied);

        HubApplyResult failed = await store.Hub.ApplyAsync(
            HubDocs.Generation(2, HubDocs.Locked, HubDocs.Command("good", "Second"), HubDocs.Command("another"), HubDocs.InvalidCommand("broken")),
            CancellationToken.None);

        Assert.False(failed.Applied);
        Assert.NotNull(failed.Error);
        ConfigItemResult broken = Assert.Single(failed.Results, item => item.State == ConfigItemStates.Invalid);
        Assert.Equal("broken", broken.Id);
        Assert.NotEmpty(broken.Errors!);
        Assert.Contains(failed.Results, item => item.Id == "good" && item.State == ConfigItemStates.Applied);

        // Nothing changed: not the documents, not the policy, not the files.
        Assert.Equal(1, store.Hub.State.Generation);
        Assert.False(store.Hub.State.LocalConfigLocked);
        Assert.Equal("First", store.Effective.GetDocument<CommandDocument>("good")!.Name);
        Assert.Null(store.Effective.GetDocument<CommandDocument>("another"));
        Assert.Null(store.Effective.GetDocument<CommandDocument>("broken"));
        string[] folders = [.. Directory.EnumerateDirectories(rig.HubDirectory).Select(directory => Path.GetFileName(directory)!)];
        string[] expectedFolders = ["g000000000001"];
        Assert.Equal(expectedFolders, folders);
    }

    [Fact]
    public async Task A_generation_that_is_not_newer_than_the_applied_one_is_refused()
    {
        using var rig = new StoreRig();
        await rig.LoadAsync();
        ConfigStore store = rig.Store;
        Assert.True((await store.Hub.ApplyAsync(HubDocs.Generation(5, HubDocs.Open, HubDocs.Command("a", "Five")), CancellationToken.None)).Applied);

        HubApplyResult same = await store.Hub.ApplyAsync(HubDocs.Generation(5, HubDocs.Open, HubDocs.Command("a", "Again")), CancellationToken.None);
        HubApplyResult older = await store.Hub.ApplyAsync(HubDocs.Generation(4, HubDocs.Open, HubDocs.Command("a", "Older")), CancellationToken.None);

        Assert.False(same.Applied);
        Assert.False(older.Applied);
        Assert.Equal("Five", store.Effective.GetDocument<CommandDocument>("a")!.Name);
        Assert.Equal(5, store.Hub.State.Generation);
    }

    [Fact]
    public async Task A_document_without_a_body_is_kept_from_the_applied_generation_but_only_with_the_same_hash()
    {
        using var rig = new StoreRig();
        await rig.LoadAsync();
        ConfigStore store = rig.Store;
        HubDocument kept = HubDocs.Command("kept", "Kept");
        Assert.True((await store.Hub.ApplyAsync(HubDocs.Generation(1, HubDocs.Open, kept), CancellationToken.None)).Applied);
        Assert.True(store.Hub.CanCarry(kept.Kind, kept.Id, kept.Hash));
        Assert.False(store.Hub.CanCarry(kept.Kind, kept.Id, new string('0', 64)));

        HubDocument added = HubDocs.Command("added", "Added");
        var carried = new HubGeneration(
            2,
            HubDocs.Open,
            [HubDocs.Carried(kept), new HubGenerationEntry(added.Kind, added.Id, added.Hash, added.Body)]);
        HubApplyResult second = await store.Hub.ApplyAsync(carried, CancellationToken.None);

        Assert.True(second.Applied, second.Error);
        Assert.Equal("Kept", store.Effective.GetDocument<CommandDocument>("kept")!.Name);
        Assert.Equal("Added", store.Effective.GetDocument<CommandDocument>("added")!.Name);

        // A body that was never sent for a hash that the agent does not have is a failure of the generation.
        var unknown = new HubGeneration(3, HubDocs.Open, [new HubGenerationEntry(kept.Kind, kept.Id, new string('a', 64))]);
        HubApplyResult third = await store.Hub.ApplyAsync(unknown, CancellationToken.None);

        Assert.False(third.Applied);
        Assert.Equal(ConfigItemStates.Invalid, Assert.Single(third.Results).State);
        Assert.Equal(2, store.Hub.State.Generation);
    }

    [Fact]
    public async Task A_document_that_the_platform_cannot_run_is_skipped_and_reported_and_the_generation_is_applied()
    {
        using var rig = new StoreRig(support: new CommandPlatformSupport(PlatformActionSupport.LinuxOs));
        await rig.LoadAsync();
        ConfigStore store = rig.Store;
        HubDocument windowsOnly = HubDocs.PowerShellCommand("windows_only");

        HubApplyResult result = await store.Hub.ApplyAsync(
            HubDocs.Generation(1, HubDocs.Open, windowsOnly, HubDocs.Command("portable")),
            CancellationToken.None);

        Assert.True(result.Applied, result.Error);
        ConfigItemResult skipped = Assert.Single(result.Results, item => item.Id == "windows_only");
        Assert.Equal(ConfigItemStates.SkippedUnsupported, skipped.State);
        Assert.NotEmpty(skipped.Errors!);
        Assert.Null(store.Effective.GetDocument<CommandDocument>("windows_only"));
        Assert.NotNull(store.Effective.GetDocument<CommandDocument>("portable"));
        HubLayerItem item = Assert.Single(store.Hub.State.Items, entry => entry.Id == "windows_only");
        Assert.Equal(HubConfigItemStates.SkippedUnsupported, item.State);

        // The hub is not asked for it again.
        Assert.True(store.Hub.CanCarry(windowsOnly.Kind, windowsOnly.Id, windowsOnly.Hash));
    }

    [Fact]
    public async Task The_hub_layer_survives_a_restart_with_its_documents_variables_and_policy()
    {
        using var rig = new StoreRig();
        await rig.LoadAsync();
        Assert.True((await rig.Store.Hub.ApplyAsync(
            HubDocs.Generation(
                3,
                new PolicyInfo(LocalConfigPolicies.Locked, DirectLinkPolicies.Deny),
                HubDocs.Command("hub_cmd"),
                HubDocs.Script("tool.sh", "echo hi\n"),
                HubDocs.Variables("site", "SITE_NAME", "Home")),
            CancellationToken.None)).Applied);

        ConfigStore reopened = await rig.ReopenAsync();

        Assert.Equal(3, reopened.Hub.State.Generation);
        Assert.True(reopened.Hub.State.LocalConfigLocked);
        Assert.True(reopened.Hub.State.DirectLinkDenied);
        Assert.Equal(DocumentOrigins.Hub, reopened.Effective.GetDocument<CommandDocument>("hub_cmd")!.Origin);
        Assert.Equal("echo hi\n", (await reopened.Effective.GetScriptAsync("tool.sh", CancellationToken.None))!.Content);
        Assert.Equal("Home", reopened.Hub.Variables["SITE_NAME"]);
        Assert.Empty(reopened.Problems);
    }

    [Fact]
    public async Task A_power_failure_at_any_step_of_an_apply_leaves_either_the_old_or_the_new_generation_never_a_mixture()
    {
        HubDocument oldCommand = HubDocs.Command("shared", "Old name");
        HubDocument oldScript = HubDocs.Script("tool.ps1", "old\n");
        HubDocument oldVariables = HubDocs.Variables("site", "SITE_NAME", "old");
        HubDocument newCommand = HubDocs.Command("shared", "New name");
        HubDocument extraCommand = HubDocs.Command("extra", "Extra");
        HubDocument newScript = HubDocs.Script("tool.ps1", "new\n");
        HubDocument newVariables = HubDocs.Variables("site", "SITE_NAME", "new");

        var outcomes = new List<string>();
        for (int failAt = 1; failAt <= 40; failAt++)
        {
            var power = new PowerFailureFileOperations();
            using var rig = new StoreRig(power);
            await rig.LoadAsync();
            Assert.True((await rig.Store.Hub.ApplyAsync(HubDocs.Generation(1, HubDocs.Open, oldCommand, oldScript, oldVariables), CancellationToken.None)).Applied);

            power.Arm(failAt);
            HubApplyResult second = await rig.Store.Hub.ApplyAsync(
                HubDocs.Generation(2, HubDocs.Locked, newCommand, extraCommand, newScript, newVariables),
                CancellationToken.None);

            // What the running agent shows, and what a restarted agent finds on disk, must be one generation as a whole.
            string running = await DescribeAsync(rig.Store);
            ConfigStore restarted = await rig.ReopenAsync();
            string afterRestart = await DescribeAsync(restarted);
            Assert.Equal(running, afterRestart);
            Assert.True(running is OldGeneration or NewGeneration, "A mixture of the generations at step " + failAt + ": " + running);
            Assert.Equal(second.Applied, running == NewGeneration);
            outcomes.Add(running);

            if (second.Applied)
            {
                // The step is beyond the last write of the apply: every earlier step was tried.
                break;
            }
        }

        Assert.Contains(OldGeneration, outcomes);
        Assert.Equal(NewGeneration, outcomes[^1]);
    }

    private const string OldGeneration = "gen=1|locked=False|shared=Old name|extra=-|script=old\n|var=old";

    private const string NewGeneration = "gen=2|locked=True|shared=New name|extra=Extra|script=new\n|var=new";

    private static async Task<string> DescribeAsync(ConfigStore store)
    {
        // The local layer is locked in the new generation, but only the hub documents are described, so Local is read through the hub layer.
        CommandDocument? shared = store.Hub.GetDocument<CommandDocument>("shared");
        CommandDocument? extra = store.Hub.GetDocument<CommandDocument>("extra");
        ScriptFile? script = await store.Hub.GetScriptAsync("tool.ps1", CancellationToken.None);
        store.Hub.Variables.TryGetValue("SITE_NAME", out string? variable);
        return $"gen={store.Hub.State.Generation}|locked={store.Hub.State.LocalConfigLocked}|shared={shared?.Name ?? "-"}|extra={extra?.Name ?? "-"}|script={script?.Content ?? "-"}|var={variable ?? "-"}";
    }

    [Fact]
    public async Task Leaving_with_remove_deletes_the_hub_layer_and_the_local_configuration_is_active_again()
    {
        using var rig = new StoreRig();
        await rig.LoadAsync();
        ConfigStore store = rig.Store;
        await HubDocs.SaveLocalCommandAsync(store, "mine", "Local command");
        Assert.True((await store.Hub.ApplyAsync(HubDocs.Generation(1, HubDocs.Locked, HubDocs.Command("hub_cmd")), CancellationToken.None)).Applied);
        Assert.Null(store.Effective.GetDocument<CommandDocument>("mine"));

        HubRemoveResult removed = await store.Hub.RemoveAsync(HubLeaveMode.Remove, store.Local, CancellationToken.None);

        Assert.True(removed.Removed);
        Assert.Equal(0, removed.Converted);
        Assert.False(store.Hub.State.IsActive);
        Assert.False(store.Hub.State.LocalConfigLocked);
        Assert.False(Directory.Exists(rig.HubDirectory));
        Assert.Null(store.Effective.GetDocument<CommandDocument>("hub_cmd"));
        Assert.Equal("Local command", store.Effective.GetDocument<CommandDocument>("mine")!.Name);
        Assert.Empty(store.Hub.Variables);

        ConfigStore reopened = await rig.ReopenAsync();
        Assert.False(reopened.Hub.State.IsActive);
    }

    [Fact]
    public async Task Leaving_with_convert_turns_the_hub_documents_into_local_ones_without_overwriting_local_documents()
    {
        using var rig = new StoreRig();
        await rig.LoadAsync();
        ConfigStore store = rig.Store;
        await HubDocs.SaveLocalCommandAsync(store, "shared", "Local version");
        Assert.True((await store.Hub.ApplyAsync(
            HubDocs.Generation(
                1,
                HubDocs.Open,
                HubDocs.Command("shared", "Hub version"),
                HubDocs.Command("only_hub", "Only on the hub"),
                HubDocs.Sensor("hub_sensor"),
                HubDocs.Script("tool.ps1", "text\n"),
                HubDocs.Variables("site", "SITE_NAME", "Home")),
            CancellationToken.None)).Applied);

        HubRemoveResult removed = await store.Hub.RemoveAsync(HubLeaveMode.Convert, store.Local, CancellationToken.None);

        Assert.True(removed.Removed);
        Assert.Equal(3, removed.Converted);
        HubDocumentRef skipped = Assert.Single(removed.Skipped);
        Assert.Equal("shared", skipped.Id);
        Assert.False(store.Hub.State.IsActive);

        // The converted documents are local now, with the origin local; the variables are dropped.
        CommandDocument converted = store.Local.GetDocument<CommandDocument>("only_hub")!;
        Assert.Equal(DocumentOrigins.Local, converted.Origin);
        Assert.Equal("Only on the hub", converted.Name);
        Assert.NotNull(store.Local.GetDocument<SensorDocument>("hub_sensor"));
        Assert.Equal("text\n", (await store.Local.GetScriptAsync("tool.ps1", CancellationToken.None))!.Content);
        Assert.Equal("Local version", store.Local.GetDocument<CommandDocument>("shared")!.Name);
        Assert.Empty(store.Hub.Variables);
    }

    [Fact]
    public async Task A_hub_layer_without_a_state_file_is_the_leftover_of_an_interrupted_apply_and_is_swept()
    {
        using var rig = new StoreRig();
        await rig.LoadAsync();
        string leftover = Path.Combine(rig.HubDirectory, "g000000000007", "commands");
        Directory.CreateDirectory(leftover);
        await File.WriteAllTextAsync(Path.Combine(leftover, "ghost.json"), "{}");

        ConfigStore reopened = await rig.ReopenAsync();

        Assert.False(reopened.Hub.State.IsActive);
        Assert.Null(reopened.Effective.GetDocument<CommandDocument>("ghost"));
        Assert.False(Directory.Exists(Path.Combine(rig.HubDirectory, "g000000000007")));
    }

    [Fact]
    public void The_variables_of_the_hub_become_QA_HUBVAR_environment_variables()
    {
        IReadOnlyDictionary<string, string> environment = HubVariables.ToEnvironment(new Dictionary<string, string>
        {
            ["site_name"] = "Home office",
            ["Backup"] = @"\\nas\backup",
        });

        Assert.Equal(2, environment.Count);
        Assert.Equal("Home office", environment["QA_HUBVAR_SITE_NAME"]);
        Assert.Equal(@"\\nas\backup", environment["QA_HUBVAR_BACKUP"]);
        Assert.Equal("QA_HUBVAR_X", HubVariables.EnvironmentName("x"));
    }

    [Fact]
    public async Task A_variables_document_with_a_bad_name_or_a_name_that_collides_in_case_is_invalid()
    {
        using var rig = new StoreRig();
        await rig.LoadAsync();

        HubApplyResult badName = await rig.Store.Hub.ApplyAsync(
            HubDocs.Generation(1, HubDocs.Open, HubDocs.Variables("site", "1BAD", "x")),
            CancellationToken.None);
        HubApplyResult collision = await rig.Store.Hub.ApplyAsync(
            HubDocs.Generation(
                1,
                HubDocs.Open,
                HubDocs.Document(DocumentKinds.Variables, "site", """{"Site":"a","SITE":"b"}""")),
            CancellationToken.None);

        Assert.False(badName.Applied);
        Assert.False(collision.Applied);
        Assert.False(rig.Store.Hub.State.IsActive);
    }
}
