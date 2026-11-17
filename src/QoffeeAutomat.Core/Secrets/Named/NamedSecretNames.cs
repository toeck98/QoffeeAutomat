using System.Text.RegularExpressions;

namespace QoffeeAutomat.Core.Secrets.Named;

/// <summary>
/// The rules for the names of named secrets (PLAN.md S09, T706). A name is the lower-case identifier that a command lists in
/// <c>secrets</c>; the run receives the value in the environment variable <c>QA_SECRET_&lt;NAME&gt;</c> (upper case). In the
/// <see cref="ISecretStore"/> the secret lives under <c>script.&lt;name&gt;</c> (<see cref="SecretNames.ScriptPrefix"/>), so it
/// can never clash with the secrets of the agent itself.
/// </summary>
public static class NamedSecretNames
{
    /// <summary>The prefix of the environment variable that carries a secret to a process.</summary>
    public const string EnvironmentPrefix = "QA_SECRET_";

    /// <summary>The longest name.</summary>
    public const int MaxNameLength = 63;

    /// <summary>The shortest value; shorter values would garble ordinary output when they are masked (see <see cref="SecretMasker"/>).</summary>
    public const int MinValueLength = 4;

    /// <summary>The longest value; an environment variable cannot be much longer on Windows.</summary>
    public const int MaxValueLength = 4096;

    private static readonly Regex NamePattern = new(
        @"^[a-z][a-z0-9_]{0,62}\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Tells whether a string is a valid name (<c>^[a-z][a-z0-9_]{0,62}$</c>).</summary>
    /// <param name="name">The candidate.</param>
    /// <returns>True when the name is valid.</returns>
    public static bool IsValid(string? name)
    {
        return !string.IsNullOrEmpty(name) && NamePattern.IsMatch(name);
    }

    /// <summary>Returns why a value cannot be stored.</summary>
    /// <param name="value">The candidate.</param>
    /// <returns>A message, or null when the value is acceptable. The message never contains the value.</returns>
    public static string? DescribeValueProblem(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "A value is required.";
        }

        if (value.Length < MinValueLength)
        {
            return $"The value must have at least {MinValueLength} characters, so that it can be masked reliably in output.";
        }

        if (value.Length > MaxValueLength)
        {
            return $"The value must not be longer than {MaxValueLength} characters.";
        }

        if (value.Contains('\0', StringComparison.Ordinal))
        {
            return "The value must not contain a NUL character.";
        }

        return null;
    }

    /// <summary>The name under which the <see cref="ISecretStore"/> keeps the secret.</summary>
    /// <param name="name">A valid name.</param>
    /// <returns><c>script.&lt;name&gt;</c>.</returns>
    public static string StoreName(string name)
    {
        return SecretNames.ScriptPrefix + name;
    }

    /// <summary>The name of the environment variable that carries the secret.</summary>
    /// <param name="name">A valid name.</param>
    /// <returns><c>QA_SECRET_&lt;NAME&gt;</c>.</returns>
    public static string EnvironmentVariable(string name)
    {
        return EnvironmentPrefix + name.ToUpperInvariant();
    }
}
