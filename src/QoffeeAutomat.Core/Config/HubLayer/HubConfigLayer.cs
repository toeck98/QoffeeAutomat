using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Core.IO;

namespace QoffeeAutomat.Core.Config.HubLayer;

/// <summary>
/// The hub layer on disk and in memory (PLAN.md S20, D-027, D-028, T908). A generation is validated completely, written into the folder
/// <c>config/hub/g&lt;generation&gt;/</c> and becomes active when <c>config/hub/state.json</c> is replaced atomically; the in-memory view follows
/// in the same step. Whatever fails before that leaves the previous generation active. Reads never touch the disk.
/// </summary>
internal sealed class HubConfigLayer : IHubConfigLayer, IDisposable
{
    internal const string LayerName = "hub";
    internal const string FolderName = "hub";

    private const string StateFileName = "state.json";
    private const string GenerationPrefix = "g";
    private const string JsonExtension = ".json";

    private readonly ConfigStoreOptions _options;
    private readonly TimeProvider _time;
    private readonly IFileOperations _files;
    private readonly Action<ConfigChange> _publish;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private volatile Snapshot _snapshot = Snapshot.Empty;
    private volatile IReadOnlyList<ConfigProblem> _problems = [];

    public HubConfigLayer(ConfigStoreOptions options, TimeProvider time, Action<ConfigChange> publish)
    {
        _options = options;
        _time = time;
        _files = options.FileOperations ?? FileOperations.Instance;
        _publish = publish;
        _directory = Path.Combine(Path.GetFullPath(options.ConfigDirectory), FolderName);
    }

    public event EventHandler? StateChanged;

    public string Name => LayerName;

    public bool IsReadOnly => true;

    public HubLayerState State => _snapshot.State;

    public IReadOnlyDictionary<string, string> Variables => _snapshot.Variables;

    public IReadOnlyList<ConfigProblem> Problems => _problems;

    // ------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------

    public T? GetDocument<T>(string id)
        where T : class, IConfigDocument
    {
        KindDescriptor<T> descriptor = KindDescriptors.For<T>();
        return id is not null
            && TryKey(descriptor.Kind, id, out DocKey key)
            && _snapshot.Documents.TryGetValue(key, out HubParsedDocument? document)
            ? document.Document as T
            : null;
    }

    public IReadOnlyList<T> List<T>()
        where T : class, IConfigDocument
    {
        KindDescriptors.For<T>();
        return
        [
            .. _snapshot.Documents
                .Where(pair => pair.Value.Document is T)
                .OrderBy(pair => pair.Key.Id, StringComparer.Ordinal)
                .Select(pair => pair.Value.Document)
                .OfType<T>(),
        ];
    }

    public Task<IReadOnlyList<ScriptInfo>> ListScriptsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Snapshot snapshot = _snapshot;
        DateTimeOffset modified = snapshot.State.AppliedAt ?? DateTimeOffset.UnixEpoch;
        IReadOnlyList<ScriptInfo> scripts =
        [
            .. snapshot.Documents.Values
                .Where(document => document.Script is not null)
                .OrderBy(document => document.Id, StringComparer.Ordinal)
                .Select(document => new ScriptInfo(document.Id, document.Hash, modified)),
        ];
        return Task.FromResult(scripts);
    }

    public Task<ScriptFile?> GetScriptAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Snapshot snapshot = _snapshot;
        if (name is not null
            && snapshot.Documents.TryGetValue(new DocKey(DocumentKinds.Script, name), out HubParsedDocument? document)
            && document.Script is { } text)
        {
            return Task.FromResult<ScriptFile?>(new ScriptFile(name, text, document.Hash, snapshot.State.AppliedAt ?? DateTimeOffset.UnixEpoch));
        }

        return Task.FromResult<ScriptFile?>(null);
    }

    public bool Owns(ConfigKind kind, string id)
    {
        return id is not null && TryKey(kind, id, out DocKey key) && _snapshot.Documents.ContainsKey(key);
    }

    public bool CanCarry(string kind, string id, string hash)
    {
        Snapshot snapshot = _snapshot;
        HubLayerItem? item = snapshot.State.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Kind, kind, StringComparison.Ordinal)
            && string.Equals(candidate.Id, id, StringComparison.Ordinal)
            && string.Equals(candidate.Hash, hash, StringComparison.Ordinal));
        return item is not null
            && (string.Equals(item.State, HubConfigItemStates.SkippedUnsupported, StringComparison.Ordinal)
                || snapshot.Documents.ContainsKey(new DocKey(kind, id)));
    }

    // ------------------------------------------------------------------
    // Loading
    // ------------------------------------------------------------------

    /// <summary>Reads the active generation from disk and sweeps what a crashed apply left behind.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var problems = new List<ConfigProblem>();
            _snapshot = await ReadStoredAsync(problems, cancellationToken).ConfigureAwait(false);
            _problems = problems;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Snapshot> ReadStoredAsync(List<ConfigProblem> problems, CancellationToken cancellationToken)
    {
        string statePath = StatePath;
        if (!File.Exists(statePath))
        {
            // Without a state file no generation was ever committed: whatever is in the folder is the leftover of an apply that did not finish.
            SweepGenerations(keep: null);
            return Snapshot.Empty;
        }

        HubStateFile? stored;
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(statePath, cancellationToken).ConfigureAwait(false);
            stored = JsonSerializer.Deserialize(bytes, HubLayerJsonContext.Default.HubStateFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Report(problems, statePath, ConfigProblemCodes.IoError, "The state of the hub layer could not be read; the hub layer is not used: " + exception.GetType().Name);
            return Snapshot.Empty;
        }

        if (stored is null || stored.Generation <= 0)
        {
            Report(problems, statePath, ConfigProblemCodes.InvalidJson, "The state of the hub layer is not valid; the hub layer is not used.");
            return Snapshot.Empty;
        }

        SweepGenerations(keep: GenerationFolder(stored.Generation));
        SweepTempFiles();

        var documents = new Dictionary<DocKey, HubParsedDocument>();
        var items = new List<HubLayerItem>();
        string generationDirectory = Path.Combine(_directory, GenerationFolder(stored.Generation));
        foreach (HubStateFileItem item in stored.Items)
        {
            items.Add(new HubLayerItem(item.Kind, item.Id, item.Hash, item.State));
            if (!string.Equals(item.State, HubConfigItemStates.Active, StringComparison.Ordinal))
            {
                continue;
            }

            if (!IsSafeId(item.Kind, item.Id))
            {
                Report(problems, statePath, ConfigProblemCodes.InvalidName, "The state of the hub layer names a document with an invalid id; it is ignored.");
                continue;
            }

            string path = Path.Combine(generationDirectory, RelativePath(item.Kind, item.Id));
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Report(problems, path, ConfigProblemCodes.IoError, "The file of a hub document could not be read: " + exception.GetType().Name);
                continue;
            }

            HubReadOutcome outcome = HubDocumentReader.ReadStored(item.Kind, item.Id, item.Hash, bytes, _options.Migrations, _options.PlatformSupport);
            if (outcome.Document is null)
            {
                Report(problems, path, ConfigProblemCodes.InvalidDocument, "A hub document is not valid and is not used: " + string.Join("; ", outcome.Errors));
                continue;
            }

            documents[new DocKey(item.Kind, item.Id)] = outcome.Document;
        }

        var state = new HubLayerState(
            stored.Generation,
            stored.AppliedAt,
            new PolicyInfo(stored.LocalConfig, stored.DirectLink),
            [.. items],
            LastError: null);
        return new Snapshot(state, documents);
    }

    // ------------------------------------------------------------------
    // Applying
    // ------------------------------------------------------------------

    public async Task<HubApplyResult> ApplyAsync(HubGeneration generation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generation);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Snapshot current = _snapshot;
            if (generation.Generation <= current.State.Generation)
            {
                return new HubApplyResult(
                    false,
                    [],
                    $"Generation {generation.Generation} is not newer than the applied generation {current.State.Generation}.");
            }

            if (!IsKnownPolicy(generation.Policy))
            {
                return new HubApplyResult(false, [], "The policy has a value that this agent does not know.");
            }

            Evaluation evaluation = Evaluate(generation, current);
            if (evaluation.Failed)
            {
                return new HubApplyResult(false, evaluation.Results, evaluation.Error);
            }

            DateTimeOffset appliedAt = _time.GetUtcNow();
            var state = new HubLayerState(generation.Generation, appliedAt, generation.Policy, [.. evaluation.Items], LastError: null);
            var next = new Snapshot(state, evaluation.Documents);

            string generationFolder = GenerationFolder(generation.Generation);
            string generationDirectory = Path.Combine(_directory, generationFolder);
            bool committed = false;
            try
            {
                // A leftover of an earlier attempt with the same number is not part of any committed generation.
                TryDeleteDirectory(generationDirectory);
                foreach (HubParsedDocument document in evaluation.Documents.Values.OrderBy(d => d.Kind, StringComparer.Ordinal).ThenBy(d => d.Id, StringComparer.Ordinal))
                {
                    string path = Path.Combine(generationDirectory, RelativePath(document.Kind, document.Id));
                    await AtomicFile.WriteAsync(_files, path, document.Bytes, ownerOnly: true, cancellationToken).ConfigureAwait(false);
                }

                byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(ToFile(state), HubLayerJsonContext.Default.HubStateFile);

                // The commit: from the moment this rename is done the new generation is the active one, also after a crash.
                await AtomicFile.WriteAsync(_files, StatePath, stateBytes, ownerOnly: true, cancellationToken).ConfigureAwait(false);
                committed = true;
            }
            catch (OperationCanceledException) when (!committed)
            {
                TryDeleteDirectory(generationDirectory);
                throw;
            }
#pragma warning disable CA1031 // Whatever goes wrong before the commit leaves the previous generation active; the reason is returned, not thrown.
            catch (Exception exception) when (!committed)
#pragma warning restore CA1031
            {
                TryDeleteDirectory(generationDirectory);
                return new HubApplyResult(false, evaluation.Results, "The generation could not be stored (" + exception.GetType().Name + "); the previous generation stays active.");
            }

            Snapshot previous = _snapshot;
            _snapshot = next;
            SweepGenerations(keep: generationFolder);
            PublishChanges(previous, next);
            RaiseStateChanged();
            return new HubApplyResult(true, evaluation.Results);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Evaluation Evaluate(HubGeneration generation, Snapshot current)
    {
        var results = new List<ConfigItemResult>(generation.Entries.Count);
        var documents = new Dictionary<DocKey, HubParsedDocument>();
        var items = new List<HubLayerItem>(generation.Entries.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int invalid = 0;

        foreach (HubGenerationEntry entry in generation.Entries.OrderBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            if (!seen.Add(entry.Kind + "/" + entry.Id))
            {
                invalid++;
                results.Add(new ConfigItemResult(entry.Kind, entry.Id, ConfigItemStates.Invalid, ["The item is listed more than once."]));
                continue;
            }

            HubReadOutcome outcome = ReadEntry(entry, current);
            switch (outcome.State)
            {
                case ConfigItemStates.Applied:
                    documents[new DocKey(entry.Kind, entry.Id)] = outcome.Document!;
                    items.Add(new HubLayerItem(entry.Kind, entry.Id, entry.Hash, HubConfigItemStates.Active));
                    results.Add(new ConfigItemResult(entry.Kind, entry.Id, ConfigItemStates.Applied));
                    break;
                case ConfigItemStates.SkippedUnsupported:
                    items.Add(new HubLayerItem(entry.Kind, entry.Id, entry.Hash, HubConfigItemStates.SkippedUnsupported));
                    results.Add(new ConfigItemResult(entry.Kind, entry.Id, ConfigItemStates.SkippedUnsupported, outcome.Errors));
                    break;
                default:
                    invalid++;
                    results.Add(new ConfigItemResult(entry.Kind, entry.Id, ConfigItemStates.Invalid, outcome.Errors));
                    break;
            }
        }

        return new Evaluation(
            invalid > 0,
            results,
            documents,
            items,
            invalid > 0 ? $"{invalid} document(s) of generation {generation.Generation} are not valid." : null);
    }

    private HubReadOutcome ReadEntry(HubGenerationEntry entry, Snapshot current)
    {
        if (entry.Body is { } body)
        {
            return HubDocumentReader.Read(entry.Kind, entry.Id, entry.Hash, body, _options.Migrations, _options.PlatformSupport);
        }

        // No body: the document must be the one the current generation has, with the same hash.
        HubLayerItem? existing = current.State.Items.FirstOrDefault(item =>
            string.Equals(item.Kind, entry.Kind, StringComparison.Ordinal)
            && string.Equals(item.Id, entry.Id, StringComparison.Ordinal)
            && string.Equals(item.Hash, entry.Hash, StringComparison.Ordinal));
        if (existing is null)
        {
            return HubReadOutcome.Invalid("The document was not sent and the applied generation does not have this version of it.");
        }

        if (string.Equals(existing.State, HubConfigItemStates.SkippedUnsupported, StringComparison.Ordinal))
        {
            return new HubReadOutcome(null, ConfigItemStates.SkippedUnsupported, ["Not supported on this platform."]);
        }

        return current.Documents.TryGetValue(new DocKey(entry.Kind, entry.Id), out HubParsedDocument? document)
            ? new HubReadOutcome(document, ConfigItemStates.Applied, [])
            : HubReadOutcome.Invalid("The stored document is missing or damaged; the hub has to send it again.");
    }

    public void RecordError(string? error)
    {
        lock (_stateLock)
        {
            Snapshot current = _snapshot;
            if (string.Equals(current.State.LastError, error, StringComparison.Ordinal))
            {
                return;
            }

            _snapshot = new Snapshot(current.State with { LastError = error }, current.Documents);
        }

        RaiseStateChanged();
    }

    // ------------------------------------------------------------------
    // Removing
    // ------------------------------------------------------------------

    public async Task<HubRemoveResult> RemoveAsync(HubLeaveMode mode, ILocalConfigLayer local, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(local);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Snapshot current = _snapshot;
            bool existed = current.State.IsActive || Directory.Exists(_directory);
            int converted = 0;
            var skipped = new List<HubDocumentRef>();
            if (mode == HubLeaveMode.Convert)
            {
                // Scripts first, so that a command that runs one finds it.
                foreach (HubParsedDocument document in current.Documents.Values
                    .Where(d => d.Kind != DocumentKinds.Variables)
                    .OrderBy(d => d.Kind == DocumentKinds.Script ? 0 : 1)
                    .ThenBy(d => d.Kind, StringComparer.Ordinal)
                    .ThenBy(d => d.Id, StringComparer.Ordinal))
                {
                    if (await ConvertAsync(document, local, cancellationToken).ConfigureAwait(false))
                    {
                        converted++;
                    }
                    else
                    {
                        skipped.Add(new HubDocumentRef(document.Kind, document.Id));
                    }
                }
            }

            // The commit of the removal: without the state file the layer does not exist after a restart.
            _files.Delete(StatePath);
            _snapshot = Snapshot.Empty;
            _problems = [];
            TryDeleteDirectory(_directory);
            PublishChanges(current, Snapshot.Empty);
            RaiseStateChanged();
            return new HubRemoveResult(existed, converted, skipped);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<bool> ConvertAsync(HubParsedDocument document, ILocalConfigLayer local, CancellationToken cancellationToken)
    {
        switch (document.Document)
        {
            case CommandDocument command:
                return (await local.SaveAsync(command, 0, cancellationToken).ConfigureAwait(false)).IsApplied;
            case SensorDocument sensor:
                return (await local.SaveAsync(sensor, 0, cancellationToken).ConfigureAwait(false)).IsApplied;
            case ModeGroupDocument mode:
                return (await local.SaveAsync(mode, 0, cancellationToken).ConfigureAwait(false)).IsApplied;
            case MetricOverridesDocument overrides:
                return (await local.SaveAsync(overrides, 0, cancellationToken).ConfigureAwait(false)).IsApplied;
        }

        if (document.Script is { } text)
        {
            // A script of the same name that exists locally stays as it is: the null ETag of a save would overwrite it.
            if (await local.GetScriptAsync(document.Id, cancellationToken).ConfigureAwait(false) is not null)
            {
                return false;
            }

            return (await local.SaveScriptAsync(document.Id, text, null, cancellationToken).ConfigureAwait(false)).IsApplied;
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private string StatePath => Path.Combine(_directory, StateFileName);

    private static string GenerationFolder(long generation) =>
        GenerationPrefix + generation.ToString("D12", System.Globalization.CultureInfo.InvariantCulture);

    private static string RelativePath(string kind, string id)
    {
        return kind switch
        {
            DocumentKinds.Command => Path.Combine("commands", id + JsonExtension),
            DocumentKinds.Sensor => Path.Combine("sensors", id + JsonExtension),
            DocumentKinds.Mode => Path.Combine("modes", id + JsonExtension),
            DocumentKinds.MetricOverrides => "metrics" + JsonExtension,
            DocumentKinds.Variables => Path.Combine("variables", id + JsonExtension),
            _ => Path.Combine("scripts", id),
        };
    }

    private static bool IsSafeId(string kind, string id)
    {
        if (!DocumentKinds.All.Contains(kind))
        {
            return false;
        }

        return kind switch
        {
            DocumentKinds.Script => ScriptNames.IsValid(id),
            DocumentKinds.MetricOverrides => string.Equals(id, DocumentKinds.MetricOverrides, StringComparison.Ordinal),
            _ => ScriptNames.IsValidDocumentId(id),
        };
    }

    private static bool TryKey(ConfigKind kind, string id, out DocKey key)
    {
        switch (kind)
        {
            case ConfigKind.Command:
                key = new DocKey(DocumentKinds.Command, id);
                return true;
            case ConfigKind.Sensor:
                key = new DocKey(DocumentKinds.Sensor, id);
                return true;
            case ConfigKind.Mode:
                key = new DocKey(DocumentKinds.Mode, id);
                return true;
            case ConfigKind.Script:
                key = new DocKey(DocumentKinds.Script, id);
                return true;
            case ConfigKind.MetricOverrides when string.Equals(id, ConfigIds.MetricOverrides, StringComparison.Ordinal):
                key = new DocKey(DocumentKinds.MetricOverrides, DocumentKinds.MetricOverrides);
                return true;
            default:
                key = default;
                return false;
        }
    }

    private static bool IsKnownPolicy(PolicyInfo policy)
    {
        return policy is not null
            && policy.LocalConfig is LocalConfigPolicies.Allow or LocalConfigPolicies.Locked
            && policy.DirectLink is DirectLinkPolicies.Allow or DirectLinkPolicies.Deny;
    }

    private static HubStateFile ToFile(HubLayerState state)
    {
        return new HubStateFile(
            state.Generation,
            state.AppliedAt ?? DateTimeOffset.UnixEpoch,
            state.Policy.LocalConfig,
            state.Policy.DirectLink,
            [.. state.Items.Select(item => new HubStateFileItem(item.Kind, item.Id, item.Hash, item.State))]);
    }

    private void PublishChanges(Snapshot before, Snapshot after)
    {
        foreach ((DocKey key, HubParsedDocument document) in after.Documents)
        {
            int? revision = (document.Document as IConfigDocument)?.Revision;
            if (!before.Documents.TryGetValue(key, out HubParsedDocument? old))
            {
                Publish(key, ConfigChangeType.Added, revision);
            }
            else if (!string.Equals(old.Hash, document.Hash, StringComparison.Ordinal))
            {
                Publish(key, ConfigChangeType.Updated, revision);
            }
        }

        foreach (DocKey key in before.Documents.Keys)
        {
            if (!after.Documents.ContainsKey(key))
            {
                Publish(key, ConfigChangeType.Removed, null);
            }
        }
    }

    private void Publish(DocKey key, ConfigChangeType type, int? revision)
    {
        ConfigKind kind = key.Kind switch
        {
            DocumentKinds.Command => ConfigKind.Command,
            DocumentKinds.Sensor => ConfigKind.Sensor,
            DocumentKinds.Mode => ConfigKind.Mode,
            DocumentKinds.MetricOverrides => ConfigKind.MetricOverrides,
            DocumentKinds.Script => ConfigKind.Script,
            _ => ConfigKind.Hub,
        };
        string id = kind == ConfigKind.MetricOverrides ? ConfigIds.MetricOverrides : key.Id;
        _publish(new ConfigChange(LayerName, kind, id, type, ConfigChangeSource.Hub, revision));
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SweepGenerations(string? keep)
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        try
        {
            foreach (string directory in Directory.EnumerateDirectories(_directory, GenerationPrefix + "*"))
            {
                string name = Path.GetFileName(directory);
                if (!string.Equals(name, keep, StringComparison.Ordinal))
                {
                    TryDeleteDirectory(directory);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A folder of an old generation is harmless and swept again at the next start.
        }
    }

    private void SweepTempFiles()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(_directory, "*" + AtomicFile.TempExtension, SearchOption.AllDirectories))
            {
                if (AtomicFile.IsTempFile(file))
                {
                    _files.Delete(file);
                }
            }
        }
#pragma warning disable CA1031 // A temporary file of a crashed write is harmless; the sweep is repeated at the next start.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _ = exception;
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left for the sweep at the next start; no committed state refers to it.
        }
    }

    private void Report(List<ConfigProblem> problems, string path, string code, string message)
    {
        var problem = new ConfigProblem(ConfigKind.Hub, path, code, message, []);
        problems.Add(problem);
        _options.ProblemReporter?.Invoke(problem);
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private readonly record struct DocKey(string Kind, string Id);

    private sealed record Evaluation(
        bool Failed,
        IReadOnlyList<ConfigItemResult> Results,
        Dictionary<DocKey, HubParsedDocument> Documents,
        List<HubLayerItem> Items,
        string? Error);

    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new(HubLayerState.None, new Dictionary<DocKey, HubParsedDocument>());

        public Snapshot(HubLayerState state, IReadOnlyDictionary<DocKey, HubParsedDocument> documents)
        {
            State = state;
            Documents = documents;

            // Several variables documents merge in the order of their ids; a later one wins (the hub merges them into one, so this is only a safeguard).
            var variables = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (HubParsedDocument document in documents.Values
                .Where(d => d.Variables is not null)
                .OrderBy(d => d.Id, StringComparer.Ordinal))
            {
                foreach ((string name, string value) in document.Variables!)
                {
                    variables[name] = value;
                }
            }

            Variables = variables;
        }

        public HubLayerState State { get; }

        public IReadOnlyDictionary<DocKey, HubParsedDocument> Documents { get; }

        public IReadOnlyDictionary<string, string> Variables { get; }
    }
}
