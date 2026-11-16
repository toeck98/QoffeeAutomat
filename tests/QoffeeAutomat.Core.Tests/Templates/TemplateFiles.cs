using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Tests.Schema;

namespace QoffeeAutomat.Core.Tests.Templates;

/// <summary>Locates and reads the files of the <c>templates/</c> folder of the repository (T309).</summary>
internal static class TemplateFiles
{
    private const string SchemaBase = "https://github.com/toeck98/QoffeeAutomat/protocol/schema/";

    private static readonly Lazy<string> RepositoryRoot = new(FindRoot);
    private static readonly Lazy<SchemaSet> Schemas = new(() => SchemaSet.Load(Path.Combine(RepositoryRoot.Value, "protocol", "schema")));

    public static string CommandsDirectory => Path.Combine(RepositoryRoot.Value, "templates", "commands");

    public static string ScriptsDirectory => Path.Combine(RepositoryRoot.Value, "templates", "scripts");

    public static string MigrationGuide => Path.Combine(RepositoryRoot.Value, "docs", "migration-from-prototype.md");

    /// <summary>The paths of all command templates, ordered by name.</summary>
    public static IReadOnlyList<string> CommandFiles() =>
        Directory.Exists(CommandsDirectory)
            ? Directory.EnumerateFiles(CommandsDirectory, "*.json").Order(StringComparer.Ordinal).ToArray()
            : [];

    /// <summary>The paths of all script templates, ordered by name.</summary>
    public static IReadOnlyList<string> ScriptFiles() =>
        Directory.Exists(ScriptsDirectory)
            ? Directory.EnumerateFiles(ScriptsDirectory, "*.ps1").Order(StringComparer.Ordinal).ToArray()
            : [];

    public static CommandDocument ReadCommand(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.CommandDocument)
            ?? throw new InvalidOperationException(path + " is null.");
    }

    /// <summary>All command templates by id.</summary>
    public static IReadOnlyDictionary<string, CommandDocument> AllCommands()
    {
        var commands = new Dictionary<string, CommandDocument>(StringComparer.Ordinal);
        foreach (string path in CommandFiles())
        {
            CommandDocument document = ReadCommand(path);
            commands[document.Id] = document;
        }

        return commands;
    }

    /// <summary>The errors that the command schema of protocol/schema/config reports for a template file.</summary>
    public static IReadOnlyList<string> SchemaErrors(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return Schemas.Value.Validate(document.RootElement.Clone(), SchemaBase + "config/command.schema.json");
    }

    private static string FindRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "global.json"))
                && Directory.Exists(Path.Combine(directory, "protocol")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("The repository root (global.json and protocol/) was not found.");
    }
}
