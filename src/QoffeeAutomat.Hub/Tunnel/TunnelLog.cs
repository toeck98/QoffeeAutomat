using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Hub.Tunnel;

/// <summary>Source-generated log messages of the tunnel proxy (T912). They name the user, the agent, the method and the path; never the query, a body or a credential.</summary>
internal static partial class TunnelLog
{
    [LoggerMessage(EventId = 600, Level = LogLevel.Information, Message = "Tunnel: {User} ({Role}) {Method} {Path} on agent {AgentId} answered {Status}.")]
    public static partial void Request(ILogger logger, string user, string role, string agentId, string method, string path, int status);

    [LoggerMessage(EventId = 601, Level = LogLevel.Information, Message = "Tunnel: {User} ({Role}) opened an event stream {Path} on agent {AgentId}.")]
    public static partial void StreamOpened(ILogger logger, string user, string role, string agentId, string path);

    [LoggerMessage(EventId = 602, Level = LogLevel.Information, Message = "Tunnel: the event stream {Path} on agent {AgentId} of {User} ended ({Reason}).")]
    public static partial void StreamEnded(ILogger logger, string user, string agentId, string path, string reason);

    [LoggerMessage(EventId = 603, Level = LogLevel.Warning, Message = "Tunnel: {User} ({Role}) {Method} {Path} on agent {AgentId} was refused: {Reason}")]
    public static partial void Refused(ILogger logger, string user, string role, string agentId, string method, string path, string reason);

    [LoggerMessage(EventId = 604, Level = LogLevel.Warning, Message = "Tunnel: {Method} {Path} on agent {AgentId} failed with {ErrorCode}.")]
    public static partial void Failed(ILogger logger, string agentId, string method, string path, string errorCode);
}
