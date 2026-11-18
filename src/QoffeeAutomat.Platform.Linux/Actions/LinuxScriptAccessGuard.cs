using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Platform.Linux.Service;

namespace QoffeeAutomat.Platform.Linux.Actions;

/// <summary>
/// Decides whether a script file may run with the rights of the service (PLAN.md S09 item 5, T803): the counterpart of the ACL check of
/// Windows. A <c>runAs: system</c> script that another user can change would let that user run code as root, so a script file or its
/// directory that group or others can write is refused, and when the agent runs as root a file that belongs to somebody else is refused
/// as well (its owner could rewrite it). Scripts of <c>runAs: user</c> are not checked: they run with the rights of the user anyway.
/// </summary>
public sealed partial class LinuxScriptAccessGuard : IScriptAccessGuard
{
    private const UnixFileMode GroupAndOtherWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    private readonly IUnixFileSystem _files;
    private readonly bool _requireRootOwner;
    private readonly ILogger? _logger;

    /// <summary>Creates the guard.</summary>
    /// <param name="files">Reads modes and owners; null uses the real file system.</param>
    /// <param name="requireRootOwner">True when the agent runs as root: a file or directory of another owner is refused.</param>
    /// <param name="logger">Receives the security warning when a script is refused.</param>
    public LinuxScriptAccessGuard(IUnixFileSystem? files = null, bool? requireRootOwner = null, ILogger? logger = null)
    {
        _files = files ?? new UnixFileSystem();
        _requireRootOwner = requireRootOwner ?? Environment.IsPrivilegedProcess;
        _logger = logger;
    }

    /// <inheritdoc />
    public ScriptAccessDecision Check(string scriptPath, string runAs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runAs);

        if (runAs == RunAsValues.User || OperatingSystem.IsWindows())
        {
            return ScriptAccessDecision.Allowed;
        }

        string? directory = Path.GetDirectoryName(scriptPath);
        string? problem = Inspect(scriptPath, "The script file") ?? (directory is null ? null : Inspect(directory, "The scripts directory"));
        if (problem is null)
        {
            return ScriptAccessDecision.Allowed;
        }

        string reason = $"{problem} A script that runs as {runAs} must be changeable by root only. Run: chmod go-w <path> and chown root:root <path>.";
        if (_logger is not null)
        {
            ScriptRefused(_logger, scriptPath, reason);
        }

        return ScriptAccessDecision.Refused(reason);
    }

    [LoggerMessage(EventId = 3350, Level = LogLevel.Warning, Message = "The script {Path} is not run with the rights of the service: {Reason}")]
    private static partial void ScriptRefused(ILogger logger, string path, string reason);

    private string? Inspect(string path, string what)
    {
        UnixFileMode mode = _files.GetMode(path);
        if ((mode & GroupAndOtherWrite) != 0)
        {
            return $"{what} can be changed by users other than the owner.";
        }

        if (_requireRootOwner && _files.GetOwner(path) is { } owner && owner != 0)
        {
            return $"{what} belongs to user {owner}, not to root.";
        }

        return null;
    }
}
