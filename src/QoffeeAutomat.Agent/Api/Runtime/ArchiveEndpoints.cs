using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using QoffeeAutomat.Agent.Api.OpenApi;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Hub.ConfigSync;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Manifest;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Agent.Api.Runtime;

/// <summary>
/// Export and import of the configuration and the diagnostics bundle (PLAN.md S08, T214). The configuration archive
/// holds all documents and scripts and no secret; an import validates the whole archive before it changes anything.
/// The diagnostics bundle holds redacted settings, versions, counters and the recent log lines, and nothing from the
/// secrets directory.
/// </summary>
public static class ArchiveEndpoints
{
    /// <summary>The largest archive that an import accepts.</summary>
    public const int MaxImportBytes = 32 * 1024 * 1024;

    /// <summary>The number of log lines in the diagnostics bundle.</summary>
    public const int MaxDiagnosticsLogLines = 5000;

    private const string Tag = "Configuration";
    private const string ZipContentType = "application/zip";

    /// <summary>Maps the endpoints.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/config/export", ExportAsync)
            .Describe(Tag, "exportConfig", "Downloads all commands, sensors, metric overrides, settings and scripts as a zip file. The archive contains no secrets.")
            .ReturnsFile(ZipContentType);
        api.MapPost("/config/import", ImportAsync)
            .GuardsLocalConfig(LocalConfigArea.Collection)
            .Describe(Tag, "importConfig", "Imports a zip file made by the export. The whole archive is validated first; nothing is changed unless it is valid. Existing items with the same id are replaced.")
            .Query("settings", "boolean", "True also imports the settings; they belong to one PC, so the default is false.")
            .TakesFile(ZipContentType)
            .Returns<ConfigImportResult>()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status413PayloadTooLarge, ApiResults.UnprocessableContent);
        api.MapPost("/config/import/preview", PreviewAsync)
            .Describe(Tag, "previewImportConfig", "Validates a zip file made by the export and tells what an import would do to each item. Nothing is changed; an invalid archive is answered with valid false and the errors.")
            .Query("settings", "boolean", "True also reads the settings; they belong to one PC, so the default is false.")
            .TakesFile(ZipContentType)
            .Returns<ConfigImportPreview>()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status413PayloadTooLarge);
        api.MapGet("/diagnostics", DiagnosticsAsync)
            .Describe("Diagnostics", "downloadDiagnostics", "Downloads a zip file with redacted settings, versions, counters and the recent log lines. It contains no token, password hash or private key.")
            .ReturnsFile(ZipContentType);
    }

    private static async Task<IResult> ExportAsync(IConfigStore config, TimeProvider time, HttpContext context, CancellationToken cancellationToken)
    {
        DateTimeOffset now = time.GetUtcNow();
        byte[] archive = await ConfigArchive.ExportAsync(config.Effective, AgentVersion.Current, now, cancellationToken).ConfigureAwait(false);
        context.Response.Headers.CacheControl = "no-store";
        return Results.File(archive, ZipContentType, "qoffeeautomat-config-" + Stamp(now) + ".zip");
    }

    private static async Task<IResult> ImportAsync(HttpContext context, IConfigStore config, bool? settings, CancellationToken cancellationToken)
    {
        (byte[]? bytes, IResult? failure) = await ApiResults.ReadBytesAsync(context.Request, MaxImportBytes, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return failure!;
        }

        if (bytes.Length == 0)
        {
            return ApiResults.Problem(StatusCodes.Status400BadRequest, "bad_request", "The request body is empty. Send the zip file that the export made.");
        }

        // Everything is read and validated before the first write: an invalid archive changes nothing.
        ArchiveReadResult read = ConfigArchive.Read(bytes, settings ?? false, config.Effective);
        if (read.Contents is not { } contents)
        {
            return ApiResults.ValidationFailed(read.Errors);
        }

        var failures = new List<ValidationError>();
        int scripts = 0;
        foreach (ArchiveScript script in contents.Scripts)
        {
            ScriptResult saved = await config.Local.SaveScriptAsync(script.Name, script.Content, null, cancellationToken).ConfigureAwait(false);
            Collect(saved.IsApplied, saved.Errors, "scripts/" + script.Name, failures, ref scripts);
        }

        int sensors = 0;
        foreach (SensorDocument sensor in contents.Sensors)
        {
            SaveResult<SensorDocument> saved = await config.Local.SaveAsync(sensor, null, cancellationToken).ConfigureAwait(false);
            Collect(saved.IsApplied, saved.Errors, "config/sensors/" + sensor.Id + ".json", failures, ref sensors);
        }

        int commands = 0;
        foreach (CommandDocument command in contents.Commands)
        {
            SaveResult<CommandDocument> saved = await config.Local.SaveAsync(command, null, cancellationToken).ConfigureAwait(false);
            Collect(saved.IsApplied, saved.Errors, "config/commands/" + command.Id + ".json", failures, ref commands);
        }

        int metrics = 0;
        if (contents.Metrics is { } overrides)
        {
            SaveResult<MetricOverridesDocument> saved = await config.Local.SaveAsync(overrides, null, cancellationToken).ConfigureAwait(false);
            Collect(saved.IsApplied, saved.Errors, "config/metrics.json", failures, ref metrics);
        }

        int appliedSettings = 0;
        if (contents.Settings is { } importedSettings)
        {
            SaveResult<AgentSettingsDocument> saved = await config.Local.SaveAsync(importedSettings, null, cancellationToken).ConfigureAwait(false);
            Collect(saved.IsApplied, saved.Errors, "config/settings.json", failures, ref appliedSettings);
        }

        if (failures.Count > 0)
        {
            // The validation above makes this unlikely (the disk is full, a file is locked); what was applied stays.
            return ApiResults.ValidationFailed(failures);
        }

        return ApiResults.Json(
            new ConfigImportResult(commands, sensors, scripts, metrics > 0, appliedSettings > 0),
            ApiJsonContext.Default.ConfigImportResult);
    }

    private static async Task<IResult> PreviewAsync(HttpContext context, IConfigStore config, bool? settings, CancellationToken cancellationToken)
    {
        (byte[]? bytes, IResult? failure) = await ApiResults.ReadBytesAsync(context.Request, MaxImportBytes, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return failure!;
        }

        if (bytes.Length == 0)
        {
            return ApiResults.Problem(StatusCodes.Status400BadRequest, "bad_request", "The request body is empty. Send the zip file that the export made.");
        }

        // The same reading and validation as the import, and nothing is written: only the effective view is read.
        ArchiveReadResult read = ConfigArchive.Read(bytes, settings ?? false, config.Effective);
        if (read.Contents is not { } contents)
        {
            ApiValidationError[] errors = [.. read.Errors.Select(error => new ApiValidationError(error.Path, error.Code, error.Message))];
            return ApiResults.Json(new ConfigImportPreview(false, [], errors), ApiJsonContext.Default.ConfigImportPreview);
        }

        IConfigView view = config.Effective;
        var items = new List<ImportPreviewItem>();
        foreach (CommandDocument command in contents.Commands)
        {
            CommandDocument? stored = view.GetDocument<CommandDocument>(command.Id);
            items.Add(new ImportPreviewItem("command", command.Id, Change(stored is null, stored is not null && Same(ApiDocuments.Normalize(command), ApiDocuments.Normalize(stored), ConfigJsonContext.Default.CommandDocument))));
        }

        foreach (SensorDocument sensor in contents.Sensors)
        {
            SensorDocument? stored = view.GetDocument<SensorDocument>(sensor.Id);
            items.Add(new ImportPreviewItem("sensor", sensor.Id, Change(stored is null, stored is not null && Same(ApiDocuments.Normalize(sensor), ApiDocuments.Normalize(stored), ConfigJsonContext.Default.SensorDocument))));
        }

        foreach (ArchiveScript script in contents.Scripts)
        {
            ScriptFile? stored = await view.GetScriptAsync(script.Name, cancellationToken).ConfigureAwait(false);
            items.Add(new ImportPreviewItem("script", script.Name, Change(stored is null, stored is not null && string.Equals(stored.Content, script.Content, StringComparison.Ordinal))));
        }

        if (contents.Metrics is { } metrics)
        {
            MetricOverridesDocument? stored = view.GetDocument<MetricOverridesDocument>(ConfigIds.MetricOverrides);
            items.Add(new ImportPreviewItem("metrics", ConfigIds.MetricOverrides, Change(stored is null, stored is not null && Same(Strip(metrics), Strip(stored), ConfigJsonContext.Default.MetricOverridesDocument))));
        }

        if (contents.Settings is { } importedSettings)
        {
            AgentSettingsDocument? stored = view.GetDocument<AgentSettingsDocument>(ConfigIds.Settings);
            items.Add(new ImportPreviewItem("settings", ConfigIds.Settings, Change(stored is null, stored is not null && Same(Strip(importedSettings), Strip(stored), ConfigJsonContext.Default.AgentSettingsDocument))));
        }

        return ApiResults.Json(new ConfigImportPreview(true, items, []), ApiJsonContext.Default.ConfigImportPreview);
    }

    private static string Change(bool isNew, bool unchanged)
    {
        return isNew ? "new" : unchanged ? "unchanged" : "changed";
    }

    // The members that the store owns (revision, time, origin) differ between an archive and the disk without a real change.
    private static MetricOverridesDocument Strip(MetricOverridesDocument document) =>
        document with { SchemaVersion = DocumentSchema.CurrentVersion, Revision = null, UpdatedAt = null, Origin = null };

    private static AgentSettingsDocument Strip(AgentSettingsDocument document) =>
        document with { SchemaVersion = DocumentSchema.CurrentVersion, Revision = null, UpdatedAt = null, Origin = null };

    private static bool Same<T>(T left, T right, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        // The documents hold lists, so record equality would compare references; the JSON text is the comparison.
        return string.Equals(JsonSerializer.Serialize(left, typeInfo), JsonSerializer.Serialize(right, typeInfo), StringComparison.Ordinal);
    }

    private static async Task<IResult> DiagnosticsAsync(
        IConfigStore config,
        IAppPaths paths,
        TimeProvider time,
        AgentUptime uptime,
        PairingService pairing,
        ManifestService manifest,
        PlatformSelection platform,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = time.GetUtcNow();
        IReadOnlyList<ScriptInfo> scripts = await config.Effective.ListScriptsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<LinkClient> clients = await pairing.ListClientsAsync(cancellationToken).ConfigureAwait(false);

        var info = new DiagnosticsInfo(
            now,
            AgentVersion.Current,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            platform.Kind.ToString().ToLowerInvariant(),
            (long)uptime.Elapsed.TotalSeconds,
            config.Effective.List<CommandDocument>().Count,
            config.Effective.List<SensorDocument>().Count,
            scripts.Count,
            clients.Count,
            manifest.Current.Rev,
            [.. config.Problems.Select(problem => new DiagnosticsProblem(
                System.IO.Path.GetFileName(problem.Path),
                problem.Code,
                SecretScrubber.Scrub(problem.Message)))]);

        // The settings hold no secret by design (S06); they still pass the scrubber, like everything else in the bundle.
        AgentSettingsDocument settings = config.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings)
            ?? new AgentSettingsDocument(DocumentSchema.CurrentVersion, Environment.MachineName);
        IReadOnlyList<string> logLines = await LogTail.ReadLinesAsync(paths.LogsDirectory, MaxDiagnosticsLogLines, cancellationToken).ConfigureAwait(false);

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddText(zip, "diagnostics.json", JsonSerializer.Serialize(info, ApiJsonContext.Default.DiagnosticsInfo) + "\n", now);
            AddText(zip, "settings.json", SecretScrubber.Scrub(JsonSerializer.Serialize(settings, ConfigJsonContext.Default.AgentSettingsDocument)) + "\n", now);
            AddText(zip, "logs/agent.log", string.Concat(logLines.Select(line => SecretScrubber.Scrub(line) + "\n")), now);
        }

        context.Response.Headers.CacheControl = "no-store";
        return Results.File(stream.ToArray(), ZipContentType, "qoffeeautomat-diagnostics-" + Stamp(now) + ".zip");
    }

    private static void Collect(bool applied, IReadOnlyList<ValidationError> errors, string entry, List<ValidationError> failures, ref int counter)
    {
        if (applied)
        {
            counter++;
            return;
        }

        string pointer = JsonPointer.Member(string.Empty, entry);
        if (errors.Count == 0)
        {
            failures.Add(new ValidationError(pointer, "not_applied", "The entry could not be stored."));
            return;
        }

        failures.AddRange(errors.Select(error => error with { Path = pointer + error.Path }));
    }

    private static void AddText(ZipArchive zip, string name, string text, DateTimeOffset time)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = time;
        using Stream target = entry.Open();
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        target.Write(bytes, 0, bytes.Length);
    }

    private static string Stamp(DateTimeOffset time)
    {
        return time.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    }
}
