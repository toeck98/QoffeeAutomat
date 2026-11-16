using System.Security.Cryptography;
using QoffeeAutomat.Core.Secrets;

namespace QoffeeAutomat.Platform.Windows.Security;

/// <summary>
/// An <see cref="ISecretProtector"/> that encrypts with DPAPI in machine scope (PLAN.md S09 item 6): any process
/// on this computer can decrypt the blob in principle, so the protection rests on the permissions of the secrets
/// directory (SYSTEM and Administrators only) and on the key never leaving the machine. The context of a secret is
/// passed as DPAPI's optional entropy, so a blob cannot be moved to another secret name.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    /// <summary>The value of <see cref="Scheme"/>.</summary>
    public const string SchemeName = "dpapi-machine";

    /// <inheritdoc />
    public string Scheme => SchemeName;

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> context)
    {
        byte[] data = plaintext.ToArray();
        try
        {
            return ProtectedData.Protect(data, context.ToArray(), DataProtectionScope.LocalMachine);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> protectedData, ReadOnlySpan<byte> context)
    {
        // ProtectedData throws CryptographicException for a damaged blob, another machine or another entropy.
        return ProtectedData.Unprotect(protectedData.ToArray(), context.ToArray(), DataProtectionScope.LocalMachine);
    }
}
