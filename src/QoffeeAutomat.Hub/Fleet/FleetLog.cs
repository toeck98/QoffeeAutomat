using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Hub.Fleet;

/// <summary>Source-generated, structured log messages of the fleet link and the pairing. No message contains a token, a pairing code or the data of a command.</summary>
internal static partial class FleetLog
{
    [LoggerMessage(EventId = 700, Level = LogLevel.Information, Message = "A fleet client connected with token {TokenId} (scope: {Scope}).")]
    public static partial void ClientConnected(ILogger logger, string tokenId, string scope);

    [LoggerMessage(EventId = 701, Level = LogLevel.Information, Message = "The fleet client of token {TokenId} disconnected.")]
    public static partial void ClientDisconnected(ILogger logger, string tokenId);

    [LoggerMessage(EventId = 702, Level = LogLevel.Warning, Message = "Closing the fleet connection of token {TokenId}: {Reason}.")]
    public static partial void Violation(ILogger logger, string tokenId, string reason);

    [LoggerMessage(EventId = 703, Level = LogLevel.Error, Message = "The fleet connection of token {TokenId} failed with {Failure}.")]
    public static partial void Fault(ILogger logger, string tokenId, string failure);

    [LoggerMessage(EventId = 704, Level = LogLevel.Information, Message = "The fleet token {TokenId} was revoked; its connections are closed.")]
    public static partial void TokenRevoked(ILogger logger, string tokenId);

    [LoggerMessage(EventId = 705, Level = LogLevel.Information, Message = "Fleet pairing: a client of kind {Kind} paired and received the token {TokenId} (scope: {Scope}).")]
    public static partial void ClientPaired(ILogger logger, string kind, string tokenId, string scope);

    [LoggerMessage(EventId = 706, Level = LogLevel.Warning, Message = "Fleet pairing from {Caller} was refused: {Reason}.")]
    public static partial void PairingRefused(ILogger logger, string caller, string reason);

    [LoggerMessage(EventId = 707, Level = LogLevel.Warning, Message = "Fleet pairing from {Caller} is locked out for {Seconds} s.")]
    public static partial void PairingLockedOut(ILogger logger, string caller, int seconds);

    [LoggerMessage(EventId = 708, Level = LogLevel.Warning, Message = "The directory of the fleet link could not be updated for agent {AgentId}: {Reason}.")]
    public static partial void DirectoryFailed(ILogger logger, string agentId, string reason);

    [LoggerMessage(EventId = 709, Level = LogLevel.Warning, Message = "An audit entry of the fleet link ({Action}) could not be written: {Reason}.")]
    public static partial void AuditWriteFailed(ILogger logger, string action, string reason);

    [LoggerMessage(EventId = 710, Level = LogLevel.Information, Message = "Fleet token {TokenId} runs command {Command} on agent {AgentId}.")]
    public static partial void RunRequested(ILogger logger, string tokenId, string command, string agentId);
}
