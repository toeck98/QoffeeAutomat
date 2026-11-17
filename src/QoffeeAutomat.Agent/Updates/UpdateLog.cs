using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Agent.Updates;

/// <summary>Source-generated log messages of the update check and the self-update (T704). Event ids 500 to 519.</summary>
internal static partial class UpdateLog
{
    [LoggerMessage(EventId = 500, Level = LogLevel.Information, Message = "Update check: the newest release is {Latest}, this agent is {Installed}.")]
    public static partial void CheckSucceeded(ILogger logger, string latest, string installed);

    [LoggerMessage(EventId = 501, Level = LogLevel.Information, Message = "Update check: the project has no release that can be offered.")]
    public static partial void NoRelease(ILogger logger);

    [LoggerMessage(EventId = 502, Level = LogLevel.Warning, Message = "Update check failed: {Reason}. It is tried again at the next interval.")]
    public static partial void CheckFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 503, Level = LogLevel.Information, Message = "Update check is switched off in the settings; the agent makes no request to GitHub.")]
    public static partial void CheckDisabled(ILogger logger);

    [LoggerMessage(EventId = 510, Level = LogLevel.Information, Message = "Update to {Version} started: downloading {Installer}.")]
    public static partial void UpdateStarted(ILogger logger, string version, string installer);

    [LoggerMessage(EventId = 511, Level = LogLevel.Information, Message = "Update to {Version}: checksum {Checksum} and signature check passed; telling the clients bye and starting the installer.")]
    public static partial void UpdateVerified(ILogger logger, string version, string checksum);

    [LoggerMessage(EventId = 512, Level = LogLevel.Information, Message = "Update to {Version}: the installer was started; the agent restarts when it replaces the files.")]
    public static partial void UpdateInstallerStarted(ILogger logger, string version);

    [LoggerMessage(EventId = 513, Level = LogLevel.Warning, Message = "Update refused: {Reason}")]
    public static partial void UpdateRefused(ILogger logger, string reason);

    [LoggerMessage(EventId = 514, Level = LogLevel.Information, Message = "Update: this agent ({Installed}) is already at the newest release ({Latest}).")]
    public static partial void AlreadyCurrent(ILogger logger, string installed, string latest);
}
