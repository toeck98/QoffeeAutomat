using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Simulated;

/// <summary>Power events that tests trigger by hand.</summary>
public sealed class SimulatedPowerEvents : IPowerEvents
{
    /// <inheritdoc />
    public event EventHandler<PowerEventArgs>? PowerChanged;

    /// <summary>Simulates a power state change; handlers run synchronously.</summary>
    /// <param name="kind">What happened.</param>
    public void Simulate(PowerEventKind kind)
    {
        PowerChanged?.Invoke(this, new PowerEventArgs(kind));
    }
}

/// <summary>Remembers what would have been advertised on the network; nothing leaves the process.</summary>
public sealed class SimulatedServiceAdvertiser : IServiceAdvertiser
{
    private readonly object _gate = new();
    private ServiceAdvertisement? _current;
    private int _startCount;

    /// <summary>The active advertisement, or null.</summary>
    public ServiceAdvertisement? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>How often an advertisement was started or replaced.</summary>
    public int StartCount
    {
        get
        {
            lock (_gate)
            {
                return _startCount;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask StartAsync(ServiceAdvertisement advertisement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _current = advertisement;
            _startCount++;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _current = null;
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Stands in for the launcher of <c>runAs: user_elevated</c> (PLAN.md D-036). It reports
/// <see cref="ErrorCodes.NoSession"/> without a ready session and
/// <see cref="ErrorCodes.ElevationUnavailable"/> when <see cref="ElevationAvailable"/> is switched off
/// or the user is not an administrator; otherwise it records the request and succeeds.
/// </summary>
public sealed class SimulatedElevatedLauncher : IElevatedLauncher
{
    private readonly ISessionBridge _session;
    private readonly object _gate = new();
    private readonly List<ProcessLaunchRequest> _launches = [];
    private volatile bool _elevationAvailable = true;

    /// <summary>Creates the launcher.</summary>
    /// <param name="session">The session whose user would be elevated.</param>
    public SimulatedElevatedLauncher(ISessionBridge session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Switch: false makes every launch fail with <see cref="ErrorCodes.ElevationUnavailable"/>.</summary>
    public bool ElevationAvailable
    {
        get => _elevationAvailable;
        set => _elevationAvailable = value;
    }

    /// <summary>The processes that were started, oldest first.</summary>
    public IReadOnlyList<ProcessLaunchRequest> Launches
    {
        get
        {
            lock (_gate)
            {
                return [.. _launches];
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<ExecutionResult> StartAsync(ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        SessionState state = _session.State;
        if (!state.IsReady)
        {
            return ValueTask.FromResult(ExecutionResult.Failure(ErrorCodes.NoSession, "Nobody is logged in."));
        }

        if (!_elevationAvailable || !state.IsUserAdministrator)
        {
            return ValueTask.FromResult(ExecutionResult.Failure(
                ErrorCodes.ElevationUnavailable,
                "The logged-in user cannot run elevated processes."));
        }

        lock (_gate)
        {
            _launches.Add(request);
        }

        return ValueTask.FromResult(new ExecutionResult(0, request.FileName, string.Empty));
    }
}
