using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Gateway;

namespace QoffeeAutomat.Hub.Metrics;

/// <summary>
/// Collects the metrics of the agents into the <see cref="IMetricStore"/> (PLAN.md S20 "Metrics on the hub" and "Fleet link", T910). For every agent whose manifest the
/// gateway cached, it works out which metrics the collection policy (<see cref="IMetricPolicySource"/>) selects and declares them to the gateway with the collection
/// interval (<see cref="IAgentGateway.SubscribeMetrics"/>); the gateway keeps the agent subscribed to the union of this and every other consumer. The <c>state</c> frames
/// that arrive are filtered by the policy (another consumer may have asked for more, or for live values), converted by the value type of the manifest, and queued in
/// memory. A writer writes the queue in batches, one transaction per batch. A job rolls up the buckets that ended and applies the retention.
/// <para>
/// The observer methods return at once: they only convert and queue. The queue is bounded (<see cref="HubMetricsOptions.MaxBufferedSamples"/>); when the database cannot keep
/// up, new samples are dropped and counted instead of growing without limit. At most about one numeric sample per interval is kept for a metric, also when another consumer
/// makes the agent send live values.
/// </para>
/// </summary>
public sealed class MetricCollector : IAgentGatewayObserver, IHostedService, IDisposable
{
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(10);

    private readonly IAgentGateway _gateway;
    private readonly IMetricStore _store;
    private readonly IMetricPolicySource _policies;
    private readonly HubMetricsOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MetricCollector> _logger;
    private readonly ConcurrentDictionary<string, AgentCollection> _agents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _background = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _bufferGate = new();
    private List<MetricSample> _buffer = [];
    private long _dropped;
    private long _totalDropped;
    private bool _flushRequested;
    private DateTimeOffset? _lastRetention;
    private IDisposable? _registration;
    private Task? _flushLoop;
    private Task? _maintenanceLoop;

    /// <summary>Creates the collector.</summary>
    /// <param name="gateway">The agent gateway: manifests, state frames and subscriptions.</param>
    /// <param name="store">Where the samples go.</param>
    /// <param name="policies">Which metrics are collected for an agent.</param>
    /// <param name="options">The timings and limits.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public MetricCollector(
        IAgentGateway gateway,
        IMetricStore store,
        IMetricPolicySource policies,
        HubMetricsOptions options,
        TimeProvider time,
        ILogger<MetricCollector> logger)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        options.Validate();
        _gateway = gateway;
        _store = store;
        _policies = policies;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>The number of samples that were dropped since the hub started because the queue was full.</summary>
    public long DroppedSamples => Interlocked.Read(ref _totalDropped);

    private sealed record CollectTarget(string ValueType, string? Unit);

    private sealed class AgentCollection
    {
        public object Gate { get; } = new();

        public IMetricSubscription? Subscription { get; set; }

        public IReadOnlyDictionary<string, CollectTarget> Targets { get; set; } = new Dictionary<string, CollectTarget>(StringComparer.Ordinal);

        public long IntervalMilliseconds { get; set; }

        public Dictionary<string, long> LastStored { get; } = new(StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registration = _gateway.Observe(this);

        // Agents that connected before this service started are handled as if their manifest had just arrived.
        foreach (AgentPresence presence in _gateway.GetAllPresence())
        {
            if (_gateway.GetManifest(presence.AgentId) is { } manifest)
            {
                OnManifestChanged(presence.AgentId, manifest);
            }
        }

        _flushLoop = Task.Run(() => FlushLoopAsync(_stop.Token), CancellationToken.None);
        _maintenanceLoop = Task.Run(() => MaintenanceLoopAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _registration?.Dispose();
        await _stop.CancelAsync().ConfigureAwait(false);
        Task[] running = [.. new[] { _flushLoop, _maintenanceLoop }.OfType<Task>(), .. _background.Keys];
        try
        {
            await Task.WhenAll(running).WaitAsync(StopWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // What is still running ends with the process; the samples in the queue are written below.
        }

        foreach (AgentCollection collection in _agents.Values)
        {
            collection.Subscription?.Dispose();
        }

        // What the agents reported until now is not lost with the process.
        await FlushSafeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _registration?.Dispose();
        _stop.Dispose();
        _flushGate.Dispose();
        _wake.Dispose();
    }

    // ------------------------------------------------------------------
    // Observer
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public void OnManifestChanged(string agentId, CachedManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(manifest);

        try
        {
            Configure(agentId, manifest);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            MetricsLog.CollectFailed(_logger, agentId, exception.GetType().Name);
        }
    }

    private void Configure(string agentId, CachedManifest cached)
    {
        Manifest manifest = LinkCodec.ParseManifest(cached.Manifest);
        MetricCollectionPolicy policy = _policies.GetPolicy(agentId);
        var targets = new Dictionary<string, CollectTarget>(StringComparer.Ordinal);
        foreach (MetricDescriptor metric in manifest.Metrics)
        {
            if (MetricValueTypes.IsKnown(metric.ValueType) && IsDemandable(metric.Id) && policy.Collects(metric.Id))
            {
                targets[metric.Id] = new CollectTarget(metric.ValueType, metric.Unit);
            }
        }

        AgentCollection collection = _agents.GetOrAdd(agentId, _ => new AgentCollection());
        lock (collection.Gate)
        {
            collection.IntervalMilliseconds = (long)policy.Interval.TotalMilliseconds;
            collection.Targets = targets;
            collection.LastStored.Clear();
            if (targets.Count == 0)
            {
                collection.Subscription?.Dispose();
                collection.Subscription = null;
                return;
            }

            MetricDemand demand = MetricDemand.Of(targets.Keys.Select(id => new KeyValuePair<string, TimeSpan?>(id, policy.Interval)));
            if (collection.Subscription is null)
            {
                collection.Subscription = _gateway.SubscribeMetrics(agentId, demand);
            }
            else
            {
                collection.Subscription.Update(demand);
            }
        }

        MetricsLog.Collecting(_logger, agentId, targets.Count, policy.Interval.TotalSeconds);
    }

    private static bool IsDemandable(string metricId)
    {
        if (string.IsNullOrEmpty(metricId) || metricId.Length > MetricDemand.MaxMetricIdLength)
        {
            return false;
        }

        foreach (char c in metricId)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public void OnStateChanged(AgentStateUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (!_agents.TryGetValue(update.AgentId, out AgentCollection? collection))
        {
            return;
        }

        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        long ts = Math.Abs(update.Ts - now) > (long)_options.MaxClockSkew.TotalMilliseconds ? now : update.Ts;
        List<MetricSample>? samples = null;
        lock (collection.Gate)
        {
            IReadOnlyDictionary<string, CollectTarget> targets = collection.Targets;
            long minimumSpacing = collection.IntervalMilliseconds / 2;
            foreach ((string metricId, JsonElement value) in update.Values)
            {
                if (!targets.TryGetValue(metricId, out CollectTarget? target) || !TryConvert(target.ValueType, value, out double number, out string? text))
                {
                    continue;
                }

                if (text is null)
                {
                    // Agents send a value at the interval; a consumer that wants live values makes them send more often than the policy wants to keep.
                    if (!update.IsSnapshot && collection.LastStored.TryGetValue(metricId, out long last) && ts - last < minimumSpacing)
                    {
                        continue;
                    }

                    collection.LastStored[metricId] = ts;
                }

                samples ??= [];
                samples.Add(new MetricSample(update.AgentId, metricId, target.ValueType, target.Unit, ts, number, text));
            }
        }

        if (samples is not null)
        {
            Enqueue(samples);
        }
    }

    private static bool TryConvert(string valueType, JsonElement value, out double number, out string? text)
    {
        number = 0;
        text = null;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when valueType == MetricValueTypes.Number:
                return value.TryGetDouble(out number) && double.IsFinite(number);
            case JsonValueKind.True when valueType == MetricValueTypes.Bool:
                number = 1;
                return true;
            case JsonValueKind.False when valueType == MetricValueTypes.Bool:
                return true;
            case JsonValueKind.String when !MetricValueTypes.IsNumeric(valueType):
                text = value.GetString();
                return text is not null;
            default:
                // Null means that the metric is unavailable; a value of another type than the manifest says is not stored.
                return false;
        }
    }

    /// <inheritdoc />
    public void OnAgentRemoved(string agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId);

        if (_agents.TryRemove(agentId, out AgentCollection? collection))
        {
            lock (collection.Gate)
            {
                collection.Subscription?.Dispose();
                collection.Subscription = null;
                collection.Targets = new Dictionary<string, CollectTarget>(StringComparer.Ordinal);
            }
        }

        Track(Task.Run(() => RemoveAgentDataAsync(agentId), CancellationToken.None));
    }

    private async Task RemoveAgentDataAsync(string agentId)
    {
        try
        {
            int series = await _store.RemoveAgentAsync(agentId, _stop.Token).ConfigureAwait(false);
            MetricsLog.AgentRemoved(_logger, agentId, series);
        }
        catch (Exception exception) when (exception is SqliteException or OperationCanceledException or ObjectDisposedException)
        {
            MetricsLog.MaintenanceFailed(_logger, exception.GetType().Name);
        }
    }

    private void Track(Task task)
    {
        _background[task] = 0;
        _ = task.ContinueWith(finished => _background.TryRemove(finished, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // ------------------------------------------------------------------
    // The queue and the writer
    // ------------------------------------------------------------------

    private void Enqueue(List<MetricSample> samples)
    {
        bool wake = false;
        long dropped = 0;
        lock (_bufferGate)
        {
            int room = _options.MaxBufferedSamples - _buffer.Count;
            if (samples.Count <= room)
            {
                _buffer.AddRange(samples);
            }
            else
            {
                if (room > 0)
                {
                    _buffer.AddRange(samples.Take(room));
                }

                dropped = samples.Count - Math.Max(room, 0);
                _dropped += dropped;
            }

            if (_buffer.Count >= _options.FlushBatchSize && !_flushRequested)
            {
                _flushRequested = true;
                wake = true;
            }
        }

        if (dropped > 0)
        {
            _ = Interlocked.Add(ref _totalDropped, dropped);
        }

        if (wake)
        {
            _ = _wake.Release();
        }
    }

    /// <summary>Writes the samples in the queue to the store and returns when they are written.</summary>
    /// <param name="cancellationToken">Cancels the wait for the store.</param>
    /// <returns>The number of samples that were written.</returns>
    public async Task<int> FlushAsync(CancellationToken cancellationToken)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<MetricSample> batch;
            long dropped;
            lock (_bufferGate)
            {
                batch = _buffer;
                _buffer = [];
                dropped = _dropped;
                _dropped = 0;
                _flushRequested = false;
            }

            if (dropped > 0)
            {
                MetricsLog.Dropped(_logger, dropped);
            }

            int written = 0;
            for (int offset = 0; offset < batch.Count; offset += _options.FlushBatchSize)
            {
                int count = Math.Min(_options.FlushBatchSize, batch.Count - offset);
                MetricSample[] chunk = [.. batch.Skip(offset).Take(count)];
                try
                {
                    written += (await _store.AppendAsync(chunk, cancellationToken).ConfigureAwait(false)).Stored;
                }
                catch (SqliteException exception)
                {
                    // The chunk is lost; the next one may work again. The queue is bounded, so keeping it would only delay the loss.
                    MetricsLog.WriteFailed(_logger, chunk.Length, exception.GetType().Name);
                }
            }

            return written;
        }
        finally
        {
            _ = _flushGate.Release();
        }
    }

    private async Task FlushSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // The host is stopping; the next start begins with an empty queue.
        }
    }

    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _ = await _wake.WaitAsync(_options.FlushInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await FlushSafeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------
    // Roll-ups and retention
    // ------------------------------------------------------------------

    private async Task MaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.RollupInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RunMaintenanceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }

    /// <summary>
    /// Writes what is queued, rolls up the buckets that ended and, when <see cref="HubMetricsOptions.RetentionInterval"/> has passed since the last time, applies the
    /// retention. The job of the host calls it every <see cref="HubMetricsOptions.RollupInterval"/>; a failure of the database is logged and the next run tries again.
    /// </summary>
    /// <param name="cancellationToken">Cancels the job.</param>
    /// <returns>A task that completes when the job is done.</returns>
    public async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await FlushAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _time.GetUtcNow();
            MetricRollupResult rolled = await _store.RollUpAsync(now, cancellationToken).ConfigureAwait(false);
            if (rolled.Buckets5m + rolled.Buckets1h > 0)
            {
                MetricsLog.RolledUp(_logger, rolled.Buckets5m, rolled.Buckets1h);
            }

            if (_lastRetention is not { } last || now - last >= _options.RetentionInterval)
            {
                MetricRetentionResult removed = await _store.ApplyRetentionAsync(now, cancellationToken).ConfigureAwait(false);
                _lastRetention = now;
                if (removed.Total > 0)
                {
                    MetricsLog.RetentionApplied(_logger, removed.Raw, removed.States, removed.Rollups5m, removed.Rollups1h);
                }
            }
        }
        catch (SqliteException exception)
        {
            MetricsLog.MaintenanceFailed(_logger, exception.GetType().Name);
        }
    }
}
