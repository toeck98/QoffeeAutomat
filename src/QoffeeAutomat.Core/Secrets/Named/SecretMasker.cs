using QoffeeAutomat.Core.History;

namespace QoffeeAutomat.Core.Secrets.Named;

/// <summary>
/// Replaces the values of named secrets in text by <see cref="Replacement"/> (PLAN.md S09, T706). It is applied to the
/// captured output of a run before the output is stored, streamed or returned, so a script that prints a secret never
/// leaves it behind. Longer values are replaced first. Only values of at least <see cref="NamedSecretNames.MinValueLength"/>
/// characters are masked; the store does not accept shorter ones.
/// </summary>
public sealed class SecretMasker
{
    /// <summary>What replaces a secret value.</summary>
    public const string Replacement = RunVariables.RedactedValue;

    private readonly string[] _values;

    /// <summary>Creates a masker.</summary>
    /// <param name="values">The secret values to remove from text.</param>
    public SecretMasker(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        _values =
        [
            .. values
                .Where(value => value.Length >= NamedSecretNames.MinValueLength)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(value => value.Length)
                .ThenBy(value => value, StringComparer.Ordinal),
        ];
    }

    /// <summary>A masker that changes nothing.</summary>
    public static SecretMasker Empty { get; } = new([]);

    /// <summary>True when there is nothing to mask.</summary>
    public bool IsEmpty => _values.Length == 0;

    /// <summary>Masks every occurrence of every secret value.</summary>
    /// <param name="text">The text, for example the standard output of a run.</param>
    /// <returns>The text without the secret values.</returns>
    public string Apply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0 || _values.Length == 0)
        {
            return text;
        }

        string result = text;
        foreach (string value in _values)
        {
            result = result.Replace(value, Replacement, StringComparison.Ordinal);
        }

        return result;
    }
}
