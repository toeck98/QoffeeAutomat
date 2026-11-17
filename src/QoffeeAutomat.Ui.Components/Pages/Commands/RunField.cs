using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Ui.Components.Pages.Commands;

/// <summary>Why the value of a field is refused before the run is sent.</summary>
public enum FieldProblemKind
{
    /// <summary>A required value is missing.</summary>
    Required = 0,

    /// <summary>The text is not a number.</summary>
    NotANumber,

    /// <summary>The text is a number but not a whole number.</summary>
    NotAWholeNumber,

    /// <summary>The number is below <c>min</c>; <see cref="FieldProblem.Bound"/> is the minimum.</summary>
    BelowMinimum,

    /// <summary>The number is above <c>max</c>; <see cref="FieldProblem.Bound"/> is the maximum.</summary>
    AboveMaximum,

    /// <summary>The number is not a multiple of <c>step</c>; <see cref="FieldProblem.Bound"/> is the step.</summary>
    NotOnStep,

    /// <summary>The text is longer than <c>maxLength</c>; <see cref="FieldProblem.Bound"/> is the limit.</summary>
    TooLong,

    /// <summary>The text does not match <c>pattern</c>.</summary>
    PatternMismatch,

    /// <summary>The value is none of the <c>options</c>.</summary>
    NotAnOption,
}

/// <summary>A refused value.</summary>
/// <param name="Kind">The reason.</param>
/// <param name="Bound">The limit that the value breaks, for the kinds that have one.</param>
public readonly record struct FieldProblem(FieldProblemKind Kind, double Bound = 0);

/// <summary>
/// The input of one parameter in the run form: the text or switch that the user edits, and the rules of the parameter
/// that the input is checked against before a run is sent. The rules mirror the checks of the agent (PLAN.md S06,
/// <c>VariableBinder</c>); the agent checks again whatever the UI did.
/// </summary>
public sealed class RunField
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Creates the field with the default of the parameter as its start value.</summary>
    /// <param name="definition">The parameter.</param>
    public RunField(ParameterDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        Definition = definition;
        if (definition.Default is { } value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    Text = value.GetString() ?? string.Empty;
                    break;
                case JsonValueKind.Number:
                    Text = value.GetRawText();
                    break;
                case JsonValueKind.True:
                    Flag = true;
                    break;
            }
        }
    }

    /// <summary>The parameter.</summary>
    public ParameterDefinition Definition { get; }

    /// <summary>The name of the parameter, which is also the name of the variable.</summary>
    public string Name => Definition.Name;

    /// <summary>The text of a <c>string</c>, <c>int</c>, <c>float</c> or <c>enum</c> parameter.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The value of a <c>bool</c> parameter.</summary>
    public bool Flag { get; set; }

    /// <summary>The message next to the field: the refusal of the form, or the one of the agent. <c>null</c> when there is none.</summary>
    public string? Message { get; set; }

    /// <summary>True when a missing value is an error: the parameter is required and has no default to fall back on.</summary>
    public bool IsRequired => Definition.Required && !HasDefault;

    /// <summary>True for a <c>bool</c> parameter, which always has a value.</summary>
    public bool IsFlag => Definition.Type == ParameterTypes.Flag;

    /// <summary>True for a number parameter.</summary>
    public bool IsNumber => Definition.Type is ParameterTypes.WholeNumber or ParameterTypes.Real;

    /// <summary>True for an <c>enum</c> parameter that lists its options; one without a list is typed in as text.</summary>
    public bool HasOptions => Definition.Type == ParameterTypes.Choice && Definition.Options is { Count: > 0 };

    private bool HasDefault => Definition.Default is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };

    /// <summary>Checks the input against the rules of the parameter.</summary>
    /// <returns>The first problem, or <c>null</c> when the input is acceptable.</returns>
    public FieldProblem? Validate()
    {
        if (IsFlag)
        {
            return null;
        }

        string text = Definition.Type == ParameterTypes.Text ? Text : Text.Trim();
        if (text.Length == 0)
        {
            return IsRequired ? new FieldProblem(FieldProblemKind.Required) : null;
        }

        return Definition.Type switch
        {
            ParameterTypes.Text => ValidateText(text),
            ParameterTypes.WholeNumber => ValidateWholeNumber(text),
            ParameterTypes.Real => ValidateReal(text),
            ParameterTypes.Choice => ValidateChoice(text),
            _ => null,
        };
    }

    /// <summary>The value to send.</summary>
    /// <returns>
    /// The JSON value, or <c>null</c> when nothing is sent: an optional field without input is left to the agent, which
    /// applies the default. Only meaningful when <see cref="Validate"/> found no problem.
    /// </returns>
    public JsonElement? ToJson()
    {
        if (IsFlag)
        {
            return JsonValues.Boolean(Flag);
        }

        string text = Definition.Type == ParameterTypes.Text ? Text : Text.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        switch (Definition.Type)
        {
            case ParameterTypes.WholeNumber:
                return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long whole)
                    ? JsonValues.Number(whole.ToString(CultureInfo.InvariantCulture))
                    : JsonValues.Text(text);
            case ParameterTypes.Real:
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double real)
                    ? JsonValues.Number(real.ToString("R", CultureInfo.InvariantCulture))
                    : JsonValues.Text(text);
            default:
                return JsonValues.Text(text);
        }
    }

    private FieldProblem? ValidateText(string text)
    {
        int maxLength = Definition.MaxLength ?? ParameterDefinition.DefaultMaxLength;
        if (text.Length > maxLength)
        {
            return new FieldProblem(FieldProblemKind.TooLong, maxLength);
        }

        if (!string.IsNullOrEmpty(Definition.Pattern))
        {
            try
            {
                if (!Regex.IsMatch(text, Definition.Pattern, RegexOptions.CultureInvariant, PatternTimeout))
                {
                    return new FieldProblem(FieldProblemKind.PatternMismatch);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
            {
                // A pattern that cannot be used here is the business of the agent, which reports it.
            }
        }

        return null;
    }

    private FieldProblem? ValidateWholeNumber(string text)
    {
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number))
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double real) && double.IsFinite(real)
                ? new FieldProblem(FieldProblemKind.NotAWholeNumber)
                : new FieldProblem(FieldProblemKind.NotANumber);
        }

        return CheckRange(number);
    }

    private FieldProblem? ValidateReal(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
        {
            return new FieldProblem(FieldProblemKind.NotANumber);
        }

        return CheckRange(number);
    }

    private FieldProblem? CheckRange(double number)
    {
        if (Definition.Min is { } min && number < min)
        {
            return new FieldProblem(FieldProblemKind.BelowMinimum, min);
        }

        if (Definition.Max is { } max && number > max)
        {
            return new FieldProblem(FieldProblemKind.AboveMaximum, max);
        }

        if (Definition.Step is { } step && step > 0)
        {
            double steps = (number - (Definition.Min ?? 0)) / step;
            if (Math.Abs(steps - Math.Round(steps)) > 1e-9 * Math.Max(1, Math.Abs(steps)))
            {
                return new FieldProblem(FieldProblemKind.NotOnStep, step);
            }
        }

        return null;
    }

    private FieldProblem? ValidateChoice(string text)
    {
        return HasOptions && !Definition.Options!.Contains(text, StringComparer.Ordinal)
            ? new FieldProblem(FieldProblemKind.NotAnOption)
            : null;
    }
}

/// <summary>Builds JSON values without reflection-based serialization.</summary>
internal static class JsonValues
{
    public static JsonElement Boolean(bool value) => Parse(value ? "true" : "false");

    public static JsonElement Number(string invariantText) => Parse(invariantText);

    public static JsonElement Text(string value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(value);
        }

        return Parse(buffer.ToArray());
    }

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement Parse(byte[] json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
