using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Hub.Fleet.Actions;

/// <summary>Source-generated, structured log messages of the fleet actions. No message contains variable values or the output of a command.</summary>
internal static partial class FleetActionLog
{
    [LoggerMessage(EventId = 720, Level = LogLevel.Information, Message = "Fleet action {ActionId} ({Kind}) started by {User} for {Agents} agents.")]
    public static partial void Started(ILogger logger, string actionId, string kind, string user, int agents);

    [LoggerMessage(EventId = 721, Level = LogLevel.Information, Message = "Fleet action {ActionId} ended as {State}.")]
    public static partial void Ended(ILogger logger, string actionId, string state);

    [LoggerMessage(EventId = 722, Level = LogLevel.Warning, Message = "The update {ActionId} stopped at agent {AgentId}: {Reason}.")]
    public static partial void UpdateStopped(ILogger logger, string actionId, string agentId, string reason);

    [LoggerMessage(EventId = 723, Level = LogLevel.Warning, Message = "The audit entry of fleet action {ActionId} could not be written: {Reason}.")]
    public static partial void AuditWriteFailed(ILogger logger, string actionId, string reason);

    [LoggerMessage(EventId = 724, Level = LogLevel.Error, Message = "Fleet action {ActionId} failed unexpectedly with {Failure}.")]
    public static partial void Fault(ILogger logger, string actionId, string failure);
}
