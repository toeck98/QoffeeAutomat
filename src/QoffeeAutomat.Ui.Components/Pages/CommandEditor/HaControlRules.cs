using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Ui.Components.Pages.CommandEditor;

/// <summary>
/// Which Home Assistant controls a command can have (PLAN.md S06 table "haControl", S10): the editor offers only these, so
/// that a mapping that the agent would refuse cannot be built. The rules are those of <c>DocumentValidator</c>.
/// </summary>
public static class HaControlRules
{
    /// <summary>All control types, in the order that they are offered.</summary>
    public static readonly IReadOnlyList<string> AllTypes =
    [
        HaControlTypes.None,
        HaControlTypes.Button,
        HaControlTypes.Switch,
        HaControlTypes.Select,
        HaControlTypes.Number,
        HaControlTypes.Text,
    ];

    /// <summary>The control types that the parameters allow.</summary>
    /// <param name="parameters">The parameters of the command.</param>
    /// <returns>
    /// <c>none</c> always; <c>button</c> when no parameter is required without a default; and a type that sets one parameter
    /// when the command has a parameter of the right type.
    /// </returns>
    public static IReadOnlyList<string> ValidTypes(IReadOnlyList<ParameterDefinition> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var types = new List<string> { HaControlTypes.None };
        if (parameters.All(parameter => !NeedsValue(parameter)))
        {
            types.Add(HaControlTypes.Button);
        }

        foreach (string type in new[] { HaControlTypes.Switch, HaControlTypes.Select, HaControlTypes.Number, HaControlTypes.Text })
        {
            if (ParametersFor(type, parameters).Count > 0)
            {
                types.Add(type);
            }
        }

        return types;
    }

    /// <summary>The parameters that a control of a type can set.</summary>
    /// <param name="type">The control type.</param>
    /// <param name="parameters">The parameters of the command.</param>
    /// <returns>The names; empty for a control that sets no parameter.</returns>
    public static IReadOnlyList<string> ParametersFor(string type, IReadOnlyList<ParameterDefinition> parameters)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(parameters);

        string[] accepted = ParameterTypesFor(type);
        return
        [
            .. parameters
                .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name) && accepted.Contains(parameter.Type, StringComparer.Ordinal))
                .Select(parameter => parameter.Name),
        ];
    }

    /// <summary>The parameter types that a control type accepts.</summary>
    /// <param name="type">The control type.</param>
    /// <returns>The types; empty for a control that sets no parameter.</returns>
    public static string[] ParameterTypesFor(string type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type switch
        {
            HaControlTypes.Switch => [ParameterTypes.Flag],
            HaControlTypes.Select => [ParameterTypes.Choice],
            HaControlTypes.Number => [ParameterTypes.WholeNumber, ParameterTypes.Real],
            HaControlTypes.Text => [ParameterTypes.Text],
            _ => [],
        };
    }

    /// <summary>Tells whether a control type sets a parameter.</summary>
    /// <param name="type">The control type.</param>
    /// <returns>True for switch, select, number and text.</returns>
    public static bool SetsParameter(string type) => ParameterTypesFor(type).Length > 0;

    /// <summary>The metrics that can report the state of a control: those with the value type that the control has.</summary>
    /// <param name="type">The control type.</param>
    /// <param name="metrics">The metrics of the agent.</param>
    /// <returns>The fitting metrics; empty for a button and for no control.</returns>
    public static IReadOnlyList<MetricInfo> StateMetricsFor(string type, IReadOnlyList<MetricInfo> metrics)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(metrics);

        string? valueType = type switch
        {
            HaControlTypes.Switch => "bool",
            HaControlTypes.Select => "enum",
            HaControlTypes.Number => "number",
            HaControlTypes.Text => "string",
            _ => null,
        };
        return valueType is null
            ? []
            : [.. metrics.Where(metric => string.Equals(metric.ValueType, valueType, StringComparison.Ordinal))];
    }

    private static bool NeedsValue(ParameterDefinition parameter)
    {
        bool hasDefault = parameter.Default is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };
        return parameter.Required && !hasDefault;
    }
}
