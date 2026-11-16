using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Tests.Config;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Core.Tests.Metrics;

/// <summary>
/// A provider whose values, failures and timing the test controls. It records every call: which ids
/// were asked for in one batch and at what (fake) time.
/// </summary>
internal sealed class StubMetricProvider : IMetricProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly List<string[]> _batches = [];
    private readonly List<DateTimeOffset> _callTimes = [];
    private readonly TaskCompletionSource _firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider _time;
    private readonly MetricDescriptor[] _descriptors;
    private Exception? _failure;
    private bool _hang;

    public StubMetricProvider(TimeProvider time, params MetricDescriptor[] descriptors)
    {
        _time = time;
        _descriptors = descriptors;
        foreach (MetricDescriptor descriptor in descriptors)
        {
            _known.Add(descriptor.Id);
        }
    }

    /// <summary>Completes when the provider is called for the first time.</summary>
    public Task FirstCall => _firstCall.Task;

    public int CallCount
    {
        get
        {
            lock (_gate)
            {
                return _batches.Count;
            }
        }
    }

    public IReadOnlyList<string[]> Batches
    {
        get
        {
            lock (_gate)
            {
                return [.. _batches];
            }
        }
    }

    public IReadOnlyList<DateTimeOffset> CallTimes
    {
        get
        {
            lock (_gate)
            {
                return [.. _callTimes];
            }
        }
    }

    /// <summary>When set, every call throws it.</summary>
    public Exception? Failure
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }

        set
        {
            lock (_gate)
            {
                _failure = value;
            }
        }
    }

    /// <summary>When true, a call waits until it is cancelled.</summary>
    public bool Hang
    {
        get
        {
            lock (_gate)
            {
                return _hang;
            }
        }

        set
        {
            lock (_gate)
            {
                _hang = value;
            }
        }
    }

    public void Set(string id, object? value)
    {
        lock (_gate)
        {
            _values[id] = value;
            _known.Add(id);
        }
    }

    public IReadOnlyList<MetricDescriptor> Describe()
    {
        return _descriptors;
    }

    public async ValueTask<IReadOnlyDictionary<string, object?>> SampleAsync(
        IReadOnlyCollection<string> metricIds,
        CancellationToken cancellationToken)
    {
        Exception? failure;
        bool hang;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        lock (_gate)
        {
            _batches.Add([.. metricIds]);
            _callTimes.Add(_time.GetUtcNow());
            failure = _failure;
            hang = _hang;
            foreach (string id in metricIds.Where(_known.Contains))
            {
                result[id] = _values.GetValueOrDefault(id);
            }
        }

        _firstCall.TrySetResult();
        if (hang)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        if (failure is not null)
        {
            throw failure;
        }

        return result;
    }
}

/// <summary>A metric engine on a real config store in a temp directory, driven by a fake clock.</summary>
internal sealed class MetricHarness : IAsyncDisposable, IDisposable
{
    private readonly TempDirectory _temp;

    private MetricHarness(TempDirectory temp, SimulatedClock clock, ConfigStore store, MetricEngine engine)
    {
        _temp = temp;
        Clock = clock;
        Store = store;
        Engine = engine;
    }

    public SimulatedClock Clock { get; }

    public ConfigStore Store { get; }

    public MetricEngine Engine { get; }

    public static async Task<MetricHarness> CreateAsync(
        SimulatedClock clock,
        IReadOnlyList<IMetricProvider> providers,
        MetricEngineOptions? options = null,
        ISessionBridge? session = null,
        IMetricProvider? customSensors = null)
    {
        var temp = new TempDirectory();
        ConfigStore store = await ConfigTestSupport.OpenAsync(ConfigTestSupport.Options(temp), clock);
        var engine = new MetricEngine(store, providers, clock, options, session, customSensors);
        return new MetricHarness(temp, clock, store, engine);
    }

    public Task PollAsync()
    {
        return Engine.PollAsync(CancellationToken.None);
    }

    /// <summary>Moves the clock by <paramref name="step"/> and polls, <paramref name="count"/> times.</summary>
    public async Task TickAsync(TimeSpan step, int count = 1)
    {
        for (int index = 0; index < count; index++)
        {
            Clock.Advance(step);
            await PollAsync();
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        Store.Dispose();
        _temp.Dispose();
    }

    /// <summary>Everything that has arrived at a subscription so far.</summary>
    public static List<MetricUpdate> Drain(MetricSubscription subscription)
    {
        var updates = new List<MetricUpdate>();
        while (subscription.Updates.TryRead(out MetricUpdate? update))
        {
            updates.Add(update);
        }

        return updates;
    }

    public static MetricSelection Select(params string[] ids)
    {
        return MetricSelection.From(ids);
    }

    public static MetricDescriptor Number(string id, int? precision = null, double? interval = 1, double? deadband = null)
    {
        return new MetricDescriptor(
            id,
            id,
            "sensor",
            "number",
            Precision: precision,
            IntervalSeconds: interval,
            Deadband: deadband,
            Context: "system");
    }

    public static double AsNumber(object? value)
    {
        return Assert.IsType<double>(value);
    }

    public static bool AsBool(object? value)
    {
        return Assert.IsType<bool>(value);
    }
}
