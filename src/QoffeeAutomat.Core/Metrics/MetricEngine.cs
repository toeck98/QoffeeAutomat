using System.Collections.ObjectModel;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Metrics;

/// <summary>
/// Samples metrics on demand and tells subscribers what changed (PLAN.md S06, S07; D-007).
/// <list type="bullet">
/// <item>A metric is sampled only while an enabled subscription selects it. With no subscribers no provider is called.</item>
/// <item>A metric is sampled at the shortest interval any subscriber needs: its own interval, or a subscriber's larger minimum interval.</item>
/// <item>Providers are called once per due batch, so one call serves several metrics. A provider that throws makes its metrics unavailable (null) and is retried with exponential backoff.</item>
/// <item>Values are rounded to the metric's precision and filtered with its deadband before they count as changed.</item>
/// <item>Overrides and custom sensors are re-read when the configuration changes, without a restart.</item>
/// </list>
/// All timing uses the injected <see cref="TimeProvider"/>. <see cref="Start"/> runs the scheduler in
/// the background; tests can instead drive it by calling <see cref="PollAsync"/> after moving a fake clock.
/// </summary>
public sealed class MetricEngine : IAsyncDisposable, IDisposable
{
    private readonly object _gate = new();
    private readonly IConfigStore _config;
    private readonly TimeProvider _time;
    private readonly MetricEngineOptions _options;
    private readonly List<(MetricDescriptor Descriptor, IMetricProvider Owner)> _builtIns = [];
    private readonly List<IDynamicMetricProvider> _dynamicProviders = [];
    private readonly ISessionBridge? _session;
    private readonly IMetricProvider? _customSensors;
    private readonly ConfigSubscription _configSubscription;
    private readonly Dictionary<string, MetricState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<IMetricProvider, ProviderHealth> _health = new();
    private readonly List<MetricSubscription> _subscriptions = [];
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private ReadOnlyCollection<MetricCatalogEntry> _catalog = new List<MetricCatalogEntry>().AsReadOnly();
    private TaskCompletionSource _wake = NewWake();
    private DateTimeOffset? _nextWake;
    private bool _catalogDirty;
    private Task? _loop;
    private bool _disposed;

    /// <summary>Creates the engine and builds the initial catalog.</summary>
    /// <param name="config">The configuration; overrides and custom sensors are read from its effective view.</param>
    /// <param name="providers">The built-in metric providers.</param>
    /// <param name="time">The clock for every interval, backoff and timestamp.</param>
    /// <param name="options">Tuning; null uses the defaults.</param>
    /// <param name="session">Optional session bridge whose user metrics join the catalog.</param>
    /// <param name="customSensors">Optional provider that samples the <c>custom.*</c> metrics; without it they are unavailable.</param>
    public MetricEngine(
        IConfigStore config,
        IEnumerable<IMetricProvider> providers,
        TimeProvider time,
        MetricEngineOptions? options = null,
        ISessionBridge? session = null,
        IMetricProvider? customSensors = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(time);

        _config = config;
        _time = time;
        _options = options ?? new MetricEngineOptions();
        _options.Validate();
        _session = session;
        _customSensors = customSensors;

        foreach (IMetricProvider provider in providers)
        {
            AddBuiltIns(provider);
            if (provider is IDynamicMetricProvider dynamicProvider)
            {
                _dynamicProviders.Add(dynamicProvider);
                dynamicProvider.DescriptorsChanged += OnDescriptorsChanged;
            }
        }

        if (session is not null)
        {
            AddBuiltIns(new SessionMetricProvider(session));
            session.StateChanged += OnSessionStateChanged;
        }

        _configSubscription = config.Subscribe();
        lock (_gate)
        {
            RebuildCatalog();
        }
    }

    /// <summary>Raised after the catalog was rebuilt because an override or a custom sensor changed.</summary>
    public event EventHandler? CatalogChanged;

    /// <summary>Raised when a provider throws; the handler logs. Handlers must not throw.</summary>
    public event EventHandler<ProviderFailedEventArgs>? ProviderFailed;

    /// <summary>All known metrics with their effective settings, including disabled ones.</summary>
    public IReadOnlyList<MetricCatalogEntry> Catalog
    {
        get
        {
            lock (_gate)
            {
                return _catalog;
            }
        }
    }

    /// <summary>Starts the background scheduler. Calling it twice has no effect.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _loop ??= Task.Run(() => RunAsync(_stop.Token));
        }
    }

    /// <summary>Starts a feed for a subscriber.</summary>
    /// <param name="selection">What to subscribe to; null creates an idle subscription until <see cref="MetricSubscription.SetSelection"/>.</param>
    /// <param name="intervals">Optional minimum intervals in seconds per metric id.</param>
    /// <returns>The subscription; dispose it when the subscriber goes away.</returns>
    public MetricSubscription Subscribe(MetricSelection? selection = null, IReadOnlyDictionary<string, double>? intervals = null)
    {
        var subscription = new MetricSubscription(this);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscriptions.Add(subscription);
        }

        if (selection is not null)
        {
            SetSelection(subscription, selection, intervals);
        }

        return subscription;
    }

    /// <summary>
    /// Asks for a fresh sample of a metric at the next opportunity. This is how event-driven metrics
    /// (interval zero) learn about changes. Nothing happens while nobody subscribes to the metric.
    /// </summary>
    /// <param name="metricId">The metric id.</param>
    public void RequestSample(string metricId)
    {
        ArgumentNullException.ThrowIfNull(metricId);
        lock (_gate)
        {
            if (_states.TryGetValue(metricId, out MetricState? state))
            {
                state.RefreshRequested = true;
            }
        }

        SignalWake();
    }

    /// <summary>The last published value of a metric.</summary>
    /// <param name="metricId">The metric id.</param>
    /// <param name="value">The value; null when the metric is unavailable.</param>
    /// <returns>False when the metric is unknown or was not sampled since somebody last subscribed to it.</returns>
    public bool TryGetCurrentValue(string metricId, out object? value)
    {
        ArgumentNullException.ThrowIfNull(metricId);
        lock (_gate)
        {
            if (_states.TryGetValue(metricId, out MetricState? state) && state.HasValue)
            {
                value = state.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>The recorded samples of a metric, oldest first (by default the last hour).</summary>
    /// <param name="metricId">The metric id.</param>
    /// <param name="since">Only samples at or after this time; null returns all kept samples.</param>
    /// <returns>The samples; empty for an unknown metric.</returns>
    public IReadOnlyList<MetricSample> GetHistory(string metricId, DateTimeOffset? since = null)
    {
        ArgumentNullException.ThrowIfNull(metricId);
        lock (_gate)
        {
            return _states.TryGetValue(metricId, out MetricState? state) ? state.History.Snapshot(since) : [];
        }
    }

    /// <summary>
    /// Does the work that is due now: applies configuration changes, samples the due metrics, and
    /// delivers snapshots and updates. The background scheduler calls this; tests call it after
    /// advancing a fake clock. Do not call it while the engine is started.
    /// </summary>
    /// <param name="cancellationToken">Cancels the poll.</param>
    /// <returns>A task that completes when the poll is done.</returns>
    public async Task PollAsync(CancellationToken cancellationToken)
    {
        await _pollGate.WaitAsync(cancellationToken);
        try
        {
            await PollCoreAsync(cancellationToken);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    /// <summary>Stops the scheduler and completes all subscriptions; prefer <see cref="DisposeAsync"/>.</summary>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Stops the scheduler and completes all subscriptions.</summary>
    /// <returns>A task that completes when the scheduler has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        MetricSubscription[] subscriptions;
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            loop = _loop;
            subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
        }

        if (_session is not null)
        {
            _session.StateChanged -= OnSessionStateChanged;
        }

        foreach (IDynamicMetricProvider dynamicProvider in _dynamicProviders)
        {
            dynamicProvider.DescriptorsChanged -= OnDescriptorsChanged;
        }

        await _stop.CancelAsync();
        if (loop is not null)
        {
            await loop;
        }

        foreach (MetricSubscription subscription in subscriptions)
        {
            subscription.IsDisposed = true;
            subscription.Complete();
        }

        _configSubscription.Dispose();
        _stop.Dispose();
        _pollGate.Dispose();
    }

    internal void SetSelection(MetricSubscription subscription, MetricSelection selection, IReadOnlyDictionary<string, double>? intervals)
    {
        var accepted = new Dictionary<string, double>(StringComparer.Ordinal);
        if (intervals is not null)
        {
            foreach ((string id, double seconds) in intervals)
            {
                if (double.IsFinite(seconds) && seconds > 0)
                {
                    accepted[id] = seconds;
                }
            }
        }

        lock (_gate)
        {
            if (subscription.IsDisposed)
            {
                return;
            }

            subscription.IsAll = selection.All;
            subscription.Ids = new HashSet<string>(selection.Ids, StringComparer.Ordinal);
            subscription.Intervals = accepted;
            subscription.Sent.Clear();
            subscription.SentAt.Clear();
            subscription.SnapshotPending = true;
        }

        SignalWake();
    }

    internal void Unsubscribe(MetricSubscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
            subscription.IsDisposed = true;
        }

        subscription.Complete();
        SignalWake();
    }

    private static TaskCompletionSource NewWake()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static bool IsRelevant(ConfigChange change)
    {
        return change.Kind is ConfigKind.Sensor or ConfigKind.MetricOverrides;
    }

    private static bool Includes(MetricSubscription subscription, string id)
    {
        return subscription.IsAll || subscription.Ids.Contains(id);
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? current, DateTimeOffset candidate)
    {
        return current is { } existing && existing <= candidate ? existing : candidate;
    }

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        foreach (MetricDescriptor descriptor in _session.DescribeUserMetrics())
        {
            RequestSample(descriptor.Id);
        }
    }

    /// <summary>
    /// A dynamic provider reports a different set of metrics (a volume appeared or disappeared): describe it again and
    /// rebuild the catalog at the next poll, which also raises <see cref="CatalogChanged"/> so that the manifest follows.
    /// </summary>
    private void OnDescriptorsChanged(object? sender, EventArgs e)
    {
        if (sender is not IDynamicMetricProvider provider)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            List<(MetricDescriptor Descriptor, IMetricProvider Owner)> fresh;
            try
            {
                fresh = [.. provider.Describe().Select(descriptor => (descriptor, (IMetricProvider)provider))];
            }
#pragma warning disable CA1031 // A provider that cannot describe itself keeps its previous metrics; the next change event retries.
            catch (Exception)
#pragma warning restore CA1031
            {
                return;
            }

            int index = _builtIns.FindIndex(entry => ReferenceEquals(entry.Owner, provider));
            _builtIns.RemoveAll(entry => ReferenceEquals(entry.Owner, provider));
            _builtIns.InsertRange(index < 0 ? _builtIns.Count : index, fresh);
            _catalogDirty = true;
        }

        SignalWake();
    }

    private void AddBuiltIns(IMetricProvider provider)
    {
        foreach (MetricDescriptor descriptor in provider.Describe())
        {
            _builtIns.Add((descriptor, provider));
        }
    }

    private void SignalWake()
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            signal = _wake;
        }

        signal.TrySetResult();
    }

    private Task ResetWake()
    {
        lock (_gate)
        {
            if (_wake.Task.IsCompleted)
            {
                _wake = NewWake();
            }

            return _wake.Task;
        }
    }

    /// <summary>The background scheduler: poll, then sleep until the next due time or until something wakes it.</summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        bool configOpen = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            Task wake = ResetWake();
            try
            {
                await PollAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The scheduler must outlive a faulty event handler; the next iteration retries.
            catch (Exception)
#pragma warning restore CA1031
            {
                await DelayAsync(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }

            DateTimeOffset? next;
            lock (_gate)
            {
                next = _nextWake;
            }

            using var iteration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var waits = new List<Task> { wake };
            waits.Add(next is { } due
                ? Task.Delay(Max(due - _time.GetUtcNow(), TimeSpan.FromMilliseconds(1)), _time, iteration.Token)
                : Task.Delay(Timeout.InfiniteTimeSpan, iteration.Token));

            Task<bool>? configReady = null;
            if (configOpen)
            {
                configReady = _configSubscription.Changes.WaitToReadAsync(iteration.Token).AsTask();
                waits.Add(configReady);
            }

            await Task.WhenAny(waits);
            await iteration.CancelAsync();

            if (configReady is { IsCompletedSuccessfully: true, Result: false })
            {
                configOpen = false;
            }
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _time, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Stopping; the caller's loop condition ends the scheduler.
        }
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right)
    {
        return left >= right ? left : right;
    }

    private async Task PollCoreAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        bool catalogRebuilt = false;
        var batches = new Dictionary<IMetricProvider, List<MetricState>>();

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            while (_configSubscription.Changes.TryRead(out ConfigChange? change))
            {
                _catalogDirty |= IsRelevant(change);
            }

            if (_catalogDirty)
            {
                _catalogDirty = false;
                RebuildCatalog();
                catalogRebuilt = true;
            }

            UpdateDemand();
            CollectDue(now, batches);
        }

        if (catalogRebuilt)
        {
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }

        SampleOutcome[] outcomes = batches.Count == 0
            ? []
            : await Task.WhenAll(batches.Select(batch => SampleBatchAsync(batch.Key, batch.Value, cancellationToken)));

        var failures = new List<ProviderFailedEventArgs>();
        lock (_gate)
        {
            foreach (SampleOutcome outcome in outcomes)
            {
                Apply(outcome, now, failures);
            }

            DateTimeOffset served = _time.GetUtcNow();
            DateTimeOffset? wake = null;
            foreach (MetricSubscription subscription in _subscriptions)
            {
                wake = Serve(subscription, served, wake);
            }

            _nextWake = NextSampleTime(wake);
        }

        foreach (ProviderFailedEventArgs failure in failures)
        {
            ProviderFailed?.Invoke(this, failure);
        }
    }

    /// <summary>Merges the provider descriptors, the overrides and the custom sensors into the catalog. The caller holds the lock.</summary>
    private void RebuildCatalog()
    {
        List<CatalogItem> items = MetricCatalogBuilder.Build(_builtIns, _customSensors, _config.Effective, _options);
        var present = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<MetricCatalogEntry>(items.Count);

        foreach (CatalogItem item in items)
        {
            string id = item.Entry.Id;
            entries.Add(item.Entry);
            present.Add(id);

            if (!_states.TryGetValue(id, out MetricState? state))
            {
                _states[id] = new MetricState(
                    item.Entry,
                    item.Owner,
                    new MetricRingBuffer(_options.HistoryRetention, _options.HistoryMaxSamples));
                continue;
            }

            bool wasEnabled = state.Entry.Enabled;
            state.Entry = item.Entry;
            state.Owner = item.Owner;
            if (wasEnabled && !item.Entry.Enabled)
            {
                // Subscribers learn once that the metric is gone: its value becomes "unavailable".
                state.Value = null;
                state.HasValue = true;
                state.Demanded = false;
                state.Interval = null;
                state.LastSampled = null;
            }
        }

        foreach (string id in _states.Keys.Where(key => !present.Contains(key)).ToList())
        {
            _states.Remove(id);
        }

        _catalog = entries.AsReadOnly();
    }

    /// <summary>
    /// Works out which metrics somebody wants and how often. The sampling interval of a metric is the
    /// shortest need among its subscribers: a subscriber that asked for at most one value per 30 s
    /// needs the metric every 30 s, but never more often than the metric's own interval.
    /// </summary>
    private void UpdateDemand()
    {
        foreach (MetricState state in _states.Values)
        {
            double? need = null;
            if (state.Entry.Enabled)
            {
                foreach (MetricSubscription subscription in _subscriptions)
                {
                    if (!Includes(subscription, state.Entry.Id))
                    {
                        continue;
                    }

                    double own = state.Entry.IntervalSeconds;
                    double wanted = own == 0
                        ? 0
                        : Math.Max(own, subscription.Intervals.GetValueOrDefault(state.Entry.Id, own));
                    need = need is { } current ? Math.Min(current, wanted) : wanted;
                }
            }

            if (need is null)
            {
                state.Demanded = false;
                state.Interval = null;
                continue;
            }

            if (!state.Demanded)
            {
                // Nobody looked at this metric for a while: whatever is stored is stale.
                state.Demanded = true;
                state.HasValue = false;
                state.LastSampled = null;
            }

            state.Interval = need;
        }
    }

    /// <summary>Groups the metrics that are due by provider. The caller holds the lock.</summary>
    private void CollectDue(DateTimeOffset now, Dictionary<IMetricProvider, List<MetricState>> batches)
    {
        foreach (MetricState state in _states.Values)
        {
            if (state.Demanded && !state.HasValue && InBackoff(state.Owner, now))
            {
                // A subscriber waits for a first value, but the provider is being left alone: say "unavailable".
                MarkUnavailable(state, now);
                continue;
            }

            if (!IsDue(state, now))
            {
                continue;
            }

            if (state.Owner is not { } owner)
            {
                MarkUnavailable(state, now);
                continue;
            }

            if (!batches.TryGetValue(owner, out List<MetricState>? batch))
            {
                batches[owner] = batch = [];
            }

            batch.Add(state);
        }
    }

    private bool InBackoff(IMetricProvider? owner, DateTimeOffset now)
    {
        return owner is not null && _health.TryGetValue(owner, out ProviderHealth? health) && health.RetryAt > now;
    }

    private bool IsDue(MetricState state, DateTimeOffset now)
    {
        DateTimeOffset? due = DueTime(state);
        return due is { } at && at <= now;
    }

    /// <summary>
    /// When a demanded metric wants its next sample, never earlier than the end of its provider's
    /// backoff; null when it is event-driven and waiting for a request, or not demanded.
    /// </summary>
    private DateTimeOffset? DueTime(MetricState state)
    {
        if (!state.Demanded || state.Interval is not { } interval)
        {
            return null;
        }

        DateTimeOffset due;
        if (state.LastSampled is not { } last || state.RefreshRequested)
        {
            due = DateTimeOffset.MinValue;
        }
        else if (interval == 0)
        {
            return null;
        }
        else
        {
            due = last + TimeSpan.FromSeconds(interval);
        }

        if (state.Owner is { } owner && _health.TryGetValue(owner, out ProviderHealth? health) && health.RetryAt is { } retry && retry > due)
        {
            return retry;
        }

        return due;
    }

    private DateTimeOffset? NextSampleTime(DateTimeOffset? wake)
    {
        foreach (MetricState state in _states.Values)
        {
            if (DueTime(state) is { } due)
            {
                wake = Earliest(wake, due);
            }
        }

        return wake;
    }

    private async Task<SampleOutcome> SampleBatchAsync(
        IMetricProvider provider,
        List<MetricState> batch,
        CancellationToken cancellationToken)
    {
        string[] ids = [.. batch.Select(state => state.Entry.Id)];
        using var timeout = new CancellationTokenSource(_options.ProviderTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            IReadOnlyDictionary<string, object?> values = await provider.SampleAsync(ids, linked.Token);
            return new SampleOutcome(provider, batch, values, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A failing provider must only make its own metrics unavailable.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return new SampleOutcome(provider, batch, null, exception);
        }
    }

    private void Apply(SampleOutcome outcome, DateTimeOffset now, List<ProviderFailedEventArgs> failures)
    {
        if (!_health.TryGetValue(outcome.Provider, out ProviderHealth? health))
        {
            _health[outcome.Provider] = health = new ProviderHealth();
        }

        if (outcome.Exception is not null)
        {
            health.Failures++;
            TimeSpan retryIn = Backoff(health.Failures);
            health.RetryAt = now + retryIn;
            foreach (MetricState state in outcome.States)
            {
                MarkUnavailable(state, now);
            }

            failures.Add(new ProviderFailedEventArgs(
                [.. outcome.States.Select(state => state.Entry.Id)],
                outcome.Exception,
                health.Failures,
                retryIn));
            return;
        }

        health.Failures = 0;
        health.RetryAt = null;
        foreach (MetricState state in outcome.States)
        {
            object? raw = null;
            outcome.Values?.TryGetValue(state.Entry.Id, out raw);
            Record(state, MetricValues.Normalize(raw, state.Entry.Descriptor.Precision), now, applyDeadband: true);
        }
    }

    private TimeSpan Backoff(int failures)
    {
        double factor = Math.Pow(2, Math.Min(failures - 1, 30));
        double ticks = Math.Min(_options.ProviderBackoffMax.Ticks, _options.ProviderBackoffInitial.Ticks * factor);
        return TimeSpan.FromTicks((long)ticks);
    }

    private static void MarkUnavailable(MetricState state, DateTimeOffset now)
    {
        Record(state, null, now, applyDeadband: false);
    }

    private static void Record(MetricState state, object? value, DateTimeOffset now, bool applyDeadband)
    {
        state.LastSampled = now;
        state.RefreshRequested = false;
        state.History.Add(now, value);

        if (!state.HasValue || MetricValues.IsPublishableChange(state.Value, value, applyDeadband ? state.Entry.Deadband : null))
        {
            state.Value = value;
            state.HasValue = true;
        }
    }

    /// <summary>
    /// Delivers the snapshot or the due changes to one subscriber and returns the earliest time at
    /// which it needs attention again. The caller holds the lock.
    /// </summary>
    private DateTimeOffset? Serve(MetricSubscription subscription, DateTimeOffset now, DateTimeOffset? wake)
    {
        if (subscription.IsDisposed)
        {
            return wake;
        }

        TimeSpan flush = _options.FlushInterval;
        DateTimeOffset windowOpens = subscription.LastFlush is { } last ? last + flush : DateTimeOffset.MinValue;

        if (subscription.SnapshotPending)
        {
            return ServeSnapshot(subscription, now, wake);
        }

        Dictionary<string, object?>? ready = null;
        DateTimeOffset? release = null;
        foreach (MetricState state in SelectedStates(subscription))
        {
            if (!TryGetOutgoing(subscription, state, out object? value))
            {
                continue;
            }

            string id = state.Entry.Id;
            DateTimeOffset releaseAt = subscription.Intervals.TryGetValue(id, out double seconds) && subscription.SentAt.TryGetValue(id, out DateTimeOffset sentAt)
                ? sentAt + TimeSpan.FromSeconds(seconds)
                : DateTimeOffset.MinValue;

            if (releaseAt <= now)
            {
                (ready ??= new Dictionary<string, object?>(StringComparer.Ordinal))[id] = value;
            }
            else
            {
                release = Earliest(release, releaseAt);
            }
        }

        if (ready is not null)
        {
            if (now < windowOpens)
            {
                wake = Earliest(wake, windowOpens);
            }
            else
            {
                subscription.LastFlush = now;
                windowOpens = now + flush;
                if (subscription.TryWrite(new MetricUpdate(now, ready, IsSnapshot: false)))
                {
                    foreach ((string id, object? value) in ready)
                    {
                        subscription.Sent[id] = value;
                        subscription.SentAt[id] = now;
                    }
                }
                else
                {
                    // The consumer does not keep up: keep the changes pending and try again at the next flush.
                    wake = Earliest(wake, windowOpens);
                }
            }
        }

        if (release is { } held)
        {
            wake = Earliest(wake, held > windowOpens ? held : windowOpens);
        }

        return wake;
    }

    private DateTimeOffset? ServeSnapshot(MetricSubscription subscription, DateTimeOffset now, DateTimeOffset? wake)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (MetricState state in SelectedStates(subscription))
        {
            if (!state.Entry.Enabled)
            {
                continue;
            }

            if (!state.HasValue)
            {
                // First samples are on their way; the poll that stores them serves the snapshot.
                return wake;
            }

            values[state.Entry.Id] = state.Value;
        }

        subscription.LastFlush = now;
        if (!subscription.TryWrite(new MetricUpdate(now, values, IsSnapshot: true)))
        {
            return Earliest(wake, now + _options.FlushInterval);
        }

        subscription.SnapshotPending = false;
        foreach ((string id, object? value) in values)
        {
            subscription.Sent[id] = value;
            subscription.SentAt[id] = now;
        }

        return wake;
    }

    /// <summary>
    /// Decides whether a metric has something new for a subscriber and what it is. A metric that was
    /// disabled after the subscriber received a value delivers null once; one that never delivered
    /// anything stays silent.
    /// </summary>
    private static bool TryGetOutgoing(MetricSubscription subscription, MetricState state, out object? value)
    {
        string id = state.Entry.Id;
        value = null;

        if (state.Entry.Enabled)
        {
            if (!state.HasValue)
            {
                return false;
            }

            value = state.Value;
        }
        else if (!(subscription.Sent.TryGetValue(id, out object? delivered) && delivered is not null))
        {
            return false;
        }

        return !(subscription.Sent.TryGetValue(id, out object? previous) && MetricValues.Same(previous, value));
    }

    private IEnumerable<MetricState> SelectedStates(MetricSubscription subscription)
    {
        if (subscription.IsAll)
        {
            foreach (MetricState state in _states.Values)
            {
                yield return state;
            }

            yield break;
        }

        foreach (string id in subscription.Ids)
        {
            if (_states.TryGetValue(id, out MetricState? state))
            {
                yield return state;
            }
        }
    }

    private sealed class MetricState
    {
        public MetricState(MetricCatalogEntry entry, IMetricProvider? owner, MetricRingBuffer history)
        {
            Entry = entry;
            Owner = owner;
            History = history;
        }

        public MetricCatalogEntry Entry { get; set; }

        public IMetricProvider? Owner { get; set; }

        public MetricRingBuffer History { get; }

        /// <summary>The published value, valid when <see cref="HasValue"/> is true; null means unavailable.</summary>
        public object? Value { get; set; }

        public bool HasValue { get; set; }

        public DateTimeOffset? LastSampled { get; set; }

        public bool Demanded { get; set; }

        /// <summary>The sampling interval in seconds while demanded; zero means event-driven.</summary>
        public double? Interval { get; set; }

        public bool RefreshRequested { get; set; }
    }

    private sealed class ProviderHealth
    {
        public int Failures { get; set; }

        public DateTimeOffset? RetryAt { get; set; }
    }

    private sealed record SampleOutcome(
        IMetricProvider Provider,
        List<MetricState> States,
        IReadOnlyDictionary<string, object?>? Values,
        Exception? Exception);
}
