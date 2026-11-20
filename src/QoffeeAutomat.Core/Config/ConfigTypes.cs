using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Config;

/// <summary>The kinds of item that the config store manages.</summary>
public enum ConfigKind
{
    /// <summary>A command document, <c>config/commands/&lt;id&gt;.json</c>.</summary>
    Command,

    /// <summary>A custom sensor document, <c>config/sensors/&lt;id&gt;.json</c>.</summary>
    Sensor,

    /// <summary>The metric overrides, <c>config/metrics.json</c>.</summary>
    MetricOverrides,

    /// <summary>The agent settings, <c>config/settings.json</c>.</summary>
    Settings,

    /// <summary>A script file in the scripts directory.</summary>
    Script,

    /// <summary>A mode group document, <c>config/modes/&lt;id&gt;.json</c> (T717).</summary>
    Mode,

    /// <summary>Something of the hub layer (T908) that is no effective document: a variables document, or the layer's own state file under <c>config/hub/</c>.</summary>
    Hub,
}

/// <summary>The fixed ids of the documents that exist only once.</summary>
public static class ConfigIds
{
    /// <summary>The id of the agent settings document.</summary>
    public const string Settings = "settings";

    /// <summary>The id of the metric overrides document.</summary>
    public const string MetricOverrides = "metrics";
}

/// <summary>What happened to an item.</summary>
public enum ConfigChangeType
{
    /// <summary>The item did not exist before.</summary>
    Added,

    /// <summary>The item exists and its content changed.</summary>
    Updated,

    /// <summary>The item was removed.</summary>
    Removed,
}

/// <summary>Where a change came from.</summary>
public enum ConfigChangeSource
{
    /// <summary>A save or delete through the store.</summary>
    Api,

    /// <summary>A restore of an earlier revision through the store.</summary>
    Restore,

    /// <summary>Somebody edited a file outside the agent; the file watcher noticed.</summary>
    External,

    /// <summary>The hub layer changed: a generation of the hub was applied, or the layer was removed or its lock changed what the local layer shows (T908).</summary>
    Hub,
}

/// <summary>A change of one item, published on the change stream.</summary>
/// <param name="Layer">The name of the layer in which the change happened.</param>
/// <param name="Kind">The kind of item.</param>
/// <param name="Id">The id of the document, or the file name of a script.</param>
/// <param name="Type">What happened.</param>
/// <param name="Source">Where the change came from.</param>
/// <param name="Revision">The new revision of a document; null for scripts and removals.</param>
public sealed record ConfigChange(
    string Layer,
    ConfigKind Kind,
    string Id,
    ConfigChangeType Type,
    ConfigChangeSource Source,
    int? Revision = null);

/// <summary>The codes of <see cref="ConfigProblem.Code"/>.</summary>
public static class ConfigProblemCodes
{
    /// <summary>The file is not valid JSON or has members of the wrong type.</summary>
    public const string InvalidJson = "invalid_json";

    /// <summary>The document was read but violates the rules of the validator.</summary>
    public const string InvalidDocument = "invalid_document";

    /// <summary>The id inside the document differs from the name of the file.</summary>
    public const string IdMismatch = "id_mismatch";

    /// <summary>The schemaVersion is older than the current one and no migration exists.</summary>
    public const string MigrationMissing = "migration_missing";

    /// <summary>A migration threw.</summary>
    public const string MigrationFailed = "migration_failed";

    /// <summary>The file could not be read.</summary>
    public const string IoError = "io_error";

    /// <summary>The file name is not a valid script name.</summary>
    public const string InvalidName = "invalid_name";
}

/// <summary>
/// A file that the store skipped. The file is never deleted or modified, so the owner can fix
/// it by hand.
/// </summary>
/// <param name="Kind">The kind of item the file would have been.</param>
/// <param name="Path">The full path of the file.</param>
/// <param name="Code">See <see cref="ConfigProblemCodes"/>.</param>
/// <param name="Message">A human-readable explanation.</param>
/// <param name="Errors">The validation errors, when the code is <see cref="ConfigProblemCodes.InvalidDocument"/>.</param>
public sealed record ConfigProblem(
    ConfigKind Kind,
    string Path,
    string Code,
    string Message,
    IReadOnlyList<ValidationError> Errors);

/// <summary>The outcome of loading all documents.</summary>
/// <param name="Loaded">The number of items that were loaded.</param>
/// <param name="Problems">The files that were skipped.</param>
public sealed record ConfigLoadReport(int Loaded, IReadOnlyList<ConfigProblem> Problems);

/// <summary>The outcome of a write operation.</summary>
public enum ChangeStatus
{
    /// <summary>The operation was carried out.</summary>
    Applied,

    /// <summary>The revision (or ETag) the caller expected is not the current one.</summary>
    Conflict,

    /// <summary>The document or name is invalid.</summary>
    Invalid,

    /// <summary>The item does not exist.</summary>
    NotFound,
}

/// <summary>The outcome of saving or restoring a document.</summary>
/// <typeparam name="T">The document type.</typeparam>
/// <param name="Status">What happened.</param>
/// <param name="Document">The stored document, with its new revision, when the status is <see cref="ChangeStatus.Applied"/>.</param>
/// <param name="CurrentRevision">The revision on disk, set on a conflict.</param>
/// <param name="Errors">The validation errors when the status is <see cref="ChangeStatus.Invalid"/>.</param>
public sealed record SaveResult<T>(
    ChangeStatus Status,
    T? Document,
    int? CurrentRevision,
    IReadOnlyList<ValidationError> Errors)
    where T : class
{
    /// <summary>True when the document was stored.</summary>
    public bool IsApplied => Status == ChangeStatus.Applied;
}

/// <summary>The outcome of deleting a document.</summary>
/// <param name="Status">What happened.</param>
/// <param name="CurrentRevision">The revision on disk, set on a conflict.</param>
public sealed record DeleteResult(ChangeStatus Status, int? CurrentRevision = null)
{
    /// <summary>True when the document was removed.</summary>
    public bool IsApplied => Status == ChangeStatus.Applied;
}

/// <summary>The outcome of saving or deleting a script.</summary>
/// <param name="Status">What happened.</param>
/// <param name="ETag">The ETag of the stored script, or the current ETag on a conflict.</param>
/// <param name="Errors">The validation errors when the status is <see cref="ChangeStatus.Invalid"/>.</param>
public sealed record ScriptResult(ChangeStatus Status, string? ETag, IReadOnlyList<ValidationError> Errors)
{
    /// <summary>True when the script was stored or removed.</summary>
    public bool IsApplied => Status == ChangeStatus.Applied;
}

/// <summary>One kept revision of a document.</summary>
/// <param name="Revision">The revision number.</param>
/// <param name="UpdatedAt">The time of that save, when the file records it.</param>
public sealed record RevisionInfo(int Revision, DateTimeOffset? UpdatedAt);

/// <summary>A script file with its content.</summary>
/// <param name="Name">The file name in the scripts directory.</param>
/// <param name="Content">The text of the script (UTF-8, a leading byte order mark is removed).</param>
/// <param name="ETag">SHA-256 of the file bytes in lower-case hex; changes with every edit.</param>
/// <param name="ModifiedAt">The time of the last modification.</param>
public sealed record ScriptFile(string Name, string Content, string ETag, DateTimeOffset ModifiedAt);

/// <summary>A script file in a listing.</summary>
/// <param name="Name">The file name in the scripts directory.</param>
/// <param name="ETag">SHA-256 of the file bytes in lower-case hex.</param>
/// <param name="ModifiedAt">The time of the last modification.</param>
public sealed record ScriptInfo(string Name, string ETag, DateTimeOffset ModifiedAt);
