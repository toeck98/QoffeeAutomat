using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Metrics.Custom;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Platform.Windows.Metrics.CustomSensors;

/// <summary>Checks that apply to the text members of several Windows sensor types.</summary>
internal static class WindowsSensorChecks
{
    /// <summary>Reports a member that is too long or holds control characters. A missing member is the business of the document validator.</summary>
    public static void CheckText(string? value, string path, int maxLength, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.Length > maxLength)
        {
            errors.Add(new ValidationError(path, ValidationCodes.MaxLength, $"The value must not be longer than {maxLength} characters."));
        }
        else if (value.Any(char.IsControl))
        {
            errors.Add(new ValidationError(path, ValidationCodes.Invalid, "The value must not contain control characters."));
        }
    }

    /// <summary>Reports a value type that the sensor type cannot deliver.</summary>
    public static void RequireValueType(SensorDocument sensor, string expected, string sourceType, List<ValidationError> errors)
    {
        if (!string.Equals(sensor.ValueType, expected, StringComparison.Ordinal))
        {
            errors.Add(new ValidationError("/valueType", ValidationCodes.Invalid, $"A {sourceType} sensor reports a value of type {expected}."));
        }
    }
}

/// <summary>
/// The custom sensor type <c>perf_counter</c> (PLAN.md S06, T307): the current value of a Windows performance counter, for
/// example <c>Processor</c>, <c>% Processor Time</c>, instance <c>_Total</c>. The value type must be <c>number</c>. Saving checks
/// the spelling of the definition only, because the counters that exist depend on the computer; whether the counter exists is
/// reported when the sensor is tested (<c>POST /sensors/test</c>) and in its status.
/// </summary>
public sealed class PerfCounterSensorHandler : ISensorSourceHandler
{
    private const int MaxNameLength = 128;
    private const int MaxInstanceLength = 256;

    private readonly IPerformanceCounterReader _reader;

    /// <summary>Creates the handler.</summary>
    /// <param name="reader">Reads the counters.</param>
    public PerfCounterSensorHandler(IPerformanceCounterReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <inheritdoc />
    public string SourceType => SensorSourceTypes.PerfCounter;

    /// <inheritdoc />
    public IReadOnlyList<ValidationError> Validate(SensorDocument sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        var errors = new List<ValidationError>();
        if (sensor.Source is not PerfCounterSensorSource source)
        {
            return errors;
        }

        WindowsSensorChecks.RequireValueType(sensor, "number", SensorSourceTypes.PerfCounter, errors);
        WindowsSensorChecks.CheckText(source.Category, "/source/category", MaxNameLength, errors);
        WindowsSensorChecks.CheckText(source.Counter, "/source/counter", MaxNameLength, errors);
        WindowsSensorChecks.CheckText(source.Instance, "/source/instance", MaxInstanceLength, errors);
        return errors;
    }

    /// <inheritdoc />
    public ValueTask<SensorParseResult> SampleAsync(SensorDocument sensor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        if (sensor.Source is not PerfCounterSensorSource source)
        {
            return ValueTask.FromResult(SensorParseResult.Fail("The sensor is not a perf_counter sensor."));
        }

        return _reader.ReadAsync(source.Category, source.Counter, string.IsNullOrWhiteSpace(source.Instance) ? null : source.Instance, cancellationToken);
    }
}
