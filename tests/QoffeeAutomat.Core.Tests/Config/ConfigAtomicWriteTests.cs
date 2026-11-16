using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using static QoffeeAutomat.Core.Tests.Config.ConfigTestSupport;

namespace QoffeeAutomat.Core.Tests.Config;

/// <summary>
/// Fault injection for the atomic write (PLAN.md T202): whatever step of write-then-rename fails, the
/// document on disk is the old complete one or does not exist, never a truncated file.
/// </summary>
[Trait("Category", "Config")]
public sealed class ConfigAtomicWriteTests
{
    [Fact]
    public async Task A_crash_before_the_rename_keeps_the_previous_document_intact()
    {
        using var temp = new TempDirectory();
        var faults = new FaultInjectingFileOperations();
        using ConfigStore store = await OpenAsync(Options(temp, files: faults));
        await store.Local.SaveAsync(Command(name: "Stable"), null, CancellationToken.None);
        string path = CommandPath(temp, "my_command");
        string before = await File.ReadAllTextAsync(path);

        faults.FailMove = true;
        faults.FailDelete = true;
        await Assert.ThrowsAsync<IOException>(() => store.Local.SaveAsync(Command(name: "Lost"), null, CancellationToken.None));

        Assert.Equal(before, await File.ReadAllTextAsync(path));
        AssertParsesAs(path, "Stable", 1);
        Assert.Equal("Stable", store.Effective.GetDocument<CommandDocument>("my_command")!.Name);
        Assert.Equal(1, store.Effective.GetDocument<CommandDocument>("my_command")!.Revision);

        // The crash left its temporary file behind; the next start removes it and loads revision 1.
        Assert.Single(Directory.GetFiles(temp.Combine("config", "commands"), "*.tmp"));
        using ConfigStore restarted = await OpenAsync(Options(temp));
        Assert.Empty(Directory.GetFiles(temp.Combine("config", "commands"), "*.tmp"));
        Assert.Equal("Stable", restarted.Effective.GetDocument<CommandDocument>("my_command")!.Name);
        Assert.Empty(restarted.Problems);
    }

    [Fact]
    public async Task A_crash_while_writing_leaves_no_truncated_document()
    {
        using var temp = new TempDirectory();
        var faults = new FaultInjectingFileOperations();
        using ConfigStore store = await OpenAsync(Options(temp, files: faults));
        await store.Local.SaveAsync(Command(name: "Stable"), null, CancellationToken.None);
        string path = CommandPath(temp, "my_command");

        faults.TruncateWrite = true;
        faults.FailDelete = true;
        await Assert.ThrowsAsync<IOException>(() => store.Local.SaveAsync(Command(name: "Half written"), null, CancellationToken.None));

        AssertParsesAs(path, "Stable", 1);
        string temporary = Assert.Single(Directory.GetFiles(temp.Combine("config", "commands"), "*.tmp"));
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(File.ReadAllText(temporary)));
    }

    [Fact]
    public async Task A_failed_first_save_leaves_no_document_at_all()
    {
        using var temp = new TempDirectory();
        var faults = new FaultInjectingFileOperations { FailMove = true };
        using ConfigStore store = await OpenAsync(Options(temp, files: faults));

        await Assert.ThrowsAsync<IOException>(() => store.Local.SaveAsync(Command(), null, CancellationToken.None));

        Assert.False(File.Exists(CommandPath(temp, "my_command")));
        Assert.Null(store.Effective.GetDocument<CommandDocument>("my_command"));
        Assert.Empty(Directory.GetFiles(temp.Combine("config", "commands"), "*.tmp"));
        Assert.False(Directory.Exists(temp.Combine("config", ".history")));
    }

    [Fact]
    public async Task A_failed_save_is_retried_with_the_same_revision()
    {
        using var temp = new TempDirectory();
        var faults = new FaultInjectingFileOperations();
        using ConfigStore store = await OpenAsync(Options(temp, files: faults));
        await store.Local.SaveAsync(Command(), null, CancellationToken.None);

        faults.FailMove = true;
        await Assert.ThrowsAsync<IOException>(() => store.Local.SaveAsync(Command(name: "Second try"), 1, CancellationToken.None));
        faults.FailMove = false;
        SaveResult<CommandDocument> retried = await store.Local.SaveAsync(Command(name: "Second try"), 1, CancellationToken.None);

        Assert.True(retried.IsApplied);
        Assert.Equal(2, retried.Document!.Revision);
        AssertParsesAs(CommandPath(temp, "my_command"), "Second try", 2);
    }

    [Fact]
    public async Task A_failure_after_the_rename_keeps_the_new_document_even_when_the_history_cannot_be_written()
    {
        using var temp = new TempDirectory();
        var faults = new FaultInjectingFileOperations();
        using ConfigStore store = await OpenAsync(Options(temp, files: faults));
        await store.Local.SaveAsync(Command(), null, CancellationToken.None);

        // Block the history folder of the next revision by making its path a file.
        string history = temp.Combine("config", ".history", "commands", "my_command");
        Directory.Delete(history, recursive: true);
        File.WriteAllText(history, "not a directory");

        SaveResult<CommandDocument> result = await store.Local.SaveAsync(Command(name: "Second"), 1, CancellationToken.None);

        Assert.True(result.IsApplied);
        AssertParsesAs(CommandPath(temp, "my_command"), "Second", 2);
    }

    private static void AssertParsesAs(string path, string expectedName, int expectedRevision)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(expectedName, json.RootElement.GetProperty("name").GetString());
        Assert.Equal(expectedRevision, json.RootElement.GetProperty("revision").GetInt32());
    }
}
