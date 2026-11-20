using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using QoffeeAutomat.Core.Secrets;
using QoffeeAutomat.Core.Security;
using QoffeeAutomat.Hub.Hosting;

namespace QoffeeAutomat.Hub.Security;

/// <summary>
/// The TLS certificate of the listener (PLAN.md S20, T902). In <see cref="HubTlsMode.SelfSigned"/> the first start creates a
/// self-signed ECDSA P-256 certificate and keeps it, with its private key, in the secret store; every later start loads the same one, so the
/// fingerprint in the enrollment strings stays valid. A stored certificate that cannot be read is never replaced silently, because agents
/// pin its fingerprint. In <see cref="HubTlsMode.Certificate"/> the operator's PKCS#12 or PEM file is loaded; in <see cref="HubTlsMode.None"/>
/// there is no certificate (plain HTTP behind a trusted proxy).
/// </summary>
public sealed class HubCertificateProvider : IDisposable
{
    private readonly HubSettings _settings;
    private readonly ISecretStore _secrets;
    private readonly TimeProvider _time;
    private readonly Func<string> _hostName;
    private readonly object _gate = new();
    private bool _loaded;
    private X509Certificate2? _certificate;
    private string? _fingerprint;

    /// <summary>Creates the provider; nothing is read before the first use.</summary>
    /// <param name="settings">The TLS settings.</param>
    /// <param name="secrets">Where a self-signed certificate is kept.</param>
    /// <param name="time">The clock.</param>
    /// <param name="hostName">Returns the host name for a new self-signed certificate; null uses the machine name.</param>
    public HubCertificateProvider(HubSettings settings, ISecretStore secrets, TimeProvider time, Func<string>? hostName = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(time);

        _settings = settings;
        _secrets = secrets;
        _time = time;
        _hostName = hostName ?? (() => Environment.MachineName);
    }

    /// <summary>How TLS is terminated.</summary>
    public HubTlsMode Mode => _settings.Tls;

    /// <summary>True when this process created the self-signed certificate rather than loaded it.</summary>
    public bool CreatedNewCertificate { get; private set; }

    /// <summary>The SHA-256 fingerprint of the certificate (hex pairs separated by colons), or null when there is none.</summary>
    public string? Fingerprint
    {
        get
        {
            TryGetCertificate();
            return _fingerprint;
        }
    }

    /// <summary>The end of the validity of the certificate, or null when there is none.</summary>
    public DateTimeOffset? NotAfter
    {
        get
        {
            X509Certificate2? certificate = TryGetCertificate();
            return certificate is null ? null : new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
        }
    }

    /// <summary>Loads or creates the certificate.</summary>
    /// <returns>The certificate with its private key, owned by the provider (do not dispose it); null for plain HTTP.</returns>
    /// <exception cref="HubStartupException">The certificate cannot be used.</exception>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public X509Certificate2? TryGetCertificate()
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return _certificate;
            }

            _certificate = _settings.Tls switch
            {
                HubTlsMode.None => null,
                HubTlsMode.Certificate => LoadUserCertificate(_settings),
                _ => Task.Run(() => LoadOrCreateSelfSignedAsync(CancellationToken.None)).GetAwaiter().GetResult(),
            };
            _fingerprint = _certificate is null ? null : CertificateFingerprint.Compute(_certificate);
            _loaded = true;
            return _certificate;
        }
    }

    /// <summary>Disposes the certificate.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _certificate?.Dispose();
            _certificate = null;
            _loaded = false;
        }
    }

    private static X509Certificate2 LoadUserCertificate(HubSettings settings)
    {
        string path = settings.TlsCertificatePath
            ?? throw new HubStartupException("--tls certificate needs --tls-cert with the path of the certificate file.");

        try
        {
            X509Certificate2 certificate;
            if (path.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".p12", StringComparison.OrdinalIgnoreCase))
            {
                string? password = settings.TlsPasswordFilePath is null
                    ? null
                    : File.ReadAllText(settings.TlsPasswordFilePath).TrimEnd('\r', '\n');
                certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.Exportable);
            }
            else
            {
                certificate = X509Certificate2.CreateFromPemFile(path, settings.TlsKeyPath);
            }

            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new HubStartupException(
                    "The certificate '" + path + "' has no private key. Use a PKCS#12 file, or a PEM certificate together with --tls-key.");
            }

            return certificate;
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new HubStartupException(
                "The certificate '" + path + "' cannot be read: " + exception.Message,
                exception);
        }
    }

    private async Task<X509Certificate2> LoadOrCreateSelfSignedAsync(CancellationToken cancellationToken)
    {
        byte[]? stored = await _secrets.GetBytesAsync(SecretNames.TlsCertificate, cancellationToken).ConfigureAwait(false);
        if (stored is not null)
        {
            try
            {
                X509Certificate2 loaded = SelfSignedCertificateFactory.Load(stored);
                if (!loaded.HasPrivateKey)
                {
                    loaded.Dispose();
                    throw new CryptographicException("The stored certificate has no private key.");
                }

                return loaded;
            }
            catch (CryptographicException exception)
            {
                throw new HubStartupException(
                    "The TLS certificate in the secret store cannot be read. The hub does not replace it, because enrolled agents pin its fingerprint. "
                    + "Restore the data directory from a backup, or remove the secret 'tls.certificate' and enroll the agents again.",
                    exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(stored);
            }
        }

        byte[] pfx = SelfSignedCertificateFactory.CreatePfx(_hostName(), _time.GetUtcNow(), SelfSignedCertificateFactory.DefaultValidity);
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
