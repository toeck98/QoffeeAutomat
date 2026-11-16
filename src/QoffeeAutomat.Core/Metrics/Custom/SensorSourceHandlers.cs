using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Metrics.Custom;

/// <summary>
/// Samples one custom sensor source type that needs the operating system (PLAN.md S06, T307): on Windows
/// <c>perf_counter</c>, <c>cim_query</c> and <c>service_state</c>. A platform registers its handlers with the
/// <see cref="CustomSensorProvider"/>; a sensor type without a handler is reported as unsupported by
/// <see cref="SensorTypeSupport"/> before it can fail at runtime.
/// </summary>
public interface ISensorSourceHandler
{
    /// <summary>The <c>source.type</c> that this handler serves, one of <see cref="SensorSourceTypes"/>.</summary>
    string SourceType { get; }

    /// <summary>
    /// Checks the members of the source beyond what <see cref="DocumentValidator.ValidateSensor"/> checks (identifier
    /// syntax, the value type that fits the source). Members that are missing are reported by the document validator, so
    /// this method ignores them.
    /// </summary>
    /// <param name="sensor">The sensor document whose source type is <see cref="SourceType"/>.</param>
    /// <returns>The problems, with JSON pointers to the members; empty when the definition is usable.</returns>
    IReadOnlyList<ValidationError> Validate(SensorDocument sensor);

    /// <summary>Reads the value of a sensor once.</summary>
    /// <param name="sensor">The sensor document whose source type is <see cref="SourceType"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value, or the reason that there is none (the sensor is then unavailable); this method does not throw for a failing source.</returns>
    ValueTask<SensorParseResult> SampleAsync(SensorDocument sensor, CancellationToken cancellationToken);
}

/// <summary>
/// Which custom sensor types this agent can sample: the types that need no operating-system API
/// (<c>powershell</c>, <c>process_running</c>, <c>state</c>) and those of the registered
/// <see cref="ISensorSourceHandler"/>s. The agent validates a sensor with it when the sensor is saved and when it is tested,
/// so a Windows-only type on another platform is refused with the code <see cref="ValidationCodes.Unsupported"/> instead of
/// failing when it is sampled.
/// </summary>
public sealed class SensorTypeSupport
{
    private static readonly string[] PlatformTypes =
    [
        SensorSourceTypes.PerfCounter,
        SensorSourceTypes.CimQuery,
        SensorSourceTypes.ServiceState,
    ];

    private readonly Dictionary<string, ISensorSourceHandler> _handlers = new(StringComparer.Ordinal);

    /// <summary>Creates the support table.</summary>
    /// <param name="handlers">The handlers of the platform; null or empty for a platform that has none.</param>
    public SensorTypeSupport(IEnumerable<ISensorSourceHandler>? handlers = null)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (ISensorSourceHandler handler in handlers)
        {
            _handlers[handler.SourceType] = handler;
        }
    }

    /// <summary>Finds the handler of a source type.</summary>
    /// <param name="sourceType">The <c>source.type</c>.</param>
    /// <returns>The handler, or null when the type has none.</returns>
    public ISensorSourceHandler? HandlerFor(string sourceType)
    {
        ArgumentNullException.ThrowIfNull(sourceType);
        return _handlers.GetValueOrDefault(sourceType);
    }

    /// <summary>Tells whether this agent can sample a source type.</summary>
    /// <param name="sourceType">The <c>source.type</c>.</param>
    /// <returns>False for a platform type without handler and for unknown types.</returns>
    public bool IsSupported(string sourceType)
    {
        ArgumentNullException.ThrowIfNull(sourceType);

        return sourceType is SensorSourceTypes.PowerShell or SensorSourceTypes.ProcessRunning or SensorSourceTypes.State
            || _handlers.ContainsKey(sourceType);
    }

    /// <summary>Validates what depends on the platform.</summary>
    /// <param name="sensor">The sensor document; its general structure is validated by <see cref="DocumentValidator.ValidateSensor"/>.</param>
    /// <returns>
    /// <see cref="ValidationCodes.Unsupported"/> at <c>/source/type</c> for a platform type that has no handler, otherwise
    /// the problems found by the handler; empty for all other types.
    /// </returns>
    public IReadOnlyList<ValidationError> Validate(SensorDocument sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        string? type = sensor.Source?.Type;
        if (type is null || !PlatformTypes.Contains(type, StringComparer.Ordinal))
        {
            return [];
        }

        return _handlers.TryGetValue(type, out ISensorSourceHandler? handler)
            ? handler.Validate(sensor)
            : [new ValidationError("/source/type", ValidationCodes.Unsupported, $"The sensor type '{type}' is available on Windows only and is not supported on this platform.")];
    }
}
