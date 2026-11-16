using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Metrics.Custom;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Platform.Windows.Metrics.CustomSensors;

/// <summary>
/// The custom sensor type <c>service_state</c> (PLAN.md S06, T307): the state of a Windows service as an enum sensor. The
/// options of the sensor choose which of the <see cref="ServiceStateNames"/> it can report, for example
/// <c>["running", "stopped"]</c>; a service that is in another state (or is not installed, unless <c>not_installed</c> is
/// one of the options) makes the sensor unavailable with an explanation instead of reporting a value that Home Assistant
/// would reject.
/// </summary>
public sealed class ServiceStateSensorHandler : ISensorSourceHandler
{
    private const int MaxServiceNameLength = 256;

    private readonly IServiceStateReader _reader;

    /// <summary>Creates the handler.</summary>
    /// <param name="reader">Asks the service control manager.</param>
    public ServiceStateSensorHandler(IServiceStateReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <inheritdoc />
    public string SourceType => SensorSourceTypes.ServiceState;

    /// <inheritdoc />
    public IReadOnlyList<ValidationError> Validate(SensorDocument sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        var errors = new List<ValidationError>();
        if (sensor.Source is not ServiceStateSensorSource source)
        {
            return errors;
        }

        WindowsSensorChecks.RequireValueType(sensor, "enum", SensorSourceTypes.ServiceState, errors);
        WindowsSensorChecks.CheckText(source.Service, "/source/service", MaxServiceNameLength, errors);
        if (!string.IsNullOrWhiteSpace(source.Service) && source.Service.AsSpan().IndexOfAny('\\', '/') >= 0)
        {
            errors.Add(new ValidationError("/source/service", ValidationCodes.Invalid, "A service name contains no slashes; use the name of the service, not its path."));
        }

        if (sensor.Options is { } options)
        {
            for (int index = 0; index < options.Count; index++)
            {
                if (!ServiceStateNames.All.Contains(options[index], StringComparer.Ordinal))
                {
                    errors.Add(new ValidationError(
                        JsonPointer.Index("/options", index),
                        ValidationCodes.Invalid,
                        "A service state option is one of " + string.Join(", ", ServiceStateNames.All) + "."));
                }
            }
        }

        return errors;
    }

    /// <inheritdoc />
    public ValueTask<SensorParseResult> SampleAsync(SensorDocument sensor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        if (sensor.Source is not ServiceStateSensorSource source)
        {
            return ValueTask.FromResult(SensorParseResult.Fail("The sensor is not a service_state sensor."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ServiceReading reading = _reader.Read(source.Service);
        if (reading.Failure is not null)
        {
            return ValueTask.FromResult(SensorParseResult.Fail(reading.Failure));
        }

        string state = reading.State ?? ServiceStateNames.NotInstalled;
        if (sensor.Options is not { Count: > 0 } options)
        {
            return ValueTask.FromResult(SensorParseResult.Fail("The sensor declares no options for its enum value."));
        }

        if (!options.Contains(state, StringComparer.Ordinal))
        {
            string problem = reading.State is null
                ? $"The service '{source.Service}' is not installed. Add the option not_installed to report that."
                : $"The service '{source.Service}' is {state}, which is not one of the options ({string.Join(", ", options)}).";
            return ValueTask.FromResult(SensorParseResult.Fail(problem));
        }

        return ValueTask.FromResult(SensorParseResult.Success(state));
    }
}
