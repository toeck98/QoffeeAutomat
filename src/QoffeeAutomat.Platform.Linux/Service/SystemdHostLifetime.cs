using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;

namespace QoffeeAutomat.Platform.Linux.Service;

/// <summary>
/// Sends the keep-alive of <c>WatchdogSec=</c> to systemd at half the watchdog interval (<c>sd_watchdog_enabled(3)</c>). The timer
/// runs on the thread pool, so a process whose thread pool is blocked stops sending and systemd restarts it, which is what the
/// watchdog is for.
/// </summary>
public sealed class SystemdWatchdog : IDisposable
{
    private readonly ISystemdNotifier _notifier;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private ITimer? _timer;

    /// <summary>Creates the watchdog; call <see cref="Start"/> to begin.</summary>
    /// <param name="notifier">Sends the keep-alive.</param>
    /// <param name="watchdogInterval">The interval that systemd waits for; keep-alives go out at half of it.</param>
    /// <param name="time">The clock that drives the timer; null uses the system clock.</param>
    public SystemdWatchdog(ISystemdNotifier notifier, TimeSpan watchdogInterval, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(watchdogInterval, TimeSpan.Zero);

        _notifier = notifier;
        _interval = SystemdEnvironment.KeepAliveInterval(watchdogInterval);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The time between two keep-alives.</summary>
    public TimeSpan Interval => _interval;

    /// <summary>Sends the first keep-alive and then one per <see cref="Interval"/>; calling it again has no effect.</summary>
    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }

        Ping();
        _timer = _time.CreateTimer(_ => Ping(), null, _interval, _interval);
    }

    /// <summary>Sends one keep-alive now.</summary>
    /// <returns>True when systemd received it.</returns>
    public bool Ping() => _notifier.Notify(SystemdStates.Watchdog);

    /// <summary>Stops the timer.</summary>
    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}

/// <summary>
/// The host lifetime of the agent as a <c>Type=notify</c> systemd service (PLAN.md S17, T802): <c>READY=1</c> when the host has
/// started (the listeners accept connections, so <c>/healthz</c> answers), <c>STOPPING=1</c> when it begins to stop, the watchdog
/// keep-alive while it runs, and a stop request on SIGTERM, SIGINT and SIGQUIT. It replaces the console lifetime, which prints
/// instructions for a person at a terminal into the journal.
/// </summary>
public sealed class SystemdHostLifetime : IHostLifetime, IDisposable
{
    private readonly ISystemdNotifier _notifier;
    private readonly IHostApplicationLifetime _application;
    private readonly TimeSpan? _watchdogInterval;
    private readonly TimeProvider _time;
    private readonly List<IDisposable> _registrations = [];
    private SystemdWatchdog? _watchdog;

    /// <summary>Creates the lifetime.</summary>
    /// <param name="notifier">Talks to systemd.</param>
    /// <param name="application">The events of the host.</param>
    /// <param name="watchdogInterval">The watchdog interval from the environment; null when the watchdog is off.</param>
    /// <param name="time">The clock for the watchdog; null uses the system clock.</param>
    public SystemdHostLifetime(
        ISystemdNotifier notifier,
        IHostApplicationLifetime application,
        TimeSpan? watchdogInterval = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(application);

        _notifier = notifier;
        _application = application;
        _watchdogInterval = watchdogInterval;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task WaitForStartAsync(CancellationToken cancellationToken)
    {
        _registrations.Add(_application.ApplicationStarted.Register(OnStarted));
        _registrations.Add(_application.ApplicationStopping.Register(OnStopping));

        // A signal ends the process abruptly unless a handler takes it; systemd stops a service with SIGTERM.
        foreach (PosixSignal signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGTERM })
        {
            _registrations.Add(PosixSignalRegistration.Create(signal, OnSignal));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Removes the handlers and stops the watchdog.</summary>
    public void Dispose()
    {
        foreach (IDisposable registration in _registrations)
        {
            registration.Dispose();
        }

        _registrations.Clear();
        _watchdog?.Dispose();
        _watchdog = null;
    }

    private void OnStarted()
    {
        _notifier.Notify(SystemdStates.Ready + "\n" + SystemdStates.Status("Running"));
        if (_watchdogInterval is { } interval)
        {
            _watchdog = new SystemdWatchdog(_notifier, interval, _time);
            _watchdog.Start();
        }
    }

    private void OnStopping()
    {
        _watchdog?.Dispose();
        _watchdog = null;
        _notifier.Notify(SystemdStates.Stopping + "\n" + SystemdStates.Status("Stopping"));
    }

    private void OnSignal(PosixSignalContext context)
    {
        // The host shuts down in an orderly way (bye to the clients, zeroconf goodbye) instead of the runtime ending the process.
        context.Cancel = true;
        _application.StopApplication();
    }
}
