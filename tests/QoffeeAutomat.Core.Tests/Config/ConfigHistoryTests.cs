using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Platform.Simulated;
using static QoffeeAutomat.Core.Tests.Config.ConfigTestSupport;

namespace QoffeeAutomat.Core.Tests.Config;

[Trait("Category", "Config")]
public sealed class ConfigHistoryTests
{
    [Fact]
    public async Task The_last_ten_revisions_of_a_document_are_kept()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));

        for (int version = 1; version <= 12; version++)
        {
            SaveResult<CommandDocument> saved = await store.Local.SaveAsync(Command(name: "Version " + version), null, CancellationToken.None);
            Assert.Equal(version, saved.Document!.Revision);
        }

        IReadOnlyList<RevisionInfo> history = await store.Local.GetHistoryAsync<CommandDocument>("my_command", CancellationToken.None);

        int[] expected = [12, 11, 10, 9, 8, 7, 6, 5, 4, 3];
        Assert.Equal(expected, history.Select(revision => revision.Revision));
        Assert.Equal(10, Directory.GetFiles(temp.Combine("config", ".history", "commands", "my_command")).Length);
        Assert.False(File.Exists(HistoryPath(temp, "commands", "my_command", 2)));
        Assert.True(File.Exists(HistoryPath(temp, "commands", "my_command", 3)));
    }

    [Fact]
    public async Task The_history_limit_is_configurable_and_records_the_time_of_each_save()
    {
        using var temp = new TempDirectory();
        var clock = new SimulatedClock(new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero));
        using ConfigStore store = await OpenAsync(Options(temp, historyLimit: 2), clock);

        await store.Local.SaveAsync(Command(), null, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(5));
        await store.Local.SaveAsync(Command(), null, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(5));
        await store.Local.SaveAsync(Command(), null, CancellationToken.None);

        IReadOnlyList<RevisionInfo> history = await store.Local.GetHistoryAsync<CommandDocument>("my_command", CancellationToken.None);

        Assert.Equal(2, history.Count);
        Assert.Equal(3, history[0].Revision);
        Assert.Equal(new DateTimeOffset(2026, 5, 6, 7, 18, 9, TimeSpan.Zero), history[0].UpdatedAt);
        Assert.Equal(2, history[1].Revision);
        Assert.Equal(new DateTimeOffset(2026, 5, 6, 7, 13, 9, TimeSpan.Zero), history[1].UpdatedAt);
    }

    [Fact]
    public async Task Restoring_stores_an_earlier_revision_as_a_new_revision()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));
        await store.Local.SaveAsync(Command(name: "One"), null, CancellationToken.None);
        await store.Local.SaveAsync(Command(name: "Two"), null, CancellationToken.None);
        await store.Local.SaveAsync(Command(name: "Three"), null, CancellationToken.None);
        using ConfigSubscription subscription = store.Subscribe();

        SaveResult<CommandDocument> restored = await store.Local.RestoreAsync<CommandDocument>("my_command", 1, 3, CancellationToken.None);

        Assert.True(restored.IsApplied);
        Assert.Equal(4, restored.Document!.Revision);
        Assert.Equal("One", restored.Document.Name);
        Assert.Equal("One", store.Effective.GetDocument<CommandDocument>("my_command")!.Name);
        Assert.True(subscription.Changes.TryRead(out ConfigChange? change));
        Assert.Equal(ConfigChangeSource.Restore, change.Source);
        Assert.Equal(ConfigChangeType.Updated, change.Type);
        Assert.Equal(4, (await store.Local.GetHistoryAsync<CommandDocument>("my_command", CancellationToken.None))[0].Revision);
    }

    [Fact]
    public async Task Restoring_checks_the_expected_revision_and_knows_missing_revisions()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));
        await store.Local.SaveAsync(Command(name: "One"), null, CancellationToken.None);
        await store.Local.SaveAsync(Command(name: "Two"), null, CancellationToken.None);

        SaveResult<CommandDocument> stale = await store.Local.RestoreAsync<CommandDocument>("my_command", 1, 1, CancellationToken.None);
        SaveResult<CommandDocument> missing = await store.Local.RestoreAsync<CommandDocument>("my_command", 42, null, CancellationToken.None);
        SaveResult<CommandDocument> traversal = await store.Local.RestoreAsync<CommandDocument>("../x", 1, null, CancellationToken.None);

        Assert.Equal(ChangeStatus.Conflict, stale.Status);
        Assert.Equal(ChangeStatus.NotFound, missing.Status);
        Assert.Equal(ChangeStatus.NotFound, traversal.Status);
        Assert.Equal("Two", store.Effective.GetDocument<CommandDocument>("my_command")!.Name);
    }

    [Fact]
    public async Task A_deleted_document_can_be_restored_and_numbering_continues()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));
        await store.Local.SaveAsync(Command(name: "One"), null, CancellationToken.None);
        await store.Local.SaveAsync(Command(name: "Two"), null, CancellationToken.None);
        await store.Local.DeleteAsync<CommandDocument>("my_command", null, CancellationToken.None);

        SaveResult<CommandDocument> restored = await store.Local.RestoreAsync<CommandDocument>("my_command", 2, null, CancellationToken.None);

        Assert.True(restored.IsApplied);
        Assert.Equal(3, restored.Document!.Revision);
        Assert.Equal("Two", restored.Document.Name);
    }

    [Fact]
    public async Task Creating_a_document_again_after_a_delete_does_not_reuse_revision_numbers()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));
        await store.Local.SaveAsync(Command(), null, CancellationToken.None);
        await store.Local.DeleteAsync<CommandDocument>("my_command", null, CancellationToken.None);

        SaveResult<CommandDocument> again = await store.Local.SaveAsync(Command(), 0, CancellationToken.None);

        Assert.Equal(2, again.Document!.Revision);
    }

    [Fact]
    public async Task Singleton_documents_have_a_history_too()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));
        await store.Local.SaveAsync(new AgentSettingsDocument(DocumentSchema.CurrentVersion, "First"), null, CancellationToken.None);
        await store.Local.SaveAsync(new AgentSettingsDocument(DocumentSchema.CurrentVersion, "Second"), null, CancellationToken.None);

        SaveResult<AgentSettingsDocument> restored = await store.Local.RestoreAsync<AgentSettingsDocument>(ConfigIds.Settings, 1, null, CancellationToken.None);

        Assert.True(restored.IsApplied);
        Assert.Equal("First", restored.Document!.Name);
        Assert.Equal(3, restored.Document.Revision);
        IReadOnlyList<RevisionInfo> history = await store.Local.GetHistoryAsync<AgentSettingsDocument>(ConfigIds.Settings, CancellationToken.None);
        Assert.Equal(3, history.Count);
    }

    [Fact]
    public async Task Unknown_ids_and_traversal_ids_have_no_history()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));

        Assert.Empty(await store.Local.GetHistoryAsync<CommandDocument>("nothing", CancellationToken.None));
        Assert.Empty(await store.Local.GetHistoryAsync<CommandDocument>("../..", CancellationToken.None));
    }
}
