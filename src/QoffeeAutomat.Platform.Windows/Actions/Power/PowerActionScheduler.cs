using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Windows.Actions.Power;

/// <summary>What the user did with the countdown dialog, as <see cref="ICountdownPresenter.ShowAsync"/> reports it.</summary>
public enum CountdownDialogResult
{
    /// <summary>The dialog ended without a decision of the user: its time was over, or the agent closed it. The countdown goes on.</summary>
    Closed,

    /// <summary>The user pressed the cancel button: the pending action is cancelled.</summary>
    Cancelled,

    /// <summary>No dialog could be shown, because nobody is logged in or the session helper failed. The delay simply elapses.</summary>
    Unavailable,
}

/// <summary>What the countdown dialog shows.</summary>
/// <param name="Action">The name of the action, for example <c>power.shutdown</c>.</param>
/// <param name="Title">The headline, for example <c>Shut down</c>.</param>
/// <param name="Message">The text of the caller; null or empty shows only the headline and the remaining time.</param>
/// <param name="Seconds">The time that is left when the dialog opens.</param>
public sealed record CountdownRequest(string Action, string Title, string? Message, int Seconds);

/// <summary>
/// Shows the countdown dialog to the user (PLAN.md S11, T715); the seam between the scheduler and the SessionHost, so that the
/// pending and cancel logic is tested without a desktop. The real implementation is <see cref="SessionHostCountdownPresenter"/>.
/// </summary>
public interface ICountdownPresenter
{
    /// <summary>Shows the dialog and waits until it ends.</summary>
    /// <param name="request">What to show.</param>
    /// <param name="cancellationToken">Closes the dialog; the call then ends with <see cref="CountdownDialogResult.Closed"/>.</param>
    /// <returns>How the dialog ended. It never throws for a problem with the session.</returns>
    ValueTask<CountdownDialogResult> ShowAsync(CountdownRequest request, CancellationToken cancellationToken);
}

/// <summary>The delayed power action that is waiting.</summary>
/// <param name="Action">The name of the action, for example <c>power.shutdown</c>.</param>
/// <param name="Title">The title of the action, for example <c>Shut down</c>.</param>
/// <param name="DelaySeconds">The delay that was asked for.</param>
/// <param name="StartedAt">When the countdown started.</param>
/// <param name="DueAt">When the action acts.</param>
public sealed record PendingPowerAction(string Action, string Title, int DelaySeconds, DateTimeOffset StartedAt, DateTimeOffset DueAt);

/// <summary>The values of <c>source</c> in the data of the event <c>power_action_cancelled</c>.</summary>
public static class PowerCancelSources
{
    /// <summary>The user pressed the cancel button of the countdown dialog.</summary>
    public const string Dialog = "dialog";

    /// <summary>The action <c>power.cancel</c> cancelled it.</summary>
    public const string Action = "action";

    /// <summary>The run itself was cancelled or timed out while the countdown ran.</summary>
    public const string Run = "run";
}

/// <summary>
/// Holds the one delayed power action that can be pending (PLAN.md S11, S16, T715): <c>power.shutdown</c>, <c>power.restart</c>,
/// <c>power.sleep</c>, <c>power.hibernate</c> and <c>session.logoff</c> with <c>DelaySeconds</c> greater than zero. The run of the
/// action waits here: with a logged-in user the presenter shows a topmost dialog with the message, the remaining time and a cancel
/// button; without one the delay simply elapses. When the time is over the action is carried out; when the user cancels in the
/// dialog, <c>power.cancel</c> is run or the run itself is cancelled, the run ends with the error <c>cancelled</c> and the event
/// <c>power_action_cancelled</c> is published. A second delayed action while one is pending is refused with <c>busy</c>.
/// </summary>
/// <remarks>All waiting uses the injected <see cref="TimeProvider"/>, so the tests move a fake clock instead of waiting.</remarks>
public sealed class PowerActionScheduler
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly ICountdownPresenter? _presenter;
    private readonly IEventBus? _events;
    private readonly ILogger? _logger;
    private Pending? _pending;

    /// <summary>Creates the scheduler.</summary>
    /// <param name="time">The clock of the countdown; null uses the system clock.</param>
    /// <param name="presenter">Shows the countdown dialog; null means that no dialog is ever shown and the delay simply elapses.</param>
    /// <param name="events">Receives <c>power_action_cancelled</c>; null publishes nothing.</param>
    /// <param name="logger">Receives the diagnostics.</param>
    public PowerActionScheduler(TimeProvider? time = null, ICountdownPresenter? presenter = null, IEventBus? events = null, ILogger? logger = null)
    {
        _time = time ?? TimeProvider.System;
        _presenter = presenter;
        _events = events;
        _logger = logger;
    }

    /// <summary>The pending action; null while nothing waits.</summary>
    public PendingPowerAction? Current
    {
        get
        {
            lock (_gate)
            {
                return _pending?.Info;
            }
        }
    }

    /// <summary>
    /// Waits for the delay and then carries the action out, or ends with <see cref="ErrorCodes.Cancelled"/> when it is cancelled
    /// first. The slot of the pending action is free again before <paramref name="execute"/> runs.
    /// </summary>
    /// <param name="action">The name of the action, for example <c>power.shutdown</c>.</param>
    /// <param name="title">The title of the action, for example <c>Shut down</c>; it is the headline of the dialog.</param>
    /// <param name="delaySeconds">1 to <see cref="PowerActionSpec.MaxDelaySeconds"/>.</param>
    /// <param name="message">The text of the dialog, or null.</param>
    /// <param name="execute">Carries the action out when the time is over; it gets the token of the run.</param>
    /// <param name="cancellationToken">Cancels the run (a timeout of the command, a cancel of the caller).</param>
    /// <returns>
    /// The result of <paramref name="execute"/>; <see cref="ErrorCodes.Cancelled"/> when the action was cancelled; <see cref="ErrorCodes.Busy"/>
    /// when another delayed action is pending; <see cref="ErrorCodes.InvalidVars"/> when the delay is out of range.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; the event is published first.</exception>
    public async ValueTask<ExecutionResult> RunDelayedAsync(
        string action,
        string title,
        int delaySeconds,
        string? message,
        Func<CancellationToken, ValueTask<ExecutionResult>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(execute);

        if (delaySeconds is < 1 or > PowerActionSpec.MaxDelaySeconds)
        {
            return ExecutionResult.Failure(
                ErrorCodes.InvalidVars,
                "DelaySeconds must be a whole number from 0 to " + PowerActionSpec.MaxDelaySeconds.ToString(CultureInfo.InvariantCulture) + ".");
        }

        DateTimeOffset now = _time.GetUtcNow();
        var info = new PendingPowerAction(action, title, delaySeconds, now, now.AddSeconds(delaySeconds));
        Pending entry;
        lock (_gate)
        {
            if (_pending is { } existing)
            {
                return ExecutionResult.Failure(
                    ErrorCodes.Busy,
                    "A delayed power action is already pending (" + existing.Info.Title + ", due at "
                    + existing.Info.DueAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    + " UTC). Only one can wait at a time: cancel it with power.cancel or in the countdown dialog first.");
            }

            entry = new Pending(info);
            _pending = entry;
        }

        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        string? cancelledBy;
        try
        {
            cancelledBy = await WaitAsync(entry, message, run).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Release(entry);
            await run.CancelAsync().ConfigureAwait(false);
            await PublishCancelledAsync(info, PowerCancelSources.Run).ConfigureAwait(false);
            throw;
        }

        Release(entry);
        await run.CancelAsync().ConfigureAwait(false); // closes the dialog, whatever ended the countdown

        // A cancel that arrived while the time ran out came before the slot was freed (Cancel only acts on a pending entry), so
        // it is honoured: whoever was told "cancelled" must not see the action carried out.
        if (cancelledBy is null && entry.Cancelled.Task.IsCompletedSuccessfully)
        {
            cancelledBy = entry.Cancelled.Task.Result;
        }

        if (cancelledBy is not null)
        {
            await PublishCancelledAsync(info, cancelledBy).ConfigureAwait(false);
            return ExecutionResult.Failure(
                ErrorCodes.Cancelled,
                $"The delayed {title.ToLowerInvariant()} was cancelled ({Describe(cancelledBy)}); nothing was done.");
        }

        return await execute(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cancels the pending action, if there is one; its run ends with <see cref="ErrorCodes.Cancelled"/> and the event is published.</summary>
    /// <param name="source">One of <see cref="PowerCancelSources"/>.</param>
    /// <returns>The action that was pending; null when nothing was pending (or it was already being cancelled).</returns>
    public PendingPowerAction? Cancel(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        lock (_gate)
        {
            return _pending is { } entry && entry.Cancelled.TrySetResult(source) ? entry.Info : null;
        }
    }

    private static string Describe(string source) => source switch
    {
        PowerCancelSources.Dialog => "by the user in the countdown dialog",
        PowerCancelSources.Action => "by power.cancel",
        _ => "the run was cancelled",
    };

    private async Task<string?> WaitAsync(Pending entry, string? message, CancellationTokenSource run)
    {
        // The timer and the dialog both stop with the linked token; the timer throws then, which is what the caller sees.
        Task timer = Task.Delay(TimeSpan.FromSeconds(entry.Info.DelaySeconds), _time, run.Token);
        Task<CountdownDialogResult>? dialog = _presenter is null
            ? null
            : ShowDialogAsync(new CountdownRequest(entry.Info.Action, entry.Info.Title, message, entry.Info.DelaySeconds), run.Token);

        while (true)
        {
            Task finished = dialog is null
                ? await Task.WhenAny(timer, entry.Cancelled.Task).ConfigureAwait(false)
                : await Task.WhenAny(timer, entry.Cancelled.Task, dialog).ConfigureAwait(false);

            if (ReferenceEquals(finished, entry.Cancelled.Task))
            {
                return await entry.Cancelled.Task.ConfigureAwait(false);
            }

            if (ReferenceEquals(finished, timer))
            {
                await timer.ConfigureAwait(false); // throws when the run was cancelled
                return null;
            }

            // The dialog ended. Only a press of the cancel button changes anything; a dialog that could not be shown or
            // that closed on its own leaves the countdown to the clock.
            CountdownDialogResult result = await dialog!.ConfigureAwait(false);
            dialog = null;
            if (result == CountdownDialogResult.Cancelled)
            {
                return PowerCancelSources.Dialog;
            }
        }
    }

#pragma warning disable CA1031 // A dialog that fails must never stop the countdown: without it the delay simply elapses.
    private async Task<CountdownDialogResult> ShowDialogAsync(CountdownRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _presenter!.ShowAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CountdownDialogResult.Closed;
        }
        catch (Exception exception)
        {
            if (_logger is not null)
            {
                PowerLog.CountdownDialogFailed(_logger, request.Action, exception.Message);
            }

            return CountdownDialogResult.Unavailable;
        }
    }
#pragma warning restore CA1031

    private void Release(Pending entry)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_pending, entry))
            {
                _pending = null;
            }
        }
    }

    private async Task PublishCancelledAsync(PendingPowerAction info, string source)
    {
        if (_logger is not null)
        {
            PowerLog.DelayedActionCancelled(_logger, info.Action, source);
        }

        if (_events is null)
        {
            return;
        }

        try
        {
            JsonElement data = EventData.Create(
                ("action", info.Action),
                ("source", source),
                ("delay_seconds", info.DelaySeconds));
            await _events.PublishAsync(EventTypes.PowerActionCancelled, data, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The agent is stopping.
        }
    }

    private sealed class Pending(PendingPowerAction info)
    {
        public PendingPowerAction Info { get; } = info;

        /// <summary>Completed with the source of the cancellation; the first one wins.</summary>
        public TaskCompletionSource<string> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>Source-generated log messages of the power actions (event ids 332 and 333, in the range 330 to 339 of the built-in actions).</summary>
internal static partial class PowerLog
{
    [LoggerMessage(EventId = 332, Level = LogLevel.Information, Message = "The delayed power action {Action} was cancelled ({Source}).")]
    public static partial void DelayedActionCancelled(ILogger logger, string action, string source);

    [LoggerMessage(EventId = 333, Level = LogLevel.Warning, Message = "The countdown dialog for {Action} could not be shown; the delay simply elapses: {Reason}")]
    public static partial void CountdownDialogFailed(ILogger logger, string action, string reason);
}
