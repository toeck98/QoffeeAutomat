namespace QoffeeAutomat.Platform.Simulated;

/// <summary>
/// A <see cref="TimeProvider"/> whose time only moves when told to (PLAN.md T204: a test clock
/// drives all timing). <see cref="Advance"/> moves the clock and runs the timers that become due, in
/// order of their due time, each at its own due time. Timers never fire on their own, not even with a
/// due time of zero; they fire during the next <see cref="Advance"/>.
/// </summary>
public sealed class SimulatedClock : TimeProvider
{
    private static readonly DateTimeOffset DefaultStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly object _gate = new();
    private readonly List<SimulatedTimer> _timers = [];
    private DateTimeOffset _utcNow;

    /// <summary>Creates a clock.</summary>
    /// <param name="start">The initial time; null starts at 2026-01-01T00:00:00Z.</param>
    public SimulatedClock(DateTimeOffset? start = null)
    {
        _utcNow = (start ?? DefaultStart).ToUniversalTime();
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    /// <summary>Moves the clock forward and runs the timers that become due.</summary>
    /// <param name="by">How far to move; must not be negative.</param>
    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(by), "A clock cannot move backwards by Advance.");
        }

        DateTimeOffset target;
        lock (_gate)
        {
            target = _utcNow + by;
        }

        AdvanceTo(target);
    }

    /// <summary>Sets the clock. A later time behaves like <see cref="Advance"/>; an earlier time just sets it without running timers.</summary>
    /// <param name="value">The new time.</param>
    public void SetUtcNow(DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        bool forward;
        lock (_gate)
        {
            forward = utc >= _utcNow;
            if (!forward)
            {
                _utcNow = utc;
            }
        }

        if (forward)
        {
            AdvanceTo(utc);
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new SimulatedTimer(this, callback, state);
        lock (_gate)
        {
            timer.Schedule(dueTime, period);
            _timers.Add(timer);
        }

        return timer;
    }

    private void AdvanceTo(DateTimeOffset target)
    {
        while (true)
        {
            SimulatedTimer? next = null;
            lock (_gate)
            {
                foreach (SimulatedTimer timer in _timers)
                {
                    if (timer.Due is { } due && due <= target && (next is null || due < next.Due))
                    {
                        next = timer;
                    }
                }

                if (next is null)
                {
                    _utcNow = target;
                    return;
                }

                if (next.Due > _utcNow)
                {
                    _utcNow = next.Due!.Value;
                }
            }

            next.Fire();
        }
    }

    private void Remove(SimulatedTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class SimulatedTimer : ITimer
    {
        private readonly SimulatedClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private bool _disposed;

        public SimulatedTimer(SimulatedClock clock, TimerCallback callback, object? state)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset? Due { get; private set; }

        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_clock._gate)
            {
                if (_disposed)
                {
                    return false;
                }

                Schedule(dueTime, period);
                return true;
            }
        }

        /// <summary>Sets due time and period; the caller holds the clock's lock.</summary>
        public void Schedule(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : _clock._utcNow + dueTime;
            Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
        }

        public void Fire()
        {
            lock (_clock._gate)
            {
                if (_disposed || Due is not { } due)
                {
                    return;
                }

                Due = Period > TimeSpan.Zero ? due + Period : null;
            }

            _callback(_state);
        }

        public void Dispose()
        {
            lock (_clock._gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Due = null;
            }

            _clock.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
