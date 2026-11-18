using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Linux.Actions;

/// <summary>Who cancelled a delayed power action; the value of <c>source</c> in the event <c>power_action_cancelled</c>.</summary>
public static class LinuxPowerCancelSources
{
    /// <summary>The action <c>power.cancel</c> cancelled it.</summary>
    public const string Action = "action";

    /// <summary>The run itself was cancelled or timed out while the delay ran.</summary>
    public const string Run = "run";
}

/// <summary>A delayed power action that is waiting.</summary>
/// <param name="Action">The built-in action, for example <c>power.shutdown</c>.</param>
/// <param name="Title">Its title, for messages.</param>
/// <param name="DelaySeconds">How long it waits.</param>
/// <param name="DueAt">When it is carried out.</param>
public sealed record LinuxPendingPowerAction(string Action, string Title, int DelaySeconds, DateTimeOffset DueAt);

/// <summary>
/// Holds the one delayed power action that can be pending (PLAN.md S11, T803): <c>power.shutdown</c>, <c>power.restart</c>,
/// <c>power.sleep</c>, <c>power.hibernate</c> and <c>session.logoff</c> with <c>DelaySeconds</c> greater than zero. The run of the action waits
/// here. A notification tells the logged-in user what will happen and when (there is no countdown dialog on Linux: a notification is what
/// every desktop has); <c>power.cancel</c> ends the wait, and the run then ends with the error <c>cancelled</c> and the event
/// <c>power_action_cancelled</c> is raised. When the time is over the action is carried out.
/// </summary>
public sealed partial class LinuxPowerScheduler
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Func<string, string, CancellationToken, ValueTask>? _notify;
    private readonly IEventBus? _events;
    private readonly ILogger? _logger;
    private PendingRun? _pending;

    /// <summary>Creates the scheduler.</summary>
    /// <param name="time">The clock; null uses the system clock.</param>
    /// <param name="notify">Shows a notification (title, text) to the logged-in user; null shows none. A failure is ignored.</param>
    /// <param name="events">Receives <c>power_action_cancelled</c>; null raises none.</param>
    /// <param name="logger">Receives the messages.</param>
    public LinuxPowerScheduler(
        TimeProvider? time = null,
        Func<string, string, CancellationToken, ValueTask>? notify = null,
        IEventBus? events = null,
        ILogger? logger = null)
    {
        _time = time ?? TimeProvider.System;
        _notify = notify;
        _events = events;
        _logger = logger;
    }

    /// <summary>The pending action; null when none waits.</summary>
    public LinuxPendingPowerAction? Pending
    {
        get
        {
            lock (_gate)
            {
                return _pending?.Info;
            }
        }
    }

    /// <summary>Waits and then carries the action out; see the class description.</summary>
    /// <param name="action">The built-in action.</param>
    /// <param name="title">Its title.</param>
    /// <param name="delaySeconds">How long to wait; greater than zero.</param>
    /// <param name="message">A text for the notification; null for the default.</param>
    /// <param name="execute">Carries the action out when the time is over.</param>
    /// <param name="cancellationToken">Cancelling it (the run was cancelled or timed out) drops the pending action.</param>
    /// <returns>The outcome of <paramref name="execute"/>; <c>busy</c> when another action waits; <c>cancelled</c> when this one was cancelled.</returns>
    public async ValueTask<ExecutionResult> RunDelayedAsync(
        string action,
        string title,
        int delaySeconds,
        string? message,
        Func<CancellationToken, ValueTask<ExecutionResult>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(delaySeconds);

        PendingRun pending;
        lock (_gate)
        {
            if (_pending is not null)
            {
                return ExecutionResult.Failure(
                    ErrorCodes.Busy,
                    $"A delayed {_pending.Info.Title.ToLowerInvariant()} is already waiting. Cancel it with power.cancel first.");
            }

            DateTimeOffset due = _time.GetUtcNow().AddSeconds(delaySeconds);
            pending = new PendingRun(new LinuxPendingPowerAction(action, title, delaySeconds, due));
            _pending = pending;
        }

        await AnnounceAsync(pending.Info, message).ConfigureAwait(false);

        using var delayLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            Task delay = Task.Delay(TimeSpan.FromSeconds(delaySeconds), _time, delayLimit.Token);
            Task finished = await Task.WhenAny(delay, pending.Cancelled.Task).ConfigureAwait(false);
            if (ReferenceEquals(finished, pending.Cancelled.Task))
            {
                await delayLimit.CancelAsync().ConfigureAwait(false);
                Release(pending);
                await PublishCancelledAsync(pending.Info, LinuxPowerCancelSources.Action).ConfigureAwait(false);
                return new ExecutionResult(
                    -1,
                    string.Empty,
                    $"The delayed {title.ToLowerInvariant()} was cancelled by power.cancel.",
                    ErrorCodes.Cancelled);
            }

            await delay.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Release(pending);
            await PublishCancelledAsync(pending.Info, LinuxPowerCancelSources.Run).ConfigureAwait(false);
            return new ExecutionResult(-1, string.Empty, $"The run ended while the {title.ToLowerInvariant()} was waiting.", ErrorCodes.Cancelled);
        }

        Release(pending);
        ExecutionResult result = await execute(cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? result with { StandardOutput = result.StandardOutput + " (after a delay of " + delaySeconds.ToString(CultureInfo.InvariantCulture) + " s)" }
            : result;
    }

    /// <summary>Cancels the pending action.</summary>
    /// <param name="source">One of <see cref="LinuxPowerCancelSources"/>.</param>
    /// <returns>What was pending; null when nothing was.</returns>
    public LinuxPendingPowerAction? Cancel(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        PendingRun? pending;
        lock (_gate)
        {
            pending = _pending;
        }

        // The run that waits ends the wait, clears the slot and raises the event, so that it happens in one place.
        pending?.Cancelled.TrySetResult(source);
        return pending?.Info;
    }

    [LoggerMessage(EventId = 3300, Level = LogLevel.Information, Message = "The delayed power action {Action} was cancelled ({Source}).")]
    private static partial void DelayedActionCancelled(ILogger logger, string action, string source);

    [LoggerMessage(EventId = 3301, Level = LogLevel.Information, Message = "The notification for the delayed power action {Action} could not be shown: {Reason}")]
    private static partial void AnnouncementFailed(ILogger logger, string action, string reason);

    private void Release(PendingRun pending)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_pending, pending))
            {
                _pending = null;
            }
        }
    }

    private async Task AnnounceAsync(LinuxPendingPowerAction info, string? message)
    {
        if (_notify is null)
        {
            return;
        }

        string text = string.IsNullOrWhiteSpace(message)
            ? $"The computer will do this in {info.DelaySeconds.ToString(CultureInfo.InvariantCulture)} seconds: {info.Title.ToLowerInvariant()}."
            : message;
#pragma warning disable CA1031 // A notification that cannot be shown must not stop the action; the delay simply elapses.
        try
        {
            await _notify(info.Title, text, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (_logger is not null)
            {
                AnnouncementFailed(_logger, info.Action, exception.Message);
            }
        }
#pragma warning restore CA1031
    }

    private async Task PublishCancelledAsync(LinuxPendingPowerAction info, string source)
    {
        if (_logger is not null)
        {
            DelayedActionCancelled(_logger, info.Action, source);
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

    private sealed class PendingRun(LinuxPendingPowerAction info)
    {
        public LinuxPendingPowerAction Info { get; } = info;

        /// <summary>Completed with the source of the cancellation; the first one wins.</summary>
        public TaskCompletionSource<string> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
