namespace QoffeeAutomat.Hub.Fleet.Actions;

/// <summary>
/// Limits and timings of the fleet actions (PLAN.md S20 "Fleet actions", T917). The defaults are the values of the specification; tests shorten the timings through
/// the service collection. Waiting uses real time (<see cref="Task.Delay(TimeSpan, CancellationToken)"/>), not the <see cref="TimeProvider"/> of the hub, which only stamps
/// the records.
/// </summary>
public sealed class FleetActionOptions
{
    /// <summary>The most agents that one action may target.</summary>
    public int MaxAgents { get; init; } = 200;

    /// <summary>How many agents a bulk run handles at the same time unless the request says otherwise.</summary>
    public int DefaultParallelism { get; init; } = 5;

    /// <summary>The largest parallelism that a request may ask for.</summary>
    public int MaxParallelism { get; init; } = 16;

    /// <summary>How long a bulk run waits for the command on one agent unless the request says otherwise.</summary>
    public TimeSpan DefaultRunTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long an update waits for one agent to come back with the new version unless the request says otherwise.</summary>
    public TimeSpan DefaultUpdateTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The longest timeout that a request may ask for.</summary>
    public TimeSpan MaxTimeout { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How often an update looks at an agent that it waits for.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The most actions that run at the same time; a further start is answered with 409 <c>too_many_actions</c>.</summary>
    public int MaxRunningActions { get; init; } = 4;

    /// <summary>How many finished actions the hub keeps in memory for the list. The audit log keeps the permanent record.</summary>
    public int MaxRetainedActions { get; init; } = 50;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAgents, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxParallelism, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(DefaultParallelism, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(DefaultParallelism, MaxParallelism);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(DefaultRunTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(DefaultUpdateTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRunningActions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRetainedActions, 1);
    }
}
