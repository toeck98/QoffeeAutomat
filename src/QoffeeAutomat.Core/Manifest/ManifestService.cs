using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Metrics;
using LinkManifest = QoffeeAutomat.Contracts.Link.Manifest;

namespace QoffeeAutomat.Core.Manifest;

/// <summary>Raised when the manifest revision changed (the source of <c>manifest_changed</c>, PLAN.md S07).</summary>
public sealed class ManifestChangedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="previousRev">The revision before the change.</param>
    /// <param name="manifest">The new manifest.</param>
    public ManifestChangedEventArgs(string previousRev, LinkManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        PreviousRev = previousRev;
        Manifest = manifest;
    }

    /// <summary>The revision before the change.</summary>
    public string PreviousRev { get; }

    /// <summary>The new revision.</summary>
    public string Rev => Manifest.Rev;

    /// <summary>The new manifest.</summary>
    public LinkManifest Manifest { get; }
}

/// <summary>What the QoffeeLink server needs from the manifest builder: the current manifest and a notification when its revision changes.</summary>
public interface IManifestSource
{
    /// <summary>The current manifest. Always reflects the commands and metrics as they are right now.</summary>
    LinkManifest Current { get; }

    /// <summary>
    /// Raised after the revision changed, on the thread that noticed the change. Deliveries are serialised and
    /// handlers must return quickly and not throw; a handler should read <see cref="Current"/> rather than trust
    /// the arguments, because a later change may already have happened.
    /// </summary>
    event EventHandler<ManifestChangedEventArgs>? Changed;
}

/// <summary>
/// Keeps the manifest up to date (PLAN.md T209). <see cref="Current"/> returns a cached manifest and rebuilds it
/// only when an input changed: the command list of the <see cref="CommandRegistry"/> or the catalog of the
/// <see cref="MetricEngine"/> is a new snapshot, or the agent identity or the features differ. Because the
/// check runs on every read, a caller that just saved a command sees it at once. After <see cref="Start"/>
/// the service also watches its sources (command changes, catalog changes, agent settings) and raises
/// <see cref="Changed"/> when the revision changes, even if nobody reads <see cref="Current"/>. A change that
/// does not alter the manifest, such as editing a script, rebuilds it but keeps the revision and raises nothing.
/// </summary>
public sealed class ManifestService : IManifestSource, IDisposable
{
    private readonly object _gate = new();
    private readonly object _notifyGate = new();
    private readonly CommandRegistry _commands;
    private readonly MetricEngine _metrics;
    private readonly Func<AgentInfo> _agent;
    private readonly Func<IReadOnlyList<string>?>? _features;
    private readonly IConfigStore? _config;
    private readonly CancellationTokenSource _stop = new();
    private Snapshot _snapshot;
    private ConfigSubscription? _subscription;
    private Task? _pump;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates the service and builds the first manifest (no notification is raised for it).</summary>
    /// <param name="commands">The command registry.</param>
    /// <param name="metrics">The metric engine, whose catalog is announced.</param>
    /// <param name="agent">Returns the agent identity; evaluated whenever the manifest is read.</param>
    /// <param name="features">Returns the features of the agent; null announces none.</param>
    /// <param name="config">
    /// When set, <see cref="Start"/> also watches the agent settings of this store and refreshes the manifest when
    /// they change (the agent name is part of the manifest).
    /// </param>
    public ManifestService(
        CommandRegistry commands,
        MetricEngine metrics,
        Func<AgentInfo> agent,
        Func<IReadOnlyList<string>?>? features = null,
        IConfigStore? config = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(agent);

        _commands = commands;
        _metrics = metrics;
        _agent = agent;
        _features = features;
        _config = config;
        _snapshot = BuildSnapshot();
    }

    /// <inheritdoc />
    public event EventHandler<ManifestChangedEventArgs>? Changed;

    /// <inheritdoc />
    public LinkManifest Current => Refresh();

    /// <summary>The revision of <see cref="Current"/>.</summary>
    public string Rev => Current.Rev;

    /// <summary>
    /// Rebuilds the manifest when an input changed and raises <see cref="Changed"/> when the revision differs
    /// from the previous one.
    /// </summary>
    /// <returns>The current manifest.</returns>
    public LinkManifest Refresh()
    {
        AgentInfo agent = _agent();
        IReadOnlyList<string>? features = _features?.Invoke();
        IReadOnlyList<CommandDocument> commands = _commands.Commands;
        IReadOnlyList<MetricCatalogEntry> catalog = _metrics.Catalog;

        ManifestChangedEventArgs? change = null;
        LinkManifest result;
        lock (_gate)
        {
            Snapshot cached = _snapshot;
            if (ReferenceEquals(cached.Commands, commands)
                && ReferenceEquals(cached.Catalog, catalog)
                && SameAgent(cached.Agent, agent)
                && SameList(cached.Features, features))
            {
                return cached.Manifest;
            }

            Snapshot rebuilt = BuildSnapshot(agent, features, commands, catalog);
            _snapshot = rebuilt;
            result = rebuilt.Manifest;
            if (!string.Equals(cached.Manifest.Rev, rebuilt.Manifest.Rev, StringComparison.Ordinal))
            {
                change = new ManifestChangedEventArgs(cached.Manifest.Rev, rebuilt.Manifest);
            }
        }

        if (change is not null)
        {
            RaiseChanged(change);
        }

        return result;
    }

    /// <summary>Starts watching the sources so that <see cref="Changed"/> is raised without anybody reading <see cref="Current"/>. Calling it twice has no effect.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            _commands.Changed += OnSourceChanged;
            _metrics.CatalogChanged += OnSourceChanged;
            if (_config is not null)
            {
                _subscription = _config.Subscribe();
                ConfigSubscription subscription = _subscription;
                _pump = Task.Run(() => PumpAsync(subscription, _stop.Token));
            }
        }

        // Something may have changed between construction and the subscription.
        Refresh();
    }

    /// <summary>Stops watching the sources.</summary>
    public void Dispose()
    {
        Task? pump;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _commands.Changed -= OnSourceChanged;
            _metrics.CatalogChanged -= OnSourceChanged;
            pump = _pump;
        }

        _stop.Cancel();
        _subscription?.Dispose();
        try
        {
            pump?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // The pump was stopped on purpose.
        }

        _stop.Dispose();
    }

    private static bool SameAgent(AgentInfo left, AgentInfo right)
    {
        return string.Equals(left.Id, right.Id, StringComparison.Ordinal)
            && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            && string.Equals(left.Version, right.Version, StringComparison.Ordinal)
            && string.Equals(left.Os, right.Os, StringComparison.Ordinal)
            && string.Equals(left.OsVersion, right.OsVersion, StringComparison.Ordinal)
            && string.Equals(left.Arch, right.Arch, StringComparison.Ordinal)
            && string.Equals(left.Hostname, right.Hostname, StringComparison.Ordinal)
            && Equals(left.UiUrl, right.UiUrl)
            && SameList(left.Macs, right.Macs);
    }

    private static bool SameList(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return (left?.Count ?? 0) == 0 && (right?.Count ?? 0) == 0;
        }

        return left.SequenceEqual(right, StringComparer.Ordinal);
    }

    private Snapshot BuildSnapshot()
    {
        return BuildSnapshot(_agent(), _features?.Invoke(), _commands.Commands, _metrics.Catalog);
    }

    private static Snapshot BuildSnapshot(
        AgentInfo agent,
        IReadOnlyList<string>? features,
        IReadOnlyList<CommandDocument> commands,
        IReadOnlyList<MetricCatalogEntry> catalog)
    {
        LinkManifest manifest = ManifestBuilder.Build(agent, catalog, commands, EventCatalog.Types, features);
        return new Snapshot(agent, features, commands, catalog, manifest);
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        Refresh();
    }

    private async Task PumpAsync(ConfigSubscription subscription, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ConfigChange change in subscription.Changes.ReadAllAsync(cancellationToken))
            {
                if (change.Kind == ConfigKind.Settings)
                {
                    Refresh();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose stopped the pump.
        }
    }

    private void RaiseChanged(ManifestChangedEventArgs change)
    {
        EventHandler<ManifestChangedEventArgs>? handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        lock (_notifyGate)
        {
            // Each handler is invoked on its own, so one that throws does not keep the others from being told.
            foreach (EventHandler<ManifestChangedEventArgs> handler in handlers.GetInvocationList())
            {
#pragma warning disable CA1031 // A faulty subscriber must neither stop the others nor the source that noticed the change.
                try
                {
                    handler(this, change);
                }
                catch (Exception)
                {
                    // Handlers must not throw.
                }
#pragma warning restore CA1031
            }
        }
    }

    private sealed record Snapshot(
        AgentInfo Agent,
        IReadOnlyList<string>? Features,
        IReadOnlyList<CommandDocument> Commands,
        IReadOnlyList<MetricCatalogEntry> Catalog,
        LinkManifest Manifest);
}
