using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Core.Secrets;

namespace QoffeeAutomat.Agent.Security.Tls;

/// <summary>
/// The TLS certificate of the link listener (PLAN.md S09 item 3). The first call creates a self-signed
/// certificate and stores it, with its private key, in the <see cref="ISecretStore"/> under
/// <see cref="SecretNames.TlsCertificate"/>; every later start loads the same certificate, so the
/// fingerprint that clients pinned stays valid. A certificate that has expired or cannot be read is
/// replaced; the paired clients then have to pair again. A store that cannot be used is an error and
/// never leads to a silent replacement.
/// </summary>
public sealed class TlsCertificateProvider : ITlsCertificateSource, IDisposable
{
    private readonly ISecretStore _secrets;
    private readonly TimeProvider _time;
    private readonly Func<string> _hostName;
    private readonly object _gate = new();
    private X509Certificate2? _certificate;
    private string? _fingerprint;

    /// <summary>Creates the provider; nothing is read before the first use.</summary>
    /// <param name="secrets">Where the certificate is kept.</param>
    /// <param name="time">The clock for the validity checks.</param>
    /// <param name="hostName">Returns the host name for a new certificate; null uses the machine name.</param>
    public TlsCertificateProvider(ISecretStore secrets, TimeProvider time, Func<string>? hostName = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(time);

        _secrets = secrets;
        _time = time;
        _hostName = hostName ?? (() => Environment.MachineName);
    }

    /// <summary>True when the certificate was created (or replaced) by this process rather than loaded.</summary>
    public bool CreatedNewCertificate { get; private set; }

    /// <summary>The SHA-256 fingerprint of the certificate; loads or creates the certificate when needed.</summary>
    public string? Fingerprint
    {
        get
        {
            GetOrCreate();
            return _fingerprint;
        }
    }

    /// <inheritdoc />
    public X509Certificate2? TryGetCertificate() => GetOrCreate();

    /// <summary>Loads the stored certificate or creates and stores a new one.</summary>
    /// <returns>The certificate with its private key. The provider owns it; do not dispose it.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public X509Certificate2 GetOrCreate()
    {
        lock (_gate)
        {
            if (_certificate is not null)
            {
                return _certificate;
            }

            // The secret store is asynchronous and this runs once while Kestrel is configured.
            X509Certificate2 certificate = Task.Run(() => LoadOrCreateAsync(CancellationToken.None)).GetAwaiter().GetResult();
            _certificate = certificate;
            _fingerprint = CertificateFingerprint.Compute(certificate);
            return certificate;
        }
    }

    /// <summary>Disposes the certificate.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _certificate?.Dispose();
            _certificate = null;
        }
    }

    private async Task<X509Certificate2> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();

        byte[]? stored = await _secrets.GetBytesAsync(SecretNames.TlsCertificate, cancellationToken).ConfigureAwait(false);
        if (stored is not null)
        {
            try
            {
                X509Certificate2 loaded = SelfSignedCertificateFactory.Load(stored);
                if (loaded.HasPrivateKey && loaded.NotAfter.ToUniversalTime() > now.UtcDateTime)
                {
                    return loaded;
                }

                loaded.Dispose();
            }
            catch (CryptographicException)
            {
                // Unreadable: fall through and replace it.
            }
            finally
            {
                CryptographicOperations.ZeroMemory(stored);
            }
        }

        byte[] pfx = SelfSignedCertificateFactory.CreatePfx(_hostName(), now, SelfSignedCertificateFactory.DefaultValidity);
        try
        {
            await _secrets.SetBytesAsync(SecretNames.TlsCertificate, pfx, cancellationToken).ConfigureAwait(false);
            CreatedNewCertificate = true;
            return SelfSignedCertificateFactory.Load(pfx);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }
}
