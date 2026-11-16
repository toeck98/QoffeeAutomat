using System.Globalization;

namespace QoffeeAutomat.Core.Metrics.Custom;

/// <summary>The outcome of parsing the output of a script sensor.</summary>
public sealed class SensorParseResult
{
    private SensorParseResult(bool isValid, object? value, string? failure)
    {
        IsValid = isValid;
        Value = value;
        Failure = failure;
    }

    /// <summary>True when the output holds a value of the sensor's type.</summary>
    public bool IsValid { get; }

    /// <summary>The value: <see cref="double"/>, <see cref="bool"/>, <see cref="string"/> or <see cref="DateTimeOffset"/> (UTC); null when <see cref="IsValid"/> is false.</summary>
    public object? Value { get; }

    /// <summary>What is wrong with the output, in words that the UI shows; null when <see cref="IsValid"/> is true.</summary>
    public string? Failure { get; }

    /// <summary>Creates a successful result.</summary>
    /// <param name="value">The parsed value.</param>
    /// <returns>The result.</returns>
    public static SensorParseResult Success(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new SensorParseResult(true, value, null);
    }

    /// <summary>Creates a failed result.</summary>
    /// <param name="failure">What is wrong.</param>
    /// <returns>The result.</returns>
    public static SensorParseResult Fail(string failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        return new SensorParseResult(false, null, failure);
    }
}

/// <summary>
/// Turns the output of a script sensor into a metric value (PLAN.md S06, T215): the last non-empty line is parsed
/// according to the sensor's <c>valueType</c>. Everything before the last line is ignored, so a script may log
/// progress first. Numbers use the invariant culture, which is also how PowerShell writes them.
/// </summary>
public static class SensorOutputParser
{
    private const int MaxQuotedLength = 80;

    /// <summary>Finds the last line that holds something other than white space.</summary>
    /// <param name="output">The standard output of the script.</param>
    /// <returns>The trimmed line, or null when there is none.</returns>
    public static string? LastNonEmptyLine(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        int end = output.Length;
        while (end > 0)
        {
            int start = end;
            while (start > 0 && output[start - 1] is not ('\n' or '\r'))
            {
                start--;
            }

            string line = output[start..end].Trim();
            if (line.Length > 0)
            {
                return line;
            }

            end = start > 0 ? start - 1 : 0;
        }

        return null;
    }

    /// <summary>Parses the output according to the value type.</summary>
    /// <param name="output">The standard output of the script.</param>
    /// <param name="valueType"><c>number</c>, <c>bool</c>, <c>string</c>, <c>timestamp</c> or <c>enum</c>.</param>
    /// <param name="options">The allowed values of an <c>enum</c> sensor.</param>
    /// <returns>The value or the reason that the output is unusable.</returns>
    public static SensorParseResult Parse(string output, string valueType, IReadOnlyList<string>? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);

        string? line = LastNonEmptyLine(output);
        if (line is null)
        {
            return SensorParseResult.Fail("The script wrote no output.");
        }

        switch (valueType)
        {
            case "number":
                return double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)
                    ? SensorParseResult.Success(number)
                    : SensorParseResult.Fail($"The last output line '{Quote(line)}' is not a number.");
            case "bool":
                return TryParseBool(line, out bool flag)
                    ? SensorParseResult.Success(flag)
                    : SensorParseResult.Fail($"The last output line '{Quote(line)}' is not true or false.");
            case "string":
                return SensorParseResult.Success(line);
            case "timestamp":
                return DateTimeOffset.TryParse(line, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset moment)
                    ? SensorParseResult.Success(moment.ToUniversalTime())
                    : SensorParseResult.Fail($"The last output line '{Quote(line)}' is not a date and time.");
            case "enum":
                return ParseEnum(line, options);
            default:
                return SensorParseResult.Fail($"The value type '{valueType}' is not supported.");
        }
    }

    private static SensorParseResult ParseEnum(string line, IReadOnlyList<string>? options)
    {
        if (options is null || options.Count == 0)
        {
            return SensorParseResult.Fail("The sensor declares no options for its enum value.");
        }

        string? match = options.FirstOrDefault(option => string.Equals(option, line, StringComparison.Ordinal))
            ?? options.FirstOrDefault(option => string.Equals(option, line, StringComparison.OrdinalIgnoreCase));
        return match is null
            ? SensorParseResult.Fail($"The last output line '{Quote(line)}' is not one of the options ({string.Join(", ", options)}).")
            : SensorParseResult.Success(match);
    }

    private static bool TryParseBool(string line, out bool value)
    {
        switch (line.ToUpperInvariant())
        {
            case "TRUE" or "1" or "YES" or "ON":
                value = true;
                return true;
            case "FALSE" or "0" or "NO" or "OFF":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static string Quote(string line)
    {
        return line.Length <= MaxQuotedLength ? line : line[..MaxQuotedLength] + "...";
    }
}
