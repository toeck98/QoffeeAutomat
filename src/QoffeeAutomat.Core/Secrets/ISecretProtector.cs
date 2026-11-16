namespace QoffeeAutomat.Core.Secrets;

/// <summary>
/// Encrypts small secrets at rest (PLAN.md S09 item 6). The file-key implementation in
/// <see cref="FileKeyProtector"/> serves non-Windows systems and the simulated platform; DPAPI with
/// machine scope follows on Windows (T301). Implementations are thread safe.
/// </summary>
public interface ISecretProtector
{
    /// <summary>
    /// A stable identifier of the scheme, stored in the secrets file so that a file written by one
    /// scheme is never read with another.
    /// </summary>
    string Scheme { get; }

    /// <summary>Encrypts a secret.</summary>
    /// <param name="plaintext">The secret.</param>
    /// <param name="context">
    /// Authenticated but not encrypted data that binds the result to its purpose (the store passes
    /// the secret's name, so a blob cannot be moved to another name).
    /// </param>
    /// <returns>The protected blob.</returns>
    byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> context);

    /// <summary>Decrypts a blob made by <see cref="Protect"/>.</summary>
    /// <param name="protectedData">The blob.</param>
    /// <param name="context">The same context that was used to protect it.</param>
    /// <returns>The secret.</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The blob is damaged, was made with another key or another context.</exception>
    byte[] Unprotect(ReadOnlySpan<byte> protectedData, ReadOnlySpan<byte> context);
}
