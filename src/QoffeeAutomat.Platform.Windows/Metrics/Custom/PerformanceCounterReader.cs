using System.ComponentModel;
using System.Diagnostics;
using QoffeeAutomat.Core.Metrics.Custom;

namespace QoffeeAutomat.Platform.Windows.Metrics.CustomSensors;

/// <summary>
/// The <see cref="IPerformanceCounterReader"/> of Windows (PLAN.md S06, T307). A counter object is kept for every counter
/// that is read, because most counters are rates and need the previous reading: the first read of a counter takes a baseline,
/// waits a moment and reads again, every later read compares with the one before. Counters that nobody read for ten minutes are
/// released. Whatever goes wrong (no such category, instance gone, no permission) is reported as a failure text.
/// </summary>
public sealed class PerformanceCounterReader : IPerformanceCounterReader, IDisposable
{
    /// <summary>How long a counter that nobody reads is kept.</summary>
    public static readonly TimeSpan IdleTime = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _firstReadDelay;
    private readonly Dictionary<string, Entry> _counters = new(StringComparer.Ordinal);

    /// <summary>Creates the reader.</summary>
    /// <param name="time">The clock for the idle time and the wait after the baseline; null uses the system clock.</param>
    /// <param name="firstReadDelay">How long to wait between the baseline and the first value; null waits 200 ms.</param>
    public PerformanceCounterReader(TimeProvider? time = null, TimeSpan? firstReadDelay = null)
    {
        _time = time ?? TimeProvider.System;
        _firstReadDelay = firstReadDelay ?? TimeSpan.FromMilliseconds(200);
    }

    /// <inheritdoc />
    public async ValueTask<SensorParseResult> ReadAsync(string category, string counter, string? instance, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(counter);

        string name = Describe(category, counter, instance);
        string key = category + "\u0001" + counter + "\u0001" + (instance ?? string.Empty);
        Entry entry;
        bool created = false;
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            DropIdle(now);
            if (!_counters.TryGetValue(key, out Entry? existing))
            {
                try
                {
                    existing = new Entry(new PerformanceCounter(category, counter, instance ?? string.Empty, readOnly: true));
                }
                catch (Exception exception) when (IsCounterFailure(exception))
                {
                    return SensorParseResult.Fail($"The performance counter {name} cannot be opened: {exception.Message}");
                }

                _counters[key] = existing;
                created = true;
            }

            existing.LastUsed = now;
            entry = existing;
        }

        try
        {
            if (created)
            {
                // A rate has no value at the first read; the second read, a moment later, has one.
                _ = entry.Read();
                await Task.Delay(_firstReadDelay, _time, cancellationToken).ConfigureAwait(false);
            }

            float value = entry.Read();
            return float.IsFinite(value)
                ? SensorParseResult.Success((double)value)
                : SensorParseResult.Fail($"The performance counter {name} returned no number.");
        }
        catch (Exception exception) when (IsCounterFailure(exception))
        {
            // The instance may be gone (a process ended): drop the counter, the next read starts over.
            Forget(key, entry);
            return SensorParseResult.Fail($"The performance counter {name} cannot be read: {exception.Message}");
        }
    }

    /// <summary>Releases all counters.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (Entry entry in _counters.Values)
            {
                entry.Dispose();
            }

            _counters.Clear();
        }
    }

    private static string Describe(string category, string counter, string? instance)
    {
        return string.IsNullOrEmpty(instance)
            ? $"'{category}\\{counter}'"
            : $"'{category}\\{counter}' (instance '{instance}')";
    }

    private static bool IsCounterFailure(Exception exception)
    {
        return exception is InvalidOperationException or ArgumentException or UnauthorizedAccessException or Win32Exception or NotSupportedException;
    }

    /// <summary>Releases the counters that were not read for <see cref="IdleTime"/>. The caller holds the lock.</summary>
    private void DropIdle(DateTimeOffset now)
    {
        foreach (string key in _counters.Where(pair => now - pair.Value.LastUsed > IdleTime).Select(pair => pair.Key).ToList())
        {
            _counters[key].Dispose();
            _counters.Remove(key);
        }
    }

    private void Forget(string key, Entry entry)
    {
        lock (_gate)
        {
            if (_counters.TryGetValue(key, out Entry? current) && ReferenceEquals(current, entry))
            {
                _counters.Remove(key);
                entry.Dispose();
            }
        }
    }

    private sealed class Entry : IDisposable
    {
        private readonly object _readGate = new();
        private readonly PerformanceCounter _counter;

        public Entry(PerformanceCounter counter)
        {
            _counter = counter;
        }

        public DateTimeOffset LastUsed { get; set; }

        // A counter object is not thread-safe, and the editor's test may read the same counter as the metric engine.
        public float Read()
        {
            lock (_readGate)
            {
                return _counter.NextValue();
            }
        }

        public void Dispose()
        {
            _counter.Dispose();
        }
    }
}
