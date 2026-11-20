using System.Globalization;
using System.Security.Cryptography;

namespace QoffeeAutomat.Core.Security;

/// <summary>
/// Salted PBKDF2-SHA256 password hashes (PLAN.md S09 item 1: at least 600 000 iterations). A stored
/// hash looks like <c>pbkdf2-sha256$600000$&lt;salt base64&gt;$&lt;hash base64&gt;</c>. The iteration count is
/// part of the hash, so it can be raised later; hashes with fewer iterations than the minimum are
/// never accepted.
/// </summary>
public static class PasswordHasher
{
    /// <summary>The iteration count of new hashes and the minimum accepted when checking.</summary>
    public const int Iterations = 600_000;

    /// <summary>The largest iteration count accepted when checking; it bounds the work for a damaged or forged hash.</summary>
    public const int MaxIterations = 10_000_000;

    private const string Scheme = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int PartCount = 4;

    /// <summary>Hashes a password with a new random salt.</summary>
    /// <param name="password">The password.</param>
    /// <returns>The stored form.</returns>
    public static string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        try
        {
            return string.Join('$', Scheme, Iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    /// <summary>Checks a password against a stored hash in constant time.</summary>
    /// <param name="password">The password to check.</param>
    /// <param name="stored">The stored form made by <see cref="Hash"/>.</param>
    /// <returns>True when the password matches; false for a wrong password and for a malformed hash.</returns>
    public static bool Verify(string password, string stored)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(stored);

        string[] parts = stored.Split('$');
        if (parts.Length != PartCount
            || !string.Equals(parts[0], Scheme, StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int iterations)
            || iterations is < Iterations or > MaxIterations)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length != HashBytes)
        {
            return false;
        }

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        try
        {
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
        }
    }
}
