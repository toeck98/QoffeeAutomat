using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Metrics;

namespace QoffeeAutomat.Agent.Api.Prometheus;

/// <summary>Timing of <see cref="PrometheusExporter"/>.</summary>
public sealed class PrometheusOptions
{
    /// <summary>
    /// How long the exported metrics are sampled after the last scrape. Prometheus scrapes every 15 to 60 seconds, so two minutes
    /// carry a scraper over a missed scrape; when the scraping stops, so does the sampling, and a script sensor never runs for nobody.
    /// </summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The longest a scrape waits for the first values after the sampling started (the first scrape, or the first one after the
    /// keep-alive window ran out). Prometheus gives a scrape ten seconds by default; what is not there yet is left out.
    /// </summary>
    public TimeSpan FirstScrapeWait { get; init; } = TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(KeepAlive, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(FirstScrapeWait, TimeSpan.Zero);
    }
}

/// <summary>
/// Produces the Prometheus export of the metrics that the user selected in the settings (PLAN.md S08, T709). Scrapes act as a
/// subscriber of the metric engine: the first scrape subscribes to the selected metrics, every scrape renews a keep-alive window, and
/// when no scrape arrives for <see cref="PrometheusOptions.KeepAlive"/> the subscription ends, so that the metrics nobody else wants are
/// no longer sampled (PLAN.md D-007). Only numeric and boolean metrics are exported; a boolean is 1 or 0, and a metric without a value
/// (unavailable, disabled, not yet sampled) is left out.
/// </summary>
public sealed class PrometheusExporter : IDisposable
{
    private readonly object _gate = new();
    private readonly MetricEngine _engine;
    private readonly IConfigStore _config;
    private readonly AgentIdentity _identity;
    private readonly TimeProvider _time;
    private readonly PrometheusOptions _options;
    private MetricSubscription? _subscription;
    private CancellationTokenSource? _readerStop;
    private ITimer? _keepAlive;
    private TaskCompletionSource _snapshot = NewSnapshot();
    private string[] _selection = [];
    private DateTimeOffset _lastScrape;
    private int _generation;
    private bool _disposed;

    /// <summary>Creates the exporter.</summary>
    /// <param name="engine">The metric engine that samples and holds the values.</param>
    /// <param name="config">The configuration, for the Prometheus settings.</param>
    /// <param name="identity">The agent id and name, the labels of every value.</param>
    /// <param name="time">The clock of the keep-alive window.</param>
    /// <param name="options">Timing; null uses the defaults.</param>
    public PrometheusExporter(MetricEngine engine, IConfigStore config, AgentIdentity identity, TimeProvider time, PrometheusOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(time);

        _engine = engine;
        _config = config;
        _identity = identity;
        _time = time;
        _options = options ?? new PrometheusOptions();
        _options.Validate();
    }

    /// <summary>Whether the endpoint is switched on in the settings; it is off by default.</summary>
    public bool IsEnabled => Settings?.EffectivePrometheusEnabled ?? false;

    /// <summary>True while the exported metrics are subscribed, that is from a scrape until the keep-alive window runs out.</summary>
    public bool IsSampling
    {
        get
        {
            lock (_gate)
            {
                return _subscription is not null;
            }
        }
    }

    private AgentSettingsDocument? Settings => _config.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings);

    /// <summary>Answers a scrape: subscribes to the selected metrics when nobody did, renews the keep-alive window and renders the values.</summary>
    /// <param name="cancellationToken">Cancels the wait for the first values.</param>
    /// <returns>The export in the Prometheus text format; empty when no metric is selected or none has a value.</returns>
    public async Task<string> ScrapeAsync(CancellationToken cancellationToken)
    {
        string[] selected = SelectedMetrics(Settings);
        Task snapshot;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _lastScrape = _time.GetUtcNow();
            snapshot = Engage(selected);
        }

        if (!snapshot.IsCompleted)
        {
            try
            {
                await snapshot.WaitAsync(_options.FirstScrapeWait, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The values that are there are exported; the others follow with the next scrape.
            }
        }

        return Render(selected);
    }

    /// <summary>Ends the subscription and the timer.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Release();
            _keepAlive?.Dispose();
            _keepAlive = null;
        }
    }

    private static TaskCompletionSource NewSnapshot() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The metric ids of the settings, without blanks and duplicates, in a stable order.</summary>
    private static string[] SelectedMetrics(AgentSettingsDocument? settings)
    {
        if (settings?.Prometheus?.Metrics is not { Count: > 0 } metrics)
        {
            return [];
        }

        return
        [
            .. metrics
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>Makes sure the selected metrics are subscribed and the keep-alive timer runs. The caller holds the lock.</summary>
    /// <returns>The task that completes when the engine has delivered the first values of the current selection.</returns>
    private Task Engage(string[] selected)
    {
        if (selected.Length == 0)
        {
            // Nothing to export: nothing is sampled.
            Release();
            return Task.CompletedTask;
        }

        if (_subscription is null)
        {
            _snapshot = NewSnapshot();
            _selection = selected;
            _subscription = _engine.Subscribe(MetricSelection.From(selected));
            StartReader(_subscription, _generation);
        }
        else if (!selected.AsSpan().SequenceEqual(_selection))
        {
            // The settings changed while somebody scrapes: follow them, and wait for the snapshot of the new selection.
            _snapshot = NewSnapshot();
            _selection = selected;
            _subscription.SetSelection(MetricSelection.From(selected));
        }

        _keepAlive ??= _time.CreateTimer(OnKeepAliveTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _keepAlive.Change(_options.KeepAlive, Timeout.InfiniteTimeSpan);
        return _snapshot.Task;
    }

    /// <summary>Ends the subscription so that the engine stops sampling what nobody else wants. The caller holds the lock.</summary>
    private void Release()
    {
        if (_subscription is null)
        {
            return;
        }

        _generation++;
        _readerStop?.Cancel();
        _readerStop?.Dispose();
        _readerStop = null;
        _subscription.Dispose();
        _subscription = null;
        _selection = [];
        _snapshot = NewSnapshot();
        _keepAlive?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void OnKeepAliveTimer(object? state)
    {
        lock (_gate)
        {
            if (_disposed || _subscription is null)
            {
                return;
            }

            // A scrape may have renewed the window while this call waited for the lock.
            TimeSpan idle = _time.GetUtcNow() - _lastScrape;
            if (idle < _options.KeepAlive)
            {
                _keepAlive?.Change(_options.KeepAlive - idle, Timeout.InfiniteTimeSpan);
                return;
            }

            Release();
        }
    }

    /// <summary>
    /// Reads the updates of the subscription. The values are taken from the engine when a scrape renders them; this loop only keeps the
    /// channel of the subscription empty (the engine holds back changes while it is full) and tells when the first snapshot has arrived.
    /// </summary>
    private void StartReader(MetricSubscription subscription, int generation)
    {
        var stop = new CancellationTokenSource();
        _readerStop = stop;
        _ = Task.Run(() => ReadAsync(subscription, generation, stop.Token), CancellationToken.None);
    }

    private async Task ReadAsync(MetricSubscription subscription, int generation, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (MetricUpdate update in subscription.Updates.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!update.IsSnapshot)
                {
                    continue;
                }

                lock (_gate)
                {
                    // A reader of an earlier subscription must not complete the wait of a later one.
                    if (_generation == generation)
                    {
                        _snapshot.TrySetResult();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The subscription was released.
        }
    }

    private string Render(string[] selected)
    {
        Dictionary<string, MetricCatalogEntry> catalog = _engine.Catalog.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var samples = new List<PrometheusSample>();
        foreach (string id in selected)
        {
            if (!catalog.TryGetValue(id, out MetricCatalogEntry? entry)
                || entry.Descriptor.ValueType is not ("number" or "bool")
                || !_engine.TryGetCurrentValue(id, out object? value))
            {
                continue;
            }

            double? number = value switch
            {
                double finite when double.IsFinite(finite) => finite,
                bool flag => flag ? 1 : 0,
                _ => null,
            };
            if (number is { } sample)
            {
                samples.Add(new PrometheusSample(id, entry.Descriptor.Name, entry.Descriptor.Unit, sample));
            }
        }

        return PrometheusFormat.Write(samples, _identity.Id, _identity.Name);
    }
}
