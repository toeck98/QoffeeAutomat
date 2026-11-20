using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Hub.Hosting;

/// <summary>
/// Source-generated, structured log messages of the hub host (every value is a named property, so the JSON console format
/// carries them as fields). No message contains a credential, with one documented exception: the one-time setup token, which
/// the operator needs from the container log (see <c>SecurityLog.SetupTokenReady</c>).
/// </summary>
internal static partial class HubLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Hub {HubId} ({HubName}) version {Version} started; data directory {DataDirectory}.")]
    public static partial void HubReady(ILogger logger, string hubId, string hubName, string version, string dataDirectory);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "The database {DatabasePath} is at schema version {SchemaVersion} (it was at {InitialSchemaVersion}).")]
    public static partial void DatabaseReady(ILogger logger, string databasePath, int schemaVersion, int initialSchemaVersion);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "The directory {Path} could be accessible by other users and could not be restricted to its owner (mode 0700). Fix the permissions of the volume.")]
    public static partial void DirectoryNotPrivate(ILogger logger, string path);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "TLS certificate fingerprint (SHA-256): {Fingerprint}")]
    public static partial void TlsFingerprint(ILogger logger, string fingerprint);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "A new self-signed TLS certificate was created and stored in the secret store.")]
    public static partial void TlsCertificateCreated(ILogger logger);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "The TLS certificate expired on {NotAfter}. Replace it before agents or browsers refuse the connection.")]
    public static partial void TlsCertificateExpired(ILogger logger, DateTimeOffset notAfter);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "A new signing key was created. Public key: {PublicKey}")]
    public static partial void SigningKeyCreated(ILogger logger, string publicKey);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Signing public key: {PublicKey}")]
    public static partial void SigningKeyLoaded(ILogger logger, string publicKey);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information, Message = "Listening on {Scheme}://{Address}:{Port} ({TlsMode}).")]
    public static partial void Listening(ILogger logger, string scheme, string address, int port, HubTlsMode tlsMode);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "Forwarded headers are honoured for {ProxyCount} trusted proxy network(s) only.")]
    public static partial void TrustedProxies(ILogger logger, int proxyCount);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "The hub is stopping.")]
    public static partial void Stopping(ILogger logger);
}
