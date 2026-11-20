using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Hub.Security;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Hosting;

/// <summary>
/// At start-up: makes the hub create or load everything it needs once (the data directory, the database with its migrations, the identity,
/// the TLS certificate and the signing key), so that a problem stops the start with a clear message instead of failing on the first request,
/// and logs who the hub is. At shutdown it logs that the hub stops; the host then closes the listener, finishes running requests (up to
/// <see cref="HubHost.ShutdownTimeout"/>) and disposes the services, which closes the database.
/// </summary>
public sealed class HubStartupService : IHostedService
{
    private readonly HubIdentity _identity;
    private readonly HubDatabase _database;
    private readonly HubDataDirectory _directory;
    private readonly HubSettings _settings;
    private readonly HubCertificateProvider _certificate;
    private readonly HubSigningKeyStore _signingKey;
    private readonly TimeProvider _time;
    private readonly ILogger<HubStartupService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="identity">The identity of the hub.</param>
    /// <param name="database">The migrated database.</param>
    /// <param name="directory">The data directory.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="certificate">The TLS certificate.</param>
    /// <param name="signingKey">The signing key.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public HubStartupService(
        HubIdentity identity,
        HubDatabase database,
        HubDataDirectory directory,
        HubSettings settings,
        HubCertificateProvider certificate,
        HubSigningKeyStore signingKey,
        TimeProvider time,
        ILogger<HubStartupService> logger)
    {
        _identity = identity;
        _database = database;
        _directory = directory;
        _settings = settings;
        _certificate = certificate;
        _signingKey = signingKey;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        HubLog.HubReady(_logger, _identity.Id, _identity.Name, HubVersion.Current, _directory.Paths.Root);
        HubLog.DatabaseReady(_logger, _database.Path, _database.SchemaVersion, _database.InitialSchemaVersion);

        if (_certificate.Fingerprint is { } fingerprint)
        {
            if (_certificate.CreatedNewCertificate)
            {
                HubLog.TlsCertificateCreated(_logger);
            }

            HubLog.TlsFingerprint(_logger, fingerprint);
            if (_certificate.NotAfter is { } notAfter && notAfter <= _time.GetUtcNow())
            {
                HubLog.TlsCertificateExpired(_logger, notAfter);
            }
        }

        if (_signingKey.CreatedNewKey)
        {
            HubLog.SigningKeyCreated(_logger, _signingKey.PublicKey);
        }
        else
        {
            HubLog.SigningKeyLoaded(_logger, _signingKey.PublicKey);
        }

        if (_settings.TrustedProxies.Count > 0)
        {
            HubLog.TrustedProxies(_logger, _settings.TrustedProxies.Count);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            string scheme = _settings.Tls == HubTlsMode.None ? "http" : "https";
            string bindAddress = _settings.Bind.ToString();
            HubLog.Listening(
                _logger,
                scheme,
                bindAddress,
                _settings.Port,
                _settings.Tls);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        HubLog.Stopping(_logger);
        return Task.CompletedTask;
    }
}
