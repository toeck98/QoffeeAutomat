using System.Security.Cryptography;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Core.Secrets;
using QoffeeAutomat.Hub.Hosting;

namespace QoffeeAutomat.Hub.Security;

/// <summary>The names of the secrets of the hub in the <see cref="ISecretStore"/> (the TLS certificate uses <see cref="SecretNames.TlsCertificate"/>).</summary>
public static class HubSecretNames
{
    /// <summary>The ECDSA P-256 private key that signs the desired states (PKCS#8).</summary>
    public const string SigningKey = "hub.signing_key";
}

/// <summary>
/// The ECDSA P-256 key that signs every desired state (PLAN.md S20, D-027, T902). The first start creates the key and keeps its private
/// part in the secret store (encrypted at rest with the key file of the data directory, mode 0600); every later start loads the same key,
/// so the public key that agents pinned at enrollment stays valid. A key that cannot be read is never replaced: that would lock out every
/// enrolled agent, so the hub refuses to start and says what to do.
/// </summary>
public sealed class HubSigningKeyStore : IDisposable
{
    private readonly ECDsa _key;
    private readonly object _gate = new();

    /// <summary>Loads or creates the key.</summary>
    /// <param name="secrets">The secret store.</param>
    /// <exception cref="HubStartupException">The stored key is damaged or not an ECDSA P-256 key.</exception>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public HubSigningKeyStore(ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        // The secret store is asynchronous and a service factory cannot await; this runs once when the first service needs the key.
        (ECDsa key, bool created) = Task.Run(() => LoadOrCreateAsync(secrets, CancellationToken.None)).GetAwaiter().GetResult();
        _key = key;
        CreatedNewKey = created;
        PublicKey = HubSigningKey.ExportPublicKey(_key);
    }

    /// <summary>True when this process created the key rather than loaded it.</summary>
    public bool CreatedNewKey { get; }

    /// <summary>The public key as it goes into enrollment strings: base64 of a DER SubjectPublicKeyInfo.</summary>
    public string PublicKey { get; }

    /// <summary>Signs a desired state (PLAN.md S20, D-027).</summary>
    /// <param name="state">The desired state.</param>
    /// <returns>The signature as standard base64.</returns>
    public string Sign(DesiredState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (_gate)
        {
            return DesiredStateSigner.Sign(state, _key);
        }
    }

    /// <summary>Disposes the key.</summary>
    public void Dispose()
    {
        _key.Dispose();
    }

    private static async Task<(ECDsa Key, bool Created)> LoadOrCreateAsync(ISecretStore secrets, CancellationToken cancellationToken)
    {
        byte[]? stored = await secrets.GetBytesAsync(HubSecretNames.SigningKey, cancellationToken).ConfigureAwait(false);
        if (stored is not null)
        {
            try
            {
                return (Import(stored), false);
            }
            catch (CryptographicException exception)
            {
                throw new HubStartupException(
                    "The signing key in the secret store is damaged. The hub does not create a new one, because every enrolled agent trusts the old one. "
                    + "Restore the data directory from a backup.",
                    exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(stored);
            }
        }

        ECDsa key = HubSigningKey.Generate();
        byte[] pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            await secrets.SetBytesAsync(HubSecretNames.SigningKey, pkcs8, cancellationToken).ConfigureAwait(false);
            return (key, true);
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    private static ECDsa Import(byte[] pkcs8)
    {
        ECDsa key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);

            // Reuses the curve check of the contracts: only NIST P-256 is a valid signing key.
            if (!HubSigningKey.TryImportPublicKey(HubSigningKey.ExportPublicKey(key), out ECDsa? check))
            {
                throw new CryptographicException("The key is not on the curve NIST P-256.");
            }

            check.Dispose();
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}
