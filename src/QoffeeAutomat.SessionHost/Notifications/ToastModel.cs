namespace QoffeeAutomat.SessionHost.Notifications;

/// <summary>A button of a notification.</summary>
/// <param name="Id">The id that comes back in the event <c>notification_action</c>.</param>
/// <param name="Label">The text on the button.</param>
public sealed record ToastButton(string Id, string Label);

/// <summary>A notification to show on the desktop of the user (PLAN.md S11, S16, T702).</summary>
/// <param name="Title">The title, or null.</param>
/// <param name="Message">The text.</param>
/// <param name="Buttons">The buttons, at most <see cref="QoffeeAutomat.Contracts.Config.NotifyToastSpec.MaxButtons"/>.</param>
/// <param name="Tag">The tag that comes back with a button press; a new notification with the same tag replaces the old one.</param>
/// <param name="ImagePath">The full path of a picture file, or null.</param>
public sealed record ToastRequest(
    string? Title,
    string Message,
    IReadOnlyList<ToastButton> Buttons,
    string? Tag = null,
    string? ImagePath = null);

/// <summary>Raised when the user presses a button of a notification.</summary>
public sealed class ToastActionEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="action">The id of the button.</param>
    /// <param name="tag">The tag of the notification, or null.</param>
    public ToastActionEventArgs(string action, string? tag)
    {
        ArgumentException.ThrowIfNullOrEmpty(action);

        Action = action;
        Tag = tag;
    }

    /// <summary>The id of the button.</summary>
    public string Action { get; }

    /// <summary>The tag of the notification, or null.</summary>
    public string? Tag { get; }
}

/// <summary>
/// Shows notifications on the desktop; the seam in front of the Windows Runtime (<see cref="WindowsToastPresenter"/>) so that the
/// action <c>notify.toast</c> is tested without a desktop.
/// </summary>
public interface IToastPresenter
{
    /// <summary>Raised when the user presses a button. Handlers must return quickly; the call comes from a thread of the pool.</summary>
    event EventHandler<ToastActionEventArgs>? ActionInvoked;

    /// <summary>Shows a notification.</summary>
    /// <param name="request">What to show.</param>
    /// <returns>Null when it was handed to Windows, otherwise why that was not possible (for example notifications are switched off).</returns>
    string? Show(ToastRequest request);
}
