using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.IO;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Config;

/// <summary>
/// The local layer: documents as JSON files under <c>config/</c>, scripts under <c>scripts/</c>
/// (PLAN.md D-013, S05, S06). Everything that is on disk is mirrored in memory; reads never touch the
/// disk except for script content. Writes are serialised by one gate and are atomic. A file watcher
/// notices edits made outside the agent and publishes one change per edit.
/// </summary>
internal sealed class FileConfigLayer : ILocalConfigLayer, IDisposable
{
    internal const string LayerName = "local";

    private const string JsonExtension = ".json";
    private const string HistoryFolder = ".history";
    private const int MaxScriptBytes = 1024 * 1024;
    private const int ReadAttempts = 4;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(40);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly ConfigStoreOptions _options;
    private readonly TimeProvider _time;
    private readonly IFileOperations _files;
    private readonly Action<ConfigChange> _publish;
    private readonly Func<string, CommandDocument?> _commandLookup;
    private readonly string _configDirectory;
    private readonly string _scriptsDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposed = new();
    private readonly ConcurrentDictionary<EntryKey, Entry> _entries = new();
    private readonly ConcurrentDictionary<string, ConfigProblem> _problems = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _timerGate = new();
    private ITimer? _debounce;
    private bool _watching;
    private bool _isDisposed;

    public FileConfigLayer(
        ConfigStoreOptions options,
        TimeProvider time,
        Action<ConfigChange> publish,
        Func<string, CommandDocument?> commandLookup)
    {
        _options = options;
        _time = time;
        _files = options.FileOperations ?? FileOperations.Instance;
        _publish = publish;
        _commandLookup = commandLookup;
        _configDirectory = Path.GetFullPath(options.ConfigDirectory);
        _scriptsDirectory = Path.GetFullPath(options.ScriptsDirectory);
    }

    public string Name => LayerName;

    public bool IsReadOnly => false;

    public IReadOnlyList<ConfigProblem> Problems =>
        [.. _problems.Values.OrderBy(problem => problem.Path, StringComparer.Ordinal)];

    // ------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------

    public T? GetDocument<T>(string id)
        where T : class, IConfigDocument
    {
        KindDescriptor<T> descriptor = KindDescriptors.For<T>();
        if (id is not null && _entries.TryGetValue(new EntryKey(descriptor.Kind, id), out Entry? entry))
        {
            return entry.Document as T;
        }

        return null;
    }

    public IReadOnlyList<T> List<T>()
        where T : class, IConfigDocument
    {
        KindDescriptor<T> descriptor = KindDescriptors.For<T>();
        return
        [
            .. _entries
                .Where(pair => pair.Key.Kind == descriptor.Kind)
                .OrderBy(pair => pair.Key.Id, StringComparer.Ordinal)
                .Select(pair => pair.Value.Document)
                .OfType<T>(),
        ];
    }

    public Task<IReadOnlyList<ScriptInfo>> ListScriptsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var scripts = new List<ScriptInfo>();
        foreach ((EntryKey key, Entry entry) in _entries)
        {
            if (key.Kind != ConfigKind.Script)
            {
                continue;
            }

            DateTimeOffset modified = new(File.GetLastWriteTimeUtc(Path.Combine(_scriptsDirectory, key.Id)), TimeSpan.Zero);
            scripts.Add(new ScriptInfo(key.Id, entry.Hash, modified));
        }

        scripts.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        return Task.FromResult<IReadOnlyList<ScriptInfo>>(scripts);
    }

    public async Task<ScriptFile?> GetScriptAsync(string name, CancellationToken cancellationToken)
    {
        if (!ScriptNames.IsValid(name))
        {
            return null;
        }

        string path = ScriptPath(name);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        DateTimeOffset modified = new(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        return new ScriptFile(name, DecodeScript(bytes), HashOf(bytes), modified);
    }

    // ------------------------------------------------------------------
    // Loading
    // ------------------------------------------------------------------

    public async Task<ConfigLoadReport> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_configDirectory);
            Directory.CreateDirectory(_scriptsDirectory);
            foreach (KindDescriptor descriptor in KindDescriptors.Documents)
            {
                if (!descriptor.IsSingleton)
                {
                    Directory.CreateDirectory(Path.Combine(_configDirectory, descriptor.Folder));
                }
            }

            SweepTempFiles(_configDirectory, SearchOption.AllDirectories);
            SweepTempFiles(_scriptsDirectory, SearchOption.TopDirectoryOnly);

            _entries.Clear();
            _problems.Clear();

            foreach (string path in EnumerateDocumentFiles())
            {
                await LoadDocumentFileAsync(path, cancellationToken).ConfigureAwait(false);
            }

            foreach (string path in EnumerateScriptFiles())
            {
                await LoadScriptFileAsync(path, cancellationToken).ConfigureAwait(false);
            }

            StartWatching();
            return new ConfigLoadReport(_entries.Count, Problems);
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<string> EnumerateDocumentFiles()
    {
        var paths = new List<string>();
        foreach (KindDescriptor descriptor in KindDescriptors.Documents)
        {
            if (descriptor.IsSingleton)
            {
                string file = Path.Combine(_configDirectory, descriptor.SingletonFileName);
                if (File.Exists(file))
                {
                    paths.Add(file);
                }

                continue;
            }

            string folder = Path.Combine(_configDirectory, descriptor.Folder);
            var inFolder = Directory.EnumerateFiles(folder, "*" + JsonExtension)
                .Where(file => file.EndsWith(JsonExtension, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal);
            paths.AddRange(inFolder);
        }

        return paths;
    }

    private List<string> EnumerateScriptFiles()
    {
        return [.. Directory.EnumerateFiles(_scriptsDirectory).Where(file => !AtomicFile.IsTempFile(file)).Order(StringComparer.Ordinal)];
    }

    private static void SweepTempFiles(string directory, SearchOption option)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*" + AtomicFile.TempExtension, option))
        {
            if (!AtomicFile.IsTempFile(file))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A temporary file of a crashed write; it is harmless and swept at the next start.
            }
        }
    }

    private async Task LoadDocumentFileAsync(string path, CancellationToken cancellationToken)
    {
        Classification? classification = Classify(path);
        if (classification is null)
        {
            return;
        }

        byte[]? bytes = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            Report(classification.Kind, path, ConfigProblemCodes.IoError, "The file could not be read.", []);
            return;
        }

        await ApplyDocumentBytesAsync(classification, path, bytes, publishAs: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadScriptFileAsync(string path, CancellationToken cancellationToken)
    {
        string name = Path.GetFileName(path);
        if (!ScriptNames.IsValid(name))
        {
            Report(ConfigKind.Script, path, ConfigProblemCodes.InvalidName, "The file name is not a valid script name; the file is ignored.", []);
            return;
        }

        byte[]? bytes = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            Report(ConfigKind.Script, path, ConfigProblemCodes.IoError, "The file could not be read.", []);
            return;
        }

        _entries[new EntryKey(ConfigKind.Script, name)] = new Entry(null, HashOf(bytes));
    }

    /// <summary>
    /// Parses and stores the content of a document file. With <paramref name="publishAs"/> set the
    /// change is published (external edits); the initial load publishes nothing.
    /// </summary>
    private async Task ApplyDocumentBytesAsync(
        Classification classification,
        string path,
        byte[] bytes,
        ConfigChangeSource? publishAs,
        CancellationToken cancellationToken)
    {
        KindDescriptor descriptor = classification.Descriptor;
        ParseOutcome outcome = descriptor.Parse(bytes, _options.Migrations);
        if (!outcome.IsValid)
        {
            Report(classification.Kind, path, outcome.ProblemCode ?? ConfigProblemCodes.InvalidDocument, outcome.ProblemMessage ?? "The document is not valid.", outcome.Errors);
            return;
        }

        object document = outcome.Document!;
        string? documentId = descriptor.IdOf(document);
        if (!string.Equals(documentId, classification.Id, StringComparison.Ordinal))
        {
            Report(
                classification.Kind,
                path,
                ConfigProblemCodes.IdMismatch,
                $"The id inside the document ('{documentId}') differs from the file name ('{classification.Id}').",
                []);
            return;
        }

        string hash = HashOf(bytes);
        if (outcome.MigratedBytes is { } migrated)
        {
            // Keep the original next to the history, then upgrade the file in place.
            await BackupBeforeMigrationAsync(descriptor, classification.Id, bytes, outcome.FromVersion, document, cancellationToken).ConfigureAwait(false);
            await AtomicFile.WriteAsync(_files, path, migrated, ownerOnly: false, cancellationToken).ConfigureAwait(false);
            hash = HashOf(migrated);
        }

        var key = new EntryKey(classification.Kind, classification.Id);
        bool existed = _entries.ContainsKey(key);
        _entries[key] = new Entry(document, hash);
        _problems.TryRemove(path, out _);

        if (publishAs is { } source)
        {
            _publish(new ConfigChange(
                LayerName,
                classification.Kind,
                classification.Id,
                existed ? ConfigChangeType.Updated : ConfigChangeType.Added,
                source,
                (document as IConfigDocument)?.Revision));
        }
    }

    private async Task BackupBeforeMigrationAsync(
        KindDescriptor descriptor,
        string id,
        byte[] originalBytes,
        int fromVersion,
        object document,
        CancellationToken cancellationToken)
    {
        int revision = (document as IConfigDocument)?.Revision ?? 0;
        string name = revision.ToString("D6", CultureInfo.InvariantCulture)
            + ".v" + fromVersion.ToString(CultureInfo.InvariantCulture) + ".bak" + JsonExtension;
        string path = Path.Combine(HistoryDirectory(descriptor, id), name);
        try
        {
            await AtomicFile.WriteAsync(_files, path, originalBytes, ownerOnly: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The backup is a courtesy; the migration itself is still safe because it is atomic.
        }
    }

    // ------------------------------------------------------------------
    // Writing documents
    // ------------------------------------------------------------------

    public async Task<SaveResult<T>> SaveAsync<T>(T document, int? expectedRevision, CancellationToken cancellationToken)
        where T : class, IConfigDocument
    {
        ArgumentNullException.ThrowIfNull(document);
        KindDescriptor<T> descriptor = KindDescriptors.For<T>();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SaveCoreAsync(descriptor, document, expectedRevision, ConfigChangeSource.Api, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SaveResult<T>> SaveCoreAsync<T>(
        KindDescriptor<T> descriptor,
        T document,
        int? expectedRevision,
        ConfigChangeSource source,
        CancellationToken cancellationToken)
        where T : class, IConfigDocument
    {
        string? id = descriptor.IdOf(document);

        // The id becomes a file name, so it is checked before anything else touches a path.
        if (!descriptor.IsSingleton && !ScriptNames.IsValidDocumentId(id))
        {
            ValidationResult early = descriptor.Validate(descriptor.Stamp(document, 0, _time.GetUtcNow()), null);
            IReadOnlyList<ValidationError> errors = early.Errors.Count > 0
                ? early.Errors
                : [new ValidationError("/id", ValidationCodes.Invalid, "The id must match ^[a-z][a-z0-9_]{0,62}$.")];
            return Rejected<T>(ChangeStatus.Invalid, null, errors);
        }

        id ??= descriptor.SingletonId!;

        // The generated command of a mode group (T717) has the id mode_<groupId>; a stored command must not shadow it.
        if (descriptor.Kind == ConfigKind.Command
            && ModeNames.TryGetGroupId(id, out string shadowedGroup)
            && _entries.ContainsKey(new EntryKey(ConfigKind.Mode, shadowedGroup)))
        {
            return Rejected<T>(
                ChangeStatus.Invalid,
                null,
                [new ValidationError("/id", ValidationCodes.Conflict, $"This id is used by the command that the mode group '{shadowedGroup}' provides.")]);
        }

        var key = new EntryKey(descriptor.Kind, id);
        T? existing = _entries.TryGetValue(key, out Entry? entry) ? entry.Document as T : null;
        int storedRevision = existing?.Revision ?? 0;

        if (expectedRevision is { } expected && expected != (existing is null ? 0 : storedRevision))
        {
            return Rejected<T>(ChangeStatus.Conflict, existing is null ? null : storedRevision, []);
        }

        int baseRevision = Math.Max(storedRevision, HighestHistoryRevision(descriptor, id));
        T stamped = descriptor.Stamp(document, baseRevision + 1, _time.GetUtcNow());

        ValidationResult validation = descriptor.Validate(stamped, _commandLookup);
        if (!validation.IsValid)
        {
            return Rejected<T>(ChangeStatus.Invalid, null, validation.Errors);
        }

        byte[] bytes = descriptor.Serialize(stamped);
        string path = DocumentPath(descriptor, id);
        await AtomicFile.WriteAsync(_files, path, bytes, ownerOnly: false, cancellationToken).ConfigureAwait(false);

        _entries[key] = new Entry(stamped, HashOf(bytes));
        _problems.TryRemove(path, out _);

        await WriteHistoryAsync(descriptor, id, stamped.Revision ?? 0, bytes, cancellationToken).ConfigureAwait(false);

        _publish(new ConfigChange(
            LayerName,
            descriptor.Kind,
            id,
            existing is null ? ConfigChangeType.Added : ConfigChangeType.Updated,
            source,
            stamped.Revision));

        return new SaveResult<T>(ChangeStatus.Applied, stamped, null, []);
    }

    private static SaveResult<T> Rejected<T>(ChangeStatus status, int? currentRevision, IReadOnlyList<ValidationError> errors)
        where T : class
    {
        return new SaveResult<T>(status, null, currentRevision, errors);
    }

    public async Task<DeleteResult> DeleteAsync<T>(string id, int? expectedRevision, CancellationToken cancellationToken)
        where T : class, IConfigDocument
    {
        KindDescriptor<T> descriptor = KindDescriptors.For<T>();
        if (descriptor.IsSingleton)
        {
            id = descriptor.SingletonId!;
        }
        else if (!ScriptNames.IsValidDocumentId(id))
        {
            return new DeleteResult(ChangeStatus.NotFound);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = new EntryKey(descriptor.Kind, id);
            if (!_entries.TryGetValue(key, out Entry? entry))
            {
                return new DeleteResult(ChangeStatus.NotFound);
            }

            int? revision = (entry.Document as IConfigDocument)?.Revision;
            if (expectedRevision is { } expected && expected != (revision ?? 0))
            {
                return new DeleteResult(ChangeStatus.Conflict, revision);
            }

            string path = DocumentPath(descriptor, id);
            _files.Delete(path);
            _entries.TryRemove(key, out _);
            _problems.TryRemove(path, out _);

            _publish(new ConfigChange(LayerName, descriptor.Kind, id, ConfigChangeType.Removed, ConfigChangeSource.Api));
            return new DeleteResult(ChangeStatus.Applied);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------
    // History
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<RevisionInfo>> GetHistoryAsync<T>(string id, CancellationToken cancellationToken)
        where T : class, IConfigDocument
    {
        KindDescriptor<T> descriptor = KindDescriptors.For<T>();
        if (descriptor.IsSingleton)
        {
            id = descriptor.SingletonId!;
        }
        else if (!ScriptNames.IsValidDocumentId(id))
        {
            return [];
        }

        return await ReadHistoryAsync(descriptor, id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<RevisionInfo>> ReadHistoryAsync(KindDescriptor descriptor, string id, CancellationToken cancellationToken)
    {
        var revisions = new List<RevisionInfo>();
        string directory = HistoryDirectory(descriptor, id);
        if (!Directory.Exists(directory))
        {
            return revisions;
        }

        foreach (string file in Directory.EnumerateFiles(directory))
        {
            if (!TryParseHistoryRevision(Path.GetFileName(file), out int revision))
            {
                continue;
            }

            revisions.Add(new RevisionInfo(revision, await ReadUpdatedAtAsync(file, cancellationToken).ConfigureAwait(false)));
        }

        revisions.Sort((left, right) => right.Revision.CompareTo(left.Revision));
        return revisions;
    }

    private static async Task<DateTimeOffset?> ReadUpdatedAtAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(StripBom(bytes)) is JsonObject root
                && root["updatedAt"] is JsonValue value
                && value.TryGetValue(out string? text)
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
            {
                return parsed;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // An unreadable history file is listed without a time.
        }

        return null;
    }

    public async Task<SaveResult<T>> RestoreAsync<T>(string id, int revision, int? expectedRevision, CancellationToken cancellationToken)
        where T : class, IConfigDocument
    {
        KindDescriptor<T> descriptor = KindDescriptors.For<T>();
        if (descriptor.IsSingleton)
        {
            id = descriptor.SingletonId!;
        }
        else if (!ScriptNames.IsValidDocumentId(id))
        {
            return Rejected<T>(ChangeStatus.NotFound, null, []);
        }

        string file = Path.Combine(HistoryDirectory(descriptor, id), HistoryFileName(revision));
        if (revision < 0 || !File.Exists(file))
        {
            return Rejected<T>(ChangeStatus.NotFound, null, []);
        }

        byte[]? bytes = await ReadFileAsync(file, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return Rejected<T>(ChangeStatus.NotFound, null, []);
        }

        ParseOutcome outcome = descriptor.Parse(bytes, _options.Migrations);
        if (outcome.Document is not T restored)
        {
            IReadOnlyList<ValidationError> errors = outcome.Errors.Count > 0
                ? outcome.Errors
                : [new ValidationError("/", ValidationCodes.Invalid, outcome.ProblemMessage ?? "The kept revision cannot be read.")];
            return Rejected<T>(ChangeStatus.Invalid, null, errors);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SaveCoreAsync(descriptor, restored, expectedRevision, ConfigChangeSource.Restore, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteHistoryAsync(KindDescriptor descriptor, string id, int revision, byte[] bytes, CancellationToken cancellationToken)
    {
        string directory = HistoryDirectory(descriptor, id);
        try
        {
            await AtomicFile.WriteAsync(_files, Path.Combine(directory, HistoryFileName(revision)), bytes, ownerOnly: false, cancellationToken).ConfigureAwait(false);

            var kept = new List<int>();
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if (TryParseHistoryRevision(Path.GetFileName(file), out int existing))
                {
                    kept.Add(existing);
                }
            }

            kept.Sort();
            int excess = kept.Count - Math.Max(1, _options.HistoryLimit);
            for (int index = 0; index < excess; index++)
            {
                _files.Delete(Path.Combine(directory, HistoryFileName(kept[index])));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The document itself is safely stored; only its history is incomplete.
        }
    }

    private int HighestHistoryRevision(KindDescriptor descriptor, string id)
    {
        string directory = HistoryDirectory(descriptor, id);
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        int highest = 0;
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            if (TryParseHistoryRevision(Path.GetFileName(file), out int revision) && revision > highest)
            {
                highest = revision;
            }
        }

        return highest;
    }

    private static string HistoryFileName(int revision) =>
        revision.ToString("D6", CultureInfo.InvariantCulture) + JsonExtension;

    private static bool TryParseHistoryRevision(string fileName, out int revision)
    {
        revision = 0;
        if (!fileName.EndsWith(JsonExtension, StringComparison.Ordinal))
        {
            return false;
        }

        string stem = fileName[..^JsonExtension.Length];
        return stem.Length > 0
            && stem.All(char.IsAsciiDigit)
            && int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out revision);
    }

    // ------------------------------------------------------------------
    // Scripts
    // ------------------------------------------------------------------

    public async Task<ScriptResult> SaveScriptAsync(string name, string content, string? expectedETag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        IReadOnlyList<ValidationError> nameErrors = ScriptNames.Validate(name, "/name");
        if (nameErrors.Count > 0)
        {
            return new ScriptResult(ChangeStatus.Invalid, null, nameErrors);
        }

        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        if (bytes.Length > MaxScriptBytes)
        {
            return new ScriptResult(
                ChangeStatus.Invalid,
                null,
                [new ValidationError("/content", ValidationCodes.Invalid, "A script must not be larger than 1 MiB.")]);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = new EntryKey(ConfigKind.Script, name);
            bool exists = _entries.TryGetValue(key, out Entry? entry);
            if (expectedETag is not null && (!exists || !string.Equals(entry!.Hash, expectedETag, StringComparison.Ordinal)))
            {
                return new ScriptResult(ChangeStatus.Conflict, exists ? entry!.Hash : null, []);
            }

            string path = ScriptPath(name);
            await AtomicFile.WriteAsync(_files, path, bytes, ownerOnly: false, cancellationToken).ConfigureAwait(false);

            string hash = HashOf(bytes);
            _entries[key] = new Entry(null, hash);
            _publish(new ConfigChange(LayerName, ConfigKind.Script, name, exists ? ConfigChangeType.Updated : ConfigChangeType.Added, ConfigChangeSource.Api));
            return new ScriptResult(ChangeStatus.Applied, hash, []);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ScriptResult> DeleteScriptAsync(string name, string? expectedETag, CancellationToken cancellationToken)
    {
        IReadOnlyList<ValidationError> nameErrors = ScriptNames.Validate(name, "/name");
        if (nameErrors.Count > 0)
        {
            return new ScriptResult(ChangeStatus.Invalid, null, nameErrors);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = new EntryKey(ConfigKind.Script, name);
            if (!_entries.TryGetValue(key, out Entry? entry))
            {
                return new ScriptResult(ChangeStatus.NotFound, null, []);
            }

            if (expectedETag is not null && !string.Equals(entry.Hash, expectedETag, StringComparison.Ordinal))
            {
                return new ScriptResult(ChangeStatus.Conflict, entry.Hash, []);
            }

            _files.Delete(ScriptPath(name));
            _entries.TryRemove(key, out _);
            _publish(new ConfigChange(LayerName, ConfigKind.Script, name, ConfigChangeType.Removed, ConfigChangeSource.Api));
            return new ScriptResult(ChangeStatus.Applied, null, []);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------
    // File watcher
    // ------------------------------------------------------------------

    private void StartWatching()
    {
        if (_watching || !_options.EnableWatcher || _isDisposed)
        {
            return;
        }

        try
        {
            Watch(_configDirectory, includeSubdirectories: true);
            Watch(_scriptsDirectory, includeSubdirectories: false);
            _watching = true;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException or PlatformNotSupportedException)
        {
            Report(ConfigKind.Settings, _configDirectory, ConfigProblemCodes.IoError, "Changes made outside the agent are not detected: " + exception.Message, []);
        }
    }

    private void Watch(string directory, bool includeSubdirectories)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            Filter = "*",
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
        };

        watcher.Created += OnFileEvent;
        watcher.Changed += OnFileEvent;
        watcher.Deleted += OnFileEvent;
        watcher.Renamed += OnFileRenamed;
        watcher.Error += OnWatcherError;
        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        Enqueue(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        Enqueue(e.OldFullPath);
        Enqueue(e.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // The event buffer overflowed, so single events are lost: compare everything with the disk.
        Enqueue(RescanMarker);
    }

    private const string RescanMarker = "*";

    private void Enqueue(string path)
    {
        if (_isDisposed)
        {
            return;
        }

        if (path != RescanMarker && Classify(path) is null && !IsScriptPath(path))
        {
            return;
        }

        _pending[path] = 0;
        lock (_timerGate)
        {
            if (_isDisposed)
            {
                return;
            }

            _debounce ??= _time.CreateTimer(OnDebounceElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _debounce.Change(_options.DebounceInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        _ = Task.Run(ProcessPendingAsync);
    }

    private async Task ProcessPendingAsync()
    {
        try
        {
            await ProcessPendingCoreAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The layer was disposed while a reload was queued.
        }
    }

    private async Task ProcessPendingCoreAsync()
    {
        try
        {
            await _gate.WaitAsync(_disposed.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var paths = _pending.Keys.ToList();
            foreach (string path in paths)
            {
                _pending.TryRemove(path, out _);
            }

            if (paths.Remove(RescanMarker))
            {
                paths.AddRange(AllKnownPaths());
            }

            foreach (string path in paths.Distinct(StringComparer.Ordinal))
            {
                await ProcessExternalAsync(path, _disposed.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
#pragma warning disable CA1031 // The watcher runs unobserved; a failure must not take the process down.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Report(ConfigKind.Settings, _configDirectory, ConfigProblemCodes.IoError, "Reloading after an external change failed: " + exception.Message, []);
        }
        finally
        {
            if (!_isDisposed)
            {
                _gate.Release();
            }
        }
    }

    private List<string> AllKnownPaths()
    {
        var paths = new List<string>(EnumerateDocumentFiles());
        paths.AddRange(EnumerateScriptFiles());
        foreach (EntryKey key in _entries.Keys)
        {
            if (key.Kind == ConfigKind.Script)
            {
                paths.Add(ScriptPath(key.Id));
            }
            else if (KindDescriptors.For(key.Kind) is { } descriptor)
            {
                paths.Add(DocumentPath(descriptor, key.Id));
            }
        }

        return paths;
    }

    /// <summary>
    /// Brings the memory in line with one file after an external event. Our own writes are
    /// recognised by their content hash, so they never produce a second change.
    /// </summary>
    private async Task ProcessExternalAsync(string path, CancellationToken cancellationToken)
    {
        Classification? classification = Classify(path);
        if (classification is null && IsScriptPath(path))
        {
            await ProcessExternalScriptAsync(path, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (classification is null)
        {
            return;
        }

        var key = new EntryKey(classification.Kind, classification.Id);
        if (!File.Exists(path))
        {
            _problems.TryRemove(path, out _);
            if (_entries.TryRemove(key, out _))
            {
                _publish(new ConfigChange(LayerName, classification.Kind, classification.Id, ConfigChangeType.Removed, ConfigChangeSource.External));
            }

            return;
        }

        byte[]? bytes = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            Report(classification.Kind, path, ConfigProblemCodes.IoError, "The file could not be read.", []);
            return;
        }

        if (_entries.TryGetValue(key, out Entry? existing) && string.Equals(existing.Hash, HashOf(bytes), StringComparison.Ordinal))
        {
            return;
        }

        await ApplyDocumentBytesAsync(classification, path, bytes, ConfigChangeSource.External, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessExternalScriptAsync(string path, CancellationToken cancellationToken)
    {
        string name = Path.GetFileName(path);
        if (!ScriptNames.IsValid(name))
        {
            return;
        }

        var key = new EntryKey(ConfigKind.Script, name);
        if (!File.Exists(path))
        {
            if (_entries.TryRemove(key, out _))
            {
                _publish(new ConfigChange(LayerName, ConfigKind.Script, name, ConfigChangeType.Removed, ConfigChangeSource.External));
            }

            return;
        }

        byte[]? bytes = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return;
        }

        string hash = HashOf(bytes);
        bool existed = _entries.TryGetValue(key, out Entry? existing);
        if (existed && string.Equals(existing!.Hash, hash, StringComparison.Ordinal))
        {
            return;
        }

        _entries[key] = new Entry(null, hash);
        _publish(new ConfigChange(LayerName, ConfigKind.Script, name, existed ? ConfigChangeType.Updated : ConfigChangeType.Added, ConfigChangeSource.External));
    }

    // ------------------------------------------------------------------
    // Paths
    // ------------------------------------------------------------------

    private string DocumentPath(KindDescriptor descriptor, string id)
    {
        return descriptor.IsSingleton
            ? Path.Combine(_configDirectory, descriptor.SingletonFileName)
            : Path.Combine(_configDirectory, descriptor.Folder, id + JsonExtension);
    }

    private string HistoryDirectory(KindDescriptor descriptor, string id)
    {
        return Path.Combine(_configDirectory, HistoryFolder, descriptor.Folder, id);
    }

    /// <summary>Combines the scripts directory and a name that was validated by <see cref="ScriptNames"/>.</summary>
    private string ScriptPath(string name)
    {
        string path = Path.GetFullPath(Path.Combine(_scriptsDirectory, name));
        string root = _scriptsDirectory.EndsWith(Path.DirectorySeparatorChar) ? _scriptsDirectory : _scriptsDirectory + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A script path left the scripts directory.");
        }

        return path;
    }

    private bool IsScriptPath(string path)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        return directory is not null
            && string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar), _scriptsDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal)
            && !AtomicFile.IsTempFile(path);
    }

    /// <summary>Maps a path inside the config directory to a document kind and id; null for anything else (history, temporary files, strangers).</summary>
    private Classification? Classify(string path)
    {
        string full = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(_configDirectory, full);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        string[] parts = relative.Split(Path.DirectorySeparatorChar);
        if (parts[0] == HistoryFolder || !parts[^1].EndsWith(JsonExtension, StringComparison.Ordinal))
        {
            return null;
        }

        foreach (KindDescriptor descriptor in KindDescriptors.Documents)
        {
            if (descriptor.IsSingleton)
            {
                if (parts.Length == 1 && string.Equals(parts[0], descriptor.SingletonFileName, StringComparison.Ordinal))
                {
                    return new Classification(descriptor, descriptor.Kind, descriptor.SingletonId!);
                }
            }
            else if (parts.Length == 2 && string.Equals(parts[0], descriptor.Folder, StringComparison.Ordinal))
            {
                string id = parts[1][..^JsonExtension.Length];
                return new Classification(descriptor, descriptor.Kind, id);
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<byte[]?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= ReadAttempts; attempt++)
        {
            try
            {
                return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An editor may still hold the file; try again shortly.
                if (attempt == ReadAttempts)
                {
                    return null;
                }

                await Task.Delay(ReadRetryDelay, _time, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private void Report(ConfigKind kind, string path, string code, string message, IReadOnlyList<ValidationError> errors)
    {
        var problem = new ConfigProblem(kind, path, code, message, errors);
        _problems[path] = problem;
        _options.ProblemReporter?.Invoke(problem);
    }

    private static string HashOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ReadOnlySpan<byte> StripBom(byte[] bytes)
    {
        ReadOnlySpan<byte> span = bytes;
        return span.StartsWith(new ReadOnlySpan<byte>(Utf8Bom)) ? span[Utf8Bom.Length..] : span;
    }

    private static string DecodeScript(byte[] bytes) => Encoding.UTF8.GetString(StripBom(bytes));

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        lock (_timerGate)
        {
            _isDisposed = true;
            _debounce?.Dispose();
        }

        _disposed.Cancel();
        foreach (FileSystemWatcher watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
        _disposed.Dispose();
        _gate.Dispose();
    }

    private readonly record struct EntryKey(ConfigKind Kind, string Id);

    private sealed record Entry(object? Document, string Hash);

    private sealed record Classification(KindDescriptor Descriptor, ConfigKind Kind, string Id);
}
