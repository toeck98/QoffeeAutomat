using System.Reflection;
using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Agent.Hosting;

/// <summary>The process exit codes of the agent executable (sysexits.h where one fits).</summary>
public static class ExitCodes
{
    /// <summary>Normal exit.</summary>
    public const int Success = 0;

    /// <summary>The agent could not start or crashed.</summary>
    public const int Failure = 1;

    /// <summary>The command line is wrong.</summary>
    public const int Usage = 64;

    /// <summary>The requested function is not available (the <c>service</c> verb before T301, or no running agent for <c>emit</c>).</summary>
    public const int Unavailable = 69;

    /// <summary>The agent refused the caller: it has no access to the local channel (<c>emit</c>).</summary>
    public const int NoPermission = 77;
}

/// <summary>The version of the agent.</summary>
public static class AgentVersion
{
    /// <summary>The semantic version from the assembly, without build metadata; <c>0.0.0</c> when unknown.</summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        string? informational = typeof(AgentVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return "0.0.0";
        }

        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}

/// <summary>The agent cannot start for a reason that the user can fix; the message says what to do.</summary>
public sealed class AgentStartupException : Exception
{
    /// <summary>Creates an exception without a message.</summary>
    public AgentStartupException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What went wrong and what to do about it.</param>
    public AgentStartupException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    /// <param name="message">What went wrong and what to do about it.</param>
    /// <param name="innerException">The cause.</param>
    public AgentStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Source-generated log messages of the host.</summary>
internal static partial class AgentLog
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "QoffeeAutomat agent {Version} started on the {Platform} platform. Data directory: {DataDirectory}.")]
    public static partial void Started(ILogger logger, string version, string platform, string dataDirectory);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "Listening on {Addresses}.")]
    public static partial void Listening(ILogger logger, string addresses);

    [LoggerMessage(EventId = 102, Level = LogLevel.Warning, Message = "{Message}")]
    public static partial void PlatformFallback(ILogger logger, string message);

    [LoggerMessage(EventId = 103, Level = LogLevel.Information, Message = "The agent is stopping.")]
    public static partial void Stopping(ILogger logger);

    [LoggerMessage(EventId = 104, Level = LogLevel.Information, Message = "The agent has stopped.")]
    public static partial void Stopped(ILogger logger);

    [LoggerMessage(EventId = 110, Level = LogLevel.Information, Message = "Configuration loaded: {Loaded} items, {Skipped} files skipped.")]
    public static partial void ConfigLoaded(ILogger logger, int loaded, int skipped);

    [LoggerMessage(EventId = 111, Level = LogLevel.Warning, Message = "Skipped configuration file {Path} ({Code}): {Reason}")]
    public static partial void ConfigProblem(ILogger logger, string path, string code, string reason);

    [LoggerMessage(EventId = 120, Level = LogLevel.Warning, Message = "The local channel file could not be written ({Reason}); 'emit' works only for processes that the agent starts.")]
    public static partial void LocalChannelFileFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 121, Level = LogLevel.Warning, Message = "An event could not be stored in the recent-events table: {Reason}")]
    public static partial void EventStoreFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 122, Level = LogLevel.Warning, Message = "Run {RunId}: {Stage} failed: {Reason}")]
    public static partial void CommandEngineFault(ILogger logger, string runId, string stage, string reason);

    [LoggerMessage(EventId = 123, Level = LogLevel.Warning, Message = "Sampling {Count} metrics failed: {Reason}. The metrics are unavailable; the next attempt is in {Seconds:0} seconds.")]
    public static partial void MetricProviderFailed(ILogger logger, int count, string reason, double seconds);
}
