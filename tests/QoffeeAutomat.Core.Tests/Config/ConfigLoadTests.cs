using System.Text;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Validation;
using static QoffeeAutomat.Core.Tests.Config.ConfigTestSupport;

namespace QoffeeAutomat.Core.Tests.Config;

[Trait("Category", "Config")]
public sealed class ConfigLoadTests
{
    [Fact]
    public async Task Loading_creates_the_directories()
    {
        using var temp = new TempDirectory();
        using var store = new ConfigStore(Options(temp));

        ConfigLoadReport report = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(0, report.Loaded);
        Assert.Empty(report.Problems);
        Assert.True(Directory.Exists(temp.Combine("config", "commands")));
        Assert.True(Directory.Exists(temp.Combine("config", "sensors")));
        Assert.True(Directory.Exists(temp.Combine("scripts")));
    }

    [Fact]
    public async Task Invalid_files_are_skipped_reported_and_never_deleted()
    {
        using var temp = new TempDirectory();
        using (ConfigStore seed = await OpenAsync(Options(temp)))
        {
            await seed.Local.SaveAsync(Command("good"), null, CancellationToken.None);
        }

        string broken = CommandPath(temp, "broken");
        string invalid = CommandPath(temp, "invalid");
        string mismatch = CommandPath(temp, "mismatch");
        string newer = CommandPath(temp, "newer");
        const string brokenText = "{ this is not json";
        const string invalidText = """{ "schemaVersion": 1, "id": "invalid", "name": "No run as", "enabled": true, "exposeToHa": false, "action": { "type": "builtin", "name": "power.shutdown" } }""";
        const string mismatchText = """{ "schemaVersion": 1, "id": "other_id", "name": "Mismatch", "enabled": true, "exposeToHa": false, "runAs": "system", "action": { "type": "builtin", "name": "power.shutdown" } }""";
        const string newerText = """{ "schemaVersion": 2, "id": "newer", "name": "Future", "enabled": true, "exposeToHa": false, "runAs": "system", "action": { "type": "builtin", "name": "power.shutdown" } }""";
        WriteFile(broken, brokenText);
        WriteFile(invalid, invalidText);
        WriteFile(mismatch, mismatchText);
        WriteFile(newer, newerText);

        var reported = new List<ConfigProblem>();
        using var store = new ConfigStore(Options(temp, reporter: reported.Add));
        ConfigLoadReport report = await store.LoadAsync(CancellationToken.None);

        string[] expectedIds = ["good"];
        Assert.Equal(expectedIds, store.Effective.List<CommandDocument>().Select(command => command.Id).ToArray());
        Assert.Equal(1, report.Loaded);
        Assert.Equal(4, report.Problems.Count);
        Assert.Equal(4, reported.Count);
        Assert.Equal(ConfigProblemCodes.InvalidJson, report.Problems.Single(problem => problem.Path == broken).Code);
        ConfigProblem invalidProblem = report.Problems.Single(problem => problem.Path == invalid);
        Assert.Equal(ConfigProblemCodes.InvalidDocument, invalidProblem.Code);
        Assert.Contains(invalidProblem.Errors, error => error.Path == "/runAs" && error.Code == ValidationCodes.Required);
        Assert.Equal(ConfigProblemCodes.IdMismatch, report.Problems.Single(problem => problem.Path == mismatch).Code);
        ConfigProblem newerProblem = report.Problems.Single(problem => problem.Path == newer);
        Assert.Contains(newerProblem.Errors, error => error.Code == ValidationCodes.Unsupported);

        // Never deleted, never rewritten.
        Assert.Equal(brokenText, await File.ReadAllTextAsync(broken));
        Assert.Equal(invalidText, await File.ReadAllTextAsync(invalid));
        Assert.Equal(mismatchText, await File.ReadAllTextAsync(mismatch));
        Assert.Equal(newerText, await File.ReadAllTextAsync(newer));
        Assert.Equal(report.Problems.Select(problem => problem.Path).Order(StringComparer.Ordinal), store.Problems.Select(problem => problem.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_file_with_a_byte_order_mark_is_read()
    {
        using var temp = new TempDirectory();
        using (ConfigStore seed = await OpenAsync(Options(temp)))
        {
            await seed.Local.SaveAsync(Command(), null, CancellationToken.None);
        }

        string path = CommandPath(temp, "my_command");
        byte[] content = await File.ReadAllBytesAsync(path);
        byte[] withBom = [.. Encoding.UTF8.GetPreamble(), .. content];
        await File.WriteAllBytesAsync(path, withBom);

        using ConfigStore store = await OpenAsync(Options(temp));

        Assert.Equal("My command", store.Effective.GetDocument<CommandDocument>("my_command")!.Name);
        Assert.Empty(store.Problems);
    }

    [Fact]
    public async Task Stale_temporary_files_are_swept_and_other_files_are_left_alone()
    {
        using var temp = new TempDirectory();
        string stale = temp.Combine("config", "commands", "my_command.json." + new string('a', 32) + ".tmp");
        string notes = temp.Combine("config", "commands", "notes.txt");
        string lookalike = temp.Combine("config", "commands", "keep.tmp");
        WriteFile(stale, "{ \"trunc");
        WriteFile(notes, "hello");
        WriteFile(lookalike, "keep");

        using ConfigStore store = await OpenAsync(Options(temp));

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(lookalike));
        Assert.Empty(store.Problems);
    }

    [Fact]
    public async Task Older_schema_versions_are_migrated_forward_and_the_file_is_upgraded()
    {
        using var temp = new TempDirectory();
        string path = CommandPath(temp, "old_command");
        WriteFile(path, OldCommandJson);
        IReadOnlyList<IDocumentMigration> migrations = [new TitleToNameMigration()];

        using ConfigStore store = await OpenAsync(Options(temp, migrations: migrations));

        CommandDocument command = store.Effective.GetDocument<CommandDocument>("old_command")!;
        Assert.Equal("Old command", command.Name);
        Assert.Equal(DocumentSchema.CurrentVersion, command.SchemaVersion);
        Assert.Empty(store.Problems);

        string upgraded = await File.ReadAllTextAsync(path);
        Assert.Contains("\"schemaVersion\": 1", upgraded, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"Old command\"", upgraded, StringComparison.Ordinal);
        Assert.DoesNotContain("title", upgraded, StringComparison.Ordinal);

        // The original is kept next to the history.
        string backup = temp.Combine("config", ".history", "commands", "old_command", "000000.v0.bak.json");
        Assert.True(File.Exists(backup));
        Assert.Contains("\"title\"", await File.ReadAllTextAsync(backup), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_migrated_file_is_not_migrated_again_on_the_next_start()
    {
        using var temp = new TempDirectory();
        WriteFile(CommandPath(temp, "old_command"), OldCommandJson);
        IReadOnlyList<IDocumentMigration> migrations = [new TitleToNameMigration()];
        ConfigStore first = await OpenAsync(Options(temp, migrations: migrations));
        first.Dispose();

        using ConfigStore second = await OpenAsync(Options(temp));

        Assert.Equal("Old command", second.Effective.GetDocument<CommandDocument>("old_command")!.Name);
        Assert.Empty(second.Problems);
    }

    [Fact]
    public async Task A_missing_migration_skips_the_file_without_touching_it()
    {
        using var temp = new TempDirectory();
        string path = CommandPath(temp, "old_command");
        WriteFile(path, OldCommandJson);

        using ConfigStore store = await OpenAsync(Options(temp));

        ConfigProblem problem = Assert.Single(store.Problems);
        Assert.Equal(ConfigProblemCodes.MigrationMissing, problem.Code);
        Assert.Null(store.Effective.GetDocument<CommandDocument>("old_command"));
        Assert.Equal(OldCommandJson, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_failing_migration_skips_the_file()
    {
        using var temp = new TempDirectory();
        string path = CommandPath(temp, "old_command");
        // The migration expects "title", which this file does not have.
        string withoutTitle = OldCommandJson.Replace("\"title\": \"Old command\",", string.Empty, StringComparison.Ordinal);
        WriteFile(path, withoutTitle);
        IReadOnlyList<IDocumentMigration> migrations = [new TitleToNameMigration()];

        using ConfigStore store = await OpenAsync(Options(temp, migrations: migrations));

        Assert.Equal(ConfigProblemCodes.MigrationFailed, Assert.Single(store.Problems).Code);
        Assert.Equal(withoutTitle, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_file_in_a_scripts_directory_with_an_invalid_name_is_reported_and_ignored()
    {
        using var temp = new TempDirectory();
        WriteFile(temp.Combine("scripts", "fine.ps1"), "Write-Output 1");
        WriteFile(temp.Combine("scripts", ".hidden"), "x");

        using ConfigStore store = await OpenAsync(Options(temp));

        IReadOnlyList<ScriptInfo> scripts = await store.Effective.ListScriptsAsync(CancellationToken.None);
        Assert.Equal("fine.ps1", Assert.Single(scripts).Name);
        Assert.Equal(ConfigProblemCodes.InvalidName, Assert.Single(store.Problems).Code);
        Assert.True(File.Exists(temp.Combine("scripts", ".hidden")));
    }
}
