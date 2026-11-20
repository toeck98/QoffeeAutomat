using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace QoffeeAutomat.Hub.Agents;

/// <summary>
/// Helpers for the two secrets of enrollment: the enrollment token and the agent credential. Both are 32 random bytes as base64url without
/// padding (43 characters, the pattern of the schemas); the hub keeps only their SHA-256 hash (PLAN.md S20 "Hub security" items 3 and 5).
/// </summary>
public static class AgentSecrets
{
    private const int SecretBytes = 32;
    private const int SecretLength = 43;
    private const int MinTokenLength = 16;
    private const int MaxTokenLength = 128;

    /// <summary>Creates a new random secret.</summary>
    /// <returns>32 random bytes as base64url without padding.</returns>
    public static string Create() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>Hashes a secret for storage.</summary>
    /// <param name="secret">The secret.</param>
    /// <returns>The SHA-256 hash as lower-case hex.</returns>
    public static string Hash(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    /// <summary>Tells whether a text has the form of an enrollment token: 16 to 128 base64url characters.</summary>
    /// <param name="token">The text.</param>
    /// <returns>True when the form is right; the token may still be unknown.</returns>
    public static bool LooksLikeToken(string? token) => IsBase64Url(token, MinTokenLength, MaxTokenLength);

    /// <summary>Tells whether a text has the form of an agent credential: 43 base64url characters.</summary>
    /// <param name="credential">The text.</param>
    /// <returns>True when the form is right; the credential may still be unknown.</returns>
    public static bool LooksLikeCredential(string? credential) => IsBase64Url(credential, SecretLength, SecretLength);

    private static bool IsBase64Url(string? text, int minLength, int maxLength)
    {
        if (text is null || text.Length < minLength || text.Length > maxLength)
        {
            return false;
        }

        foreach (char character in text)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
