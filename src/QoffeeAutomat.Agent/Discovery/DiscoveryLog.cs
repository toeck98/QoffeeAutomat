using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Agent.Discovery;

/// <summary>Source-generated log messages of the zeroconf advertisement.</summary>
internal static partial class DiscoveryLog
{
    [LoggerMessage(EventId = 300, Level = LogLevel.Information, Message = "Announcing {Instance} on port {Port} on {Interfaces} network interfaces.")]
    public static partial void Announced(ILogger logger, string instance, int port, int interfaces);

    [LoggerMessage(EventId = 301, Level = LogLevel.Information, Message = "The network advertisement of {Instance} was withdrawn.")]
    public static partial void Withdrawn(ILogger logger, string instance);

    [LoggerMessage(EventId = 302, Level = LogLevel.Warning, Message = "The agent could not be announced on the network ({Reason}); it is tried again when the network changes.")]
    public static partial void AnnounceFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 303, Level = LogLevel.Information, Message = "Zeroconf is switched off in the settings; the agent is not announced on the network.")]
    public static partial void Disabled(ILogger logger);

    [LoggerMessage(EventId = 304, Level = LogLevel.Debug, Message = "A multicast DNS datagram could not be sent: {Reason}")]
    public static partial void SendFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 305, Level = LogLevel.Debug, Message = "The multicast DNS receive loop ended: {Reason}")]
    public static partial void ReceiveEnded(ILogger logger, string reason);
}
