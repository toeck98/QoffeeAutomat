using System.Globalization;
using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Pages.Commands;

namespace QoffeeAutomat.Ui.Components.Pages.CommandEditor;

/// <summary>
/// One row of the parameter table while it is edited. Numbers are kept as the text that the user typed, so that half a
/// number never blocks typing; <see cref="ToDefinition"/> turns the text into the parameter and reports what is not a number.
/// </summary>
public sealed class ParameterDraft
{
    /// <summary>The problem code for a number field that does not hold a number.</summary>
    public const string NotANumberCode = "not_a_number";

    private string _type = ParameterTypes.Text;

    /// <summary>A key that stays the same while the row moves, for the renderer.</summary>
    public Guid Key { get; } = Guid.NewGuid();

    /// <summary>The name of the parameter (the name of the variable).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>One of <see cref="ParameterTypes"/>. Changing it clears the default, which would not fit the new type.</summary>
    public string Type
    {
        get => _type;
        set
        {
            if (!string.Equals(_type, value, StringComparison.Ordinal))
            {
                _type = value;
                DefaultText = string.Empty;
            }
        }
    }

    /// <summary>A missing required value is an error.</summary>
    public bool Required { get; set; }

    /// <summary>The default: text, number, or <c>true</c> / <c>false</c> for a bool; empty for none.</summary>
    public string DefaultText { get; set; } = string.Empty;

    /// <summary>Lower bound (int and float).</summary>
    public string Min { get; set; } = string.Empty;

    /// <summary>Upper bound (int and float).</summary>
    public string Max { get; set; } = string.Empty;

    /// <summary>Step (int and float).</summary>
    public string Step { get; set; } = string.Empty;

    /// <summary>A regular expression (string).</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>The longest text (string).</summary>
    public string MaxLength { get; set; } = string.Empty;

    /// <summary>The allowed values, one per line (enum).</summary>
    public string OptionsText { get; set; } = string.Empty;

    /// <summary>The value is redacted in history and logs.</summary>
    public bool Secret { get; set; }

    /// <summary>The unit shown next to the value.</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>The description shown in the UI and in Home Assistant.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The options as a list.</summary>
    public IReadOnlyList<string> Options => SplitLines(OptionsText);

    /// <summary>True for an int or float parameter.</summary>
    public bool IsNumber => Type is ParameterTypes.WholeNumber or ParameterTypes.Real;

    /// <summary>Creates the row for a parameter of a stored command.</summary>
    /// <param name="definition">The parameter.</param>
    /// <returns>The row.</returns>
    public static ParameterDraft From(ParameterDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var draft = new ParameterDraft
        {
            Name = definition.Name ?? string.Empty,
            Required = definition.Required,
            Min = Format(definition.Min),
            Max = Format(definition.Max),
            Step = Format(definition.Step),
            Pattern = definition.Pattern ?? string.Empty,
            MaxLength = definition.MaxLength?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            OptionsText = string.Join('\n', definition.Options ?? []),
            Secret = definition.Secret,
            Unit = definition.Unit ?? string.Empty,
            Description = definition.Description ?? string.Empty,
        };

        // The type is set first: assigning it clears the default.
        draft._type = definition.Type ?? ParameterTypes.Text;
        draft.DefaultText = definition.Default is { } value ? DefaultToText(value) : string.Empty;
        return draft;
    }

    /// <summary>Builds the parameter.</summary>
    /// <param name="path">The JSON pointer of the parameter in the document, for the problems.</param>
    /// <param name="problems">Receives what is not a number.</param>
    /// <returns>The parameter.</returns>
    public ParameterDefinition ToDefinition(string path, List<ApiValidationError> problems)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(problems);

        bool number = IsNumber;
        return new ParameterDefinition(
            Name.Trim(),
            Type,
            Required,
            BuildDefault(),
            number ? ParseDouble(Min, path + "/min", problems) : null,
            number ? ParseDouble(Max, path + "/max", problems) : null,
            number ? ParseDouble(Step, path + "/step", problems) : null,
            Type == ParameterTypes.Text ? Blank(Pattern) : null,
            Type == ParameterTypes.Text ? ParseInt(MaxLength, path + "/maxLength", problems) : null,
            Type == ParameterTypes.Choice ? Options : null,
            null,
            Secret,
            Blank(Unit),
            Blank(Description));
    }

    internal static List<string> SplitLines(string? text)
    {
        return
        [
            .. (text ?? string.Empty)
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0),
        ];
    }

    internal static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Format(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string DefaultToText(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }

    private JsonElement? BuildDefault()
    {
        string text = Type == ParameterTypes.Text || Type == ParameterTypes.Choice ? DefaultText : DefaultText.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        switch (Type)
        {
            case ParameterTypes.WholeNumber:
                return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long whole)
                    ? JsonValues.Number(whole.ToString(CultureInfo.InvariantCulture))
                    : JsonValues.Text(text);
            case ParameterTypes.Real:
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double real) && double.IsFinite(real)
                    ? JsonValues.Number(real.ToString("R", CultureInfo.InvariantCulture))
                    : JsonValues.Text(text);
            case ParameterTypes.Flag:
                return text switch
                {
                    "true" => JsonValues.Boolean(true),
                    "false" => JsonValues.Boolean(false),
                    _ => JsonValues.Text(text),
                };
            default:
                // A text that does not fit the type is sent as it is: the agent says so with the path of the default.
                return JsonValues.Text(text);
        }
    }

    private static double? ParseDouble(string text, string path, List<ApiValidationError> problems)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
        {
            return value;
        }

        problems.Add(new ApiValidationError(path, NotANumberCode, "This is not a number."));
        return null;
    }

    private static int? ParseInt(string text, string path, List<ApiValidationError> problems)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (int.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        problems.Add(new ApiValidationError(path, NotANumberCode, "This is not a whole number."));
        return null;
    }
}
