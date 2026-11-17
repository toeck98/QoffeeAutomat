using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace QoffeeAutomat.Agent.Updates;

/// <summary>
/// A semantic version (PLAN.md S13): <c>major.minor.patch</c> with an optional pre-release (<c>-rc.1</c>); build metadata (<c>+abc</c>)
/// is ignored, as the SemVer rules say, and a leading <c>v</c> (the tag of a release) is accepted. A pre-release is older than the
/// release it precedes.
/// </summary>
public sealed class SemanticVersion
{
    private readonly string[] _preRelease;

    private SemanticVersion(int major, int minor, int patch, string[] preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _preRelease = preRelease;
    }

    /// <summary>The major version.</summary>
    public int Major { get; }

    /// <summary>The minor version.</summary>
    public int Minor { get; }

    /// <summary>The patch version.</summary>
    public int Patch { get; }

    /// <summary>True for a pre-release such as <c>1.2.0-rc.1</c>.</summary>
    public bool IsPreRelease => _preRelease.Length > 0;

    /// <summary>Reads a version.</summary>
    /// <param name="text">For example <c>1.2.3</c>, <c>v1.2.3</c> or <c>1.2.3-rc.1+build5</c>.</param>
    /// <param name="version">The version.</param>
    /// <returns>True when <paramref name="text"/> is a version.</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed[0] is 'v' or 'V')
        {
            trimmed = trimmed[1..];
        }

        int plus = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            trimmed = trimmed[..plus];
        }

        string[] preRelease = [];
        int dash = trimmed.IndexOf('-', StringComparison.Ordinal);
        string core = trimmed;
        if (dash >= 0)
        {
            core = trimmed[..dash];
            preRelease = trimmed[(dash + 1)..].Split('.');
            if (preRelease.Any(part => !IsValidIdentifier(part)))
            {
                return false;
            }
        }

        string[] numbers = core.Split('.');
        if (numbers.Length != 3
            || !TryNumber(numbers[0], out int major)
            || !TryNumber(numbers[1], out int minor)
            || !TryNumber(numbers[2], out int patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, preRelease);
        return true;
    }

    /// <summary>Compares two versions by the SemVer precedence rules.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <returns>A negative number when <paramref name="left"/> is older, zero when they are equal, a positive number when it is newer.</returns>
    public static int Compare(SemanticVersion left, SemanticVersion right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        int result = left.Major.CompareTo(right.Major);
        if (result != 0)
        {
            return result;
        }

        result = left.Minor.CompareTo(right.Minor);
        if (result != 0)
        {
            return result;
        }

        result = left.Patch.CompareTo(right.Patch);
        if (result != 0)
        {
            return result;
        }

        // A release is newer than its pre-releases.
        if (left._preRelease.Length == 0 || right._preRelease.Length == 0)
        {
            return right._preRelease.Length.CompareTo(left._preRelease.Length);
        }

        int shared = Math.Min(left._preRelease.Length, right._preRelease.Length);
        for (int index = 0; index < shared; index++)
        {
            result = CompareIdentifiers(left._preRelease[index], right._preRelease[index]);
            if (result != 0)
            {
                return result;
            }
        }

        return left._preRelease.Length.CompareTo(right._preRelease.Length);
    }

    /// <summary>Tells whether this version is newer than another one.</summary>
    /// <param name="other">The other version.</param>
    /// <returns>True when this version has the higher precedence.</returns>
    public bool IsNewerThan(SemanticVersion other) => Compare(this, other) > 0;

    /// <summary>The version without a leading <c>v</c> and without build metadata.</summary>
    /// <returns>For example <c>1.2.3</c> or <c>1.2.3-rc.1</c>.</returns>
    public override string ToString()
    {
        string core = Major.ToString(CultureInfo.InvariantCulture)
            + "." + Minor.ToString(CultureInfo.InvariantCulture)
            + "." + Patch.ToString(CultureInfo.InvariantCulture);
        return _preRelease.Length == 0 ? core : core + "-" + string.Join('.', _preRelease);
    }

    private static bool TryNumber(string text, out int number)
    {
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static bool IsValidIdentifier(string part)
    {
        return part.Length > 0 && part.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
    }

    private static int CompareIdentifiers(string left, string right)
    {
        bool leftNumeric = left.All(char.IsAsciiDigit);
        bool rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            // Compare by length first, then by text, so that no number can overflow.
            string leftDigits = left.TrimStart('0');
            string rightDigits = right.TrimStart('0');
            int byLength = leftDigits.Length.CompareTo(rightDigits.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(leftDigits, rightDigits);
        }

        if (leftNumeric != rightNumeric)
        {
            // Numeric identifiers have lower precedence than alphanumeric ones.
            return leftNumeric ? -1 : 1;
        }

        return string.CompareOrdinal(left, right);
    }
}
