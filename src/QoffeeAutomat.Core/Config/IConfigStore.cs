using System.Threading.Channels;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config.HubLayer;

namespace QoffeeAutomat.Core.Config;

/// <summary>
/// A read-only view of configuration: command, sensor, metric override and settings documents
/// plus script files. Both a single layer and the effective (merged) view implement it, so
/// consumers do not care how many layers exist.
/// </summary>
public interface IConfigView
{
    /// <summary>Reads a document from memory.</summary>
    /// <typeparam name="T">
    /// <see cref="CommandDocument"/>, <see cref="SensorDocument"/>, <see cref="MetricOverridesDocument"/>
    /// or <see cref="AgentSettingsDocument"/>.
    /// </typeparam>
    /// <param name="id">The document id; <see cref="ConfigIds"/> holds the ids of the documents that exist once.</param>
    /// <returns>The document, or null when there is none.</returns>
    T? GetDocument<T>(string id)
        where T : class, IConfigDocument;

    /// <summary>Lists the documents of one type, ordered by id.</summary>
    /// <typeparam name="T">The document type, see <see cref="GetDocument{T}"/>.</typeparam>
    /// <returns>A snapshot of the documents.</returns>
    IReadOnlyList<T> List<T>()
        where T : class, IConfigDocument;

    /// <summary>Lists the script files, ordered by name.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The scripts without their content.</returns>
    Task<IReadOnlyList<ScriptInfo>> ListScriptsAsync(CancellationToken cancellationToken);

    /// <summary>Reads a script file.</summary>
    /// <param name="name">The file name; an invalid name (path separators, <c>..</c>) yields null without touching the disk.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The script, or null when it does not exist.</returns>
    Task<ScriptFile?> GetScriptAsync(string name, CancellationToken cancellationToken);
}

/// <summary>One layer of the configuration (PLAN.md S20: the local layer now, the hub layer later).</summary>
public interface IConfigLayer : IConfigView
{
    /// <summary>The name of the layer, <c>local</c> or later <c>hub</c>.</summary>
    string Name { get; }

    /// <summary>True when the layer cannot be changed through the store (the hub layer).</summary>
    bool IsReadOnly { get; }
}

/// <summary>The writable local layer: documents on disk under <c>config/</c> and scripts under <c>scripts/</c>.</summary>
public interface ILocalConfigLayer : IConfigLayer
{
    /// <summary>
    /// Validates and stores a document. The store sets <c>schemaVersion</c>, <c>revision</c>
    /// (previous revision plus one), <c>updatedAt</c> and <c>origin</c>; the values in the argument
    /// are ignored. The write is atomic and the new revision is kept in the history.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="document">The document.</param>
    /// <param name="expectedRevision">
    /// Optimistic concurrency: when set, the save fails with <see cref="ChangeStatus.Conflict"/>
    /// unless it equals the stored revision. Zero means "must not exist yet". Null skips the check.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    Task<SaveResult<T>> SaveAsync<T>(T document, int? expectedRevision, CancellationToken cancellationToken)
        where T : class, IConfigDocument;

    /// <summary>Deletes a document. Its history stays, so it can be restored.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="id">The document id.</param>
    /// <param name="expectedRevision">See <see cref="SaveAsync{T}"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    Task<DeleteResult> DeleteAsync<T>(string id, int? expectedRevision, CancellationToken cancellationToken)
        where T : class, IConfigDocument;

    /// <summary>Lists the kept revisions of a document, newest first.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="id">The document id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The revisions; empty when none are kept.</returns>
    Task<IReadOnlyList<RevisionInfo>> GetHistoryAsync<T>(string id, CancellationToken cancellationToken)
        where T : class, IConfigDocument;

    /// <summary>Stores an earlier revision as a new revision.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="id">The document id.</param>
    /// <param name="revision">The revision to restore.</param>
    /// <param name="expectedRevision">See <see cref="SaveAsync{T}"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result; <see cref="ChangeStatus.NotFound"/> when the revision is not kept.</returns>
    Task<SaveResult<T>> RestoreAsync<T>(string id, int revision, int? expectedRevision, CancellationToken cancellationToken)
        where T : class, IConfigDocument;

    /// <summary>Stores a script file. The name is validated, so a path never leaves the scripts directory.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="content">The script text; written as UTF-8 without byte order mark.</param>
    /// <param name="expectedETag">When set, the save fails with a conflict unless it equals the ETag of the stored script.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    Task<ScriptResult> SaveScriptAsync(string name, string content, string? expectedETag, CancellationToken cancellationToken);

    /// <summary>Deletes a script file.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="expectedETag">When set, the delete fails with a conflict unless it equals the stored ETag.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    Task<ScriptResult> DeleteScriptAsync(string name, string? expectedETag, CancellationToken cancellationToken);
}

/// <summary>
/// The configuration of the agent as an ordered list of layers (highest priority first) with an
/// effective view. Consumers read <see cref="Effective"/> and subscribe to changes; writes go to
/// <see cref="Local"/>. When the hub layer of PLAN.md S20 arrives it joins <see cref="Layers"/> and
/// consumers stay unchanged.
/// </summary>
public interface IConfigStore
{
    /// <summary>The layers, highest priority first: the hub layer, then the local layer.</summary>
    IReadOnlyList<IConfigLayer> Layers { get; }

    /// <summary>The merged view: on equal kind and id the first layer wins; a locked local layer does not take part (except for the settings).</summary>
    IConfigView Effective { get; }

    /// <summary>The writable local layer.</summary>
    ILocalConfigLayer Local { get; }

    /// <summary>The read-only hub layer under <c>config/hub/</c> (PLAN.md S20, D-028); empty while no hub manages this agent.</summary>
    IHubConfigLayer Hub { get; }

    /// <summary>The files that are currently skipped because they are invalid.</summary>
    IReadOnlyList<ConfigProblem> Problems { get; }

    /// <summary>
    /// Loads and validates every document. Invalid files are skipped and reported, never
    /// deleted. Starts the file watcher when it is enabled.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What was loaded and what was skipped.</returns>
    Task<ConfigLoadReport> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Starts receiving changes. Registration happens immediately, so nothing between this call and the first read is lost.</summary>
    /// <returns>A subscription; dispose it to stop receiving.</returns>
    ConfigSubscription Subscribe();
}

/// <summary>A stream of <see cref="ConfigChange"/> values for one subscriber.</summary>
public sealed class ConfigSubscription : IDisposable
{
    private readonly Action<ConfigSubscription> _onDispose;
    private readonly Channel<ConfigChange> _channel;

    internal ConfigSubscription(Action<ConfigSubscription> onDispose)
    {
        _onDispose = onDispose;
        _channel = Channel.CreateBounded<ConfigChange>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>The changes in the order they happened. The channel completes when the subscription or the store is disposed.</summary>
    public ChannelReader<ConfigChange> Changes => _channel.Reader;

    /// <inheritdoc />
    public void Dispose()
    {
        _onDispose(this);
        _channel.Writer.TryComplete();
    }

    internal void Post(ConfigChange change)
    {
        _channel.Writer.TryWrite(change);
    }

    internal void Complete()
    {
        _channel.Writer.TryComplete();
    }
}
