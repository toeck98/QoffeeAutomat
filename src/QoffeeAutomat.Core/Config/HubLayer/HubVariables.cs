namespace QoffeeAutomat.Core.Config.HubLayer;

/// <summary>
/// The variables document of the hub as environment variables (PLAN.md S20): every run gets <c>QA_HUBVAR_&lt;NAME&gt;</c> with the value of the variable.
/// The values reach a process only through its environment, like <c>QA_VAR_&lt;NAME&gt;</c>; they are never spliced into script text or command lines (D-012).
/// </summary>
public static class HubVariables
{
    /// <summary>The prefix of the environment variable names.</summary>
    public const string Prefix = "QA_HUBVAR_";

    /// <summary>The name of the environment variable for a hub variable.</summary>
    /// <param name="name">The variable name as the hub wrote it.</param>
    /// <returns>The prefix followed by the upper-case name.</returns>
    public static string EnvironmentName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Prefix + name.ToUpperInvariant();
    }

    /// <summary>Maps hub variables to the environment variables of a run.</summary>
    /// <param name="variables">The variables: name to value.</param>
    /// <returns>The environment variable name to value; empty when there are no variables.</returns>
    public static IReadOnlyDictionary<string, string> ToEnvironment(IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        var environment = new Dictionary<string, string>(variables.Count, StringComparer.Ordinal);
        foreach ((string name, string value) in variables)
        {
            environment[EnvironmentName(name)] = value;
        }

        return environment;
    }
}
