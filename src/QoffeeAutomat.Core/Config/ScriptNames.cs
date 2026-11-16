using System.Text.RegularExpressions;
using QoffeeAutomat.Core.IO;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Config;

/// <summary>
/// Rules for the names of script files and the ids of documents. A name is a plain file name:
/// no path separators, no <c>..</c>, nothing that Windows treats specially. The rules are what keeps
/// every path the store builds inside its directory.
/// </summary>
public static class ScriptNames
{
    /// <summary>The longest script name.</summary>
    public const int MaxLength = 128;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex NamePattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex IdPattern = new(
        @"^[a-z][a-z0-9_]{0,62}\z",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Tells whether a string is a valid script name.</summary>
    /// <param name="name">The candidate.</param>
    /// <returns>True when the name is safe to combine with the scripts directory.</returns>
    public static bool IsValid(string? name)
    {
        return Validate(name, "/name").Count == 0;
    }

    /// <summary>Checks a script name and explains what is wrong with it.</summary>
    /// <param name="name">The candidate.</param>
    /// <param name="path">The JSON pointer used in the errors.</param>
    /// <returns>The errors; empty when the name is valid.</returns>
    public static IReadOnlyList<ValidationError> Validate(string? name, string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (string.IsNullOrEmpty(name))
        {
            return [new ValidationError(path, ValidationCodes.Required, "A script name is required.")];
        }

        if (name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal)
            || name.Contains(':', StringComparison.Ordinal))
        {
            return [new ValidationError(path, ValidationCodes.Invalid, "A script name must not contain path separators.")];
        }

        if (name.Contains("..", StringComparison.Ordinal))
        {
            return [new ValidationError(path, ValidationCodes.Invalid, "A script name must not contain '..'.")];
        }

        if (!NamePattern.IsMatch(name)
            || name.EndsWith('.')
            || name.EndsWith(AtomicFile.TempExtension, StringComparison.OrdinalIgnoreCase)
            || ReservedWindowsNames.Contains(StemOf(name)))
        {
            return [new ValidationError(
                path,
                ValidationCodes.Invalid,
                "A script name has 1 to 128 letters, digits, '.', '_' or '-', starts with a letter or digit and is not a reserved device name.")];
        }

        return [];
    }

    /// <summary>Tells whether a string is a valid document id (<c>^[a-z][a-z0-9_]{0,62}$</c>).</summary>
    /// <param name="id">The candidate.</param>
    /// <returns>True when the id can be used as a file name.</returns>
    public static bool IsValidDocumentId(string? id)
    {
        return !string.IsNullOrEmpty(id) && IdPattern.IsMatch(id);
    }

    private static string StemOf(string name)
    {
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? name : name[..dot];
    }
}
