using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Agent.Hub;

/// <summary>Source-generated log messages of the hub link. No message contains the enrollment string, a token or a credential; addresses are logged without query.</summary>
internal static partial class HubLog
{
    [LoggerMessage(EventId = 250, Level = LogLevel.Information, Message = "Hub: joined {Url} (hub {HubId}).")]
    public static partial void Joined(ILogger logger, string url, string hubId);

    [LoggerMessage(EventId = 251, Level = LogLevel.Information, Message = "Hub: waiting for an administrator of {Url} to approve this agent.")]
    public static partial void Pending(ILogger logger, string url);

    [LoggerMessage(EventId = 252, Level = LogLevel.Information, Message = "Hub: approved by {Url}; the agent received its credential.")]
    public static partial void Approved(ILogger logger, string url);

    [LoggerMessage(EventId = 253, Level = LogLevel.Warning, Message = "Hub: the enrollment with {Url} was refused: {Reason}")]
    public static partial void EnrollmentRefused(ILogger logger, string url, string reason);

    [LoggerMessage(EventId = 254, Level = LogLevel.Information, Message = "Hub: left; the credential was deleted.")]
    public static partial void Left(ILogger logger);

    [LoggerMessage(EventId = 255, Level = LogLevel.Warning, Message = "Hub: the hub revoked the credential; it was deleted and the agent stops dialling until it joins again.")]
    public static partial void Revoked(ILogger logger);

    [LoggerMessage(EventId = 256, Level = LogLevel.Warning, Message = "Hub: the hub removed this agent ({Reason}); the credential was deleted.")]
    public static partial void Unenrolled(ILogger logger, string reason);

    [LoggerMessage(EventId = 257, Level = LogLevel.Information, Message = "Hub: the credential was replaced at the request of the hub.")]
    public static partial void CredentialRotated(ILogger logger);

    [LoggerMessage(EventId = 258, Level = LogLevel.Warning, Message = "Hub: the credential of the hub could not be stored ({Reason}); the old one stays in use.")]
    public static partial void RotationFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 259, Level = LogLevel.Information, Message = "Hub: could not complete the enrollment with {Url} ({Reason}); trying again in {DelaySeconds:0.#} s.")]
    public static partial void EnrollmentRetry(ILogger logger, string url, string reason, double delaySeconds);

    [LoggerMessage(EventId = 260, Level = LogLevel.Information, Message = "Hub: the management tunnel is now {State}.")]
    public static partial void TunnelSwitched(ILogger logger, string state);

    [LoggerMessage(EventId = 261, Level = LogLevel.Warning, Message = "Hub: the stored hub address cannot be used: {Reason}")]
    public static partial void InvalidStoredEndpoint(ILogger logger, string reason);

    [LoggerMessage(EventId = 262, Level = LogLevel.Warning, Message = "Hub: the secret store cannot be used ({Reason}); the hub link stays off.")]
    public static partial void StoreUnavailable(ILogger logger, string reason);

    [LoggerMessage(EventId = 263, Level = LogLevel.Warning, Message = "Hub: the enrollment loop failed unexpectedly ({Reason}).")]
    public static partial void EnrollmentLoopFault(ILogger logger, string reason);

    [LoggerMessage(EventId = 264, Level = LogLevel.Warning, Message = "Hub: the channel file of the command line could not be written: {Reason}")]
    public static partial void AdminChannelFileFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 265, Level = LogLevel.Information, Message = "Hub: configuration generation {Generation} applied ({Documents} documents, local_config {LocalConfig}, direct_link {DirectLink}).")]
    public static partial void ConfigApplied(ILogger logger, long generation, int documents, string localConfig, string directLink);

    [LoggerMessage(EventId = 266, Level = LogLevel.Warning, Message = "Hub: configuration generation {Generation} was rejected and the previous generation stays active: {Reason}")]
    public static partial void ConfigRejected(ILogger logger, long generation, string reason);

    [LoggerMessage(EventId = 267, Level = LogLevel.Information, Message = "Hub: configuration generation {Generation} is not newer than the applied generation {Applied}; ignored.")]
    public static partial void ConfigIgnored(ILogger logger, long generation, long applied);

    [LoggerMessage(EventId = 268, Level = LogLevel.Information, Message = "Hub: the hub layer was removed ({Reason}); the local configuration is active again. {Converted} documents became local documents.")]
    public static partial void ConfigLayerRemoved(ILogger logger, string reason, int converted);

    [LoggerMessage(EventId = 269, Level = LogLevel.Warning, Message = "Hub: the hub layer could not be removed ({Reason}).")]
    public static partial void ConfigLayerRemovalFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 270, Level = LogLevel.Information, Message = "Hub: direct links are now {State} by the hub policy.")]
    public static partial void DirectLinkPolicy(ILogger logger, string state);

    [LoggerMessage(EventId = 271, Level = LogLevel.Information, Message = "Hub: {Count} direct link clients were closed by the hub policy.")]
    public static partial void DirectClientsClosed(ILogger logger, int count);

    // The management tunnel (T912) leaves one line per request: who (hub user and role), what (method and path, never the query or the body) and the outcome.
    [LoggerMessage(EventId = 272, Level = LogLevel.Information, Message = "Hub tunnel: {User} ({Role}) {Method} {Path} answered {Status} in {ElapsedMs} ms.")]
    public static partial void TunnelRequest(ILogger logger, string user, string role, string method, string path, int status, long elapsedMs);

    [LoggerMessage(EventId = 273, Level = LogLevel.Warning, Message = "Hub tunnel: refused {Method} {Path} of {User} ({Role}): {Reason}")]
    public static partial void TunnelDenied(ILogger logger, string method, string path, string user, string role, string reason);

    [LoggerMessage(EventId = 274, Level = LogLevel.Information, Message = "Hub tunnel: {User} ({Role}) opened the stream {StreamId} on {Path}.")]
    public static partial void TunnelStreamOpened(ILogger logger, string user, string role, string streamId, string path);

    [LoggerMessage(EventId = 275, Level = LogLevel.Information, Message = "Hub tunnel: the stream {StreamId} on {Path} of {User} ended ({Reason}).")]
    public static partial void TunnelStreamClosed(ILogger logger, string streamId, string path, string user, string reason);

    [LoggerMessage(EventId = 276, Level = LogLevel.Warning, Message = "Hub tunnel: {Method} {Path} failed with {Failure}.")]
    public static partial void TunnelFault(ILogger logger, string method, string path, string failure);
}
