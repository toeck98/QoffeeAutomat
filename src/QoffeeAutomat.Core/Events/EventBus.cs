using System.Text.Json;
using System.Threading.Channels;

namespace QoffeeAutomat.Core.Events;

/// <summary>The in-process event bus (PLAN.md T217): publishers raise events, subscribers receive them.</summary>
public interface IEventBus
{
    /// <summary>
    /// Publishes an event: catalogued types are stored first, then the event is delivered to every
    /// subscription whose filter matches, exactly once. A failing store does not stop the delivery.
    /// </summary>
    /// <param name="type">The event type, see <see cref="EventTypes"/>.</param>
    /// <param name="data">Optional data; copied, so the caller may dispose the document it came from.</param>
    /// <param name="cancellationToken">Cancels the call before the event is stored.</param>
    /// <returns>The published event, with its id when it was stored.</returns>
    Task<AgentEvent> PublishAsync(string type, JsonElement? data = null, CancellationToken cancellationToken = default);

    /// <summary>Starts receiving events. Registration happens immediately, so nothing published afterwards is missed.</summary>
    /// <param name="types">Only these event types, or null for all.</param>
    /// <returns>The subscription; dispose it to stop receiving.</returns>
    EventSubscription Subscribe(IReadOnlyCollection<string>? types = null);

    /// <summary>Reads the stored recent events.</summary>
    /// <param name="limit">The most events to return.</param>
    /// <param name="type">Only events of this type, or null for all.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The events, newest first; empty when the bus has no store.</returns>
    Task<IReadOnlyList<AgentEvent>> GetRecentAsync(int limit, string? type = null, CancellationToken cancellationToken = default);
}

/// <summary>A stream of <see cref="AgentEvent"/> values for one subscriber.</summary>
public sealed class EventSubscription : IDisposable
{
    private const int Capacity = 1024;

    private readonly Action<EventSubscription> _onDispose;
    private readonly Channel<AgentEvent> _channel;
    private readonly HashSet<string>? _types;

    internal EventSubscription(Action<EventSubscription> onDispose, IReadOnlyCollection<string>? types)
    {
        _onDispose = onDispose;
        _types = types is null ? null : new HashSet<string>(types, StringComparer.Ordinal);
        _channel = Channel.CreateBounded<AgentEvent>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>The events in the order they were published. The channel completes when the subscription or the bus is disposed. A subscriber that falls more than 1024 events behind loses the oldest ones.</summary>
    public ChannelReader<AgentEvent> Events => _channel.Reader;

    /// <inheritdoc />
    public void Dispose()
    {
        _onDispose(this);
        _channel.Writer.TryComplete();
    }

    internal void Post(AgentEvent value)
    {
        if (_types is null || _types.Contains(value.Type))
        {
            _channel.Writer.TryWrite(value);
        }
    }

    internal void Complete()
    {
        _channel.Writer.TryComplete();
    }
}

/// <summary>Raised when the event store could not store an event.</summary>
public sealed class EventStoreFailedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="type">The type of the event that was not stored.</param>
    /// <param name="exception">What went wrong.</param>
    public EventStoreFailedEventArgs(string type, Exception exception)
    {
        Type = type;
        Exception = exception;
    }

    /// <summary>The type of the event that was not stored.</summary>
    public string Type { get; }

    /// <summary>What went wrong.</summary>
    public Exception Exception { get; }
}

/// <summary>
/// The default <see cref="IEventBus"/>. Publishing is serialised, so the order in which events are
/// stored is the order in which every subscriber sees them. Delivery never blocks: each subscriber has
/// its own bounded channel.
/// </summary>
public sealed class EventBus : IEventBus, IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly TimeProvider _time;
    private readonly IEventStore? _store;
    private EventSubscription[] _subscribers = [];
    private bool _disposed;

    /// <summary>Creates the bus.</summary>
    /// <param name="time">The clock for the event timestamps.</param>
    /// <param name="store">Where catalogued events are stored; null keeps events in flight only.</param>
    public EventBus(TimeProvider time, IEventStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(time);

        _time = time;
        _store = store;
    }

    /// <summary>Raised when storing an event failed; the event was still delivered. Handlers log and must not throw.</summary>
    public event EventHandler<EventStoreFailedEventArgs>? StoreFailed;

    /// <inheritdoc />
    public async Task<AgentEvent> PublishAsync(string type, JsonElement? data = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        ObjectDisposedException.ThrowIf(_disposed, this);

        JsonElement? copy = data is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } element
            ? element.Clone()
            : null;
        DateTimeOffset timestamp = _time.GetUtcNow();

        await _publishGate.WaitAsync(cancellationToken);
        try
        {
            long id = 0;
            if (_store is not null && EventCatalog.Contains(type))
            {
                try
                {
                    id = await _store.AddAsync(type, timestamp, copy, cancellationToken);
                }
#pragma warning disable CA1031 // A broken database must not silence the events; the failure is reported and delivery goes on.
                catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    StoreFailed?.Invoke(this, new EventStoreFailedEventArgs(type, exception));
                }
            }

            var published = new AgentEvent(id, type, timestamp, copy);
            Deliver(published);
            return published;
        }
        finally
        {
            _publishGate.Release();
        }
    }

    /// <inheritdoc />
    public EventSubscription Subscribe(IReadOnlyCollection<string>? types = null)
    {
        var subscription = new EventSubscription(Remove, types);
        lock (_gate)
        {
            if (_disposed)
            {
                subscription.Complete();
            }
            else
            {
                _subscribers = [.. _subscribers, subscription];
            }
        }

        return subscription;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentEvent>> GetRecentAsync(int limit, string? type = null, CancellationToken cancellationToken = default)
    {
        if (_store is null)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
            return Task.FromResult<IReadOnlyList<AgentEvent>>([]);
        }

        return _store.GetRecentAsync(limit, type, cancellationToken);
    }

    /// <summary>Completes every subscription. Publishing afterwards throws.</summary>
    public void Dispose()
    {
        EventSubscription[] snapshot;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            snapshot = _subscribers;
            _subscribers = [];
        }

        foreach (EventSubscription subscriber in snapshot)
        {
            subscriber.Complete();
        }
    }

    private void Deliver(AgentEvent published)
    {
        EventSubscription[] snapshot;
        lock (_gate)
        {
            snapshot = _subscribers;
        }

        foreach (EventSubscription subscriber in snapshot)
        {
            subscriber.Post(published);
        }
    }

    private void Remove(EventSubscription subscription)
    {
        lock (_gate)
        {
            _subscribers = [.. _subscribers.Where(existing => !ReferenceEquals(existing, subscription))];
        }
    }
}
