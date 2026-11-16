using System.Text;

namespace QoffeeAutomat.Platform.Windows.Sessions;

/// <summary>
/// Reads and writes Windows environment blocks (PLAN.md S11, T311): the user's block that <c>CreateEnvironmentBlock</c> returns,
/// with the variables of the run (<c>QA_*</c>) added, is what the elevated process starts with. A block is a sequence of
/// <c>name=value</c> entries, each ended by a NUL character, and one more NUL at the end; the entries are sorted by name,
/// ignoring case. Names are case-insensitive, and the hidden entries of the form <c>=C:=C:\</c> start with an equals sign.
/// </summary>
public static class ElevatedEnvironment
{
    /// <summary>Reads a block.</summary>
    /// <param name="block">The block as text, with the NUL separators; anything after the terminating empty entry is ignored.</param>
    /// <returns>The variables by name, compared ignoring case.</returns>
    public static Dictionary<string, string> Parse(string block)
    {
        ArgumentNullException.ThrowIfNull(block);

        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int start = 0;
        while (start < block.Length)
        {
            int end = block.IndexOf('\0', start);
            if (end < 0)
            {
                end = block.Length;
            }

            if (end == start)
            {
                break;
            }

            string entry = block[start..end];
            int separator = entry.IndexOf('=', 1);
            if (separator > 0)
            {
                variables[entry[..separator]] = entry[(separator + 1)..];
            }

            start = end + 1;
        }

        return variables;
    }

    /// <summary>Merges the variables of a run into the user's and writes the block.</summary>
    /// <param name="user">The variables of the user.</param>
    /// <param name="additions">The variables of the run; they replace variables of the same name.</param>
    /// <returns>The block, ready to be passed with <c>CREATE_UNICODE_ENVIRONMENT</c>.</returns>
    /// <exception cref="ArgumentException">A name is empty or contains <c>=</c>, or a name or value contains a NUL character.</exception>
    public static string Build(IReadOnlyDictionary<string, string> user, IReadOnlyDictionary<string, string> additions)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(additions);

        var merged = new Dictionary<string, string>(user, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in additions)
        {
            Validate(name, value);
            merged[name] = value;
        }

        var block = new StringBuilder();
        foreach (KeyValuePair<string, string> pair in merged.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }

        // The terminating empty entry. An empty environment still needs two NUL characters.
        if (merged.Count == 0)
        {
            block.Append('\0');
        }

        block.Append('\0');
        return block.ToString();
    }

    private static void Validate(string name, string value)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('=', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("An environment variable name must not be empty or contain '=' or NUL.", nameof(name));
        }

        if (value is null || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException($"The value of the environment variable '{name}' must not contain NUL.", nameof(value));
        }
    }
}
