using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Metrics.Custom;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Platform.Windows.Metrics.CustomSensors;

/// <summary>
/// The custom sensor type <c>cim_query</c> (PLAN.md S06, T307): one property of the first object that a CIM class returns,
/// for example namespace <c>root\cimv2</c>, class <c>Win32_OperatingSystem</c>, property <c>FreePhysicalMemory</c>, with an optional
/// WQL <c>filter</c> such as <c>DeviceID='C:'</c>. The value is converted to the value type of the sensor; CIM date and time
/// strings become timestamps.
/// </summary>
public sealed partial class CimQuerySensorHandler : ISensorSourceHandler
{
    private const int MaxFilterLength = 512;
    private const int MaxNamespaceLength = 128;
    private const int MaxIdentifierLength = 128;

    private readonly ICimQueryRunner _runner;

    /// <summary>Creates the handler.</summary>
    /// <param name="runner">Runs the queries.</param>
    public CimQuerySensorHandler(ICimQueryRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <inheritdoc />
    public string SourceType => SensorSourceTypes.CimQuery;

    /// <inheritdoc />
    public IReadOnlyList<ValidationError> Validate(SensorDocument sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        var errors = new List<ValidationError>();
        if (sensor.Source is not CimQuerySensorSource source)
        {
            return errors;
        }

        // The names go into the text of the query, so they must be plain identifiers; nothing else can change its shape.
        CheckName(source.Namespace, "/source/namespace", MaxNamespaceLength, NamespacePattern(), "A CIM namespace such as root\\cimv2", errors);
        CheckName(source.ClassName, "/source/class", MaxIdentifierLength, IdentifierPattern(), "A CIM class name such as Win32_OperatingSystem", errors);
        CheckName(source.Property, "/source/property", MaxIdentifierLength, IdentifierPattern(), "A CIM property name such as Caption", errors);
        WindowsSensorChecks.CheckText(source.Filter, "/source/filter", MaxFilterLength, errors);
        return errors;
    }

    /// <inheritdoc />
    public async ValueTask<SensorParseResult> SampleAsync(SensorDocument sensor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        if (sensor.Source is not CimQuerySensorSource source)
        {
            return SensorParseResult.Fail("The sensor is not a cim_query sensor.");
        }

        CimReading reading = await _runner.QueryFirstAsync(source.Namespace, source.ClassName, source.Property, source.Filter, cancellationToken).ConfigureAwait(false);
        if (reading.Value is null)
        {
            return SensorParseResult.Fail(reading.Failure ?? "The query returned no value.");
        }

        return ConvertValue(reading.Value, sensor);
    }

    /// <summary>Converts the value of a CIM property to the value type of a sensor.</summary>
    /// <param name="value">The raw value that WMI returned.</param>
    /// <param name="sensor">The sensor whose <see cref="SensorDocument.ValueType"/> and <see cref="SensorDocument.Options"/> apply.</param>
    /// <returns>The value or the reason that it does not fit.</returns>
    public static SensorParseResult ConvertValue(object value, SensorDocument sensor)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(sensor);

        if (value is Array)
        {
            return SensorParseResult.Fail("The property holds a list; a sensor needs a single value.");
        }

        if (string.Equals(sensor.ValueType, "timestamp", StringComparison.Ordinal) && TryTimestamp(value, out DateTimeOffset moment))
        {
            return SensorParseResult.Success(moment);
        }

        string text = value switch
        {
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        // A text sensor may be empty; the parser of script output would call that "no output".
        if (string.Equals(sensor.ValueType, "string", StringComparison.Ordinal))
        {
            return SensorParseResult.Success(text.Trim());
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return SensorParseResult.Fail("The property of the first result is empty.");
        }

        SensorParseResult parsed = SensorOutputParser.Parse(text, sensor.ValueType, sensor.Options);
        return parsed.IsValid ? parsed : SensorParseResult.Fail(parsed.Failure!.Replace("The last output line", "The value", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_]+([\\/][A-Za-z0-9_]+)*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex NamespacePattern();

    private static void CheckName(string? value, string path, int maxLength, Regex pattern, string expectation, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.Length > maxLength || !pattern.IsMatch(value))
        {
            errors.Add(new ValidationError(path, ValidationCodes.Invalid, expectation + " is expected."));
        }
    }

    private static bool TryTimestamp(object value, out DateTimeOffset moment)
    {
        switch (value)
        {
            case DateTimeOffset offset:
                moment = offset.ToUniversalTime();
                return true;
            case DateTime dateTime:
                moment = new DateTimeOffset(dateTime.ToUniversalTime());
                return true;
            case string text when text.Length == 25 && text[14] == '.':
                // The CIM format: yyyymmddHHMMSS.mmmmmmsUUU, with the offset to UTC in minutes.
                try
                {
                    moment = new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(text).ToUniversalTime());
                    return true;
                }
                catch (ArgumentOutOfRangeException)
                {
                    break;
                }

            default:
                break;
        }

        moment = default;
        return false;
    }
}
