using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config.HubLayer;

namespace QoffeeAutomat.Core.Config;

/// <summary>
/// The file-based configuration store (PLAN.md D-013, S05, S06): commands, sensors, metric
/// overrides and settings as JSON documents plus script files. It owns an ordered list of
/// layers: the hub layer (<c>config/hub/</c>, read-only, T908) in front of the local layer, so
/// consumers use <see cref="Effective"/> and <see cref="Subscribe"/> and do not care how many layers exist.
/// </summary>
public sealed class ConfigStore : IConfigStore, IDisposable
{
    private readonly ChangeBroadcaster _broadcaster = new();
    private readonly FileConfigLayer _local;
    private readonly HubConfigLayer _hub;
    private readonly IReadOnlyList<IConfigLayer> _layers;
    private readonly LayeredConfigView _effective;
    private bool _wasLocked;

    /// <summary>Creates a store. Call <see cref="LoadAsync"/> before reading.</summary>
    /// <param name="options">The directories and tuning.</param>
    /// <param name="timeProvider">The clock for <c>updatedAt</c> and the watcher's debounce; null uses the system clock.</param>
    public ConfigStore(ConfigStoreOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        TimeProvider time = timeProvider ?? TimeProvider.System;
        _local = new FileConfigLayer(
            options,
            time,
            _broadcaster.Publish,
            id => _effective?.GetDocument<CommandDocument>(id));
        _hub = new HubConfigLayer(options, time, _broadcaster.Publish);
        _layers = [_hub, _local];
        _effective = new LayeredConfigView(_layers, () => _hub.State.LocalConfigLocked);
        _hub.StateChanged += OnHubStateChanged;
    }

    /// <inheritdoc />
    public IReadOnlyList<IConfigLayer> Layers => _layers;

    /// <inheritdoc />
    public IConfigView Effective => _effective;

    /// <inheritdoc />
    public ILocalConfigLayer Local => _local;

    /// <inheritdoc />
    public IHubConfigLayer Hub => _hub;

    /// <inheritdoc />
    public IReadOnlyList<ConfigProblem> Problems => [.. _local.Problems, .. _hub.Problems];

    /// <inheritdoc />
    public async Task<ConfigLoadReport> LoadAsync(CancellationToken cancellationToken)
    {
        ConfigLoadReport local = await _local.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _hub.LoadAsync(cancellationToken).ConfigureAwait(false);
        _wasLocked = _hub.State.LocalConfigLocked;
        return local with { Problems = Problems };
    }

    /// <inheritdoc />
    public ConfigSubscription Subscribe()
    {
        return _broadcaster.Subscribe();
    }

    /// <summary>Stops the file watcher and completes every subscription.</summary>
    public void Dispose()
    {
        _hub.StateChanged -= OnHubStateChanged;
        _local.Dispose();
        _hub.Dispose();
        _broadcaster.Complete();
    }

    /// <summary>
    /// When the lock of the local configuration switches, the local documents appear or disappear from the effective view although no
    /// local file changed; one change per local item tells the engines to read the effective view again.
    /// </summary>
    private void OnHubStateChanged(object? sender, EventArgs e)
    {
        bool locked = _hub.State.LocalConfigLocked;
        if (locked == _wasLocked)
        {
            return;
        }

        _wasLocked = locked;
        ConfigChangeType type = locked ? ConfigChangeType.Removed : ConfigChangeType.Added;
        PublishLocal<CommandDocument>(ConfigKind.Command, type);
        PublishLocal<SensorDocument>(ConfigKind.Sensor, type);
        PublishLocal<ModeGroupDocument>(ConfigKind.Mode, type);
        PublishLocal<MetricOverridesDocument>(ConfigKind.MetricOverrides, type);
        foreach (ScriptInfo script in _local.ListScriptsAsync(CancellationToken.None).GetAwaiter().GetResult())
        {
            _broadcaster.Publish(new ConfigChange(FileConfigLayer.LayerName, ConfigKind.Script, script.Name, type, ConfigChangeSource.Hub));
        }
    }

    private void PublishLocal<T>(ConfigKind kind, ConfigChangeType type)
        where T : class, IConfigDocument
    {
        foreach (T document in _local.List<T>())
        {
            string id = document switch
            {
                CommandDocument command => command.Id,
                SensorDocument sensor => sensor.Id,
                ModeGroupDocument mode => mode.Id,
                _ => ConfigIds.MetricOverrides,
            };
            _broadcaster.Publish(new ConfigChange(FileConfigLayer.LayerName, kind, id, type, ConfigChangeSource.Hub, document.Revision));
        }
    }
}
