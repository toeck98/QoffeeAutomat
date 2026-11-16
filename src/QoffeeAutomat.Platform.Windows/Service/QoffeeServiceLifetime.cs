using System.ServiceProcess;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using QoffeeAutomat.Platform.Windows.Sessions;

namespace QoffeeAutomat.Platform.Windows.Service;

/// <summary>
/// The host lifetime of the agent when it runs as a Windows service. It does what the lifetime of
/// Microsoft.Extensions.Hosting.WindowsServices does (start and stop follow the control manager, stop waits for the
/// host to shut down) and additionally subscribes to power and session change notifications and forwards them to
/// <see cref="WindowsPowerEvents"/> and <see cref="WindowsSessionMonitor"/> (PLAN.md S11). A shutdown is announced
/// before the host stops, so the QoffeeLink clients get <c>bye</c> with the reason <c>shutdown</c> or <c>restart</c>.
/// </summary>
public sealed class QoffeeServiceLifetime : ServiceBase, IHostLifetime
{
    private readonly TaskCompletionSource<object?> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _stopped = new();
    private readonly IHostApplicationLifetime _application;
    private readonly HostOptions _hostOptions;
    private readonly WindowsPowerEvents _power;
    private readonly WindowsSessionMonitor _session;
    private readonly WindowsSessionManager _sessionHosts;

    /// <summary>Creates the lifetime.</summary>
    /// <param name="application">Stops the application when the control manager asks.</param>
    /// <param name="hostOptions">The shutdown timeout.</param>
    /// <param name="serviceOptions">The service name.</param>
    /// <param name="power">Receives power notifications.</param>
    /// <param name="session">Receives session change notifications.</param>
    /// <param name="sessionHosts">Starts and stops the SessionHosts when users log on and off (T302).</param>
    public QoffeeServiceLifetime(
        IHostApplicationLifetime application,
        IOptions<HostOptions> hostOptions,
        IOptions<QoffeeServiceOptions> serviceOptions,
        WindowsPowerEvents power,
        WindowsSessionMonitor session,
        WindowsSessionManager sessionHosts)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(hostOptions);
        ArgumentNullException.ThrowIfNull(serviceOptions);
        ArgumentNullException.ThrowIfNull(power);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sessionHosts);

        _application = application;
        _hostOptions = hostOptions.Value;
        _power = power;
        _session = session;
        _sessionHosts = sessionHosts;

        ServiceName = serviceOptions.Value.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanHandlePowerEvent = true;
        CanHandleSessionChangeEvent = true;

        // The Microsoft.Extensions.Logging.EventLog provider writes the log; the automatic entries of ServiceBase would duplicate it.
        AutoLog = false;
    }

    /// <inheritdoc />
    public Task WaitForStartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.Register(() => _started.TrySetCanceled(cancellationToken));
        _application.ApplicationStopping.Register(Stop);

        var thread = new Thread(RunService) { IsBackground = true, Name = "QoffeeAutomat service control" };
        thread.Start();
        return _started.Task;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // The host waits for this task before it reports the application as stopped, and Stop waits for that report.
        _ = Task.Run(Stop, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override void OnStart(string[] args)
    {
        _started.TrySetResult(null);
        base.OnStart(args);
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
        _application.StopApplication();
        _stopped.Wait(_hostOptions.ShutdownTimeout);
        base.OnStop();
    }

    /// <inheritdoc />
    protected override void OnShutdown()
    {
        // First tell the clients (bye, flushed), then stop the host.
        _power.HandleShutdown();
        _application.StopApplication();
        _stopped.Wait(_hostOptions.ShutdownTimeout);
        base.OnShutdown();
    }

    /// <inheritdoc />
    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        _power.Handle(powerStatus);
        return true;
    }

    /// <inheritdoc />
    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        // The monitor first, so that the user is known when the SessionHost of the session connects.
        _session.Handle(changeDescription.Reason, changeDescription.SessionId);
        _sessionHosts.Handle(changeDescription.Reason, changeDescription.SessionId);
        base.OnSessionChange(changeDescription);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stopped.Set();
        }

        base.Dispose(disposing);
    }

    private void RunService()
    {
        try
        {
            Run(this);
            _started.TrySetException(new InvalidOperationException("The service stopped without starting."));
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _started.TrySetException(exception);
        }
    }
}

/// <summary>The settings of <see cref="QoffeeServiceLifetime"/>.</summary>
public sealed class QoffeeServiceOptions
{
    /// <summary>The service name that the control manager knows the process by.</summary>
    public string ServiceName { get; set; } = ServiceDefaults.Name;
}
