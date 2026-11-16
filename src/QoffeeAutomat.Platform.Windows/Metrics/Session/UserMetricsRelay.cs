using System.Text.Json;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Platform.Windows.Sessions;

namespace QoffeeAutomat.Platform.Windows.Metrics.SessionMetrics;

/// <summary>
/// The agent's end of the user metrics (PLAN.md S06, S11, D-007, T305). The metric engine asks for the metrics that the
/// SessionHost samples (<see cref="SessionMetricCatalog.SessionHostSide"/>) only while somebody subscribes to them; the
/// relay turns that demand into <c>metrics_subscribe</c> messages and answers the engine from the values of the
/// <c>metrics_state</c> messages:
/// <list type="bullet">
/// <item>A metric that is asked for for the first time is subscribed, and the answer waits for the first values (a moment, never longer than the first-value timeout).</item>
/// <item>A metric that is not asked for any more (the engine stopped sampling it because nobody subscribes) is dropped from the subscription after a while, so the SessionHost stops sampling it; an empty subscription stops the sampling in the session altogether.</item>
/// <item>Without a SessionHost connection every metric is unavailable (null), silently: no session is a normal state.</item>
/// <item>A new connection starts without values and is subscribed to what is still wanted.</item>
/// </list>
/// </summary>
public sealed class UserMetricsRelay : IDisposable
{
    /// <summary>How long a call waits for the first values of a newly subscribed metric.</summary>
    public static readonly TimeSpan DefaultFirstValueTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The shortest time a metric stays subscribed after it was last asked for; longer for slow metrics (four intervals).</summary>
    public static readonly TimeSpan DefaultExpiry = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PruneInterval = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly TimeProvider _time;
    private readonly TimeSpan _firstValueTimeout;
    private readonly TimeSpan _expiry;
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _requested = new(StringComparer.Ordinal);
    private readonly ITimer? _pruneTimer;
    private SessionConnection? _connection;
    private TaskCompletionSource _arrival = NewArrival();
    private bool _disposed;

    /// <summary>Creates the relay without a connection.</summary>
    /// <param name="time">The clock for the expiry of the demand.</param>
    /// <param name="firstValueTimeout">How long to wait for the first values of a newly subscribed metric; null uses <see cref="DefaultFirstValueTimeout"/>.</param>
    /// <param name="expiry">How long a metric stays subscribed without being asked for; null uses <see cref="DefaultExpiry"/>.</param>
    /// <param name="pruneInterval">How often expired metrics are dropped; null checks every 10 seconds, <see cref="Timeout.InfiniteTimeSpan"/> never (tests call <see cref="Prune"/>).</param>
    public UserMetricsRelay(
        TimeProvider time,
        TimeSpan? firstValueTimeout = null,
        TimeSpan? expiry = null,
        TimeSpan? pruneInterval = null)
    {
        ArgumentNullException.ThrowIfNull(time);

        _time = time;
        _firstValueTimeout = firstValueTimeout ?? DefaultFirstValueTimeout;
        _expiry = expiry ?? DefaultExpiry;
        TimeSpan interval = pruneInterval ?? PruneInterval;
        if (interval != Timeout.InfiniteTimeSpan)
        {
            _pruneTimer = time.CreateTimer(_ => Prune(), null, interval, interval);
        }
    }

    /// <summary>The metrics that the SessionHost is subscribed to now, sorted.</summary>
    public IReadOnlyList<string> Subscribed
    {
        get
        {
            lock (_gate)
            {
                return [.. _requested.Keys.Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>
    /// Switches to the SessionHost of the active session; null when there is none. The values of the previous connection are
    /// discarded, and the new connection is subscribed to the metrics that are still wanted.
    /// </summary>
    /// <param name="connection">The connection, or null.</param>
    public void Attach(SessionConnection? connection)
    {
        SessionConnection? previous;
        bool resubscribe;
        lock (_gate)
        {
            if (_disposed || ReferenceEquals(_connection, connection))
            {
                return;
            }

            previous = _connection;
            _connection = connection;
            _values.Clear();
            resubscribe = connection is not null && _requested.Count > 0;
            ReleaseWaiters();
        }

        if (previous is not null)
        {
            previous.MetricsStateReceived -= OnMetricsState;
        }

        if (connection is not null)
        {
            connection.MetricsStateReceived += OnMetricsState;
            if (resubscribe)
            {
                _ = SendSubscriptionAsync(connection);
            }
        }
    }

    /// <summary>Returns the latest values of session metrics, subscribing the SessionHost to them first.</summary>
    /// <param name="metricIds">The ids; ids that the SessionHost does not sample are ignored.</param>
    /// <param name="cancellationToken">Cancels the wait for the first values.</param>
    /// <returns>The values by metric id; null where the metric is unavailable or there is no session.</returns>
    public async ValueTask<IReadOnlyDictionary<string, object?>> SampleAsync(
        IReadOnlyCollection<string> metricIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metricIds);

        var wanted = metricIds.Where(SessionMetricCatalog.IsSessionHostMetric).Distinct(StringComparer.Ordinal).ToList();
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string id in wanted)
        {
            result[id] = null;
        }

        SessionConnection? connection;
        Task? arrival = null;
        bool subscriptionChanged = false;
        lock (_gate)
        {
            connection = _connection;
            if (connection is null || _disposed || wanted.Count == 0)
            {
                return result;
            }

            DateTimeOffset now = _time.GetUtcNow();
            foreach (string id in wanted)
            {
                if (!_requested.ContainsKey(id))
                {
                    subscriptionChanged = true;
                }

                _requested[id] = now;
            }

            if (wanted.Any(id => !_values.ContainsKey(id)))
            {
                // Taken before the subscription is sent, so that the values of the reply cannot be missed.
                arrival = _arrival.Task;
            }
        }

        if (subscriptionChanged)
        {
            await SendSubscriptionAsync(connection).ConfigureAwait(false);
        }

        if (arrival is not null)
        {
            try
            {
                await arrival.WaitAsync(_firstValueTimeout, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The SessionHost did not answer in time: the metrics are unavailable for now and arrive with a later sample.
            }
        }

        lock (_gate)
        {
            foreach (string id in wanted)
            {
                if (ReferenceEquals(_connection, connection) && _values.TryGetValue(id, out object? value))
                {
                    result[id] = value;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Drops the metrics that were not asked for within their expiry from the subscription. The metric engine stops asking
    /// when nobody subscribes to a metric, so this is what ends the sampling in the session.
    /// </summary>
    public void Prune()
    {
        SessionConnection? connection;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            DateTimeOffset now = _time.GetUtcNow();
            bool changed = false;
            foreach ((string id, DateTimeOffset askedAt) in _requested.ToList())
            {
                TimeSpan keep = TimeSpan.FromSeconds(Math.Max(_expiry.TotalSeconds, 4 * SessionMetricCatalog.IntervalSecondsOf(id)));
                if (now - askedAt > keep)
                {
                    _requested.Remove(id);
                    _values.Remove(id);
                    changed = true;
                }
            }

            connection = changed ? _connection : null;
        }

        if (connection is not null)
        {
            _ = SendSubscriptionAsync(connection);
        }
    }

    /// <summary>Detaches from the connection and stops the timer.</summary>
    public void Dispose()
    {
        SessionConnection? connection;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            connection = _connection;
            _connection = null;
            ReleaseWaiters();
        }

        _pruneTimer?.Dispose();
        _sendGate.Dispose();
        if (connection is not null)
        {
            connection.MetricsStateReceived -= OnMetricsState;
        }
    }

    private static TaskCompletionSource NewArrival()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static object? ToValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDouble(out double number) ? number : null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => element.GetString(),
            _ => null,
        };
    }

    /// <summary>Wakes everybody who waits for values. The caller holds the lock.</summary>
    private void ReleaseWaiters()
    {
        TaskCompletionSource released = _arrival;
        _arrival = NewArrival();
        released.TrySetResult();
    }

    private void OnMetricsState(object? sender, SessionMetricsEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _connection))
            {
                return;
            }

            foreach ((string id, JsonElement element) in e.Message.Values)
            {
                _values[id] = ToValue(element);
            }

            ReleaseWaiters();
        }
    }

    /// <summary>
    /// Sends the metrics that are wanted now. The set is read inside the send gate, so concurrent callers cannot deliver an
    /// older set after a newer one.
    /// </summary>
    private async Task SendSubscriptionAsync(SessionConnection connection)
    {
        try
        {
            await _sendGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            string[] ids;
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_connection, connection))
                {
                    return;
                }

                ids = [.. _requested.Keys.Order(StringComparer.Ordinal)];
            }

            await connection.SubscribeMetricsAsync(ids, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException or SessionProtocolException)
        {
            // The connection is closing; the bridge attaches the next one, which is subscribed again.
        }
        finally
        {
            try
            {
                _sendGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Disposed while sending.
            }
        }
    }
}
