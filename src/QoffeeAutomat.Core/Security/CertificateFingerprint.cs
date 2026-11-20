using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace QoffeeAutomat.Core.Security;

/// <summary>
/// The SHA-256 fingerprint of a certificate as users compare it (PLAN.md S09 item 3): upper-case hex
/// pairs separated by colons, for example <c>AB:CD:...</c>. The pairing dialog and the Home Assistant
/// config flow show the same text.
/// </summary>
public static class CertificateFingerprint
{
    /// <summary>Computes the fingerprint of a certificate.</summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>The fingerprint, 32 hex pairs separated by colons.</returns>
    public static string Compute(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        return Format(SHA256.HashData(certificate.RawData));
    }

    /// <summary>Formats a SHA-256 hash as a fingerprint.</summary>
    /// <param name="hash">The hash bytes.</param>
    /// <returns>Upper-case hex pairs separated by colons.</returns>
    public static string Format(ReadOnlySpan<byte> hash)
    {
        string hex = Convert.ToHexString(hash);
        var builder = new StringBuilder(hex.Length + (hex.Length / 2));
        for (int index = 0; index < hex.Length; index += 2)
        {
            if (index > 0)
            {
                builder.Append(':');
            }

            builder.Append(hex, index, 2);
        }

        return builder.ToString();
    }

    /// <summary>Tells whether two fingerprints are equal, ignoring case, colons and spaces.</summary>
    /// <param name="left">A fingerprint in any common notation.</param>
    /// <param name="right">Another fingerprint.</param>
    /// <returns>True when both denote the same bytes.</returns>
    public static bool AreEqual(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        return string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
    }

    private static string Normalize(string fingerprint)
    {
        var builder = new StringBuilder(fingerprint.Length);
        foreach (char character in fingerprint)
        {
            if (character is ':' or ' ' or '-')
            {
                continue;
            }

            builder.Append(char.ToUpperInvariant(character));
        }

        return builder.ToString();
    }
}
