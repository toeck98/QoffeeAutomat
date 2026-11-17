using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Link.Pairing;

namespace QoffeeAutomat.Agent.Security;

/// <summary>Source-generated log messages of the security code (pairing, TLS, authentication). No message contains a secret.</summary>
internal static partial class SecurityLog
{
    [LoggerMessage(EventId = 130, Level = LogLevel.Information, Message = "TLS certificate fingerprint (SHA-256): {Fingerprint}")]
    public static partial void TlsFingerprint(ILogger logger, string fingerprint);

    [LoggerMessage(EventId = 131, Level = LogLevel.Information, Message = "A client of kind {Kind} paired; its id is {ClientId}.")]
    public static partial void ClientPaired(ILogger logger, string kind, string clientId);

    [LoggerMessage(EventId = 132, Level = LogLevel.Warning, Message = "A pairing request was refused: {Attempt}.")]
    public static partial void PairingRefused(ILogger logger, PairingAttempt attempt);

    [LoggerMessage(EventId = 133, Level = LogLevel.Information, Message = "The link client {ClientId} was revoked.")]
    public static partial void ClientRevoked(ILogger logger, string clientId);

    [LoggerMessage(EventId = 134, Level = LogLevel.Warning, Message = "No password is set. Open the UI and enter the setup token from {Path} to set one.")]
    public static partial void SetupTokenReady(ILogger logger, string path);

    [LoggerMessage(EventId = 135, Level = LogLevel.Information, Message = "The management password was set.")]
    public static partial void PasswordSet(ILogger logger);

    [LoggerMessage(EventId = 136, Level = LogLevel.Warning, Message = "Sign-in attempts are locked out for {Seconds} seconds after repeated failures.")]
    public static partial void LoginLockedOut(ILogger logger, int seconds);

    [LoggerMessage(EventId = 137, Level = LogLevel.Information, Message = "A one-time login link from the tray icon started a session.")]
    public static partial void OneTimeLoginUsed(ILogger logger);

    [LoggerMessage(EventId = 138, Level = LogLevel.Warning, Message = "A one-time login link was refused: {Outcome}.")]
    public static partial void OneTimeLoginRefused(ILogger logger, Auth.OneTimeLoginOutcome outcome);
}
