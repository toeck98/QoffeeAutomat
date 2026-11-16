using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Simulated;

/// <summary>
/// Deterministic metrics for tests, CI and development on any OS (PLAN.md D-021): a sine wave
/// "CPU" value, a counter, a binary sensor that toggles and an enum sensor that cycles. Every value
/// is a pure function of the time of the injected <see cref="TimeProvider"/>, so the same fake time
/// always gives the same values.
/// </summary>
public sealed class SimulatedMetricProvider : IMetricProvider
{
    /// <summary>A sine wave between 10 and 90 percent with a period of 60 seconds.</summary>
    public const string CpuId = "sim.cpu";

    /// <summary>A counter that grows by one every second.</summary>
    public const string CounterId = "sim.counter";

    /// <summary>A binary sensor that is on for 10 seconds, then off for 10 seconds.</summary>
    public const string ToggleId = "sim.toggle";

    /// <summary>An enum sensor that moves to the next option every 15 seconds.</summary>
    public const string ModeId = "sim.mode";

    /// <summary>The options of <see cref="ModeId"/>, in the order they are visited.</summary>
    public static readonly IReadOnlyList<string> ModeOptions = ["idle", "active", "busy"];

    private const double CpuPeriodSeconds = 60;
    private const double ToggleHalfPeriodSeconds = 10;
    private const double ModeStepSeconds = 15;

    private static readonly MetricDescriptor[] Descriptors =
    [
        new(
            CpuId,
            "Simulated CPU usage",
            "sensor",
            "number",
            TranslationKey: "sim_cpu",
            Unit: "%",
            StateClass: "measurement",
            Precision: 1,
            EnabledDefault: true,
            IntervalSeconds: 1,
            Context: "system"),
        new(
            CounterId,
            "Simulated counter",
            "sensor",
            "number",
            TranslationKey: "sim_counter",
            StateClass: "total_increasing",
            Precision: 0,
            EnabledDefault: true,
            IntervalSeconds: 1,
            Context: "system"),
        new(
            ToggleId,
            "Simulated toggle",
            "binary_sensor",
            "bool",
            TranslationKey: "sim_toggle",
            EnabledDefault: true,
            IntervalSeconds: 1,
            Context: "system"),
        new(
            ModeId,
            "Simulated mode",
            "sensor",
            "enum",
            TranslationKey: "sim_mode",
            DeviceClass: "enum",
            Options: ModeOptions,
            EnabledDefault: true,
            IntervalSeconds: 1,
            Context: "system"),
    ];

    private readonly TimeProvider _time;
    private readonly HashSet<string> _unavailable = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _sampleCallCount;
    private int _sampledIdCount;

    /// <summary>Creates the provider.</summary>
    /// <param name="time">The clock that all values are derived from.</param>
    public SimulatedMetricProvider(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>
    /// How often <see cref="SampleAsync"/> was called. The metric engine tests use it to prove that
    /// nothing is sampled while nobody subscribes (PLAN.md D-007).
    /// </summary>
    public int SampleCallCount => Volatile.Read(ref _sampleCallCount);

    /// <summary>The total number of metric ids that were requested over all <see cref="SampleAsync"/> calls.</summary>
    public int SampledIdCount => Volatile.Read(ref _sampledIdCount);

    /// <summary>The value of <see cref="CpuId"/> at a time.</summary>
    /// <param name="at">The time.</param>
    /// <returns>A value between 10 and 90, rounded to two decimals.</returns>
    public static double CpuAt(DateTimeOffset at)
    {
        return Math.Round(50 + (40 * Math.Sin(2 * Math.PI * SecondsSinceEpoch(at) / CpuPeriodSeconds)), 2);
    }

    /// <summary>The value of <see cref="CounterId"/> at a time.</summary>
    /// <param name="at">The time.</param>
    /// <returns>The whole seconds since the Unix epoch.</returns>
    public static double CounterAt(DateTimeOffset at)
    {
        return Math.Floor(SecondsSinceEpoch(at));
    }

    /// <summary>The value of <see cref="ToggleId"/> at a time.</summary>
    /// <param name="at">The time.</param>
    /// <returns>True during the first 10 seconds of every 20-second cycle.</returns>
    public static bool ToggleAt(DateTimeOffset at)
    {
        return (long)Math.Floor(SecondsSinceEpoch(at) / ToggleHalfPeriodSeconds) % 2 == 0;
    }

    /// <summary>The value of <see cref="ModeId"/> at a time.</summary>
    /// <param name="at">The time.</param>
    /// <returns>One of <see cref="ModeOptions"/>.</returns>
    public static string ModeAt(DateTimeOffset at)
    {
        long step = (long)Math.Floor(SecondsSinceEpoch(at) / ModeStepSeconds);
        return ModeOptions[(int)(step % ModeOptions.Count)];
    }

    /// <inheritdoc />
    public IReadOnlyList<MetricDescriptor> Describe()
    {
        return Descriptors;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyDictionary<string, object?>> SampleAsync(
        IReadOnlyCollection<string> metricIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metricIds);
        cancellationToken.ThrowIfCancellationRequested();

        Interlocked.Increment(ref _sampleCallCount);
        Interlocked.Add(ref _sampledIdCount, metricIds.Count);
        DateTimeOffset now = _time.GetUtcNow();
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string id in metricIds)
        {
            object? value;
            switch (id)
            {
                case CpuId:
                    value = CpuAt(now);
                    break;
                case CounterId:
                    value = CounterAt(now);
                    break;
                case ToggleId:
                    value = ToggleAt(now);
                    break;
                case ModeId:
                    value = ModeAt(now);
                    break;
                default:
                    continue;
            }

            values[id] = IsAvailable(id) ? value : null;
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, object?>>(values);
    }

    /// <summary>Makes a metric report "unavailable" (null) or available again.</summary>
    /// <param name="metricId">One of the ids of this provider.</param>
    /// <param name="available">False makes samples of the metric null.</param>
    public void SetAvailable(string metricId, bool available)
    {
        ArgumentNullException.ThrowIfNull(metricId);
        lock (_gate)
        {
            if (available)
            {
                _unavailable.Remove(metricId);
            }
            else
            {
                _unavailable.Add(metricId);
            }
        }
    }

    private bool IsAvailable(string id)
    {
        lock (_gate)
        {
            return !_unavailable.Contains(id);
        }
    }

    private static double SecondsSinceEpoch(DateTimeOffset at)
    {
        return at.ToUnixTimeMilliseconds() / 1000.0;
    }
}
