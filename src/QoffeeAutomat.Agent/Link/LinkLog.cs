using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Agent.Link;

/// <summary>Source-generated log messages of the QoffeeLink server. No message contains a token or a variable value.</summary>
internal static partial class LinkLog
{
    [LoggerMessage(EventId = 200, Level = LogLevel.Information, Message = "Link client {ClientId} ({Kind}) connected.")]
    public static partial void Connected(ILogger logger, string clientId, string kind);

    [LoggerMessage(EventId = 201, Level = LogLevel.Information, Message = "Link client {ClientId} disconnected.")]
    public static partial void Disconnected(ILogger logger, string clientId);

    [LoggerMessage(EventId = 202, Level = LogLevel.Warning, Message = "A link connection was refused: {Reason}.")]
    public static partial void Refused(ILogger logger, string reason);

    [LoggerMessage(EventId = 203, Level = LogLevel.Warning, Message = "Link client {ClientId} does not keep up with the frames that are sent to it and is disconnected.")]
    public static partial void SlowClient(ILogger logger, string clientId);

    [LoggerMessage(EventId = 204, Level = LogLevel.Warning, Message = "Link client {ClientId} violated the protocol ({Reason}); the connection is closed.")]
    public static partial void ProtocolViolation(ILogger logger, string clientId, string reason);

    [LoggerMessage(EventId = 205, Level = LogLevel.Warning, Message = "A {Type} frame for link client {ClientId} is larger than the frame limit and was not sent.")]
    public static partial void FrameTooLarge(ILogger logger, string type, string clientId);

    [LoggerMessage(EventId = 206, Level = LogLevel.Warning, Message = "Link client {ClientId}: unexpected failure ({Reason}); the connection is closed.")]
    public static partial void Fault(ILogger logger, string clientId, string reason);

    [LoggerMessage(EventId = 207, Level = LogLevel.Debug, Message = "Link client {ClientId}: ignored a frame ({Reason}).")]
    public static partial void Ignored(ILogger logger, string clientId, string reason);

    [LoggerMessage(EventId = 208, Level = LogLevel.Information, Message = "Told {Count} link clients that the agent goes away ({Reason}).")]
    public static partial void ByeSent(ILogger logger, int count, string reason);
}
