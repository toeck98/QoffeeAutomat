using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Core.Config;

/// <summary>
/// The file-based configuration store (PLAN.md D-013, S05, S06): commands, sensors, metric
/// overrides and settings as JSON documents plus script files. It owns an ordered list of
/// layers; today that is the local layer only, so the hub layer of S20 can be added without
/// changing consumers (they use <see cref="Effective"/> and <see cref="Subscribe"/>).
/// </summary>
public sealed class ConfigStore : IConfigStore, IDisposable
{
    private readonly ChangeBroadcaster _broadcaster = new();
    private readonly FileConfigLayer _local;
    private readonly IReadOnlyList<IConfigLayer> _layers;
    private readonly LayeredConfigView _effective;

    /// <summary>Creates a store. Call <see cref="LoadAsync"/> before reading.</summary>
    /// <param name="options">The directories and tuning.</param>
    /// <param name="timeProvider">The clock for <c>updatedAt</c> and the watcher's debounce; null uses the system clock.</param>
    public ConfigStore(ConfigStoreOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _local = new FileConfigLayer(
            options,
            timeProvider ?? TimeProvider.System,
            _broadcaster.Publish,
            id => _effective?.GetDocument<CommandDocument>(id));
        _layers = [_local];
        _effective = new LayeredConfigView(_layers);
    }

    /// <inheritdoc />
    public IReadOnlyList<IConfigLayer> Layers => _layers;

    /// <inheritdoc />
    public IConfigView Effective => _effective;

    /// <inheritdoc />
    public ILocalConfigLayer Local => _local;

    /// <inheritdoc />
    public IReadOnlyList<ConfigProblem> Problems => _local.Problems;

    /// <inheritdoc />
    public Task<ConfigLoadReport> LoadAsync(CancellationToken cancellationToken)
    {
        return _local.LoadAsync(cancellationToken);
    }

    /// <inheritdoc />
    public ConfigSubscription Subscribe()
    {
        return _broadcaster.Subscribe();
    }

    /// <summary>Stops the file watcher and completes every subscription.</summary>
    public void Dispose()
    {
        _local.Dispose();
        _broadcaster.Complete();
    }
}
