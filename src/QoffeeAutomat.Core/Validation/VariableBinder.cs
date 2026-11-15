using System.Text.Json;
using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Core.Validation;

/// <summary>
/// The outcome of <see cref="VariableBinder.Bind"/>.
/// </summary>
/// <param name="Values">
/// The bound values by parameter name: <see cref="string"/> for string and enum, <see cref="long"/>
/// for int, <see cref="double"/> for float, <see cref="bool"/> for bool. Optional parameters
/// without a value and without a default are absent. Only meaningful when <see cref="IsValid"/>.
/// </param>
/// <param name="Errors">Errors with JSON pointers of the form <c>/vars/&lt;name&gt;</c>.</param>
public sealed record BindResult(IReadOnlyDictionary<string, object> Values, IReadOnlyList<ValidationError> Errors)
{
    /// <summary>True when every variable was accepted.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Validates a dictionary of variable values against the parameter list of a command, applies
/// defaults and converts types (PLAN.md S06, D-012). Unknown names are rejected, required values
/// must be present, numbers must respect min, max and step, strings maxLength (default 1024)
/// and pattern (a .NET regex with a 100 ms match timeout), enums their options. The agent runs
/// this for every call, whatever the client checked.
/// </summary>
public static class VariableBinder
{
    /// <summary>The pointer prefix of the error paths.</summary>
    public const string VarsPath = "/vars";

    /// <summary>Binds the variables of a run.</summary>
    /// <param name="parameters">The parameter definitions of the command; null means none.</param>
    /// <param name="variables">The values sent by the caller; null means none. A JSON null counts as not sent.</param>
    /// <returns>The bound values and the errors.</returns>
    public static BindResult Bind(
        IReadOnlyList<ParameterDefinition>? parameters,
        IReadOnlyDictionary<string, JsonElement>? variables)
    {
        var errors = new List<ValidationError>();
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        var declared = new Dictionary<string, ParameterDefinition>(StringComparer.Ordinal);

        if (parameters is not null)
        {
            foreach (ParameterDefinition parameter in parameters)
            {
                if (parameter is not null && !string.IsNullOrEmpty(parameter.Name))
                {
                    declared.TryAdd(parameter.Name, parameter);
                }
            }
        }

        if (variables is not null)
        {
            foreach (string name in variables.Keys)
            {
                if (!declared.ContainsKey(name))
                {
                    errors.Add(new ValidationError(
                        JsonPointer.Member(VarsPath, name),
                        ValidationCodes.UnknownVariable,
                        "The command declares no such variable."));
                }
            }
        }

        foreach (ParameterDefinition parameter in declared.Values)
        {
            string path = JsonPointer.Member(VarsPath, parameter.Name);

            if (variables is not null
                && variables.TryGetValue(parameter.Name, out JsonElement supplied)
                && IsSupplied(supplied))
            {
                object? converted = ParameterValues.Convert(parameter, supplied, path, coerce: true, errors);
                if (converted is not null)
                {
                    values[parameter.Name] = converted;
                }

                continue;
            }

            if (parameter.Default is { } defaultValue && IsSupplied(defaultValue))
            {
                object? converted = ParameterValues.Convert(parameter, defaultValue, path, coerce: false, errors);
                if (converted is not null)
                {
                    values[parameter.Name] = converted;
                }

                continue;
            }

            if (parameter.Required)
            {
                errors.Add(new ValidationError(path, ValidationCodes.Required, "A value is required."));
            }
        }

        return new BindResult(values, errors);
    }

    private static bool IsSupplied(JsonElement value)
    {
        return value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }
}
