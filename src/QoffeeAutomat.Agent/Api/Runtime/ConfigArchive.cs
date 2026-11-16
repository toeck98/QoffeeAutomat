using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Agent.Api.Runtime;

/// <summary>A script in a configuration archive.</summary>
/// <param name="Name">The file name.</param>
/// <param name="Content">The text.</param>
public sealed record ArchiveScript(string Name, string Content);

/// <summary>What a configuration archive holds, after every part was parsed and validated.</summary>
/// <param name="Commands">The commands.</param>
/// <param name="Sensors">The sensors.</param>
/// <param name="Scripts">The scripts.</param>
/// <param name="Metrics">The metric overrides, when the archive has them.</param>
/// <param name="Settings">The settings, when the archive has them and the caller asked for them.</param>
public sealed record ArchiveContents(
    IReadOnlyList<CommandDocument> Commands,
    IReadOnlyList<SensorDocument> Sensors,
    IReadOnlyList<ArchiveScript> Scripts,
    MetricOverridesDocument? Metrics,
    AgentSettingsDocument? Settings);

/// <summary>The outcome of reading an archive: its contents when it is valid, otherwise every error found.</summary>
/// <param name="Contents">The contents; null when there are errors.</param>
/// <param name="Errors">The errors; each path starts with a JSON pointer to the archive entry.</param>
public sealed record ArchiveReadResult(ArchiveContents? Contents, IReadOnlyList<ValidationError> Errors);

/// <summary>
/// The zip archive of <c>GET /config/export</c> and <c>POST /config/import</c> (PLAN.md S08, T214). The layout mirrors the
/// data directory: <c>config/commands/&lt;id&gt;.json</c>, <c>config/sensors/&lt;id&gt;.json</c>, <c>config/metrics.json</c>,
/// <c>config/settings.json</c>, <c>scripts/&lt;name&gt;</c>, plus <c>export.json</c> with the format version. Nothing from the
/// secrets directory is ever included: the archive is made from the config store only. Reading an archive validates
/// everything first, the entry names (nothing is extracted to disk, so a name cannot leave a directory), the sizes
/// (a small archive must not expand to gigabytes), every document and the cycles between sequences; the caller
/// applies the archive only when there are no errors.
/// </summary>
public static class ConfigArchive
{
    /// <summary>The version of the archive layout.</summary>
    public const int Format = 1;

    /// <summary>The name of the entry that describes the archive.</summary>
    public const string InfoEntry = "export.json";

    /// <summary>The most entries an archive may have.</summary>
    public const int MaxEntries = 2000;

    /// <summary>The largest entry after decompression: a script may be 1 MiB, a document is small.</summary>
    public const int MaxEntryBytes = 2 * 1024 * 1024;

    /// <summary>The largest sum of all entries after decompression.</summary>
    public const long MaxTotalBytes = 64L * 1024 * 1024;

    private const string CommandsFolder = "config/commands/";
    private const string SensorsFolder = "config/sensors/";
    private const string ScriptsFolder = "scripts/";
    private const string MetricsEntry = "config/metrics.json";
    private const string SettingsEntry = "config/settings.json";
    private const string JsonExtension = ".json";
    private const int MaxScriptBytes = 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly DateTimeOffset EntryTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Writes every document and script of the configuration into an archive.</summary>
    /// <param name="view">The configuration to export.</param>
    /// <param name="agentVersion">The version of the agent, for <c>export.json</c>.</param>
    /// <param name="now">The export time, for <c>export.json</c>.</param>
    /// <param name="cancellationToken">Cancels the export.</param>
    /// <returns>The zip file.</returns>
    public static async Task<byte[]> ExportAsync(IConfigView view, string agentVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(agentVersion);

        IReadOnlyList<CommandDocument> commands = view.List<CommandDocument>();
        IReadOnlyList<SensorDocument> sensors = view.List<SensorDocument>();
        IReadOnlyList<ScriptInfo> scripts = await view.ListScriptsAsync(cancellationToken).ConfigureAwait(false);

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (CommandDocument command in commands)
            {
                Add(zip, CommandsFolder + command.Id + JsonExtension, Json(command, ConfigJsonContext.Default.CommandDocument));
            }

            foreach (SensorDocument sensor in sensors)
            {
                Add(zip, SensorsFolder + sensor.Id + JsonExtension, Json(sensor, ConfigJsonContext.Default.SensorDocument));
            }

            if (view.GetDocument<MetricOverridesDocument>(ConfigIds.MetricOverrides) is { } overrides)
            {
                Add(zip, MetricsEntry, Json(overrides, ConfigJsonContext.Default.MetricOverridesDocument));
            }

            if (view.GetDocument<AgentSettingsDocument>(ConfigIds.Settings) is { } settings)
            {
                Add(zip, SettingsEntry, Json(settings, ConfigJsonContext.Default.AgentSettingsDocument));
            }

            int exportedScripts = 0;
            foreach (ScriptInfo info in scripts)
            {
                ScriptFile? script = await view.GetScriptAsync(info.Name, cancellationToken).ConfigureAwait(false);
                if (script is not null)
                {
                    Add(zip, ScriptsFolder + script.Name, Utf8.GetBytes(script.Content));
                    exportedScripts++;
                }
            }

            var summary = new ConfigExportInfo(Format, "QoffeeAutomat", agentVersion, now, commands.Count, sensors.Count, exportedScripts);
            Add(zip, InfoEntry, Json(summary, ApiJsonContext.Default.ConfigExportInfo));
        }

        return stream.ToArray();
    }

    /// <summary>Reads and validates an archive without applying anything.</summary>
    /// <param name="archive">The bytes of the zip file.</param>
    /// <param name="includeSettings">True to read <c>config/settings.json</c> too; the settings belong to one PC, so they are left out by default.</param>
    /// <param name="existing">The configuration the archive is merged into; sequences are checked for cycles against it and the archive together.</param>
    /// <returns>The contents, or all errors found.</returns>
    public static ArchiveReadResult Read(byte[] archive, bool includeSettings, IConfigView existing)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(existing);

        var errors = new List<ValidationError>();
        var commands = new Dictionary<string, CommandDocument>(StringComparer.Ordinal);
        var sensors = new Dictionary<string, SensorDocument>(StringComparer.Ordinal);
        var scripts = new List<ArchiveScript>();
        MetricOverridesDocument? metrics = null;
        AgentSettingsDocument? settings = null;
        string? settingsEntry = null;
        string? metricsEntry = null;

        try
        {
            using var stream = new MemoryStream(archive, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries)
            {
                return Failed(errors, "/", "too_many_entries", $"The archive has more than {MaxEntries} entries.");
            }

            long total = zip.Entries.Sum(entry => entry.Length);
            if (total > MaxTotalBytes)
            {
                return Failed(errors, "/", "too_large", "The archive is too large when unpacked.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string name = entry.FullName;
                if (name.EndsWith('/') && entry.Length == 0)
                {
                    continue;
                }

                string pointer = JsonPointer.Member(string.Empty, name);
                if (!seen.Add(name))
                {
                    errors.Add(new ValidationError(pointer, ValidationCodes.Duplicate, "The archive has this entry twice."));
                    continue;
                }

                byte[]? content = ReadEntry(entry, pointer, errors);
                if (content is null)
                {
                    continue;
                }

                Classify(name, pointer, content, includeSettings, commands, sensors, scripts, errors, ref metrics, ref settings, ref metricsEntry, ref settingsEntry);
            }
        }
        catch (InvalidDataException)
        {
            return Failed(errors, "/", "invalid_archive", "The file is not a zip archive.");
        }

        Func<string, CommandDocument?> lookup = id => commands.GetValueOrDefault(id) ?? existing.GetDocument<CommandDocument>(id);
        foreach ((string id, CommandDocument command) in commands)
        {
            AddAll(errors, CommandsFolder + id + JsonExtension, DocumentValidator.ValidateCommand(command, lookup).Errors);
        }

        foreach ((string id, SensorDocument sensor) in sensors)
        {
            AddAll(errors, SensorsFolder + id + JsonExtension, DocumentValidator.ValidateSensor(sensor).Errors);
        }

        if (metrics is not null)
        {
            AddAll(errors, metricsEntry!, DocumentValidator.ValidateMetricOverrides(metrics).Errors);
        }

        if (settings is not null)
        {
            AddAll(errors, settingsEntry!, DocumentValidator.ValidateSettings(settings).Errors);
        }

        if (errors.Count > 0)
        {
            return new ArchiveReadResult(null, errors);
        }

        return new ArchiveReadResult(
            new ArchiveContents(
                [.. commands.Values.OrderBy(command => command.Id, StringComparer.Ordinal)],
                [.. sensors.Values.OrderBy(sensor => sensor.Id, StringComparer.Ordinal)],
                [.. scripts.OrderBy(script => script.Name, StringComparer.Ordinal)],
                metrics,
                settings),
            []);
    }

    private static void Classify(
        string name,
        string pointer,
        byte[] content,
        bool includeSettings,
        Dictionary<string, CommandDocument> commands,
        Dictionary<string, SensorDocument> sensors,
        List<ArchiveScript> scripts,
        List<ValidationError> errors,
        ref MetricOverridesDocument? metrics,
        ref AgentSettingsDocument? settings,
        ref string? metricsEntry,
        ref string? settingsEntry)
    {
        if (string.Equals(name, InfoEntry, StringComparison.Ordinal))
        {
            CheckInfo(content, pointer, errors);
        }
        else if (TryDocumentId(name, CommandsFolder, out string? commandId))
        {
            if (ParseDocument(content, ConfigJsonContext.Default.CommandDocument, pointer, errors) is { } command)
            {
                CheckId(command.Id, commandId!, pointer, errors);
                commands[commandId!] = command;
            }
        }
        else if (TryDocumentId(name, SensorsFolder, out string? sensorId))
        {
            if (ParseDocument(content, ConfigJsonContext.Default.SensorDocument, pointer, errors) is { } sensor)
            {
                CheckId(sensor.Id, sensorId!, pointer, errors);
                sensors[sensorId!] = sensor;
            }
        }
        else if (string.Equals(name, MetricsEntry, StringComparison.Ordinal))
        {
            metrics = ParseDocument(content, ConfigJsonContext.Default.MetricOverridesDocument, pointer, errors);
            metricsEntry = name;
        }
        else if (string.Equals(name, SettingsEntry, StringComparison.Ordinal))
        {
            if (includeSettings)
            {
                settings = ParseDocument(content, ConfigJsonContext.Default.AgentSettingsDocument, pointer, errors);
                settingsEntry = name;
            }
        }
        else if (name.StartsWith(ScriptsFolder, StringComparison.Ordinal))
        {
            AddScript(name[ScriptsFolder.Length..], pointer, content, scripts, errors);
        }
        else
        {
            errors.Add(new ValidationError(pointer, "unexpected_entry", "This entry does not belong in a configuration archive."));
        }
    }

    private static void AddScript(string scriptName, string pointer, byte[] content, List<ArchiveScript> scripts, List<ValidationError> errors)
    {
        IReadOnlyList<ValidationError> nameErrors = ScriptNames.Validate(scriptName, pointer);
        if (nameErrors.Count > 0)
        {
            errors.AddRange(nameErrors);
            return;
        }

        if (content.Length > MaxScriptBytes)
        {
            errors.Add(new ValidationError(pointer, ValidationCodes.Invalid, "A script must not be larger than 1 MiB."));
            return;
        }

        ReadOnlySpan<byte> text = content;
        if (text.StartsWith(new ReadOnlySpan<byte>(Utf8Bom)))
        {
            text = text[Utf8Bom.Length..];
        }

        try
        {
            scripts.Add(new ArchiveScript(scriptName, StrictUtf8.GetString(text)));
        }
        catch (DecoderFallbackException)
        {
            errors.Add(new ValidationError(pointer, ValidationCodes.Invalid, "The script is not valid UTF-8."));
        }
    }

    private static void CheckInfo(byte[] content, string pointer, List<ValidationError> errors)
    {
        ConfigExportInfo? info = ParseDocument(content, ApiJsonContext.Default.ConfigExportInfo, pointer, errors);
        if (info is not null && info.Format > Format)
        {
            errors.Add(new ValidationError(pointer + "/format", ValidationCodes.Unsupported, "The archive was made by a newer version of the agent."));
        }
    }

    private static void CheckId(string documentId, string fileId, string pointer, List<ValidationError> errors)
    {
        if (!string.Equals(documentId, fileId, StringComparison.Ordinal))
        {
            errors.Add(new ValidationError(pointer + "/id", "id_mismatch", "The id inside the document differs from the name of the entry."));
        }
    }

    private static bool TryDocumentId(string name, string folder, out string? id)
    {
        id = null;
        if (!name.StartsWith(folder, StringComparison.Ordinal) || !name.EndsWith(JsonExtension, StringComparison.Ordinal))
        {
            return false;
        }

        string candidate = name[folder.Length..^JsonExtension.Length];
        if (candidate.Contains('/', StringComparison.Ordinal) || !ScriptNames.IsValidDocumentId(candidate))
        {
            // Not a document of this folder: reported as unexpected by the caller's fallthrough.
            return false;
        }

        id = candidate;
        return true;
    }

    private static T? ParseDocument<T>(byte[] content, JsonTypeInfo<T> typeInfo, string pointer, List<ValidationError> errors)
        where T : class
    {
        try
        {
            ReadOnlySpan<byte> json = content;
            if (json.StartsWith(new ReadOnlySpan<byte>(Utf8Bom)))
            {
                json = json[Utf8Bom.Length..];
            }

            T? document = JsonSerializer.Deserialize(json, typeInfo);
            if (document is null)
            {
                errors.Add(new ValidationError(pointer, "invalid_json", "The entry does not contain a document."));
            }

            return document;
        }
        catch (JsonException exception)
        {
            errors.Add(new ValidationError(pointer, "invalid_json", "The entry is not a valid document: " + exception.Message));
            return null;
        }
    }

    /// <summary>Reads one entry up to the size limit; the declared size is not trusted.</summary>
    private static byte[]? ReadEntry(ZipArchiveEntry entry, string pointer, List<ValidationError> errors)
    {
        if (entry.Length > MaxEntryBytes)
        {
            errors.Add(new ValidationError(pointer, "too_large", "The entry is too large."));
            return null;
        }

        using Stream source = entry.Open();
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxEntryBytes)
            {
                errors.Add(new ValidationError(pointer, "too_large", "The entry is too large."));
                return null;
            }
        }

        return buffer.ToArray();
    }

    private static void AddAll(List<ValidationError> errors, string entryName, IReadOnlyList<ValidationError> found)
    {
        string prefix = JsonPointer.Member(string.Empty, entryName);
        foreach (ValidationError error in found)
        {
            errors.Add(error with { Path = prefix + error.Path });
        }
    }

    private static ArchiveReadResult Failed(List<ValidationError> errors, string path, string code, string message)
    {
        errors.Add(new ValidationError(path, code, message));
        return new ArchiveReadResult(null, errors);
    }

    private static void Add(ZipArchive zip, string name, byte[] content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = EntryTime;
        using Stream target = entry.Open();
        target.Write(content, 0, content.Length);
    }

    private static byte[] Json<T>(T document, JsonTypeInfo<T> typeInfo)
    {
        return Bytes(JsonSerializer.Serialize(document, typeInfo) + "\n");
    }

    private static byte[] Bytes(string text)
    {
        return Utf8.GetBytes(text);
    }
}
