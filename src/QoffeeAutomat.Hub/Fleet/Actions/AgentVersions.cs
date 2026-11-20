using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace QoffeeAutomat.Hub.Fleet.Actions;

/// <summary>
/// Reads and compares the versions that agents report (PLAN.md S13: <c>major.minor.patch</c> with an optional pre-release; build metadata is ignored, a leading <c>v</c> is accepted,
/// a pre-release is older than its release). The hub cannot reference the agent, so it has its own small reader.
/// </summary>
public static class AgentVersions
{
    /// <summary>Tells whether a text is a version.</summary>
    /// <param name="text">For example <c>1.2.3</c>, <c>v1.2.3</c> or <c>1.2.3-rc.1+build5</c>.</param>
    /// <returns>True when it is a version.</returns>
    public static bool IsValid(string? text) => TryParse(text, out _);

    /// <summary>Compares two versions.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <returns>A negative number when <paramref name="left"/> is older, 0 when they are equal, a positive number when it is newer; null when one of them is not a version.</returns>
    public static int? Compare(string? left, string? right)
    {
        if (!TryParse(left, out ParsedVersion? a) || !TryParse(right, out ParsedVersion? b))
        {
            return null;
        }

        int result = a.Major.CompareTo(b.Major);
        if (result == 0)
        {
            result = a.Minor.CompareTo(b.Minor);
        }

        if (result == 0)
        {
            result = a.Patch.CompareTo(b.Patch);
        }

        return result != 0 ? result : ComparePreRelease(a.PreRelease, b.PreRelease);
    }

    /// <summary>Tells whether a version is the target or newer.</summary>
    /// <param name="version">The version that an agent reports; may be null.</param>
    /// <param name="target">The version that is wanted.</param>
    /// <returns>True when both are versions and <paramref name="version"/> is not older than <paramref name="target"/>.</returns>
    public static bool AtLeast(string? version, string target) => Compare(version, target) is >= 0;

    /// <summary>Picks the newer of two versions.</summary>
    /// <param name="current">The newest so far; may be null.</param>
    /// <param name="candidate">A version that may be newer; ignored when it is not a version.</param>
    /// <returns>The newer version, as it was written.</returns>
    public static string? Newest(string? current, string? candidate)
    {
        if (!IsValid(candidate))
        {
            return current;
        }

        return current is null || Compare(candidate, current) is > 0 ? candidate : current;
    }

    private static int ComparePreRelease(string[] left, string[] right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            // No pre-release is newer than any pre-release.
            return right.Length.CompareTo(left.Length);
        }

        for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            bool leftNumeric = ulong.TryParse(left[index], NumberStyles.None, CultureInfo.InvariantCulture, out ulong leftNumber);
            bool rightNumeric = ulong.TryParse(right[index], NumberStyles.None, CultureInfo.InvariantCulture, out ulong rightNumber);
            int result = (leftNumeric, rightNumeric) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(left[index], right[index]),
            };
            if (result != 0)
            {
                return result;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static bool TryParse(string? text, [NotNullWhen(true)] out ParsedVersion? version)
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
        if (dash >= 0)
        {
            preRelease = trimmed[(dash + 1)..].Split('.');
            trimmed = trimmed[..dash];
            if (preRelease.Any(identifier => identifier.Length == 0))
            {
                return false;
            }
        }

        string[] parts = trimmed.Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
        {
            return false;
        }

        version = new ParsedVersion(major, minor, patch, preRelease);
        return true;
    }

    private sealed record ParsedVersion(int Major, int Minor, int Patch, string[] PreRelease);
}
