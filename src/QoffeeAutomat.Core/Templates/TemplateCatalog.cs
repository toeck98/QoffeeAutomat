using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Templates;

/// <summary>
/// One template of the gallery (PLAN.md S05, S16, T701, T717): a command, a custom sensor or a mode group document with
/// the scripts that it refers to. A template is never changed; adding it to the configuration creates a new document and
/// copies the scripts (<see cref="TemplateInstantiator"/>).
/// </summary>
public sealed class TemplateEntry
{
    internal TemplateEntry(
        string id,
        CommandDocument? command,
        SensorDocument? sensor,
        IReadOnlyDictionary<string, string> scripts,
        ModeGroupDocument? mode = null)
    {
        Id = id;
        Command = command;
        Sensor = sensor;
        Mode = mode;
        Scripts = scripts;
    }

    /// <summary>The template id, which equals the file name without extension and the id inside the document.</summary>
    public string Id { get; }

    /// <summary>The command document of a command template; null for another kind. It carries no revision, time or origin.</summary>
    public CommandDocument? Command { get; }

    /// <summary>The sensor document of a sensor template; null for another kind. It carries no revision, time or origin.</summary>
    public SensorDocument? Sensor { get; }

    /// <summary>The mode group document of a mode template; null for another kind. It carries no revision, time or origin.</summary>
    public ModeGroupDocument? Mode { get; }

    /// <summary>The scripts the document refers to, by file name, with their text.</summary>
    public IReadOnlyDictionary<string, string> Scripts { get; }

    /// <summary>The kind, see <see cref="TemplateKinds"/>.</summary>
    public string Kind => Command is not null ? TemplateKinds.Command : Mode is not null ? TemplateKinds.Mode : TemplateKinds.Sensor;

    /// <summary>The display name.</summary>
    public string Name => Command?.Name ?? Sensor?.Name ?? Mode?.Name ?? Id;

    /// <summary>The description.</summary>
    public string? Description => Command?.Description ?? Sensor?.Description ?? Mode?.Description;

    /// <summary>The icon.</summary>
    public string? Icon => Command?.Icon ?? Sensor?.Icon ?? Mode?.Icon;

    /// <summary>The action type of a command, the source type of a sensor, or <c>mode</c> for a mode group.</summary>
    public string ActionType => Command?.Action.Type ?? Sensor?.Source.Type ?? (Mode is not null ? "mode" : string.Empty);

    /// <summary>The ids of the commands that the steps of a sequence or mode template run, in order of first use.</summary>
    public IReadOnlyList<string> Requires { get; internal init; } = [];
}

/// <summary>A template file that was left out of the catalog.</summary>
/// <param name="Path">The file.</param>
/// <param name="Message">Why it was left out.</param>
public sealed record TemplateProblem(string Path, string Message);

/// <summary>
/// The templates that ship with the agent: <c>templates/commands/*.json</c>, <c>templates/sensors/*.json</c>,
/// <c>templates/modes/*.json</c> and the scripts in <c>templates/scripts/</c> (PLAN.md S05, T701, T717). A template that
/// does not validate, whose id differs from its file name, or whose script is missing is left out and reported in
/// <see cref="Problems"/>; CI fails on such a file (<c>Category=Templates</c>), so a shipped catalog has none.
/// </summary>
public sealed class TemplateCatalog
{
    /// <summary>The folder of the command templates below the templates directory.</summary>
    public const string CommandsFolder = "commands";

    /// <summary>The folder of the sensor templates below the templates directory.</summary>
    public const string SensorsFolder = "sensors";

    /// <summary>The folder of the mode group templates below the templates directory.</summary>
    public const string ModesFolder = "modes";

    /// <summary>The folder of the script templates below the templates directory.</summary>
    public const string ScriptsFolder = "scripts";

    private readonly Dictionary<string, TemplateEntry> _byId;

    private TemplateCatalog(List<TemplateEntry> entries, List<TemplateProblem> problems)
    {
        Entries = entries;
        Problems = problems;
        _byId = entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
    }

    /// <summary>A catalog without templates.</summary>
    public static TemplateCatalog Empty { get; } = new([], []);

    /// <summary>The templates, commands first, then sensors, then mode groups, each group ordered by id.</summary>
    public IReadOnlyList<TemplateEntry> Entries { get; }

    /// <summary>The files that were left out.</summary>
    public IReadOnlyList<TemplateProblem> Problems { get; }

    /// <summary>Finds a template.</summary>
    /// <param name="id">The template id.</param>
    /// <returns>The template, or null.</returns>
    public TemplateEntry? Find(string id)
    {
        return id is not null && _byId.TryGetValue(id, out TemplateEntry? entry) ? entry : null;
    }

    /// <summary>Reads and validates all templates of a directory. A missing directory yields an empty catalog.</summary>
    /// <param name="directory">The templates directory.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The catalog.</returns>
    public static async Task<TemplateCatalog> LoadAsync(string directory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var problems = new List<TemplateProblem>();
        if (!Directory.Exists(directory))
        {
            return new TemplateCatalog([], problems);
        }

        var commands = new Dictionary<string, (CommandDocument Document, string Path)>(StringComparer.Ordinal);
        foreach (string path in Files(Path.Combine(directory, CommandsFolder), problems))
        {
            CommandDocument? command = await ReadAsync(path, ConfigJsonContext.Default.CommandDocument, problems, cancellationToken).ConfigureAwait(false);
            if (command is not null && CheckId(path, command.Id, problems))
            {
                commands[command.Id] = (Strip(command), path);
            }
        }

        var sensors = new Dictionary<string, (SensorDocument Document, string Path)>(StringComparer.Ordinal);
        foreach (string path in Files(Path.Combine(directory, SensorsFolder), problems))
        {
            SensorDocument? sensor = await ReadAsync(path, ConfigJsonContext.Default.SensorDocument, problems, cancellationToken).ConfigureAwait(false);
            if (sensor is not null && CheckId(path, sensor.Id, problems))
            {
                sensors[sensor.Id] = (Strip(sensor), path);
            }
        }

        var modes = new Dictionary<string, (ModeGroupDocument Document, string Path)>(StringComparer.Ordinal);
        foreach (string path in Files(Path.Combine(directory, ModesFolder), problems))
        {
            ModeGroupDocument? mode = await ReadAsync(path, ConfigJsonContext.Default.ModeGroupDocument, problems, cancellationToken).ConfigureAwait(false);
            if (mode is not null && CheckId(path, mode.Id, problems))
            {
                modes[mode.Id] = (Strip(mode), path);
            }
        }

        var entries = new List<TemplateEntry>();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        string scriptsDirectory = Path.Combine(directory, ScriptsFolder);
        foreach ((string id, (CommandDocument document, string path)) in commands.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            ValidationResult result = DocumentValidator.ValidateCommand(
                document,
                other => commands.TryGetValue(other, out var found) ? found.Document : null);
            if (!result.IsValid)
            {
                problems.Add(new TemplateProblem(path, Describe(result)));
                continue;
            }

            string? scriptName = document.Action is PowerShellAction { Source: PowerShellSources.File } action ? action.Script : null;
            IReadOnlyDictionary<string, string>? scripts = await ReadScriptsAsync(scriptsDirectory, scriptName, path, problems, cancellationToken).ConfigureAwait(false);
            if (scripts is null)
            {
                continue;
            }

            string[] requires = document.Action is SequenceAction sequence
                ? [.. sequence.Steps.Where(step => step.Command is not null).Select(step => step.Command!).Distinct(StringComparer.Ordinal)]
                : [];
            entries.Add(new TemplateEntry(id, document, null, scripts) { Requires = requires });
            paths[id] = path;
        }

        foreach ((string id, (SensorDocument document, string path)) in sensors.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (commands.ContainsKey(id))
            {
                problems.Add(new TemplateProblem(path, "The id is also used by a command template; template ids are unique across kinds."));
                continue;
            }

            ValidationResult result = DocumentValidator.ValidateSensor(document);
            if (!result.IsValid)
            {
                problems.Add(new TemplateProblem(path, Describe(result)));
                continue;
            }

            string? scriptName = document.Source is PowerShellSensorSource { Source: PowerShellSources.File } source ? source.Script : null;
            IReadOnlyDictionary<string, string>? scripts = await ReadScriptsAsync(scriptsDirectory, scriptName, path, problems, cancellationToken).ConfigureAwait(false);
            if (scripts is null)
            {
                continue;
            }

            entries.Add(new TemplateEntry(id, null, document, scripts));
            paths[id] = path;
        }

        foreach ((string id, (ModeGroupDocument document, string path)) in modes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (commands.ContainsKey(id) || sensors.ContainsKey(id))
            {
                problems.Add(new TemplateProblem(path, "The id is also used by a command or sensor template; template ids are unique across kinds."));
                continue;
            }

            ValidationResult result = DocumentValidator.ValidateModeGroup(
                document,
                other => commands.TryGetValue(other, out var found) ? found.Document : null);
            if (!result.IsValid)
            {
                problems.Add(new TemplateProblem(path, Describe(result)));
                continue;
            }

            string[] requires =
            [
                .. document.Modes
                    .SelectMany(definition => (definition.Enter ?? []).Concat(definition.Exit ?? []))
                    .Where(step => step.Command is not null)
                    .Select(step => step.Command!)
                    .Distinct(StringComparer.Ordinal),
            ];
            entries.Add(new TemplateEntry(id, null, null, new Dictionary<string, string>(), document) { Requires = requires });
            paths[id] = path;
        }

        // A command or mode template that runs a template which was left out cannot be added; leave it out as well.
        HashSet<string> available = entries.Where(entry => entry.Command is not null).Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (TemplateEntry entry in entries.ToArray())
            {
                string? missing = entry.Requires.FirstOrDefault(required => !available.Contains(required));
                if (missing is not null)
                {
                    string runner = entry.Mode is not null ? "mode group" : "sequence";
                    problems.Add(new TemplateProblem(paths[entry.Id], $"The {runner} runs the command '{missing}', which is not a valid template."));
                    entries.Remove(entry);
                    available.Remove(entry.Id);
                    changed = true;
                }
            }
        }
        while (changed);

        return new TemplateCatalog(entries, problems);
    }

    private static string[] Files(string directory, List<TemplateProblem> problems)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problems.Add(new TemplateProblem(directory, exception.Message));
            return [];
        }
    }

    private static async Task<T?> ReadAsync<T>(
        string path,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        List<TemplateProblem> problems,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            T? document = JsonSerializer.Deserialize(json, typeInfo);
            if (document is null)
            {
                problems.Add(new TemplateProblem(path, "The file does not hold a JSON object."));
            }

            return document;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            problems.Add(new TemplateProblem(path, exception.Message));
            return null;
        }
    }

    private static bool CheckId(string path, string id, List<TemplateProblem> problems)
    {
        if (!string.Equals(Path.GetFileNameWithoutExtension(path), id, StringComparison.Ordinal))
        {
            problems.Add(new TemplateProblem(path, "The id of a template must be its file name without the extension."));
            return false;
        }

        return true;
    }

    private static async Task<IReadOnlyDictionary<string, string>?> ReadScriptsAsync(
        string scriptsDirectory,
        string? scriptName,
        string templatePath,
        List<TemplateProblem> problems,
        CancellationToken cancellationToken)
    {
        if (scriptName is null)
        {
            return new Dictionary<string, string>();
        }

        if (!ScriptNames.IsValid(scriptName))
        {
            problems.Add(new TemplateProblem(templatePath, $"'{scriptName}' is not a valid script name."));
            return null;
        }

        string path = Path.Combine(scriptsDirectory, scriptName);
        try
        {
            string content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return new Dictionary<string, string>(StringComparer.Ordinal) { [scriptName] = content };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problems.Add(new TemplateProblem(templatePath, $"The script '{scriptName}' cannot be read: {exception.Message}"));
            return null;
        }
    }

    private static CommandDocument Strip(CommandDocument document)
    {
        return document with { Revision = null, UpdatedAt = null, Origin = null };
    }

    private static SensorDocument Strip(SensorDocument document)
    {
        return document with { Revision = null, UpdatedAt = null, Origin = null };
    }

    private static ModeGroupDocument Strip(ModeGroupDocument document)
    {
        return document with { Revision = null, UpdatedAt = null, Origin = null };
    }

    private static string Describe(ValidationResult result)
    {
        return string.Join("; ", result.Errors.Select(error => $"{error.Path} {error.Code}: {error.Message}"));
    }
}

/// <summary>
/// Loads the catalog of a templates directory once, on first use, and hands the same instance to every caller. The
/// templates are part of the installation and do not change while the agent runs.
/// </summary>
public sealed class TemplateCatalogSource
{
    private readonly Lazy<Task<TemplateCatalog>> _catalog;

    /// <summary>Creates a source for a directory.</summary>
    /// <param name="directory">The templates directory; it need not exist.</param>
    public TemplateCatalogSource(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        DirectoryPath = directory;
        _catalog = new Lazy<Task<TemplateCatalog>>(
            () => Task.Run(() => TemplateCatalog.LoadAsync(directory, CancellationToken.None)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The templates directory.</summary>
    public string DirectoryPath { get; }

    /// <summary>Gets the catalog, loading it when this is the first call.</summary>
    /// <param name="cancellationToken">Stops waiting; the load itself continues.</param>
    /// <returns>The catalog.</returns>
    public Task<TemplateCatalog> GetAsync(CancellationToken cancellationToken)
    {
        return _catalog.Value.WaitAsync(cancellationToken);
    }
}
