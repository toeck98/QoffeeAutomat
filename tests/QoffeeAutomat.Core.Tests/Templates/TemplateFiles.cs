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

    public static string TemplatesDirectory => Path.Combine(RepositoryRoot.Value, "templates");

    public static string CommandsDirectory => Path.Combine(RepositoryRoot.Value, "templates", "commands");

    public static string SensorsDirectory => Path.Combine(RepositoryRoot.Value, "templates", "sensors");

    public static string ModesDirectory => Path.Combine(RepositoryRoot.Value, "templates", "modes");

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

    /// <summary>The paths of all sensor templates, ordered by name.</summary>
    public static IReadOnlyList<string> SensorFiles() =>
        Directory.Exists(SensorsDirectory)
            ? Directory.EnumerateFiles(SensorsDirectory, "*.json").Order(StringComparer.Ordinal).ToArray()
            : [];

    /// <summary>The paths of all mode group templates, ordered by name.</summary>
    public static IReadOnlyList<string> ModeFiles() =>
        Directory.Exists(ModesDirectory)
            ? Directory.EnumerateFiles(ModesDirectory, "*.json").Order(StringComparer.Ordinal).ToArray()
            : [];

    public static ModeGroupDocument ReadMode(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.ModeGroupDocument)
            ?? throw new InvalidOperationException(path + " is null.");
    }

    /// <summary>All mode group templates by id.</summary>
    public static IReadOnlyDictionary<string, ModeGroupDocument> AllModes()
    {
        var modes = new Dictionary<string, ModeGroupDocument>(StringComparer.Ordinal);
        foreach (string path in ModeFiles())
        {
            ModeGroupDocument document = ReadMode(path);
            modes[document.Id] = document;
        }

        return modes;
    }

    /// <summary>The errors that the mode schema of protocol/schema/config reports for a template file.</summary>
    public static IReadOnlyList<string> ModeSchemaErrors(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return Schemas.Value.Validate(document.RootElement.Clone(), SchemaBase + "config/mode.schema.json");
    }

    public static SensorDocument ReadSensor(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.SensorDocument)
            ?? throw new InvalidOperationException(path + " is null.");
    }

    /// <summary>All sensor templates by id.</summary>
    public static IReadOnlyDictionary<string, SensorDocument> AllSensors()
    {
        var sensors = new Dictionary<string, SensorDocument>(StringComparer.Ordinal);
        foreach (string path in SensorFiles())
        {
            SensorDocument document = ReadSensor(path);
            sensors[document.Id] = document;
        }

        return sensors;
    }

    /// <summary>The errors that the sensor schema of protocol/schema/config reports for a template file.</summary>
    public static IReadOnlyList<string> SensorSchemaErrors(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return Schemas.Value.Validate(document.RootElement.Clone(), SchemaBase + "config/sensor.schema.json");
    }

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
