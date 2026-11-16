using System.ServiceProcess;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Windows.Service;

/// <summary>
/// The <see cref="IPowerEvents"/> of the Windows service (PLAN.md S11). The service control manager delivers
/// suspend and resume as <see cref="PowerBroadcastStatus"/> and shutdown as a separate control; the service lifetime
/// forwards them here. Handlers run synchronously on the control thread, which is what the QoffeeLink server needs:
/// the computer goes to sleep when the handler returns, so <c>bye</c> must be written (and flushed) before.
/// </summary>
/// <remarks>
/// Windows reports neither hibernate (it is a suspend) nor restart (it is a shutdown) as such. The agent knows when
/// it starts one of them itself and says so with <see cref="ExpectHibernate"/> or <see cref="ExpectRestart"/>; the
/// hint is valid for <see cref="HintLifetime"/>. A hibernate or restart started by someone else is reported as sleep
/// or shutdown.
/// </remarks>
public sealed class WindowsPowerEvents : IPowerEvents
{
    /// <summary>How long after <see cref="ExpectHibernate"/> or <see cref="ExpectRestart"/> the hint applies.</summary>
    public static readonly TimeSpan HintLifetime = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private PowerEventKind? _hint;
    private DateTimeOffset _hintAt;
    private bool _resumed;

    /// <summary>Creates the source.</summary>
    /// <param name="time">The clock; null uses the system clock.</param>
    public WindowsPowerEvents(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public event EventHandler<PowerEventArgs>? PowerChanged;

    /// <summary>Maps a notification of the control manager to a power event.</summary>
    /// <param name="status">The notification.</param>
    /// <returns>Sleep for a suspend, resume for any of the resume notifications, null for all others (battery and so on).</returns>
    public static PowerEventKind? Map(PowerBroadcastStatus status)
    {
        return status switch
        {
            PowerBroadcastStatus.Suspend => PowerEventKind.Sleep,
            PowerBroadcastStatus.ResumeSuspend or PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeCritical => PowerEventKind.Resume,
            _ => null,
        };
    }

    /// <summary>Tells that the agent is about to hibernate this computer, so the next suspend is reported as hibernate.</summary>
    public void ExpectHibernate() => SetHint(PowerEventKind.Hibernate);

    /// <summary>Tells that the agent is about to restart this computer, so the next shutdown is reported as restart.</summary>
    public void ExpectRestart() => SetHint(PowerEventKind.Restart);

    /// <summary>Handles a power notification of the control manager.</summary>
    /// <param name="status">The notification.</param>
    public void Handle(PowerBroadcastStatus status)
    {
        if (Map(status) is not { } kind)
        {
            return;
        }

        if (kind == PowerEventKind.Resume)
        {
            // Windows sends up to three resume notifications for one wake-up; the first one counts.
            lock (_gate)
            {
                if (_resumed)
                {
                    return;
                }

                _resumed = true;
            }

            Raise(PowerEventKind.Resume);
            return;
        }

        lock (_gate)
        {
            _resumed = false;
        }

        Raise(TakeHint(PowerEventKind.Hibernate) ? PowerEventKind.Hibernate : PowerEventKind.Sleep);
    }

    /// <summary>Handles the shutdown control of the service control manager.</summary>
    public void HandleShutdown()
    {
        Raise(TakeHint(PowerEventKind.Restart) ? PowerEventKind.Restart : PowerEventKind.Shutdown);
    }

    private void SetHint(PowerEventKind kind)
    {
        lock (_gate)
        {
            _hint = kind;
            _hintAt = _time.GetUtcNow();
        }
    }

    private bool TakeHint(PowerEventKind kind)
    {
        lock (_gate)
        {
            bool fresh = _time.GetUtcNow() - _hintAt <= HintLifetime;
            bool matches = _hint == kind && fresh;

            // A hint is used up by the event it announced, and an old one is dropped.
            if (matches || !fresh)
            {
                _hint = null;
            }

            return matches;
        }
    }

    private void Raise(PowerEventKind kind)
    {
        EventHandler<PowerEventArgs>? handlers = PowerChanged;
        if (handlers is null)
        {
            return;
        }

        // One failing subscriber must not keep the others from saying goodbye before the computer sleeps.
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<PowerEventArgs>)handler)(this, new PowerEventArgs(kind));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A handler's failure is its own to log; the power transition goes on.
            }
        }
    }
}
