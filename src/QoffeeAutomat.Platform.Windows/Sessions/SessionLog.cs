using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Platform.Windows.Sessions;

/// <summary>Source-generated log messages of the session manager and the session pipe (event ids 310 to 329).</summary>
internal static partial class SessionLog
{
    [LoggerMessage(EventId = 310, Level = LogLevel.Warning, Message = "Security: rejected a connection to the session pipe: {Reason}")]
    public static partial void PeerRejected(ILogger logger, string reason);

    [LoggerMessage(EventId = 311, Level = LogLevel.Warning, Message = "The handshake with a session helper failed: {Reason}")]
    public static partial void HandshakeFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 312, Level = LogLevel.Information, Message = "The session helper of session {SessionId} (user {UserName}, process {ProcessId}) is connected.")]
    public static partial void SessionHostConnected(ILogger logger, int sessionId, string? userName, int processId);

    [LoggerMessage(EventId = 313, Level = LogLevel.Information, Message = "The session helper of session {SessionId} disconnected.")]
    public static partial void SessionHostDisconnected(ILogger logger, int sessionId);

    [LoggerMessage(EventId = 314, Level = LogLevel.Warning, Message = "The connection to the session helper of session {SessionId} failed: {Reason}")]
    public static partial void ConnectionFault(ILogger logger, int sessionId, string reason);

    [LoggerMessage(EventId = 315, Level = LogLevel.Error, Message = "The session pipe could not be created: {Reason} Trying again in {Seconds} s.")]
    public static partial void PipeUnavailable(ILogger logger, string reason, int seconds);

    [LoggerMessage(EventId = 316, Level = LogLevel.Warning, Message = "The session helper {Path} does not exist, so commands that run as the user are unavailable.")]
    public static partial void SessionHostMissing(ILogger logger, string path);

    [LoggerMessage(EventId = 317, Level = LogLevel.Information, Message = "Started the session helper (process {ProcessId}) in session {SessionId}.")]
    public static partial void SessionHostStarted(ILogger logger, int processId, int sessionId);

    [LoggerMessage(EventId = 318, Level = LogLevel.Warning, Message = "The session helper could not be started in session {SessionId}: {Reason} Trying again in {Seconds} s.")]
    public static partial void SessionHostStartFailed(ILogger logger, int sessionId, string reason, int seconds);

    [LoggerMessage(EventId = 319, Level = LogLevel.Warning, Message = "The session helper of session {SessionId} exited with code {ExitCode} after {Seconds} s; starting it again in {Delay} s.")]
    public static partial void SessionHostExited(ILogger logger, int sessionId, int exitCode, int seconds, int delay);

    [LoggerMessage(EventId = 320, Level = LogLevel.Warning, Message = "The sessions of this computer could not be listed: {Reason}")]
    public static partial void SessionEnumerationFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 321, Level = LogLevel.Information, Message = "A session helper already runs in session {SessionId}; waiting for it to connect.")]
    public static partial void SessionHostAlreadyRunning(ILogger logger, int sessionId);

    [LoggerMessage(EventId = 322, Level = LogLevel.Debug, Message = "Ignored an unexpected message of the session helper of session {SessionId}: {MessageType}.")]
    public static partial void UnexpectedMessage(ILogger logger, int sessionId, string messageType);

    [LoggerMessage(EventId = 323, Level = LogLevel.Information, Message = "The commands were {State} from the tray icon of session {SessionId} (user {UserName}).")]
    public static partial void PauseChangedFromTray(ILogger logger, string state, int sessionId, string? userName);

    [LoggerMessage(EventId = 324, Level = LogLevel.Warning, Message = "Security: ignored the request of the session helper of session {SessionId} (user {UserName}) to pause or resume the commands, because it is not the session of the console.")]
    public static partial void PauseRefused(ILogger logger, int sessionId, string? userName);

    [LoggerMessage(EventId = 325, Level = LogLevel.Warning, Message = "The state of the tray icon could not be determined: {Reason}")]
    public static partial void TrayStateUnavailable(ILogger logger, string reason);

    [LoggerMessage(EventId = 326, Level = LogLevel.Information, Message = "Issued a one-time login token to the administrator {UserName} of session {SessionId}.")]
    public static partial void LoginTokenIssued(ILogger logger, int sessionId, string? userName);

    [LoggerMessage(EventId = 327, Level = LogLevel.Warning, Message = "Security: refused a one-time login token to session {SessionId} (user {UserName}): {Reason}.")]
    public static partial void LoginTokenDenied(ILogger logger, int sessionId, string? userName, string reason);

    [LoggerMessage(EventId = 328, Level = LogLevel.Warning, Message = "The hotkey '{Name}' ({Keys}) is not active: {State}. {Detail}")]
    public static partial void HotkeyNotActive(ILogger logger, string name, string keys, string state, string detail);

    [LoggerMessage(EventId = 329, Level = LogLevel.Warning, Message = "The hotkeys could not be sent to the session helper of session {SessionId}: {Reason}")]
    public static partial void HotkeysNotSent(ILogger logger, int sessionId, string reason);
}
