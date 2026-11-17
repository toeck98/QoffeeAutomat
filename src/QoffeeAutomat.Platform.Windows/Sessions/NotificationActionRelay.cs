using System.Text.Json;
using Microsoft.Extensions.Hosting;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Events;

namespace QoffeeAutomat.Platform.Windows.Sessions;

/// <summary>
/// Turns the button presses of notifications into events (PLAN.md S11, S16, T702). A SessionHost reports a press as the
/// <c>event</c> message <c>notification_action</c> with the data <c>action</c> (the id of the button) and, when the notification
/// had one, <c>tag</c>; the relay publishes it as the catalogued event <see cref="EventTypes.NotificationAction"/> with the same
/// data, which Home Assistant receives like any other event.
/// </summary>
/// <remarks>
/// A SessionHost can only be the real one (the agent checks the image of the peer and the session when it connects), and what it
/// reports is still checked: the action and the tag must be valid ids (<see cref="NotifyToastSpec.IsValidId"/>), so the event
/// stream never carries free text from the session.
/// </remarks>
public sealed class NotificationActionRelay : IHostedService, IDisposable
{
    private readonly WindowsSessionManager _manager;
    private readonly IEventBus _bus;
    private readonly object _gate = new();
    private readonly HashSet<SessionConnection> _attached = [];
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Creates the relay.</summary>
    /// <param name="manager">Holds the connections of the SessionHosts.</param>
    /// <param name="bus">Receives the <c>notification_action</c> events.</param>
    public NotificationActionRelay(WindowsSessionManager manager, IEventBus bus)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(bus);

        _manager = manager;
        _bus = bus;
    }

    /// <summary>Reads the data of a <c>notification_action</c> event of a session.</summary>
    /// <param name="message">The <c>event</c> message.</param>
    /// <param name="action">The id of the button.</param>
    /// <param name="tag">The tag of the notification, or null.</param>
    /// <returns>False when the message is another event or its data is not valid.</returns>
    public static bool TryRead(SessionEventMessage message, out string action, out string? tag)
    {
        ArgumentNullException.ThrowIfNull(message);

        action = string.Empty;
        tag = null;
        if (message.Type != SessionNotificationEvents.Action || message.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            return false;
        }

        if (!data.TryGetProperty("action", out JsonElement actionElement)
            || actionElement.ValueKind != JsonValueKind.String
            || actionElement.GetString() is not { } candidate
            || !NotifyToastSpec.IsValidId(candidate))
        {
            return false;
        }

        if (data.TryGetProperty("tag", out JsonElement tagElement) && tagElement.ValueKind != JsonValueKind.Null)
        {
            if (tagElement.ValueKind != JsonValueKind.String || tagElement.GetString() is not { } tagText || !NotifyToastSpec.IsValidId(tagText))
            {
                return false;
            }

            tag = tagText;
        }

        action = candidate;
        return true;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _manager.ConnectionsChanged += OnConnectionsChanged;
        Attach();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _manager.ConnectionsChanged -= OnConnectionsChanged;
        await _stop.CancelAsync().ConfigureAwait(false);
        lock (_gate)
        {
            foreach (SessionConnection connection in _attached)
            {
                connection.EventReceived -= OnEvent;
            }

            _attached.Clear();
        }
    }

    private void OnConnectionsChanged(object? sender, EventArgs e) => Attach();

    private void Attach()
    {
        IReadOnlyList<SessionConnection> open = _manager.GetConnections();
        lock (_gate)
        {
            foreach (SessionConnection connection in open)
            {
                if (_attached.Add(connection))
                {
                    connection.EventReceived += OnEvent;
                }
            }

            foreach (SessionConnection connection in _attached.Where(connection => !open.Contains(connection)).ToArray())
            {
                connection.EventReceived -= OnEvent;
                _attached.Remove(connection);
            }
        }
    }

    private void OnEvent(object? sender, SessionHostEventArgs e)
    {
        if (TryRead(e.Message, out string action, out string? tag))
        {
            _ = PublishAsync(action, tag);
        }
    }

    private async Task PublishAsync(string action, string? tag)
    {
        try
        {
            JsonElement data = EventData.Create(("action", action), ("tag", tag));
            await _bus.PublishAsync(EventTypes.NotificationAction, data, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // The agent is stopping.
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stop.Dispose();
}
