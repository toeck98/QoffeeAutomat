using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace QoffeeAutomat.Agent.Updates;

/// <summary>
/// The checksum file of a release, <c>SHA256SUMS.txt</c> in the format of <c>sha256sum</c> (PLAN.md S13): one line per file with
/// 64 hexadecimal digits, white space, an optional <c>*</c> (binary mode) and the file name.
/// </summary>
public static class ChecksumFile
{
    private const int DigestLength = 64;

    /// <summary>Finds the checksum of a file.</summary>
    /// <param name="content">The text of <c>SHA256SUMS.txt</c>.</param>
    /// <param name="fileName">The name of the file, compared exactly.</param>
    /// <param name="digest">The checksum as 64 lower-case hexadecimal digits.</param>
    /// <returns>False when the file has no line, or its line is not well formed.</returns>
    public static bool TryFind(string content, string fileName, [NotNullWhen(true)] out string? digest)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        digest = null;
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length <= DigestLength + 1 || !IsHex(line.AsSpan(0, DigestLength)))
            {
                continue;
            }

            ReadOnlySpan<char> rest = line.AsSpan(DigestLength);
            if (rest[0] is not (' ' or '\t'))
            {
                continue;
            }

            string name = rest.TrimStart(" \t").ToString();
            if (name.StartsWith('*'))
            {
                name = name[1..];
            }

            if (string.Equals(name, fileName, StringComparison.Ordinal))
            {
                digest = Convert.ToHexStringLower(Convert.FromHexString(line.AsSpan(0, DigestLength)));
                return true;
            }
        }

        return false;
    }

    /// <summary>Computes the SHA-256 checksum of a file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The checksum as 64 lower-case hexadecimal digits.</returns>
    public static async Task<string> ComputeAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Compares two checksums without leaking where they differ.</summary>
    /// <param name="expected">The checksum from the release.</param>
    /// <param name="actual">The checksum of the downloaded file.</param>
    /// <returns>True when both are the same 64 digits, whatever their case.</returns>
    public static bool Matches(string expected, string actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        if (expected.Length != DigestLength || actual.Length != DigestLength || !IsHex(expected) || !IsHex(actual))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual));
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (char character in text)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
