using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using static QoffeeAutomat.Core.Tests.Config.ConfigTestSupport;

namespace QoffeeAutomat.Core.Tests.Config;

/// <summary>Tests with a real file watcher; they edit files the way an editor or a script would.</summary>
[Trait("Category", "Config")]
public sealed class ConfigWatcherTests
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(20);

    // Long enough for several debounce intervals (80 ms) to pass, so a second event would have arrived.
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(800);

    [Fact]
    public async Task An_externally_edited_file_raises_exactly_one_change_event()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp, watcher: true));
        await store.Local.SaveAsync(Command(name: "Before"), null, CancellationToken.None);
        await Task.Delay(QuietPeriod); // let the events of our own save pass
        using ConfigSubscription subscription = store.Subscribe();

        string path = CommandPath(temp, "my_command");
        string text = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, text.Replace("Before", "Edited by hand", StringComparison.Ordinal));

        ConfigChange change = await NextAsync(subscription);

        Assert.Equal(ConfigChangeType.Updated, change.Type);
        Assert.Equal(ConfigChangeSource.External, change.Source);
        Assert.Equal(ConfigKind.Command, change.Kind);
        Assert.Equal("my_command", change.Id);
        Assert.Equal("Edited by hand", store.Effective.GetDocument<CommandDocument>("my_command")!.Name);
        await AssertNoFurtherChangeAsync(subscription);
    }

    [Fact]
    public async Task Our_own_writes_raise_only_the_api_event_and_never_an_external_one()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp, watcher: true));
        using ConfigSubscription subscription = store.Subscribe();

        await store.Local.SaveAsync(Command(), null, CancellationToken.None);
        await store.Local.SaveAsync(Command(name: "Second"), null, CancellationToken.None);
        await store.Local.SaveScriptAsync("a.ps1", "one", null, CancellationToken.None);

        var changes = new List<ConfigChange>
        {
            await NextAsync(subscription),
            await NextAsync(subscription),
            await NextAsync(subscription),
        };

        Assert.All(changes, change => Assert.Equal(ConfigChangeSource.Api, change.Source));
        await AssertNoFurtherChangeAsync(subscription);
    }

    [Fact]
    public async Task A_new_file_and_a_deleted_file_are_noticed()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp, watcher: true));
        using ConfigSubscription subscription = store.Subscribe();

        // Create a valid document with the store's own serializer in a second store, then copy the file.
        string source = temp.Combine("elsewhere");
        using (ConfigStore other = await OpenAsync(new ConfigStoreOptions
        {
            ConfigDirectory = Path.Combine(source, "config"),
            ScriptsDirectory = Path.Combine(source, "scripts"),
            EnableWatcher = false,
        }))
        {
            await other.Local.SaveAsync(Command("dropped_in"), null, CancellationToken.None);
        }

        string target = CommandPath(temp, "dropped_in");
        File.Copy(Path.Combine(source, "config", "commands", "dropped_in.json"), target);

        ConfigChange added = await NextAsync(subscription);
        Assert.Equal(ConfigChangeType.Added, added.Type);
        Assert.Equal(ConfigChangeSource.External, added.Source);
        Assert.Equal("dropped_in", added.Id);
        Assert.NotNull(store.Effective.GetDocument<CommandDocument>("dropped_in"));
        await AssertNoFurtherChangeAsync(subscription);

        File.Delete(target);

        ConfigChange removed = await NextAsync(subscription);
        Assert.Equal(ConfigChangeType.Removed, removed.Type);
        Assert.Equal(ConfigChangeSource.External, removed.Source);
        Assert.Null(store.Effective.GetDocument<CommandDocument>("dropped_in"));
        await AssertNoFurtherChangeAsync(subscription);
    }

    [Fact]
    public async Task A_bad_edit_is_reported_and_keeps_the_last_good_document_until_it_is_fixed()
    {
        using var temp = new TempDirectory();
        var reported = new List<ConfigProblem>();
        using ConfigStore store = await OpenAsync(Options(temp, watcher: true, reporter: problem =>
        {
            lock (reported)
            {
                reported.Add(problem);
            }
        }));
        await store.Local.SaveAsync(Command(name: "Good"), null, CancellationToken.None);
        await Task.Delay(QuietPeriod);
        using ConfigSubscription subscription = store.Subscribe();
        string path = CommandPath(temp, "my_command");
        string good = await File.ReadAllTextAsync(path);

        await File.WriteAllTextAsync(path, "{ definitely not json");
        await WaitUntilAsync(() => store.Problems.Count == 1);

        Assert.Equal("Good", store.Effective.GetDocument<CommandDocument>("my_command")!.Name);
        Assert.Equal(path, store.Problems[0].Path);
        Assert.Equal(ConfigProblemCodes.InvalidJson, store.Problems[0].Code);
        Assert.NotEmpty(reported);
        Assert.False(subscription.Changes.TryRead(out _));
        Assert.Equal("{ definitely not json", await File.ReadAllTextAsync(path)); // never rewritten

        await File.WriteAllTextAsync(path, good.Replace("Good", "Fixed", StringComparison.Ordinal));
        ConfigChange change = await NextAsync(subscription);

        Assert.Equal(ConfigChangeType.Updated, change.Type);
        Assert.Equal("Fixed", store.Effective.GetDocument<CommandDocument>("my_command")!.Name);
        Assert.Empty(store.Problems);
        await AssertNoFurtherChangeAsync(subscription);
    }

    [Fact]
    public async Task Externally_edited_scripts_are_noticed()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp, watcher: true));
        using ConfigSubscription subscription = store.Subscribe();

        string path = temp.Combine("scripts", "hand_made.ps1");
        await File.WriteAllTextAsync(path, "Write-Output 1");
        ConfigChange added = await NextAsync(subscription);

        Assert.Equal(ConfigKind.Script, added.Kind);
        Assert.Equal(ConfigChangeType.Added, added.Type);
        Assert.Equal("hand_made.ps1", added.Id);
        Assert.Equal(ConfigChangeSource.External, added.Source);
        Assert.Equal("Write-Output 1", (await store.Effective.GetScriptAsync("hand_made.ps1", CancellationToken.None))!.Content);
        await AssertNoFurtherChangeAsync(subscription);

        await File.WriteAllTextAsync(path, "Write-Output 2");
        ConfigChange updated = await NextAsync(subscription);
        Assert.Equal(ConfigChangeType.Updated, updated.Type);
        await AssertNoFurtherChangeAsync(subscription);

        File.Delete(path);
        ConfigChange removed = await NextAsync(subscription);
        Assert.Equal(ConfigChangeType.Removed, removed.Type);
    }

    [Fact]
    public async Task Writing_the_same_content_again_is_not_a_change()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp, watcher: true));
        await store.Local.SaveAsync(Command(), null, CancellationToken.None);
        await Task.Delay(QuietPeriod);
        using ConfigSubscription subscription = store.Subscribe();
        string path = CommandPath(temp, "my_command");

        await File.WriteAllTextAsync(path, await File.ReadAllTextAsync(path));

        await AssertNoFurtherChangeAsync(subscription);
    }

    [Fact]
    public async Task Files_in_the_history_folder_do_not_raise_events()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp, watcher: true));
        await store.Local.SaveAsync(Command(), null, CancellationToken.None);
        await Task.Delay(QuietPeriod);
        using ConfigSubscription subscription = store.Subscribe();

        await File.WriteAllTextAsync(HistoryPath(temp, "commands", "my_command", 99), "{}");

        await AssertNoFurtherChangeAsync(subscription);
    }

    private static async Task<ConfigChange> NextAsync(ConfigSubscription subscription)
    {
        using var timeout = new CancellationTokenSource(EventTimeout);
        return await subscription.Changes.ReadAsync(timeout.Token);
    }

    private static async Task AssertNoFurtherChangeAsync(ConfigSubscription subscription)
    {
        await Task.Delay(QuietPeriod);
        bool extra = subscription.Changes.TryRead(out ConfigChange? change);
        Assert.False(extra, extra ? $"Unexpected extra change: {change}" : string.Empty);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + EventTimeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(25);
        }
    }
}
