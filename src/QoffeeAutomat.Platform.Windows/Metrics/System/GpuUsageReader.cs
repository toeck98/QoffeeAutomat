using System.Diagnostics;

namespace QoffeeAutomat.Platform.Windows.Metrics.SystemMetrics;

/// <summary>
/// Reads the utilisation of the GPU from the "GPU Engine" performance counters of Windows (PLAN.md S06, T304): the counters
/// exist per process and engine (<c>pid_4711_luid_0x00000000_0x0000F3A1_phys_0_eng_0_engtype_3D</c>); the utilisation of an
/// engine is the sum over all processes, and the GPU counts as busy as its busiest engine, which is what the Task Manager
/// shows. Utilisation is a rate, so the first read only takes a baseline and returns null. Where the counters do not exist
/// (older Windows, some virtual machines, a driver without WDDM 2.0) or cannot be read, the result is null as well, and
/// the category is not asked for again for a while.
/// </summary>
public sealed class GpuUsageReader
{
    /// <summary>The performance counter category.</summary>
    public const string CategoryName = "GPU Engine";

    /// <summary>The counter in the category.</summary>
    public const string CounterName = "Utilization Percentage";

    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time;
    private Dictionary<string, CounterSample> _previous = new(StringComparer.Ordinal);
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;

    /// <summary>Creates the reader.</summary>
    /// <param name="time">The clock for the pause after a failure.</param>
    public GpuUsageReader(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>
    /// Takes the current counter values and compares them with those of the previous call. Not thread-safe: the metric engine
    /// calls its providers one batch at a time.
    /// </summary>
    /// <returns>Percent of the busiest engine, 0 to 100; null when there is no baseline yet or the counters are not available.</returns>
    public double? Read()
    {
        if (_time.GetUtcNow() < _retryAt)
        {
            return null;
        }

        try
        {
            if (!PerformanceCounterCategory.Exists(CategoryName))
            {
                Pause();
                return null;
            }

            InstanceDataCollectionCollection data = new PerformanceCounterCategory(CategoryName).ReadCategory();
            if (!data.Contains(CounterName))
            {
                Pause();
                return null;
            }

            return Evaluate(data[CounterName]);
        }
#pragma warning disable CA1031 // No counters, no permission or a broken counter database: the GPU metric is unavailable.
        catch (Exception)
#pragma warning restore CA1031
        {
            _previous = new Dictionary<string, CounterSample>(StringComparer.Ordinal);
            Pause();
            return null;
        }
    }

    /// <summary>Extracts the part of an instance name that identifies the engine, without the process: <c>luid_..._phys_0_eng_0_engtype_3D</c>.</summary>
    /// <param name="instanceName">The instance name of the counter.</param>
    /// <returns>The engine key.</returns>
    public static string EngineKey(string instanceName)
    {
        ArgumentNullException.ThrowIfNull(instanceName);

        int index = instanceName.IndexOf("_luid_", StringComparison.Ordinal);
        return index >= 0 ? instanceName[(index + 1)..] : instanceName;
    }

    private double? Evaluate(InstanceDataCollection counter)
    {
        var current = new Dictionary<string, CounterSample>(StringComparer.Ordinal);
        var engines = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (InstanceData instance in counter.Values)
        {
            current[instance.InstanceName] = instance.Sample;
            if (_previous.TryGetValue(instance.InstanceName, out CounterSample before))
            {
                float percent = CounterSample.Calculate(before, instance.Sample);
                if (float.IsFinite(percent) && percent > 0)
                {
                    string key = EngineKey(instance.InstanceName);
                    engines[key] = engines.GetValueOrDefault(key) + percent;
                }
            }
        }

        bool hadBaseline = _previous.Count > 0;
        _previous = current;
        if (!hadBaseline || current.Count == 0)
        {
            return null;
        }

        return engines.Count == 0 ? 0 : Math.Clamp(engines.Values.Max(), 0, 100);
    }

    private void Pause()
    {
        _retryAt = _time.GetUtcNow() + RetryAfterFailure;
    }
}
