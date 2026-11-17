using QoffeeAutomat.SessionHost.Ipc;

namespace QoffeeAutomat.SessionHost.Notifications;

/// <summary>
/// Connects the notifications with the agent (PLAN.md S11, S16, T702): every press of a button that the
/// <see cref="IToastPresenter"/> reports becomes the event <c>notification_action</c> with the id of the button and the tag of the
/// notification. A press while there is no connection to the agent is lost (and logged): the event is about something that just
/// happened, and a late event would trigger an automation at a moment nobody expects.
/// </summary>
public sealed class NotificationCoordinator : IDisposable
{
    private readonly SessionHostClient _client;
    private readonly IToastPresenter _presenter;
    private readonly ISessionHostLog _log;
    private int _disposed;

    /// <summary>Creates the coordinator and starts listening.</summary>
    /// <param name="client">The connection to the agent.</param>
    /// <param name="presenter">Shows the notifications and reports the button presses.</param>
    /// <param name="log">The diagnostics log.</param>
    public NotificationCoordinator(SessionHostClient client, IToastPresenter presenter, ISessionHostLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(presenter);

        _client = client;
        _presenter = presenter;
        _log = log ?? NullSessionHostLog.Instance;
        _presenter.ActionInvoked += OnActionInvoked;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _presenter.ActionInvoked -= OnActionInvoked;
    }

    private void OnActionInvoked(object? sender, ToastActionEventArgs e)
    {
        _ = ReportAsync(e.Action, e.Tag);
    }

    private async Task ReportAsync(string action, string? tag)
    {
        if (!await _client.ReportNotificationActionAsync(action, tag).ConfigureAwait(false))
        {
            _log.Warning("A notification button was pressed but the event was not sent: no connection to the agent.");
        }
    }
}
