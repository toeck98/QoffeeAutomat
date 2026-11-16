using System.Text.Json;
using QoffeeAutomat.Contracts.Session;

namespace QoffeeAutomat.SessionHost.Metrics;

/// <summary>
/// Samples the user metrics of the session, but only the ones the agent subscribed to (PLAN.md D-007, S11, T305):
/// <c>metrics_subscribe</c> replaces the set; an empty set stops the sampling altogether, so a user session that nobody
/// watches costs nothing and no window title is read. Each metric is sampled at the interval of its descriptor in
/// <see cref="SessionMetricCatalog"/>, and a <c>metrics_state</c> message carries the values that were sampled. Ids that are
/// not metrics of the session are answered once as unavailable and never sampled.
/// </summary>
public sealed class UserMetricSampler : IDisposable
{
    /// <summary>How often the background loop checks whether a metric is due.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly IUserMetricProbe _probe;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Dictionary<string, DateTimeOffset> _lastSampled = new(StringComparer.Ordinal);
    private readonly HashSet<string> _subscribed = new(StringComparer.Ordinal);

    /// <summary>Creates the sampler.</summary>
    /// <param name="probe">Reads the metrics.</param>
    /// <param name="time">The clock for the intervals and the timestamps.</param>
    public UserMetricSampler(IUserMetricProbe probe, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(time);

        _probe = probe;
        _time = time;
    }

    /// <summary>True while at least one metric is subscribed.</summary>
    public bool IsSubscribed
    {
        get
        {
            lock (_gate)
            {
                return _subscribed.Count > 0;
            }
        }
    }

    /// <summary>The metrics that are sampled now, in no particular order.</summary>
    public IReadOnlyCollection<string> Subscribed
    {
        get
        {
            lock (_gate)
            {
                return [.. _subscribed];
            }
        }
    }

    /// <summary>Replaces the subscription and samples everything that is subscribed once, at once.</summary>
    /// <param name="metricIds">The ids; empty ends the sampling.</param>
    /// <returns>The first values, or null when the subscription is empty.</returns>
    public SessionMetricsStateMessage? Subscribe(IReadOnlyList<string> metricIds)
    {
        ArgumentNullException.ThrowIfNull(metricIds);

        lock (_gate)
        {
            _subscribed.Clear();
            _lastSampled.Clear();
            var unknown = new List<string>();
            foreach (string id in metricIds.Distinct(StringComparer.Ordinal))
            {
                if (SessionMetricCatalog.IsSessionHostMetric(id))
                {
                    _subscribed.Add(id);
                }
                else
                {
                    unknown.Add(id);
                }
            }

            if (_subscribed.Count == 0 && unknown.Count == 0)
            {
                return null;
            }

            DateTimeOffset now = _time.GetUtcNow();
            SessionMetricsStateMessage first = Sample([.. _subscribed], now);

            // The loop sleeps while nothing is subscribed.
            WakeLoop();
            if (unknown.Count == 0)
            {
                return first;
            }

            var values = new Dictionary<string, JsonElement>(first.Values, StringComparer.Ordinal);
            foreach (string id in unknown)
            {
                values[id] = ToElement(null);
            }

            return first with { Values = values };
        }
    }

    /// <summary>Samples the metrics whose interval has passed since their last sample.</summary>
    /// <returns>The values, or null when nothing is due or nothing is subscribed.</returns>
    public SessionMetricsStateMessage? SampleDue()
    {
        lock (_gate)
        {
            if (_subscribed.Count == 0)
            {
                return null;
            }

            DateTimeOffset now = _time.GetUtcNow();
            var due = new List<string>();
            foreach (string id in _subscribed)
            {
                TimeSpan interval = TimeSpan.FromSeconds(SessionMetricCatalog.IntervalSecondsOf(id)) - (Tick / 2);
                if (!_lastSampled.TryGetValue(id, out DateTimeOffset last) || now - last >= interval)
                {
                    due.Add(id);
                }
            }

            return due.Count == 0 ? null : Sample(due, now);
        }
    }

    /// <summary>Samples what is due until the token is cancelled; sleeps while nothing is subscribed.</summary>
    /// <param name="publish">Sends a <c>metrics_state</c> message to the agent.</param>
    /// <param name="cancellationToken">Ends the loop with an <see cref="OperationCanceledException"/>.</param>
    /// <returns>A task that ends when the token is cancelled.</returns>
    public async Task RunAsync(Func<SessionMetricsStateMessage, Task> publish, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSubscribed)
            {
                await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            SessionMetricsStateMessage? message = SampleDue();
            if (message is not null)
            {
                await publish(message).ConfigureAwait(false);
            }

            await Task.Delay(Tick, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Releases the wake-up signal.</summary>
    public void Dispose()
    {
        _wake.Dispose();
    }

    private static JsonElement ToElement(object? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            switch (value)
            {
                case bool flag:
                    writer.WriteBooleanValue(flag);
                    break;
                case double number when double.IsFinite(number):
                    writer.WriteNumberValue(number);
                    break;
                case string text:
                    writer.WriteStringValue(text);
                    break;
                default:
                    writer.WriteNullValue();
                    break;
            }
        }

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>Reads the metrics and remembers when. The caller holds the lock.</summary>
    private SessionMetricsStateMessage Sample(List<string> ids, DateTimeOffset now)
    {
        var values = new Dictionary<string, JsonElement>(ids.Count, StringComparer.Ordinal);
        foreach (string id in ids)
        {
            object? value;
            try
            {
                value = _probe.Read(id);
            }
#pragma warning disable CA1031 // A probe that fails makes its metric unavailable; it must not end the sampling.
            catch (Exception)
#pragma warning restore CA1031
            {
                value = null;
            }

            values[id] = ToElement(value);
            _lastSampled[id] = now;
        }

        return new SessionMetricsStateMessage(now.ToUnixTimeMilliseconds(), values);
    }

    private void WakeLoop()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }
}
