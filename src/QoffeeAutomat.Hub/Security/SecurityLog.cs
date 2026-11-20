using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Hub.Security;

/// <summary>Source-generated, structured log messages of the security code. No message contains a password, an API token or a session id.</summary>
internal static partial class SecurityLog
{
    // The one credential that is logged on purpose (PLAN.md S09 item 1, T903): in a container the log is the only place where the
    // operator can read the one-time setup token. It works only while the hub has no account, only once, and the file in the data
    // directory has the mode 0600. Nothing else that is secret is ever logged.
    [LoggerMessage(EventId = 100, Level = LogLevel.Warning, Message = "The hub has no administrator yet. Open the hub UI and enter this one-time setup token to create one: {SetupToken} (it is also in the file {SetupTokenPath}).")]
    public static partial void SetupTokenReady(ILogger logger, string setupToken, string setupTokenPath);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning, Message = "The setup token file {SetupTokenPath} could not be deleted. Setup is closed, so the file is harmless; delete it by hand.")]
    public static partial void SetupTokenNotDeleted(ILogger logger, string setupTokenPath);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information, Message = "The first administrator {Username} was created; the setup token is gone.")]
    public static partial void AdministratorCreated(ILogger logger, string username);

    [LoggerMessage(EventId = 103, Level = LogLevel.Error, Message = "The audit entry for {Action} could not be written.")]
    public static partial void AuditWriteFailed(ILogger logger, Exception exception, string action);

    [LoggerMessage(EventId = 104, Level = LogLevel.Warning, Message = "Sign-in attempts are locked out for {Seconds} seconds after repeated failures.")]
    public static partial void LockedOut(ILogger logger, int seconds);

    [LoggerMessage(EventId = 105, Level = LogLevel.Information, Message = "An old setup token file {SetupTokenPath} was removed because the hub already has an account.")]
    public static partial void StaleSetupTokenRemoved(ILogger logger, string setupTokenPath);
}
