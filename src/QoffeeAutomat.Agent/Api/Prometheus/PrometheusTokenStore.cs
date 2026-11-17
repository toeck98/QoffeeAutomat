using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using QoffeeAutomat.Core.Secrets;

namespace QoffeeAutomat.Agent.Api.Prometheus;

/// <summary>
/// The read-only token of the Prometheus endpoint (PLAN.md S08, S09, T709). It is separate from the password and from the link tokens:
/// it opens <c>GET /metrics</c> and nothing else. The token is shown once when it is created; only its SHA-256 hash is kept, in the secret
/// store, so neither the secrets file nor a backup contains something that can be presented. The token has 256 random bits, so a fast hash
/// is enough. Creating a token replaces the old one, which stops working at once.
/// </summary>
public sealed class PrometheusTokenStore : IDisposable
{
    /// <summary>The start of every token; it tells what the token is for (and lets a secret scanner find a leaked one).</summary>
    public const string TokenPrefix = "qapm_";

    private const int TokenBytes = 32;

    private readonly ISecretStore _secrets;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[]? _hash;
    private bool _loaded;

    /// <summary>Creates the store.</summary>
    /// <param name="secrets">Where the hash is kept.</param>
    public PrometheusTokenStore(ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        _secrets = secrets;
    }

    /// <summary>Whether a token exists.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when a token was created and not revoked.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task<bool> IsSetAsync(CancellationToken cancellationToken)
    {
        return await LoadAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>Creates a new token and forgets the old one.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token; it is not stored and cannot be read again.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task<string> CreateAsync(CancellationToken cancellationToken)
    {
        string token = TokenPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _secrets.SetStringAsync(SecretNames.PrometheusTokenHash, Convert.ToHexStringLower(hash), cancellationToken).ConfigureAwait(false);
            _hash = hash;
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }

        return token;
    }

    /// <summary>Removes the token; the endpoint then refuses every request.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when there was a token.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task<bool> RevokeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool existed = await _secrets.RemoveAsync(SecretNames.PrometheusTokenHash, cancellationToken).ConfigureAwait(false);
            _hash = null;
            _loaded = true;
            return existed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Checks a presented token in constant time.</summary>
    /// <param name="presented">The token of the request; null or empty never matches.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when a token exists and the presented one is it.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task<bool> VerifyAsync(string? presented, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        byte[]? stored = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), stored);
    }

    /// <summary>Releases the lock.</summary>
    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <summary>The hash of the token, read from the secret store once and kept.</summary>
    private async Task<byte[]?> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded)
            {
                string? stored = await _secrets.GetStringAsync(SecretNames.PrometheusTokenHash, cancellationToken).ConfigureAwait(false);
                _hash = ParseHash(stored);
                _loaded = true;
            }

            return _hash;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A stored value that is not a SHA-256 hash counts as no token, so that a damaged entry never lets a request in.</summary>
    private static byte[]? ParseHash(string? stored)
    {
        if (stored is null || stored.Length != SHA256.HashSizeInBytes * 2)
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(stored);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
