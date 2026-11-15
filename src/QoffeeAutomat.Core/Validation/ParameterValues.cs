using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Core.Validation;

/// <summary>
/// Checks and converts one JSON value against a parameter definition. Shared by
/// <see cref="VariableBinder"/> (values from callers) and <see cref="DocumentValidator"/>
/// (default values). Messages never contain the value, because it may be a secret.
/// </summary>
internal static class ParameterValues
{
    /// <summary>The match timeout of <c>pattern</c> (PLAN.md S06).</summary>
    internal static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    private const double Tolerance = 1e-9;

    /// <summary>
    /// Converts a value. Returns the converted value (string, long, double or bool) or null after
    /// adding errors. With <paramref name="coerce"/> numbers and booleans may arrive as strings.
    /// </summary>
    internal static object? Convert(
        ParameterDefinition parameter,
        JsonElement value,
        string path,
        bool coerce,
        List<ValidationError> errors)
    {
        return parameter.Type switch
        {
            ParameterTypes.Text => ConvertText(parameter, value, path, errors),
            ParameterTypes.WholeNumber => ConvertInteger(parameter, value, path, coerce, errors),
            ParameterTypes.Real => ConvertReal(parameter, value, path, coerce, errors),
            ParameterTypes.Flag => ConvertFlag(value, path, coerce, errors),
            ParameterTypes.Choice => ConvertChoice(parameter, value, path, errors),
            _ => Fail(errors, path, ValidationCodes.Unsupported, "The parameter type is not supported."),
        };
    }

    private static object? Fail(List<ValidationError> errors, string path, string code, string message)
    {
        errors.Add(new ValidationError(path, code, message));
        return null;
    }

    private static string? ConvertText(
        ParameterDefinition parameter,
        JsonElement value,
        string path,
        List<ValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            Fail(errors, path, ValidationCodes.TypeMismatch, "Expected a string.");
            return null;
        }

        string text = value.GetString()!;
        int maxLength = parameter.MaxLength ?? ParameterDefinition.DefaultMaxLength;
        if (text.Length > maxLength)
        {
            Fail(errors, path, ValidationCodes.MaxLength, $"The string is longer than {maxLength.ToString(CultureInfo.InvariantCulture)} characters.");
            return null;
        }

        if (!string.IsNullOrEmpty(parameter.Pattern))
        {
            try
            {
                if (!Regex.IsMatch(text, parameter.Pattern, RegexOptions.CultureInvariant, PatternTimeout))
                {
                    Fail(errors, path, ValidationCodes.Pattern, "The string does not match the required pattern.");
                    return null;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                Fail(errors, path, ValidationCodes.PatternTimeout, "Matching the pattern took longer than 100 ms.");
                return null;
            }
            catch (ArgumentException)
            {
                Fail(errors, path, ValidationCodes.InvalidPattern, "The pattern of the parameter is not a valid regular expression.");
                return null;
            }
        }

        return text;
    }

    private static object? ConvertInteger(
        ParameterDefinition parameter,
        JsonElement value,
        string path,
        bool coerce,
        List<ValidationError> errors)
    {
        long number;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetInt64(out number))
            {
                if (!value.TryGetDouble(out double asDouble) || asDouble != Math.Floor(asDouble) || Math.Abs(asDouble) > 9e15)
                {
                    return Fail(errors, path, ValidationCodes.TypeMismatch, "Expected an integer.");
                }

                number = (long)asDouble;
            }
        }
        else if (coerce
            && value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            // Accepted: the value arrived as a numeric string.
        }
        else
        {
            return Fail(errors, path, ValidationCodes.TypeMismatch, "Expected an integer.");
        }

        return CheckNumber(parameter, number, path, errors) ? number : null;
    }

    private static object? ConvertReal(
        ParameterDefinition parameter,
        JsonElement value,
        string path,
        bool coerce,
        List<ValidationError> errors)
    {
        double number;
        if (value.ValueKind == JsonValueKind.Number)
        {
            number = value.GetDouble();
        }
        else if (coerce
            && value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            // Accepted: the value arrived as a numeric string.
        }
        else
        {
            return Fail(errors, path, ValidationCodes.TypeMismatch, "Expected a number.");
        }

        if (!double.IsFinite(number))
        {
            return Fail(errors, path, ValidationCodes.TypeMismatch, "Expected a finite number.");
        }

        return CheckNumber(parameter, number, path, errors) ? number : null;
    }

    private static bool CheckNumber(ParameterDefinition parameter, double number, string path, List<ValidationError> errors)
    {
        if (parameter.Min is { } min && number < min)
        {
            errors.Add(new ValidationError(path, ValidationCodes.Range, $"The value is below the minimum {min.ToString(CultureInfo.InvariantCulture)}."));
            return false;
        }

        if (parameter.Max is { } max && number > max)
        {
            errors.Add(new ValidationError(path, ValidationCodes.Range, $"The value is above the maximum {max.ToString(CultureInfo.InvariantCulture)}."));
            return false;
        }

        if (parameter.Step is { } step && step > 0)
        {
            double origin = parameter.Min ?? 0;
            double steps = (number - origin) / step;
            if (Math.Abs(steps - Math.Round(steps)) > Tolerance * Math.Max(1, Math.Abs(steps)))
            {
                errors.Add(new ValidationError(path, ValidationCodes.Step, $"The value is not a multiple of the step {step.ToString(CultureInfo.InvariantCulture)}."));
                return false;
            }
        }

        return true;
    }

    private static object? ConvertFlag(JsonElement value, string path, bool coerce, List<ValidationError> errors)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String when coerce:
                string? text = value.GetString();
                if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                break;
        }

        return Fail(errors, path, ValidationCodes.TypeMismatch, "Expected true or false.");
    }

    private static string? ConvertChoice(
        ParameterDefinition parameter,
        JsonElement value,
        string path,
        List<ValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            Fail(errors, path, ValidationCodes.TypeMismatch, "Expected a string.");
            return null;
        }

        string text = value.GetString()!;
        if (parameter.Options is { Count: > 0 } options)
        {
            if (!options.Contains(text, StringComparer.Ordinal))
            {
                Fail(errors, path, ValidationCodes.Options, "The value is not one of the allowed options.");
                return null;
            }
        }
        else if (text.Length == 0)
        {
            Fail(errors, path, ValidationCodes.Options, "The value must not be empty.");
            return null;
        }

        return text;
    }
}
