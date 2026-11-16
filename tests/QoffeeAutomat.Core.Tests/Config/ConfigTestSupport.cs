using System.Text.Json.Nodes;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.IO;

namespace QoffeeAutomat.Core.Tests.Config;

/// <summary>Builders and helpers shared by the config store tests.</summary>
internal static class ConfigTestSupport
{
    public static ConfigStoreOptions Options(
        TempDirectory temp,
        bool watcher = false,
        IFileOperations? files = null,
        IReadOnlyList<IDocumentMigration>? migrations = null,
        Action<ConfigProblem>? reporter = null,
        int historyLimit = ConfigStoreOptions.DefaultHistoryLimit)
    {
        return new ConfigStoreOptions
        {
            ConfigDirectory = temp.Combine("config"),
            ScriptsDirectory = temp.Combine("scripts"),
            EnableWatcher = watcher,
            DebounceInterval = TimeSpan.FromMilliseconds(80),
            FileOperations = files,
            Migrations = migrations ?? [],
            ProblemReporter = reporter,
            HistoryLimit = historyLimit,
        };
    }

    public static async Task<ConfigStore> OpenAsync(ConfigStoreOptions options, TimeProvider? time = null)
    {
        var store = new ConfigStore(options, time);
        await store.LoadAsync(CancellationToken.None);
        return store;
    }

    public static CommandDocument Command(string id = "my_command", string name = "My command") => new(
        DocumentSchema.CurrentVersion,
        id,
        name,
        true,
        false,
        RunAsValues.System,
        new BuiltinAction("power.shutdown"));

    public static CommandDocument SequenceCommand(string id, string calls) => Command(id) with
    {
        Action = new SequenceAction([new SequenceStep(Command: calls)]),
    };

    public static SensorDocument Sensor(string id = "my_sensor") => new(
        DocumentSchema.CurrentVersion,
        id,
        "My sensor",
        true,
        false,
        "binary_sensor",
        "bool",
        new ProcessRunningSensorSource("notepad"));

    public static string CommandPath(TempDirectory temp, string id) => temp.Combine("config", "commands", id + ".json");

    public static string HistoryPath(TempDirectory temp, string folder, string id, int revision) =>
        temp.Combine("config", ".history", folder, id, revision.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".json");

    public static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A command document in the JSON shape of schema version 0, which had <c>title</c> instead of <c>name</c>.</summary>
    public const string OldCommandJson = """
        {
          "schemaVersion": 0,
          "id": "old_command",
          "title": "Old command",
          "enabled": true,
          "exposeToHa": false,
          "runAs": "system",
          "action": { "type": "builtin", "name": "power.shutdown" }
        }
        """;

    /// <summary>The migration from schema version 0: <c>title</c> becomes <c>name</c>.</summary>
    internal sealed class TitleToNameMigration : IDocumentMigration
    {
        public ConfigKind Kind => ConfigKind.Command;

        public int FromVersion => 0;

        public JsonObject Migrate(JsonObject document)
        {
            string title = document["title"]!.GetValue<string>();
            document.Remove("title");
            document["name"] = title;
            return document;
        }
    }
}
