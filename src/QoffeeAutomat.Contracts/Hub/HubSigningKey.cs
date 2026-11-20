using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace QoffeeAutomat.Contracts.Hub;

/// <summary>
/// Helpers for the ECDSA P-256 key that signs desired states (PLAN.md S20, D-027). The public key travels in the
/// enrollment string as base64 of a DER SubjectPublicKeyInfo.
/// </summary>
public static class HubSigningKey
{
    private const string P256Oid = "1.2.840.10045.3.1.7";

    /// <summary>Creates a new signing key pair on the curve NIST P-256.</summary>
    /// <returns>The key; the caller disposes it.</returns>
    public static ECDsa Generate()
    {
        return ECDsa.Create(ECCurve.NamedCurves.nistP256);
    }

    /// <summary>Exports the public key as base64 of the DER SubjectPublicKeyInfo.</summary>
    /// <param name="key">A key pair or a public key.</param>
    /// <returns>The standard base64 text with padding.</returns>
    public static string ExportPublicKey(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Imports a public key from the text of the enrollment string.</summary>
    /// <param name="base64">Base64 of a DER SubjectPublicKeyInfo of an ECDSA P-256 key.</param>
    /// <returns>A key that can verify signatures; the caller disposes it.</returns>
    /// <exception cref="FormatException">The text is not base64.</exception>
    /// <exception cref="CryptographicException">The data is not a public key on the curve NIST P-256.</exception>
    public static ECDsa ImportPublicKey(string base64)
    {
        ArgumentNullException.ThrowIfNull(base64);

        byte[] der = Convert.FromBase64String(base64);
        ECDsa key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(der, out _);
            if (!IsP256(key.ExportParameters(includePrivateParameters: false).Curve))
            {
                throw new CryptographicException("The key is not on the curve NIST P-256.");
            }

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static bool IsP256(ECCurve curve)
    {
        if (!curve.IsNamed)
        {
            return false;
        }

        return string.Equals(curve.Oid.Value, P256Oid, StringComparison.Ordinal)
            || curve.Oid.FriendlyName is "nistP256" or "ECDSA_P256" or "secp256r1" or "prime256v1";
    }

    /// <summary>Imports a public key without throwing.</summary>
    /// <param name="base64">Base64 of a DER SubjectPublicKeyInfo of an ECDSA P-256 key.</param>
    /// <param name="key">The key when the import succeeds; the caller disposes it.</param>
    /// <returns>True when the text is a valid P-256 public key.</returns>
    public static bool TryImportPublicKey(string? base64, [NotNullWhen(true)] out ECDsa? key)
    {
        key = null;
        if (base64 is null)
        {
            return false;
        }

        try
        {
            key = ImportPublicKey(base64);
            return true;
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
