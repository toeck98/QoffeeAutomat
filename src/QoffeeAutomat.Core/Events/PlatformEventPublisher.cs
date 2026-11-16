using System.Text.Json;
using System.Threading.Channels;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Events;

/// <summary>
/// Turns changes of the session state and of the power state into events on the bus: user logged in
/// and out, session locked and unlocked, session ready and resumed. The events are published in the
/// order of the changes, from a background loop, so the platform callbacks return at once.
/// </summary>
public sealed class PlatformEventPublisher : IDisposable
{
    private readonly object _gate = new();
    private readonly IEventBus _bus;
    private readonly ISessionBridge? _session;
    private readonly IPowerEvents? _power;
    private readonly Channel<(string Type, JsonElement? Data)> _queue = Channel.CreateUnbounded<(string, JsonElement?)>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private SessionState _previous;
    private bool _disposed;

    /// <summary>Starts publishing.</summary>
    /// <param name="bus">The bus to publish on.</param>
    /// <param name="session">The session bridge, or null when the platform has none.</param>
    /// <param name="power">The power events, or null when the platform has none.</param>
    public PlatformEventPublisher(IEventBus bus, ISessionBridge? session, IPowerEvents? power)
    {
        ArgumentNullException.ThrowIfNull(bus);

        _bus = bus;
        _session = session;
        _power = power;
        _previous = session?.State ?? SessionState.None;

        if (session is not null)
        {
            session.StateChanged += OnSessionStateChanged;
        }

        if (power is not null)
        {
            power.PowerChanged += OnPowerChanged;
        }

        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Stops publishing and waits for the loop to end; events still queued are dropped.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (_session is not null)
        {
            _session.StateChanged -= OnSessionStateChanged;
        }

        if (_power is not null)
        {
            _power.PowerChanged -= OnPowerChanged;
        }

        _queue.Writer.TryComplete();
        _stop.Cancel();
        try
        {
            _loop.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // The loop was stopped on purpose.
        }

        _stop.Dispose();
    }

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        SessionState previous;
        lock (_gate)
        {
            previous = _previous;
            _previous = e.State;
        }

        foreach ((string type, JsonElement? data) in Transitions(previous, e.State))
        {
            _queue.Writer.TryWrite((type, data));
        }
    }

    private void OnPowerChanged(object? sender, PowerEventArgs e)
    {
        if (e.Kind == PowerEventKind.Resume)
        {
            _queue.Writer.TryWrite((EventTypes.Resumed, null));
        }
    }

    private static IEnumerable<(string Type, JsonElement? Data)> Transitions(SessionState previous, SessionState current)
    {
        bool sameUser = previous.UserName is not null
            && string.Equals(previous.UserName, current.UserName, StringComparison.Ordinal);

        if (previous.UserName is not null && !sameUser)
        {
            yield return (EventTypes.UserLoggedOut, EventData.Create(("user", previous.UserName)));
        }

        if (current.UserName is not null && !sameUser)
        {
            yield return (EventTypes.UserLoggedIn, EventData.Create(("user", current.UserName)));
        }

        if (sameUser && previous.IsLocked != current.IsLocked)
        {
            yield return (
                current.IsLocked ? EventTypes.SessionLocked : EventTypes.SessionUnlocked,
                EventData.Create(("user", current.UserName)));
        }

        if (!previous.IsReady && current.IsReady)
        {
            yield return (EventTypes.SessionReady, EventData.Create(("user", current.UserName)));
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach ((string type, JsonElement? data) in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await _bus.PublishAsync(type, data, cancellationToken);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose stopped the loop.
        }
    }
}
