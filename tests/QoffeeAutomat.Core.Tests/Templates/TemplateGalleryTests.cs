using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Templates;
using QoffeeAutomat.Core.Tests.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Tests.Templates;

/// <summary>
/// The template gallery (T701): the templates that ship with the agent load and validate, every one can be added to a
/// configuration twice without an id conflict, and a copy has its own id, name and script files.
/// </summary>
[Trait("Category", "Templates")]
public sealed class TemplateGalleryTests
{
    private static readonly string[] SeedCommands =
    [
        "set_display_profile",
        "save_display_profile",
        "launch_application",
        "open_url",
        "set_volume",
        "lock_pc",
        "sleep_pc",
        "kill_process",
        "empty_recycle_bin",
    ];

    private static readonly string[] SeedSensors =
    [
        "disk_free_percent",
        "app_running",
        "service_state",
    ];

    private static readonly string[] SeedModes =
    [
        "pc_mode",
    ];

    [Fact]
    public async Task The_shipped_templates_load_without_problems_and_include_the_seed_set()
    {
        TemplateCatalog catalog = await TemplateCatalog.LoadAsync(TemplateFiles.TemplatesDirectory, CancellationToken.None);

        Assert.True(catalog.Problems.Count == 0, string.Join(Environment.NewLine, catalog.Problems.Select(problem => $"{problem.Path}: {problem.Message}")));
        foreach (string id in SeedCommands)
        {
            Assert.Equal(TemplateKinds.Command, catalog.Find(id)?.Kind);
        }

        foreach (string id in SeedSensors)
        {
            Assert.Equal(TemplateKinds.Sensor, catalog.Find(id)?.Kind);
        }

        foreach (string id in SeedModes)
        {
            Assert.Equal(TemplateKinds.Mode, catalog.Find(id)?.Kind);
        }

        // Every file is in the catalog: a template that failed would be reported above, and one that is skipped silently would be missing here.
        int files = TemplateFiles.CommandFiles().Count + TemplateFiles.SensorFiles().Count + TemplateFiles.ModeFiles().Count;
        Assert.Equal(files, catalog.Entries.Count);
    }

    [Fact]
    public async Task Entries_are_ordered_commands_first_then_sensors_then_modes()
    {
        TemplateCatalog catalog = await TemplateCatalog.LoadAsync(TemplateFiles.TemplatesDirectory, CancellationToken.None);

        string[] kinds = [.. catalog.Entries.Select(entry => entry.Kind).Distinct(StringComparer.Ordinal)];

        Assert.Equal(KindOrder, kinds);
    }

    [Fact]
    public async Task A_mode_entry_requires_the_commands_of_its_steps_in_order_of_first_use()
    {
        TemplateCatalog catalog = await TemplateCatalog.LoadAsync(TemplateFiles.TemplatesDirectory, CancellationToken.None);

        TemplateEntry entry = catalog.Find("pc_mode")!;

        Assert.Equal("PC mode", entry.Name);
        Assert.Equal("mode", entry.ActionType);
        Assert.Equal("mdi:monitor-dashboard", entry.Icon);
        Assert.NotNull(entry.Mode);
        Assert.Null(entry.Command);
        Assert.Null(entry.Sensor);
        Assert.Equal(PcModeRequirements, entry.Requires.ToArray());
    }

    [Fact]
    public void Every_mode_template_matches_the_mode_schema()
    {
        IReadOnlyList<string> files = TemplateFiles.ModeFiles();
        Assert.NotEmpty(files);

        foreach (string path in files)
        {
            IReadOnlyList<string> errors = TemplateFiles.ModeSchemaErrors(path);
            Assert.True(errors.Count == 0, $"{Path.GetFileName(path)} does not match the schema: {string.Join("; ", errors)}");
        }
    }

    [Fact]
    public void Every_mode_template_passes_the_validator_with_the_command_templates_as_lookup()
    {
        IReadOnlyDictionary<string, CommandDocument> commands = TemplateFiles.AllCommands();
        IReadOnlyDictionary<string, ModeGroupDocument> modes = TemplateFiles.AllModes();
        Assert.NotEmpty(modes);

        foreach (ModeGroupDocument mode in modes.Values)
        {
            ValidationResult result = DocumentValidator.ValidateModeGroup(mode, id => commands.GetValueOrDefault(id));
            Assert.True(result.IsValid, $"{mode.Id}: " + string.Join("; ", result.Errors.Select(error => $"{error.Path} {error.Code} {error.Message}")));
            Assert.True(mode.Revision is null && mode.UpdatedAt is null && mode.Origin is null, mode.Id);
        }

        foreach (string path in TemplateFiles.ModeFiles())
        {
            Assert.Equal(Path.GetFileNameWithoutExtension(path), TemplateFiles.ReadMode(path).Id);
        }
    }

    [Fact]
    public void Every_sensor_template_matches_the_sensor_schema()
    {
        IReadOnlyList<string> files = TemplateFiles.SensorFiles();
        Assert.NotEmpty(files);

        foreach (string path in files)
        {
            IReadOnlyList<string> errors = TemplateFiles.SensorSchemaErrors(path);
            Assert.True(errors.Count == 0, $"{Path.GetFileName(path)} does not match the schema: {string.Join("; ", errors)}");
        }
    }

    [Fact]
    public void Every_sensor_template_passes_the_validator_and_is_named_like_its_file()
    {
        IReadOnlyDictionary<string, SensorDocument> sensors = TemplateFiles.AllSensors();
        Assert.NotEmpty(sensors);

        foreach (SensorDocument sensor in sensors.Values)
        {
            ValidationResult result = DocumentValidator.ValidateSensor(sensor);
            Assert.True(result.IsValid, $"{sensor.Id}: " + string.Join("; ", result.Errors.Select(error => $"{error.Path} {error.Code} {error.Message}")));
            Assert.True(sensor.Revision is null && sensor.UpdatedAt is null && sensor.Origin is null, sensor.Id);
        }

        foreach (string path in TemplateFiles.SensorFiles())
        {
            Assert.Equal(Path.GetFileNameWithoutExtension(path), TemplateFiles.ReadSensor(path).Id);
        }
    }

    [Fact]
    public void Template_ids_are_unique_across_commands_sensors_and_modes()
    {
        IEnumerable<string> commands = TemplateFiles.AllCommands().Keys;
        IEnumerable<string> sensors = TemplateFiles.AllSensors().Keys;
        IEnumerable<string> modes = TemplateFiles.AllModes().Keys;

        Assert.Empty(commands.Intersect(sensors, StringComparer.Ordinal));
        Assert.Empty(commands.Intersect(modes, StringComparer.Ordinal));
        Assert.Empty(sensors.Intersect(modes, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Every_shipped_template_can_be_added_twice_without_an_id_conflict()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await ConfigTestSupport.OpenAsync(ConfigTestSupport.Options(temp));
        var source = new TemplateCatalogSource(TemplateFiles.TemplatesDirectory);
        TemplateCatalog catalog = await source.GetAsync(CancellationToken.None);
        var instantiator = new TemplateInstantiator(source, store);
        Assert.NotEmpty(catalog.Entries);

        foreach (TemplateEntry entry in catalog.Entries)
        {
            TemplateInstantiationResult first = await instantiator.InstantiateAsync(entry.Id, CancellationToken.None);
            TemplateInstantiationResult second = await instantiator.InstantiateAsync(entry.Id, CancellationToken.None);

            Assert.True(first.Status == TemplateInstantiationStatus.Created, Describe(entry.Id, first));
            Assert.True(second.Status == TemplateInstantiationStatus.Created, Describe(entry.Id, second));
            Assert.Equal(entry.Kind, first.Instantiation!.Kind);
            Assert.NotEqual(first.Instantiation.Id, second.Instantiation!.Id);
        }

        // Everything that was written is valid and every script that a document names exists.
        IReadOnlyList<CommandDocument> commands = store.Effective.List<CommandDocument>();
        IReadOnlyList<SensorDocument> sensors = store.Effective.List<SensorDocument>();
        IReadOnlyList<ModeGroupDocument> modes = store.Effective.List<ModeGroupDocument>();
        Assert.Equal(commands.Count, commands.Select(command => command.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(sensors.Count, sensors.Select(sensor => sensor.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(modes.Count, modes.Select(mode => mode.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(TemplateFiles.ModeFiles().Count * 2, modes.Count);
        Assert.Empty(store.Problems);

        foreach (string script in commands.Select(command => command.Action).OfType<PowerShellAction>().Select(action => action.Script).OfType<string>()
            .Concat(sensors.Select(sensor => sensor.Source).OfType<PowerShellSensorSource>().Select(source => source.Script).OfType<string>()))
        {
            Assert.NotNull(await store.Effective.GetScriptAsync(script, CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_command_template_added_twice_gets_a_unique_id_a_numbered_name_and_its_own_script()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await ConfigTestSupport.OpenAsync(ConfigTestSupport.Options(temp));
        TemplateInstantiator instantiator = Instantiator(store);

        TemplateInstantiationResult first = await instantiator.InstantiateAsync("set_display_profile", CancellationToken.None);
        TemplateInstantiationResult second = await instantiator.InstantiateAsync("set_display_profile", CancellationToken.None);

        Assert.Equal("set_display_profile", first.Instantiation!.Id);
        Assert.Equal("set_display_profile_2", second.Instantiation!.Id);
        Assert.Equal(
            new[] { new TemplateCreatedItem(TemplateKinds.Script, "set_display_profile.ps1"), new TemplateCreatedItem(TemplateKinds.Command, "set_display_profile") },
            first.Instantiation.Created.ToArray());
        Assert.Equal(
            new[] { new TemplateCreatedItem(TemplateKinds.Script, "set_display_profile_2.ps1"), new TemplateCreatedItem(TemplateKinds.Command, "set_display_profile_2") },
            second.Instantiation.Created.ToArray());

        CommandDocument original = store.Effective.GetDocument<CommandDocument>("set_display_profile")!;
        CommandDocument copy = store.Effective.GetDocument<CommandDocument>("set_display_profile_2")!;
        Assert.Equal("Set display profile", original.Name);
        Assert.Equal("Set display profile (2)", copy.Name);
        Assert.Equal("set_display_profile.ps1", Assert.IsType<PowerShellAction>(original.Action).Script);
        Assert.Equal("set_display_profile_2.ps1", Assert.IsType<PowerShellAction>(copy.Action).Script);
        Assert.Equal(1, original.Revision);
        Assert.Equal(DocumentOrigins.Local, copy.Origin);

        // The copies are independent: editing the script of one leaves the other as it was.
        string template = await File.ReadAllTextAsync(Path.Combine(TemplateFiles.ScriptsDirectory, "set_display_profile.ps1"));
        ScriptResult edited = await store.Local.SaveScriptAsync("set_display_profile.ps1", "Write-Output 'changed'", null, CancellationToken.None);
        Assert.True(edited.IsApplied);
        Assert.Equal(template.ReplaceLineEndings("\n"), (await store.Effective.GetScriptAsync("set_display_profile_2.ps1", CancellationToken.None))!.Content.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task A_sensor_template_added_twice_gets_a_unique_id_a_numbered_name_and_its_own_script()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await ConfigTestSupport.OpenAsync(ConfigTestSupport.Options(temp));
        TemplateInstantiator instantiator = Instantiator(store);

        TemplateInstantiationResult first = await instantiator.InstantiateAsync("disk_free_percent", CancellationToken.None);
        TemplateInstantiationResult second = await instantiator.InstantiateAsync("disk_free_percent", CancellationToken.None);

        Assert.Equal(TemplateKinds.Sensor, first.Instantiation!.Kind);
        Assert.Equal("disk_free_percent", first.Instantiation.Id);
        Assert.Equal("disk_free_percent_2", second.Instantiation!.Id);

        SensorDocument copy = store.Effective.GetDocument<SensorDocument>("disk_free_percent_2")!;
        Assert.Equal("Disk space free (2)", copy.Name);
        Assert.Equal("disk_free_percent_2.ps1", Assert.IsType<PowerShellSensorSource>(copy.Source).Script);
        Assert.NotNull(await store.Effective.GetScriptAsync("disk_free_percent.ps1", CancellationToken.None));
        Assert.NotNull(await store.Effective.GetScriptAsync("disk_free_percent_2.ps1", CancellationToken.None));
    }

    [Fact]
    public async Task A_template_without_a_script_is_added_without_copying_anything()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await ConfigTestSupport.OpenAsync(ConfigTestSupport.Options(temp));
        TemplateInstantiator instantiator = Instantiator(store);

        TemplateInstantiationResult result = await instantiator.InstantiateAsync("lock_pc", CancellationToken.None);

        Assert.Equal(new[] { new TemplateCreatedItem(TemplateKinds.Command, "lock_pc") }, result.Instantiation!.Created.ToArray());
        Assert.Empty(await store.Effective.ListScriptsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_pc_mode_template_brings_the_commands_it_runs_once_and_reuses_them_afterwards()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await ConfigTestSupport.OpenAsync(ConfigTestSupport.Options(temp));
        TemplateInstantiator instantiator = Instantiator(store);

        TemplateInstantiationResult first = await instantiator.InstantiateAsync("pc_mode", CancellationToken.None);
        TemplateInstantiationResult second = await instantiator.InstantiateAsync("pc_mode", CancellationToken.None);

        Assert.True(first.Status == TemplateInstantiationStatus.Created, Describe("pc_mode", first));
        Assert.Equal(TemplateKinds.Mode, first.Instantiation!.Kind);
        Assert.Equal("pc_mode", first.Instantiation.Id);
        TemplateCreatedItem[] created = [.. first.Instantiation.Created];

        // The group comes last; the commands that its steps run were created before it, each with the id its step refers to.
        Assert.Equal(new TemplateCreatedItem(TemplateKinds.Mode, "pc_mode"), created[^1]);
        string[] createdCommands = [.. created.Where(item => item.Kind == TemplateKinds.Command).Select(item => item.Id)];
        Assert.Equal(ModeDependencyIds, createdCommands.Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(created, item => item == new TemplateCreatedItem(TemplateKinds.Script, "set_display_profile.ps1"));

        // The second copy runs the commands that exist; it does not create them again.
        Assert.Equal("pc_mode_2", second.Instantiation!.Id);
        Assert.Equal(TemplateKinds.Mode, second.Instantiation.Kind);
        Assert.Equal(new[] { new TemplateCreatedItem(TemplateKinds.Mode, "pc_mode_2") }, second.Instantiation.Created.ToArray());
        Assert.Equal(ModeDependencyIds.Length, store.Effective.List<CommandDocument>().Count);

        ModeGroupDocument original = store.Effective.GetDocument<ModeGroupDocument>("pc_mode")!;
        ModeGroupDocument copy = store.Effective.GetDocument<ModeGroupDocument>("pc_mode_2")!;
        Assert.Equal("PC mode", original.Name);
        Assert.Equal("PC mode (2)", copy.Name);
        Assert.Equal(original.Modes.Count, copy.Modes.Count);
        Assert.Equal(1, original.Revision);
        Assert.Equal(DocumentOrigins.Local, copy.Origin);
        Assert.Empty(store.Problems);
    }

    private static readonly string[] ModeDependencyIds =
    [
        "kill_process",
        "launch_moza_pit_house",
        "launch_simhub",
        "launch_steam_big_picture",
        "set_display_profile",
        "set_volume",
    ];

    private static readonly string[] PcModeRequirements =
    [
        "set_display_profile",
        "set_volume",
        "launch_steam_big_picture",
        "kill_process",
        "launch_moza_pit_house",
        "launch_simhub",
    ];

    private static readonly string[] KindOrder = [TemplateKinds.Command, TemplateKinds.Sensor, TemplateKinds.Mode];
    private static readonly string[] GoodOnlyIds = ["good"];
    private static readonly string[] BrokenFileNames = ["bad_runas.json", "broken.json", "needs_other.json", "no_script.json", "wrong_name.json"];
    private static readonly string[] GoodAndWorksIds = ["good", "works"];
    private static readonly string[] BrokenModeFileNames = ["broken_dep.json", "good.json", "needs_broken.json", "wrong_name.json"];

    [Fact]
    public async Task An_unknown_template_is_not_found()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await ConfigTestSupport.OpenAsync(ConfigTestSupport.Options(temp));

        TemplateInstantiationResult result = await Instantiator(store).InstantiateAsync("no_such_template", CancellationToken.None);

        Assert.Equal(TemplateInstantiationStatus.NotFound, result.Status);
        Assert.Null(result.Instantiation);
    }

    [Fact]
    public async Task Adding_at_the_same_time_never_picks_the_same_id()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await ConfigTestSupport.OpenAsync(ConfigTestSupport.Options(temp));
        TemplateInstantiator instantiator = Instantiator(store);

        TemplateInstantiationResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => instantiator.InstantiateAsync("set_volume", CancellationToken.None)));

        Assert.All(results, result => Assert.Equal(TemplateInstantiationStatus.Created, result.Status));
        Assert.Equal(6, results.Select(result => result.Instantiation!.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(6, store.Effective.List<CommandDocument>().Count);
    }

    [Fact]
    public void A_taken_id_gets_a_numeric_suffix_and_stays_within_the_length_of_an_id()
    {
        Assert.Equal(("lock_pc", 1), TemplateInstantiator.UniqueId("lock_pc", _ => false));
        Assert.Equal(("lock_pc_2", 2), TemplateInstantiator.UniqueId("lock_pc", id => id == "lock_pc"));
        Assert.Equal(("lock_pc_3", 3), TemplateInstantiator.UniqueId("lock_pc", id => id is "lock_pc" or "lock_pc_2"));

        string longId = "a" + new string('b', 62);
        (string unique, int number) = TemplateInstantiator.UniqueId(longId, id => id == longId);
        Assert.Equal(2, number);
        Assert.Equal(63, unique.Length);
        Assert.EndsWith("_2", unique, StringComparison.Ordinal);
        Assert.Matches("^[a-z][a-z0-9_]{0,62}$", unique);
    }

    [Fact]
    public async Task A_template_that_is_broken_is_left_out_and_reported()
    {
        using var temp = new TempDirectory();
        Write(temp, "commands/good.json", """{ "schemaVersion": 1, "id": "good", "name": "Good", "enabled": true, "exposeToHa": false, "runAs": "system", "action": { "type": "builtin", "name": "power.sleep" } }""");
        Write(temp, "commands/broken.json", """{ "id": "broken" """);
        Write(temp, "commands/wrong_name.json", """{ "schemaVersion": 1, "id": "other_id", "name": "Other", "enabled": true, "exposeToHa": false, "runAs": "system", "action": { "type": "builtin", "name": "power.sleep" } }""");
        Write(temp, "commands/bad_runas.json", """{ "schemaVersion": 1, "id": "bad_runas", "name": "Bad", "enabled": true, "exposeToHa": false, "runAs": "root", "action": { "type": "builtin", "name": "power.sleep" } }""");
        Write(temp, "commands/no_script.json", """{ "schemaVersion": 1, "id": "no_script", "name": "No script", "enabled": true, "exposeToHa": false, "runAs": "user", "action": { "type": "powershell", "source": "file", "script": "missing.ps1" } }""");
        Write(temp, "commands/needs_other.json", """{ "schemaVersion": 1, "id": "needs_other", "name": "Needs other", "enabled": true, "exposeToHa": false, "runAs": "user", "action": { "type": "sequence", "steps": [ { "command": "not_a_template" } ] } }""");

        TemplateCatalog catalog = await TemplateCatalog.LoadAsync(temp.FullPath, CancellationToken.None);

        Assert.Equal(GoodOnlyIds, catalog.Entries.Select(entry => entry.Id).ToArray());
        Assert.Equal(
            BrokenFileNames,
            catalog.Problems.Select(problem => Path.GetFileName(problem.Path)).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_mode_template_whose_required_command_is_missing_is_left_out_and_reported()
    {
        using var temp = new TempDirectory();
        Write(temp, "commands/good.json", """{ "schemaVersion": 1, "id": "good", "name": "Good", "enabled": true, "exposeToHa": false, "runAs": "system", "action": { "type": "builtin", "name": "power.sleep" } }""");
        Write(temp, "commands/broken_dep.json", """{ "schemaVersion": 1, "id": "broken_dep", "name": "Broken", "enabled": true, "exposeToHa": false, "runAs": "user", "action": { "type": "powershell", "source": "file", "script": "missing.ps1" } }""");
        Write(temp, "modes/works.json", """{ "schemaVersion": 1, "id": "works", "name": "Works", "enabled": true, "exposeToHa": false, "defaultMode": "a", "modes": [ { "id": "a", "name": "A", "enter": [ { "command": "good" } ], "exit": [ { "delaySeconds": 1 } ] } ] }""");
        Write(temp, "modes/needs_broken.json", """{ "schemaVersion": 1, "id": "needs_broken", "name": "Needs broken", "enabled": true, "exposeToHa": false, "defaultMode": "a", "modes": [ { "id": "a", "name": "A", "enter": [ { "command": "good" } ], "exit": [ { "command": "broken_dep" } ] } ] }""");
        Write(temp, "modes/wrong_name.json", """{ "schemaVersion": 1, "id": "other_id", "name": "Other", "enabled": true, "exposeToHa": false, "defaultMode": "a", "modes": [ { "id": "a", "name": "A" } ] }""");
        Write(temp, "modes/good.json", """{ "schemaVersion": 1, "id": "good", "name": "Same id as a command", "enabled": true, "exposeToHa": false, "defaultMode": "a", "modes": [ { "id": "a", "name": "A" } ] }""");

        TemplateCatalog catalog = await TemplateCatalog.LoadAsync(temp.FullPath, CancellationToken.None);

        Assert.Equal(GoodAndWorksIds, catalog.Entries.Select(entry => entry.Id).ToArray());
        Assert.Equal(TemplateKinds.Mode, catalog.Find("works")!.Kind);
        Assert.Equal(GoodOnlyIds, catalog.Find("works")!.Requires.ToArray());
        Assert.Equal(
            BrokenModeFileNames,
            catalog.Problems.Select(problem => Path.GetFileName(problem.Path)).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(catalog.Problems, problem => Path.GetFileName(problem.Path) == "needs_broken.json" && problem.Message.Contains("broken_dep", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_templates_directory_gives_an_empty_catalog()
    {
        using var temp = new TempDirectory();

        TemplateCatalog catalog = await TemplateCatalog.LoadAsync(temp.Combine("nothing_here"), CancellationToken.None);

        Assert.Empty(catalog.Entries);
        Assert.Empty(catalog.Problems);
    }

    private static TemplateInstantiator Instantiator(ConfigStore store) =>
        new(new TemplateCatalogSource(TemplateFiles.TemplatesDirectory), store);

    private static string Describe(string id, TemplateInstantiationResult result) =>
        $"{id}: {result.Status} " + string.Join("; ", result.Errors.Select(error => $"{error.Path} {error.Code} {error.Message}"));

    private static void Write(TempDirectory temp, string relativePath, string content) =>
        ConfigTestSupport.WriteFile(temp.Combine(relativePath.Split('/')), content);
}
