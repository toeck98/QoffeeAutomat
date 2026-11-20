using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Hub.Enrollment;

/// <summary>Source-generated, structured log messages of enrollment and the agent registry. No message contains a token or a credential.</summary>
internal static partial class EnrollmentLog
{
    [LoggerMessage(EventId = 200, Level = LogLevel.Information, Message = "Agent {AgentId} ({AgentName}) enrolled and received a credential.")]
    public static partial void AgentEnrolled(ILogger logger, string agentId, string agentName);

    [LoggerMessage(EventId = 201, Level = LogLevel.Information, Message = "Agent {AgentId} asked to be enrolled and waits for approval.")]
    public static partial void AgentPending(ILogger logger, string agentId);

    [LoggerMessage(EventId = 202, Level = LogLevel.Warning, Message = "An enroll request from {Caller} was refused: {Reason}.")]
    public static partial void EnrollRefused(ILogger logger, string caller, string reason);

    [LoggerMessage(EventId = 203, Level = LogLevel.Warning, Message = "Enroll requests from {Caller} are locked out for {Seconds} seconds after repeated failures.")]
    public static partial void EnrollLockedOut(ILogger logger, string caller, int seconds);

    [LoggerMessage(EventId = 204, Level = LogLevel.Error, Message = "The audit entry for {Action} could not be written.")]
    public static partial void AuditWriteFailed(ILogger logger, Exception exception, string action);
}
