namespace QoffeeAutomat.Core.Secrets.Named;

/// <summary>
/// The values of the named secrets that one run asked for. It deliberately has no record semantics, so that
/// <see cref="ToString"/> and logging of the object can never print a value.
/// </summary>
public sealed class ResolvedSecrets
{
    private readonly Dictionary<string, string> _values;

    /// <summary>Creates the result.</summary>
    /// <param name="values">The values by secret name.</param>
    /// <param name="missing">The names that are not stored.</param>
    public ResolvedSecrets(IReadOnlyDictionary<string, string> values, IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(missing);

        _values = new Dictionary<string, string>(values, StringComparer.Ordinal);
        Missing = missing;
    }

    /// <summary>No secrets were asked for.</summary>
    public static ResolvedSecrets None { get; } = new(new Dictionary<string, string>(), []);

    /// <summary>The names that the command lists but that are not stored.</summary>
    public IReadOnlyList<string> Missing { get; }

    /// <summary>True when every requested secret was found.</summary>
    public bool IsComplete => Missing.Count == 0;

    /// <summary>The number of values.</summary>
    public int Count => _values.Count;

    /// <summary>The environment variables for the process: <c>QA_SECRET_&lt;NAME&gt;</c> to the value.</summary>
    /// <returns>A new dictionary.</returns>
    public IReadOnlyDictionary<string, string> ToEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, string value) in _values)
        {
            environment[NamedSecretNames.EnvironmentVariable(name)] = value;
        }

        return environment;
    }

    /// <summary>Creates the masker for the output of the run.</summary>
    /// <returns>A masker that removes every value.</returns>
    public SecretMasker CreateMasker()
    {
        return _values.Count == 0 ? SecretMasker.Empty : new SecretMasker(_values.Values);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"ResolvedSecrets({_values.Count} values)";
    }
}
