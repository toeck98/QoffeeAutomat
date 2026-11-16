using System.Globalization;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Execution.PowerShell;

namespace QoffeeAutomat.Platform.Windows.Security;

/// <summary>
/// Refuses to start a script with the rights of LocalSystem or of an administrator when a standard user could
/// have changed it (PLAN.md S09 item 5): the file, the directory that holds it and, for every ancestor, the right
/// to rename or delete the directory are checked. The check fails closed: a file whose permissions cannot be read
/// is refused. A refusal is logged as a security warning.
/// </summary>
public sealed class SystemScriptAclGuard : IScriptAccessGuard
{
    private readonly IFileSystemAcl _acl;
    private readonly ILogger _logger;

    /// <summary>Creates the guard.</summary>
    /// <param name="acl">Reads permissions.</param>
    /// <param name="logger">Receives the security warnings.</param>
    public SystemScriptAclGuard(IFileSystemAcl acl, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(acl);
        ArgumentNullException.ThrowIfNull(logger);

        _acl = acl;
        _logger = logger;
    }

    /// <inheritdoc />
    public ScriptAccessDecision Check(string scriptPath, string runAs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        ArgumentNullException.ThrowIfNull(runAs);

        if (runAs == RunAsValues.User)
        {
            return ScriptAccessDecision.Allowed;
        }

        IReadOnlyList<string> problems = Inspect(scriptPath);
        if (problems.Count == 0)
        {
            return ScriptAccessDecision.Allowed;
        }

        string summary = string.Join(" ", problems);
        SecurityWarnings.ScriptRefused(_logger, scriptPath, runAs, summary);
        return ScriptAccessDecision.Refused(
            "The script was not started because standard users could change it (see the security warning in the log): " + summary);
    }

    /// <summary>Lists why a script file is not safe to run with elevated rights.</summary>
    /// <param name="scriptPath">The full path of the script file.</param>
    /// <returns>The reasons; empty when the file is safe.</returns>
    public IReadOnlyList<string> Inspect(string scriptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);

        var problems = new List<string>();
        string full = Path.GetFullPath(scriptPath);

        AclSnapshot? file = _acl.Read(full);
        if (file is null)
        {
            problems.Add("The permissions of the script file could not be read.");
            return problems;
        }

        AddWriters(problems, "The script file", file);

        string? directory = Path.GetDirectoryName(full);
        bool first = true;
        while (!string.IsNullOrEmpty(directory))
        {
            AclSnapshot? acl = _acl.Read(directory);
            if (acl is null)
            {
                problems.Add($"The permissions of the directory '{directory}' could not be read.");
                break;
            }

            if (first)
            {
                // Everything that can be written in the directory that holds the file can replace the file.
                AddWriters(problems, $"The directory '{directory}'", acl);
                first = false;
            }
            else
            {
                IReadOnlyList<string> replacers = AclEvaluator.UntrustedReplacers(acl);
                if (replacers.Count > 0)
                {
                    problems.Add($"The directory '{directory}' can be renamed or deleted by {string.Join(", ", replacers)}.");
                }

                if (!AclEvaluator.IsOwnerTrusted(acl))
                {
                    problems.Add($"The directory '{directory}' is owned by {acl.OwnerSid ?? "an unknown principal"}.");
                }
            }

            directory = Path.GetDirectoryName(directory);
        }

        return problems;
    }

    private static void AddWriters(List<string> problems, string subject, AclSnapshot acl)
    {
        IReadOnlyList<string> writers = AclEvaluator.UntrustedWriters(acl);
        if (writers.Count > 0)
        {
            problems.Add($"{subject} can be changed by {string.Join(", ", writers)}.");
        }

        if (!AclEvaluator.IsOwnerTrusted(acl))
        {
            problems.Add(string.Format(CultureInfo.InvariantCulture, "{0} is owned by {1}.", subject, acl.OwnerSid ?? "an unknown principal"));
        }
    }
}

/// <summary>Source-generated log messages about security decisions of the Windows platform.</summary>
internal static partial class SecurityWarnings
{
    [LoggerMessage(EventId = 300, Level = LogLevel.Warning, Message = "Security: refused to run the script {Script} as {RunAs}. {Reason}")]
    public static partial void ScriptRefused(ILogger logger, string script, string runAs, string reason);
}
