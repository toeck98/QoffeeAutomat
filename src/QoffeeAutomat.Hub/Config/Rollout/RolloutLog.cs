using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Hub.Config.Rollout;

/// <summary>Source-generated, structured log messages of the configuration roll-out. No message contains the body of a document.</summary>
internal static partial class RolloutLog
{
    [LoggerMessage(EventId = 400, Level = LogLevel.Information, Message = "Pushed generation {Generation} to agent {AgentId}: {Documents} document(s) in {Frames} frame(s).")]
    public static partial void Pushed(ILogger logger, string agentId, long generation, int documents, int frames);

    [LoggerMessage(EventId = 401, Level = LogLevel.Warning, Message = "Generation {Generation} could not be pushed to agent {AgentId}: {Reason}")]
    public static partial void PushFailed(ILogger logger, string agentId, long generation, string reason);

    [LoggerMessage(EventId = 402, Level = LogLevel.Information, Message = "Agent {AgentId} reported generation {Generation} as {State}.")]
    public static partial void StatusReceived(ILogger logger, string agentId, long generation, string state);

    [LoggerMessage(EventId = 403, Level = LogLevel.Warning, Message = "A config_status of agent {AgentId} was ignored: {Reason}.")]
    public static partial void StatusIgnored(ILogger logger, string agentId, string reason);

    [LoggerMessage(EventId = 404, Level = LogLevel.Error, Message = "Rolling out the configuration to agent {AgentId} failed with {Failure}.")]
    public static partial void Failed(ILogger logger, string agentId, string failure);
}
