using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Hub.Gateway;

/// <summary>Source-generated, structured log messages of the agent gateway. No message contains a credential or the data of a command.</summary>
internal static partial class GatewayLog
{
    [LoggerMessage(EventId = 300, Level = LogLevel.Information, Message = "Agent {AgentId} connected (features: {Features}).")]
    public static partial void AgentConnected(ILogger logger, string agentId, string features);

    [LoggerMessage(EventId = 301, Level = LogLevel.Information, Message = "Agent {AgentId} disconnected (power state: {PowerState}).")]
    public static partial void AgentDisconnected(ILogger logger, string agentId, string powerState);

    [LoggerMessage(EventId = 302, Level = LogLevel.Information, Message = "Agent {AgentId} opened a new connection; the old one is closed.")]
    public static partial void AgentReplaced(ILogger logger, string agentId);

    [LoggerMessage(EventId = 303, Level = LogLevel.Warning, Message = "Closing the connection of agent {AgentId}: {Reason}.")]
    public static partial void Violation(ILogger logger, string agentId, string reason);

    [LoggerMessage(EventId = 304, Level = LogLevel.Error, Message = "The connection of agent {AgentId} failed with {Failure}.")]
    public static partial void Fault(ILogger logger, string agentId, string failure);

    [LoggerMessage(EventId = 305, Level = LogLevel.Information, Message = "The manifest of agent {AgentId} is at revision {Rev}.")]
    public static partial void ManifestCached(ILogger logger, string agentId, string rev);

    [LoggerMessage(EventId = 306, Level = LogLevel.Warning, Message = "The manifest of agent {AgentId} could not be fetched: {Reason}.")]
    public static partial void ManifestFailed(ILogger logger, string agentId, string reason);

    [LoggerMessage(EventId = 307, Level = LogLevel.Debug, Message = "Agent {AgentId} is subscribed to {Count} metric(s){Wildcard}.")]
    public static partial void Subscribed(ILogger logger, string agentId, int count, string wildcard);

    [LoggerMessage(EventId = 308, Level = LogLevel.Warning, Message = "The subscription of agent {AgentId} could not be updated: {Reason}.")]
    public static partial void SubscribeFailed(ILogger logger, string agentId, string reason);

    [LoggerMessage(EventId = 309, Level = LogLevel.Error, Message = "An observer of the agent gateway failed with {Failure}.")]
    public static partial void ObserverFailed(ILogger logger, string failure);

    [LoggerMessage(EventId = 310, Level = LogLevel.Warning, Message = "An event of agent {AgentId} could not be stored: {Reason}.")]
    public static partial void EventNotStored(ILogger logger, string agentId, string reason);

    [LoggerMessage(EventId = 311, Level = LogLevel.Warning, Message = "The last-seen time of agent {AgentId} could not be stored: {Reason}.")]
    public static partial void LastSeenNotStored(ILogger logger, string agentId, string reason);

    [LoggerMessage(EventId = 312, Level = LogLevel.Information, Message = "Agent {AgentId} was told to leave the hub ({Reason}).")]
    public static partial void AgentUnenrolled(ILogger logger, string agentId, string reason);

    [LoggerMessage(EventId = 313, Level = LogLevel.Information, Message = "The credential of agent {AgentId} was rotated over its connection.")]
    public static partial void CredentialRotated(ILogger logger, string agentId);

    [LoggerMessage(EventId = 315, Level = LogLevel.Warning, Message = "The tunnelled stream {StreamId} of agent {AgentId} was closed: its consumer does not read the events.")]
    public static partial void StreamOverflow(ILogger logger, string agentId, string streamId);

    [LoggerMessage(EventId = 314, Level = LogLevel.Warning, Message = "A config_status frame of agent {AgentId} is not valid and was ignored.")]
    public static partial void ConfigStatusIgnored(ILogger logger, string agentId);
}
