using QoffeeAutomat.Core.IO;

namespace QoffeeAutomat.Core.Config;

/// <summary>Settings of <see cref="ConfigStore"/>.</summary>
public sealed class ConfigStoreOptions
{
    /// <summary>The default time the watcher waits for the file system to settle.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>The default number of revisions kept per document.</summary>
    public const int DefaultHistoryLimit = 10;

    /// <summary>The config directory; holds <c>commands/</c>, <c>sensors/</c>, <c>metrics.json</c>, <c>settings.json</c> and <c>.history/</c>.</summary>
    public required string ConfigDirectory { get; init; }

    /// <summary>The scripts directory.</summary>
    public required string ScriptsDirectory { get; init; }

    /// <summary>Time to wait after the last file event before reloading. External edits made within this window count as one.</summary>
    public TimeSpan DebounceInterval { get; init; } = DefaultDebounce;

    /// <summary>The number of revisions of each document that are kept under <c>.history/</c>.</summary>
    public int HistoryLimit { get; init; } = DefaultHistoryLimit;

    /// <summary>Whether a file watcher picks up external edits after loading.</summary>
    public bool EnableWatcher { get; init; } = true;

    /// <summary>The forward migrations, applied in order of schema version.</summary>
    public IReadOnlyList<IDocumentMigration> Migrations { get; init; } = [];

    /// <summary>The file system operations; null uses the real file system. Tests inject faults here.</summary>
    public IFileOperations? FileOperations { get; init; }

    /// <summary>Called for every file that is skipped, also later when the watcher finds a bad edit.</summary>
    public Action<ConfigProblem>? ProblemReporter { get; init; }
}
