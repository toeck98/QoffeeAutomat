using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Agent.Link.Outbound;

/// <summary>Source-generated log messages of the outbound links. No message contains a credential; URLs are logged without the query.</summary>
internal static partial class OutboundLog
{
    [LoggerMessage(EventId = 210, Level = LogLevel.Information, Message = "Outbound link: connected to {Url}.")]
    public static partial void Connected(ILogger logger, string url);

    [LoggerMessage(EventId = 211, Level = LogLevel.Information, Message = "Outbound link: the connection to {Url} ended.")]
    public static partial void Disconnected(ILogger logger, string url);

    [LoggerMessage(EventId = 212, Level = LogLevel.Information, Message = "Outbound link: could not connect to {Url} ({Reason}); trying again in {DelaySeconds:0.#} s.")]
    public static partial void DialFailed(ILogger logger, string url, string reason, double delaySeconds);

    [LoggerMessage(EventId = 213, Level = LogLevel.Warning, Message = "Outbound link: {Url} rejected the credential ({Reason}); the agent stops dialling until it is enrolled again.")]
    public static partial void Rejected(ILogger logger, string url, string reason);

    [LoggerMessage(EventId = 214, Level = LogLevel.Warning, Message = "Outbound link: the endpoint cannot be used: {Reason}")]
    public static partial void InvalidEndpoint(ILogger logger, string reason);

    [LoggerMessage(EventId = 215, Level = LogLevel.Information, Message = "Reverse link: joined {Url}.")]
    public static partial void Joined(ILogger logger, string url);

    [LoggerMessage(EventId = 216, Level = LogLevel.Information, Message = "Reverse link: left; the token was deleted.")]
    public static partial void Left(ILogger logger);

    [LoggerMessage(EventId = 217, Level = LogLevel.Warning, Message = "Outbound link: unexpected failure ({Reason}); trying again in {DelaySeconds:0.#} s.")]
    public static partial void Fault(ILogger logger, string reason, double delaySeconds);
}
