using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Platform.Windows.Actions.Power;

/// <summary>What <see cref="KeepAwakeService"/> holds right now.</summary>
/// <param name="IsOn">The agent keeps the computer from going to sleep.</param>
/// <param name="KeepDisplayOn">The displays are kept on as well.</param>
/// <param name="Until">When the request ends by itself; null means until it is switched off.</param>
public sealed record KeepAwakeState(bool IsOn, bool KeepDisplayOn, DateTimeOffset? Until)
{
    /// <summary>The state while nothing is held.</summary>
    public static KeepAwakeState Off { get; } = new(false, false, null);
}

/// <summary>
/// Keeps the computer awake for <c>power.keep_awake</c> (PLAN.md S11, S16, T715). The agent owns one power request (see
/// <see cref="IPowerRequestApi"/>); the service switches it on and off and ends it by itself after the time that was asked for.
/// Nothing is stored: the request ends with the agent, and after a restart the computer is not held awake any more, which is
/// what <c>power.keep_awake</c> promises. The state is the real one, so the metric <c>power.keep_awake</c> and the Home Assistant
/// switch that follows it show what Windows is told, not what was last requested.
/// </summary>
public sealed class KeepAwakeService : IDisposable
{
    private readonly object _gate = new();
    private readonly IPowerRequestApi _api;
    private readonly TimeProvider _time;
    private ITimer? _timer;
    private long _generation;
    private KeepAwakeState _state = KeepAwakeState.Off;
    private bool _disposed;

    /// <summary>Creates the service.</summary>
    /// <param name="api">The calls to Windows; tests pass a fake that never holds the computer awake.</param>
    /// <param name="time">The clock; the timed end uses its timers.</param>
    public KeepAwakeService(IPowerRequestApi api, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(api);

        _api = api;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after <see cref="State"/> changed, also when the time was over. Handlers return quickly and run on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>What is held right now.</summary>
    public KeepAwakeState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Tells whether the computer is held awake.</summary>
    public bool IsOn => State.IsOn;

    /// <summary>Switches keeping awake on or off; switching on again replaces the time and the display part.</summary>
    /// <param name="on">True keeps the computer awake, false gives the request back.</param>
    /// <param name="keepDisplayOn">Also keep the displays on (ignored when switching off).</param>
    /// <param name="minutes">0 to <see cref="PowerActionSpec.MaxKeepAwakeMinutes"/>; 0 means until it is switched off (ignored when switching off).</param>
    /// <returns>The state afterwards.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minutes"/> is out of range.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Windows refused the request; the state is not changed then.</exception>
    public KeepAwakeState Switch(bool on, bool keepDisplayOn, int minutes)
    {
        if (on)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(minutes);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, PowerActionSpec.MaxKeepAwakeMinutes);
        }

        KeepAwakeState before;
        KeepAwakeState after;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            before = _state;
            if (on)
            {
                _api.Acquire(keepDisplayOn);
                StopTimerLocked();
                DateTimeOffset? until = null;
                if (minutes > 0)
                {
                    TimeSpan length = TimeSpan.FromMinutes(minutes);
                    until = _time.GetUtcNow() + length;
                    long generation = _generation;
                    _timer = _time.CreateTimer(_ => OnTimeOver(generation), null, length, Timeout.InfiniteTimeSpan);
                }

                _state = new KeepAwakeState(true, keepDisplayOn, until);
            }
            else
            {
                ReleaseLocked();
            }

            after = _state;
        }

        if (after != before)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return after;
    }

    /// <summary>Gives the request back when it is held, without ever throwing; the agent calls it when it stops.</summary>
    public void Release()
    {
        bool changed;
        lock (_gate)
        {
            changed = _state.IsOn;
            ReleaseLocked();
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            ReleaseLocked();
        }
    }

    private void OnTimeOver(long generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation || !_state.IsOn)
            {
                return;
            }

            ReleaseLocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ReleaseLocked()
    {
        StopTimerLocked();
        _api.Release();
        _state = KeepAwakeState.Off;
    }

    private void StopTimerLocked()
    {
        _generation++;
        _timer?.Dispose();
        _timer = null;
    }
}
