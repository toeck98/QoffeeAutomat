using System.Globalization;
using System.Text;
using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Pages.Commands;

namespace QoffeeAutomat.Ui.Components.Pages.CommandEditor;

/// <summary>The Home Assistant entity that a command will make.</summary>
/// <param name="Platform">The platform: <c>button</c>, <c>switch</c>, <c>select</c>, <c>number</c> or <c>text</c>.</param>
/// <param name="Name">The name of the entity.</param>
/// <param name="Parameter">The parameter that the entity sets.</param>
/// <param name="StateMetric">The metric that reports the state; <c>null</c> when the entity is optimistic.</param>
/// <param name="Options">The options of a select.</param>
/// <param name="Min">The lower bound of a number.</param>
/// <param name="Max">The upper bound of a number.</param>
/// <param name="Step">The step of a number.</param>
public sealed record HaEntityPreview(
    string Platform,
    string Name,
    string? Parameter,
    string? StateMetric,
    IReadOnlyList<string> Options,
    double? Min,
    double? Max,
    double? Step)
{
    /// <summary>True when no metric reports the state, so that Home Assistant assumes it.</summary>
    public bool IsOptimistic => StateMetric is null;
}

/// <summary>
/// What Home Assistant will show for a command (PLAN.md S06, S10), and the YAML of an action that calls it with variables.
/// </summary>
public static class HaPreview
{
    /// <summary>The service that runs a command.</summary>
    public const string Service = "qoffeeautomat.run_command";

    /// <summary>The entity of a command.</summary>
    /// <param name="command">The command as it is drafted.</param>
    /// <returns>The entity, or <c>null</c> when the command makes none: no control, or not enabled and shown.</returns>
    public static HaEntityPreview? Entity(CommandDocument command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.HaControl is not { } control || control.Type == HaControlTypes.None || !command.Enabled || !command.ExposeToHa)
        {
            return null;
        }

        ParameterDefinition? parameter = command.Parameters?.FirstOrDefault(
            candidate => string.Equals(candidate.Name, control.Parameter, StringComparison.Ordinal));
        string name = string.IsNullOrWhiteSpace(command.Name) ? command.Id : command.Name;
        return new HaEntityPreview(
            control.Type,
            name,
            control.Parameter,
            control.StateMetric,
            control.Type == HaControlTypes.Select ? parameter?.Options ?? [] : [],
            control.Type == HaControlTypes.Number ? parameter?.Min : null,
            control.Type == HaControlTypes.Number ? parameter?.Max : null,
            control.Type == HaControlTypes.Number ? parameter?.Step : null);
    }

    /// <summary>The YAML of an action that runs the command with variables.</summary>
    /// <param name="command">The command as it is drafted.</param>
    /// <param name="entered">Variable values that were entered (in the test panel); they take the place of examples.</param>
    /// <returns>The YAML, ending with a line break.</returns>
    public static string Snippet(CommandDocument command, IReadOnlyDictionary<string, JsonElement>? entered)
    {
        ArgumentNullException.ThrowIfNull(command);

        var yaml = new StringBuilder();
        yaml.Append("action: ").Append(Service).Append('\n');
        yaml.Append("target:\n");
        yaml.Append("  device_id: \"<the device of this PC>\"\n");
        yaml.Append("data:\n");
        yaml.Append("  command: ").Append(Scalar(command.Id)).Append('\n');

        var variables = new List<(string Name, string Value)>();
        foreach (ParameterDefinition parameter in command.Parameters ?? [])
        {
            if (string.IsNullOrWhiteSpace(parameter.Name))
            {
                continue;
            }

            JsonElement? value = entered is not null && entered.TryGetValue(parameter.Name, out JsonElement given) ? given : Example(parameter);
            if (value is { } json)
            {
                variables.Add((parameter.Name, Yaml(json)));
            }
        }

        if (variables.Count > 0)
        {
            yaml.Append("  variables:\n");
            foreach ((string name, string value) in variables)
            {
                yaml.Append("    ").Append(Key(name)).Append(": ").Append(value).Append('\n');
            }
        }

        yaml.Append("  wait: true\n");
        return yaml.ToString();
    }

    private static JsonElement? Example(ParameterDefinition parameter)
    {
        if (parameter.Default is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } fallback)
        {
            return fallback;
        }

        switch (parameter.Type)
        {
            case ParameterTypes.WholeNumber:
                return JsonValues.Number(Math.Round(parameter.Min ?? 0).ToString("R", CultureInfo.InvariantCulture));
            case ParameterTypes.Real:
                return JsonValues.Number((parameter.Min ?? 0).ToString("R", CultureInfo.InvariantCulture));
            case ParameterTypes.Flag:
                return JsonValues.Boolean(true);
            case ParameterTypes.Choice:
                return parameter.Options is { Count: > 0 } options ? JsonValues.Text(options[0]) : JsonValues.Text("value");
            default:
                return JsonValues.Text("value");
        }
    }

    private static string Yaml(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => Scalar(value.GetString() ?? string.Empty),
            _ => Scalar(value.GetRawText()),
        };
    }

    /// <summary>A parameter name is a plain key (letters, digits and underscore); anything else is quoted.</summary>
    private static string Key(string name) => name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? name : Scalar(name);

    /// <summary>A double-quoted YAML scalar; the escapes of YAML are those of JSON for everything that is used here.</summary>
    private static string Scalar(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        builder.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }
}
