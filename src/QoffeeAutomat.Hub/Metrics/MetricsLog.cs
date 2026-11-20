using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Hub.Metrics;

/// <summary>Source-generated, structured log messages of the metric collection. No message contains a metric value.</summary>
internal static partial class MetricsLog
{
    [LoggerMessage(EventId = 500, Level = LogLevel.Information, Message = "Collecting {Count} metric(s) of agent {AgentId} every {Interval} second(s).")]
    public static partial void Collecting(ILogger logger, string agentId, int count, double interval);

    [LoggerMessage(EventId = 501, Level = LogLevel.Warning, Message = "The metrics of agent {AgentId} cannot be collected: {Reason}.")]
    public static partial void CollectFailed(ILogger logger, string agentId, string reason);

    [LoggerMessage(EventId = 502, Level = LogLevel.Warning, Message = "{Count} metric sample(s) were dropped because the database could not keep up.")]
    public static partial void Dropped(ILogger logger, long count);

    [LoggerMessage(EventId = 503, Level = LogLevel.Error, Message = "{Count} metric sample(s) could not be written: {Failure}.")]
    public static partial void WriteFailed(ILogger logger, int count, string failure);

    [LoggerMessage(EventId = 504, Level = LogLevel.Debug, Message = "Rolled up {Buckets5m} five minute and {Buckets1h} one hour bucket(s).")]
    public static partial void RolledUp(ILogger logger, int buckets5m, int buckets1h);

    [LoggerMessage(EventId = 505, Level = LogLevel.Information, Message = "Metric retention removed {Raw} raw sample(s), {States} state change(s), {Rollups5m} five minute and {Rollups1h} one hour bucket(s).")]
    public static partial void RetentionApplied(ILogger logger, long raw, long states, long rollups5m, long rollups1h);

    [LoggerMessage(EventId = 506, Level = LogLevel.Error, Message = "The metric maintenance failed with {Failure}.")]
    public static partial void MaintenanceFailed(ILogger logger, string failure);

    [LoggerMessage(EventId = 507, Level = LogLevel.Information, Message = "The metrics of agent {AgentId} were removed with the agent ({Series} series).")]
    public static partial void AgentRemoved(ILogger logger, string agentId, int series);
}
