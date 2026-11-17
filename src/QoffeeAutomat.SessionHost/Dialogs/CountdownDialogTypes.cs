using System.Globalization;

namespace QoffeeAutomat.SessionHost.Dialogs;

/// <summary>What the countdown dialog shows (PLAN.md S11, T715).</summary>
/// <param name="Title">The headline, the title of the action, for example <c>Shut down</c>.</param>
/// <param name="Message">The text of the caller; null or blank shows a standard sentence instead.</param>
/// <param name="Duration">The time that is left when the dialog opens.</param>
public sealed record CountdownDialogRequest(string Title, string? Message, TimeSpan Duration);

/// <summary>How the countdown dialog ended.</summary>
public enum CountdownDialogOutcome
{
    /// <summary>The time was over.</summary>
    Elapsed,

    /// <summary>The user pressed the cancel button (or Esc).</summary>
    Cancelled,

    /// <summary>The dialog was closed from outside (the agent cancelled the run) or could not be shown.</summary>
    Closed,
}

/// <summary>
/// Shows the countdown dialog on the desktop of the user: a topmost window with the message, the remaining time and a cancel
/// button (PLAN.md S11, T715). The seam in front of Windows Forms (<see cref="WinFormsCountdownDialog"/>), so that the action
/// <c>power.countdown</c> is tested without a desktop.
/// </summary>
public interface ICountdownDialog
{
    /// <summary>Shows the dialog and waits until it ends.</summary>
    /// <param name="request">What to show.</param>
    /// <param name="cancellationToken">Closes the dialog; the call then ends with <see cref="CountdownDialogOutcome.Closed"/>.</param>
    /// <returns>How the dialog ended.</returns>
    /// <exception cref="InvalidOperationException">The dialog could not be shown (no desktop).</exception>
    Task<CountdownDialogOutcome> ShowAsync(CountdownDialogRequest request, CancellationToken cancellationToken);
}

/// <summary>The texts of the countdown dialog; plain functions, so the formatting is tested without a desktop.</summary>
public static class CountdownText
{
    /// <summary>The text of the cancel button.</summary>
    public const string CancelButton = "Cancel";

    /// <summary>The caption of the window.</summary>
    /// <param name="title">The title of the action.</param>
    /// <returns>The caption.</returns>
    public static string Caption(string title) => "QoffeeAutomat - " + title;

    /// <summary>The text under the headline: the message of the caller, or a sentence that says what is going to happen.</summary>
    /// <param name="title">The title of the action.</param>
    /// <param name="message">The message of the caller, or null.</param>
    /// <returns>The text.</returns>
    public static string Body(string title, string? message)
    {
        return string.IsNullOrWhiteSpace(message)
            ? title + " was requested and is going to happen when the time is over. Press Cancel to stop it."
            : message.Trim();
    }

    /// <summary>The remaining time as <c>m:ss</c>, or <c>h:mm:ss</c> from one hour; a part of a second counts as a whole one.</summary>
    /// <param name="remaining">The time that is left; zero or less shows <c>0:00</c>.</param>
    /// <returns>The text.</returns>
    public static string Remaining(TimeSpan remaining)
    {
        long seconds = remaining <= TimeSpan.Zero ? 0 : (long)Math.Ceiling(remaining.TotalSeconds);
        long hours = seconds / 3600;
        long minutes = seconds % 3600 / 60;
        long rest = seconds % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{rest:00}");
    }
}
