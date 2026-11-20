using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Validation;
using QoffeeAutomat.Platform.Simulated;
using static QoffeeAutomat.Core.Tests.Config.ConfigTestSupport;

namespace QoffeeAutomat.Core.Tests.Config;

[Trait("Category", "Config")]
public sealed class ConfigStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [Fact]
    public async Task Saving_a_new_command_stamps_the_header_and_writes_the_file()
    {
        using var temp = new TempDirectory();
        var clock = new SimulatedClock(Start);
        using ConfigStore store = await OpenAsync(Options(temp), clock);

        SaveResult<CommandDocument> result = await store.Local.SaveAsync(Command(), null, CancellationToken.None);

        Assert.True(result.IsApplied);
        CommandDocument stored = Assert.IsType<CommandDocument>(result.Document);
        Assert.Equal(1, stored.Revision);
        Assert.Equal(Start, stored.UpdatedAt);
        Assert.Equal(DocumentOrigins.Local, stored.Origin);
        Assert.Equal(DocumentSchema.CommandVersion, stored.SchemaVersion);
        Assert.True(File.Exists(CommandPath(temp, "my_command")));

        using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(CommandPath(temp, "my_command")));
        Assert.Equal(1, json.RootElement.GetProperty("revision").GetInt32());
        Assert.Equal("2026-03-04T05:06:07Z", json.RootElement.GetProperty("updatedAt").GetString());
    }

    [Fact]
    public async Task The_store_ignores_header_values_that_the_caller_supplied()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));

        CommandDocument forged = Command() with { Revision = 99, Origin = DocumentOrigins.Hub, UpdatedAt = Start };
        SaveResult<CommandDocument> result = await store.Local.SaveAsync(forged, null, CancellationToken.None);

        Assert.Equal(1, result.Document!.Revision);
        Assert.Equal(DocumentOrigins.Local, result.Document.Origin);
    }

    [Fact]
    public async Task Every_save_increments_the_revision()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));

        SaveResult<CommandDocument> first = await store.Local.SaveAsync(Command(), null, CancellationToken.None);
        SaveResult<CommandDocument> second = await store.Local.SaveAsync(Command(name: "Renamed"), first.Document!.Revision, CancellationToken.None);

        Assert.Equal(2, second.Document!.Revision);
        Assert.Equal("Renamed", store.Effective.GetDocument<CommandDocument>("my_command")!.Name);
    }

    [Fact]
    public async Task Saving_with_a_stale_revision_fails_with_a_conflict_result()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));
        await store.Local.SaveAsync(Command(), null, CancellationToken.None);
        await store.Local.SaveAsync(Command(name: "Second"), 1, CancellationToken.None);

        SaveResult<CommandDocument> stale = await store.Local.SaveAsync(Command(name: "Stale"), 1, CancellationToken.None);

        Assert.Equal(ChangeStatus.Conflict, stale.Status);
        Assert.Equal(2, stale.CurrentRevision);
        Assert.Null(stale.Document);
        Assert.Equal("Second", store.Effective.GetDocument<CommandDocument>("my_command")!.Name);
        string onDisk = await File.ReadAllTextAsync(CommandPath(temp, "my_command"));
        Assert.Contains("\"Second\"", onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("Stale", onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revision_zero_means_the_document_must_not_exist_yet()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));

        SaveResult<CommandDocument> created = await store.Local.SaveAsync(Command(), 0, CancellationToken.None);
        SaveResult<CommandDocument> again = await store.Local.SaveAsync(Command(name: "Other"), 0, CancellationToken.None);

        Assert.True(created.IsApplied);
        Assert.Equal(ChangeStatus.Conflict, again.Status);
    }

    [Fact]
    public async Task Saving_without_an_expected_revision_overwrites()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));
        await store.Local.SaveAsync(Command(), null, CancellationToken.None);

        SaveResult<CommandDocument> result = await store.Local.SaveAsync(Command(name: "Overwritten"), null, CancellationToken.None);

        Assert.Equal(2, result.Document!.Revision);
    }

    [Fact]
    public async Task An_invalid_document_is_rejected_and_nothing_is_written()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));

        SaveResult<CommandDocument> result = await store.Local.SaveAsync(Command(name: ""), null, CancellationToken.None);

        Assert.Equal(ChangeStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, error => error.Path == "/name" && error.Code == ValidationCodes.Required);
        Assert.False(File.Exists(CommandPath(temp, "my_command")));
        Assert.Empty(store.Effective.List<CommandDocument>());
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("Upper")]
    [InlineData("")]
    public async Task A_document_id_that_is_not_a_plain_id_is_rejected_before_any_path_is_built(string id)
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));

        SaveResult<CommandDocument> result = await store.Local.SaveAsync(Command(id), null, CancellationToken.None);

        Assert.Equal(ChangeStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, error => error.Path == "/id");
        Assert.Empty(Directory.GetFiles(temp.FullPath, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Sequence_cycles_are_detected_against_the_stored_commands()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));
        SaveResult<CommandDocument> first = await store.Local.SaveAsync(SequenceCommand("first", "second"), null, CancellationToken.None);
        Assert.True(first.IsApplied);

        SaveResult<CommandDocument> second = await store.Local.SaveAsync(SequenceCommand("second", "first"), null, CancellationToken.None);

        Assert.Equal(ChangeStatus.Invalid, second.Status);
        Assert.Contains(second.Errors, error => error.Code == ValidationCodes.Cycle);
    }

    [Fact]
    public async Task Sensors_metric_overrides_and_settings_are_stored_too()
    {
        using var temp = new TempDirectory();
        using (ConfigStore store = await OpenAsync(Options(temp)))
        {
            Assert.True((await store.Local.SaveAsync(Sensor(), null, CancellationToken.None)).IsApplied);
            var overrides = new MetricOverridesDocument(
                DocumentSchema.CurrentVersion,
                new Dictionary<string, MetricOverride> { ["cpu.usage"] = new(Enabled: false, IntervalSeconds: 10) });
            Assert.True((await store.Local.SaveAsync(overrides, null, CancellationToken.None)).IsApplied);
            var settings = new AgentSettingsDocument(DocumentSchema.CurrentVersion, "Gaming PC", UiPort: 9000);
            Assert.True((await store.Local.SaveAsync(settings, null, CancellationToken.None)).IsApplied);
        }

        Assert.True(File.Exists(temp.Combine("config", "sensors", "my_sensor.json")));
        Assert.True(File.Exists(temp.Combine("config", "metrics.json")));
        Assert.True(File.Exists(temp.Combine("config", "settings.json")));

        using ConfigStore reopened = await OpenAsync(Options(temp));
        Assert.Equal("My sensor", reopened.Effective.GetDocument<SensorDocument>("my_sensor")!.Name);
        MetricOverride cpu = reopened.Effective.GetDocument<MetricOverridesDocument>(ConfigIds.MetricOverrides)!.Overrides["cpu.usage"];
        Assert.False(cpu.Enabled);
        Assert.Equal(10, cpu.IntervalSeconds);
        AgentSettingsDocument loadedSettings = reopened.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings)!;
        Assert.Equal("Gaming PC", loadedSettings.Name);
        Assert.Equal(9000, loadedSettings.EffectiveUiPort);
    }

    [Fact]
    public async Task Documents_survive_a_restart()
    {
        using var temp = new TempDirectory();
        using (ConfigStore store = await OpenAsync(Options(temp)))
        {
            await store.Local.SaveAsync(Command("first"), null, CancellationToken.None);
            await store.Local.SaveAsync(Command("second"), null, CancellationToken.None);
        }

        using ConfigStore reopened = await OpenAsync(Options(temp));

        IReadOnlyList<CommandDocument> commands = reopened.Effective.List<CommandDocument>();
        string[] expectedIds = ["first", "second"];
        Assert.Equal(expectedIds, commands.Select(command => command.Id));
        Assert.All(commands, command => Assert.Equal(1, command.Revision));
    }

    [Fact]
    public async Task Deleting_removes_the_document_and_the_file()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));
        await store.Local.SaveAsync(Command(), null, CancellationToken.None);

        DeleteResult result = await store.Local.DeleteAsync<CommandDocument>("my_command", 1, CancellationToken.None);

        Assert.True(result.IsApplied);
        Assert.Null(store.Effective.GetDocument<CommandDocument>("my_command"));
        Assert.False(File.Exists(CommandPath(temp, "my_command")));
    }

    [Fact]
    public async Task Deleting_with_a_stale_revision_conflicts_and_unknown_ids_are_not_found()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));
        await store.Local.SaveAsync(Command(), null, CancellationToken.None);
        await store.Local.SaveAsync(Command(name: "Again"), null, CancellationToken.None);

        DeleteResult stale = await store.Local.DeleteAsync<CommandDocument>("my_command", 1, CancellationToken.None);
        DeleteResult missing = await store.Local.DeleteAsync<CommandDocument>("nothing", null, CancellationToken.None);
        DeleteResult traversal = await store.Local.DeleteAsync<CommandDocument>("../my_command", null, CancellationToken.None);

        Assert.Equal(ChangeStatus.Conflict, stale.Status);
        Assert.Equal(2, stale.CurrentRevision);
        Assert.Equal(ChangeStatus.NotFound, missing.Status);
        Assert.Equal(ChangeStatus.NotFound, traversal.Status);
        Assert.True(File.Exists(CommandPath(temp, "my_command")));
    }

    [Fact]
    public async Task The_effective_view_of_the_single_local_layer_equals_the_layer()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));
        await store.Local.SaveAsync(Command(), null, CancellationToken.None);
        await store.Local.SaveScriptAsync("run.ps1", "Write-Output 1", null, CancellationToken.None);

        Assert.Equal(2, store.Layers.Count);
        Assert.False(store.Hub.State.IsActive);
        IConfigLayer layer = store.Local;

        Assert.Equal("local", layer.Name);
        Assert.False(layer.IsReadOnly);
        Assert.Same(store.Local, layer);
        Assert.Equal(layer.List<CommandDocument>(), store.Effective.List<CommandDocument>());
        Assert.Equal(
            (await layer.ListScriptsAsync(CancellationToken.None)).Select(script => script.Name),
            (await store.Effective.ListScriptsAsync(CancellationToken.None)).Select(script => script.Name));
    }

    [Fact]
    public async Task Saves_and_deletes_publish_one_change_each()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));
        using ConfigSubscription subscription = store.Subscribe();

        await store.Local.SaveAsync(Command(), null, CancellationToken.None);
        await store.Local.SaveAsync(Command(name: "Changed"), null, CancellationToken.None);
        await store.Local.DeleteAsync<CommandDocument>("my_command", null, CancellationToken.None);

        var changes = new List<ConfigChange>();
        while (subscription.Changes.TryRead(out ConfigChange? change))
        {
            changes.Add(change);
        }

        ConfigChangeType[] expectedTypes = [ConfigChangeType.Added, ConfigChangeType.Updated, ConfigChangeType.Removed];
        Assert.Equal(expectedTypes, changes.Select(change => change.Type));
        Assert.All(changes, change =>
        {
            Assert.Equal(ConfigChangeSource.Api, change.Source);
            Assert.Equal(ConfigKind.Command, change.Kind);
            Assert.Equal("my_command", change.Id);
            Assert.Equal("local", change.Layer);
        });
        Assert.Equal(2, changes[1].Revision);
    }

    [Fact]
    public async Task A_disposed_subscription_receives_nothing_and_disposing_the_store_completes_the_stream()
    {
        using var temp = new TempDirectory();
        ConfigStore store = await OpenAsync(Options(temp));
        using ConfigSubscription stopped = store.Subscribe();
        using ConfigSubscription active = store.Subscribe();
        stopped.Dispose();

        await store.Local.SaveAsync(Command(), null, CancellationToken.None);

        Assert.False(stopped.Changes.TryRead(out _));
        Assert.True(active.Changes.TryRead(out ConfigChange? change));
        Assert.Equal(ConfigChangeType.Added, change.Type);

        store.Dispose();
        await active.Changes.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
