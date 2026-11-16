using QoffeeAutomat.Core.Metrics.Custom;

namespace QoffeeAutomat.Platform.Windows.Metrics.CustomSensors;

/// <summary>Reads Windows performance counters (PLAN.md S06, T307): the seam between <see cref="PerfCounterSensorHandler"/> and the operating system.</summary>
public interface IPerformanceCounterReader
{
    /// <summary>Reads the current value of a counter.</summary>
    /// <param name="category">The category, for example <c>Processor</c>.</param>
    /// <param name="counter">The counter, for example <c>% Processor Time</c>.</param>
    /// <param name="instance">The instance, for example <c>_Total</c>; null or empty for categories that have a single instance.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A number (<see cref="double"/>), or the reason that the counter cannot be read.</returns>
    ValueTask<SensorParseResult> ReadAsync(string category, string counter, string? instance, CancellationToken cancellationToken);
}

/// <summary>The outcome of a CIM query: the property of the first result, or the reason there is none.</summary>
/// <param name="Value">The raw value that the CIM provider returned; null when the query failed or the property is empty.</param>
/// <param name="Failure">Why the query failed or returned nothing; null when <paramref name="Value"/> is the value.</param>
public sealed record CimReading(object? Value, string? Failure);

/// <summary>Runs CIM (WMI) queries (PLAN.md S06, T307): the seam between <see cref="CimQuerySensorHandler"/> and the operating system.</summary>
public interface ICimQueryRunner
{
    /// <summary>Reads one property of the first object of a class.</summary>
    /// <param name="cimNamespace">The CIM namespace, for example <c>root\cimv2</c>.</param>
    /// <param name="className">The class, for example <c>Win32_OperatingSystem</c>.</param>
    /// <param name="propertyName">The property, for example <c>Caption</c>.</param>
    /// <param name="filter">An optional WQL condition without the <c>WHERE</c>, for example <c>DeviceID='C:'</c>.</param>
    /// <param name="cancellationToken">Cancels the wait for the result.</param>
    /// <returns>The reading; a failing query is a reading with a failure, not an exception.</returns>
    Task<CimReading> QueryFirstAsync(string cimNamespace, string className, string propertyName, string? filter, CancellationToken cancellationToken);
}

/// <summary>The outcome of asking for the state of a service.</summary>
/// <param name="State">One of <see cref="ServiceStateNames"/>; null when the service is not installed or could not be asked.</param>
/// <param name="Failure">Why there is no state; null when the service is not installed (which is a state of its own) or <paramref name="State"/> is set.</param>
public sealed record ServiceReading(string? State, string? Failure)
{
    /// <summary>The reading of a service that does not exist.</summary>
    public static ServiceReading NotInstalled { get; } = new(null, null);
}

/// <summary>Reads the state of Windows services (PLAN.md S06, T307): the seam between <see cref="ServiceStateSensorHandler"/> and the service control manager.</summary>
public interface IServiceStateReader
{
    /// <summary>Asks the service control manager for the state of a service.</summary>
    /// <param name="serviceName">The service name (not the display name), for example <c>EventLog</c>.</param>
    /// <returns>The state, <see cref="ServiceReading.NotInstalled"/>, or a failure.</returns>
    ServiceReading Read(string serviceName);
}

/// <summary>The values of an enum sensor of type <c>service_state</c>: the states of the service control manager in snake case, plus <see cref="NotInstalled"/>.</summary>
public static class ServiceStateNames
{
    /// <summary>The service is not running.</summary>
    public const string Stopped = "stopped";

    /// <summary>The service is starting.</summary>
    public const string StartPending = "start_pending";

    /// <summary>The service is stopping.</summary>
    public const string StopPending = "stop_pending";

    /// <summary>The service is running.</summary>
    public const string Running = "running";

    /// <summary>The service is about to continue after a pause.</summary>
    public const string ContinuePending = "continue_pending";

    /// <summary>The service is about to pause.</summary>
    public const string PausePending = "pause_pending";

    /// <summary>The service is paused.</summary>
    public const string Paused = "paused";

    /// <summary>There is no service with this name. Only reported when the sensor lists it as an option.</summary>
    public const string NotInstalled = "not_installed";

    /// <summary>All states, in the order of the service control manager.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Stopped,
        StartPending,
        StopPending,
        Running,
        ContinuePending,
        PausePending,
        Paused,
        NotInstalled,
    ];
}
