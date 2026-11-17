using System.ComponentModel;
using QoffeeAutomat.Platform.Windows.Actions.Power;
using QoffeeAutomat.SessionHost.Dialogs;

namespace QoffeeAutomat.Platform.Windows.Tests.Actions;

/// <summary>
/// A clock that only moves when the test calls <see cref="Advance"/>: the timers of <see cref="TimeProvider.CreateTimer"/> (and with
/// them <c>Task.Delay(delay, timeProvider)</c>) fire when the clock passes their due time, in the order of their due times. No test of the
/// power delay waits for real time, and none can ever run into a real shutdown, because the power calls go to fakes as well.
/// </summary>
internal sealed class FakeClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The number of timers that are armed.</summary>
    public int ArmedTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(timer => timer.Due is not null);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward and runs every timer that falls due on the way, each at its own due time.</summary>
    /// <param name="by">How far to move.</param>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + by;
        }

        while (true)
        {
            FakeTimer? next;
            lock (_gate)
            {
                next = _timers.Where(timer => timer.Due is { } due && due <= target).MinBy(timer => timer.Due);
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.Due!.Value;
                next.Rearm(_now);
            }

            next.Fire();
        }
    }

    private void Remove(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private DateTimeOffset Now()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    private sealed class FakeTimer(FakeClock clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Now() + dueTime;
            return true;
        }

        public void Rearm(DateTimeOffset firedAt)
        {
            Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : firedAt + _period;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            Due = null;
            clock.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>An <see cref="IPowerRequestApi"/> that records the calls and never holds the computer awake.</summary>
internal sealed class FakePowerRequestApi : IPowerRequestApi
{
    /// <summary>Thrown by <see cref="Acquire"/> after it was recorded, to test the refusal of Windows.</summary>
    public Exception? Refusal { get; set; }

    public List<string> Calls { get; } = [];

    /// <summary>True while a request is held, as Windows would see it.</summary>
    public bool Held { get; private set; }

    public bool DisplayHeld { get; private set; }

    public void Acquire(bool keepDisplayOn)
    {
        Calls.Add(keepDisplayOn ? "acquire:display" : "acquire");
        if (Refusal is not null)
        {
            throw Refusal;
        }

        Held = true;
        DisplayHeld = keepDisplayOn;
    }

    public void Release()
    {
        Calls.Add("release");
        Held = false;
        DisplayHeld = false;
    }

    public static Win32Exception Denied() => new(5, "Access is denied.");
}

/// <summary>An <see cref="ICountdownPresenter"/> whose dialog the test controls: it stays open until the test cancels it or the agent closes it.</summary>
internal sealed class FakeCountdownPresenter : ICountdownPresenter
{
    private TaskCompletionSource<CountdownDialogResult>? _open;

    /// <summary>When set, the dialog ends at once with this result (for example <see cref="CountdownDialogResult.Unavailable"/> without a user).</summary>
    public CountdownDialogResult? Immediate { get; set; }

    /// <summary>When set, showing the dialog throws this exception.</summary>
    public Exception? Failure { get; set; }

    public List<CountdownRequest> Requests { get; } = [];

    /// <summary>True when the agent closed the dialog through the cancellation token.</summary>
    public bool ClosedByAgent { get; private set; }

    public bool IsOpen => _open is { Task.IsCompleted: false };

    /// <summary>The user presses the cancel button.</summary>
    public void PressCancel() => _open?.TrySetResult(CountdownDialogResult.Cancelled);

    /// <summary>The dialog ends on its own, without a decision of the user.</summary>
    public void EndByItself() => _open?.TrySetResult(CountdownDialogResult.Closed);

    public async ValueTask<CountdownDialogResult> ShowAsync(CountdownRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (Failure is not null)
        {
            throw Failure;
        }

        if (Immediate is { } result)
        {
            return result;
        }

        var open = new TaskCompletionSource<CountdownDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _open = open;
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            ClosedByAgent = true;
            open.TrySetResult(CountdownDialogResult.Closed);
        });
        return await open.Task;
    }
}

/// <summary>An <see cref="ICountdownDialog"/> for the SessionHost whose window the test controls; nothing is ever shown on a desktop.</summary>
internal sealed class FakeCountdownDialog : ICountdownDialog
{
    private TaskCompletionSource<CountdownDialogOutcome>? _open;

    public List<CountdownDialogRequest> Requests { get; } = [];

    /// <summary>True when the agent closed the window through the cancellation token.</summary>
    public bool ClosedByAgent { get; private set; }

    /// <summary>The user presses the cancel button.</summary>
    public void PressCancel() => _open?.TrySetResult(CountdownDialogOutcome.Cancelled);

    public async Task<CountdownDialogOutcome> ShowAsync(CountdownDialogRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var open = new TaskCompletionSource<CountdownDialogOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _open = open;
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            ClosedByAgent = true;
            open.TrySetResult(CountdownDialogOutcome.Closed);
        });
        return await open.Task;
    }
}
