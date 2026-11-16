namespace QoffeeAutomat.Core.Platform;

/// <summary>
/// Reads the validated variables that <see cref="IBuiltinActionProvider.ExecuteAsync"/> receives (string, long, double or
/// bool, as produced by the variable binder; an optional parameter without value is absent). A provider uses these
/// accessors instead of casting by hand, so a missing or mistyped value gives the same answer everywhere.
/// </summary>
public static class BuiltinActionVariables
{
    /// <summary>Reads a boolean variable.</summary>
    /// <param name="variables">The variables of the run.</param>
    /// <param name="name">The parameter name.</param>
    /// <param name="fallback">The value when the variable is absent or not a boolean.</param>
    /// <returns>The value.</returns>
    public static bool GetBool(IReadOnlyDictionary<string, object> variables, string name, bool fallback = false)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(name);

        return variables.TryGetValue(name, out object? value) && value is bool flag ? flag : fallback;
    }

    /// <summary>Reads a whole-number variable.</summary>
    /// <param name="variables">The variables of the run.</param>
    /// <param name="name">The parameter name.</param>
    /// <returns>The value; null when the variable is absent or not a whole number.</returns>
    public static long? GetLong(IReadOnlyDictionary<string, object> variables, string name)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(name);

        if (!variables.TryGetValue(name, out object? value))
        {
            return null;
        }

        return value switch
        {
            long whole => whole,
            int smallWhole => smallWhole,
            _ => null,
        };
    }

    /// <summary>Reads a string (or enum) variable.</summary>
    /// <param name="variables">The variables of the run.</param>
    /// <param name="name">The parameter name.</param>
    /// <returns>The value; null when the variable is absent or not a string.</returns>
    public static string? GetString(IReadOnlyDictionary<string, object> variables, string name)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(name);

        return variables.TryGetValue(name, out object? value) && value is string text ? text : null;
    }
}
