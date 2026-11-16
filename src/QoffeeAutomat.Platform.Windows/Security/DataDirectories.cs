using System.Security.AccessControl;

namespace QoffeeAutomat.Platform.Windows.Security;

/// <summary>The directories of PLAN.md S05 ("Runtime data directories").</summary>
/// <param name="Root">The directory that holds all the others, <c>%ProgramData%\QoffeeAutomat</c> by default.</param>
/// <param name="Config">Configuration documents.</param>
/// <param name="Scripts">Script files.</param>
/// <param name="Secrets">Secrets, the setup token and the local channel file.</param>
/// <param name="Data">The SQLite databases and the agent id.</param>
/// <param name="Logs">Log files.</param>
public sealed record DataDirectories(string Root, string Config, string Scripts, string Secrets, string Data, string Logs);

/// <summary>What a directory is for; decides who may access it.</summary>
public enum DirectoryRole
{
    /// <summary>The parent of the others.</summary>
    Root,

    /// <summary>Configuration documents.</summary>
    Config,

    /// <summary>Script files; they run as LocalSystem, and standard users may read them (<c>runAs: user</c> scripts).</summary>
    Scripts,

    /// <summary>Secrets; only SYSTEM and Administrators have access.</summary>
    Secrets,

    /// <summary>Databases.</summary>
    Data,

    /// <summary>Log files.</summary>
    Logs,
}

/// <summary>The permissions that one directory must have.</summary>
/// <param name="Path">The directory.</param>
/// <param name="Role">What it is for.</param>
/// <param name="Grants">What <see cref="DataDirectoryProvisioner.Provision"/> sets.</param>
/// <param name="StandardUsersMayRead">True when a read entry for standard users is acceptable; no one but SYSTEM and Administrators may write in any case.</param>
public sealed record DirectoryPolicy(string Path, DirectoryRole Role, IReadOnlyList<AclGrant> Grants, bool StandardUsersMayRead);

/// <summary>A deviation from the policy found by <see cref="DataDirectoryProvisioner.Verify"/>.</summary>
/// <param name="Path">The directory.</param>
/// <param name="Message">What is wrong and who is affected.</param>
public sealed record AclProblem(string Path, string Message);

/// <summary>The data directories are not protected as PLAN.md S05 and S09 demand; the message says how to fix that.</summary>
public sealed class DataDirectorySecurityException : Exception
{
    /// <summary>Creates an exception without a message.</summary>
    public DataDirectorySecurityException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What is wrong and what to do about it.</param>
    public DataDirectorySecurityException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    /// <param name="message">What is wrong and what to do about it.</param>
    /// <param name="innerException">The cause.</param>
    public DataDirectorySecurityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Creates the data directories with the permissions of PLAN.md S05 and S09 and checks them again at every start.
/// Config, secrets, data and logs belong to SYSTEM and Administrators alone; the scripts directory additionally lets
/// standard users read (scripts of <c>runAs: user</c> run with the user's token), and nobody but SYSTEM and
/// Administrators may write anywhere, because scripts run as LocalSystem.
/// </summary>
public sealed class DataDirectoryProvisioner
{
    private const FileSystemRights Full = FileSystemRights.FullControl;
    private const FileSystemRights ReadAndExecute = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;

    private readonly IFileSystemAcl _acl;

    /// <summary>Creates the provisioner.</summary>
    /// <param name="acl">Reads and writes permissions.</param>
    public DataDirectoryProvisioner(IFileSystemAcl acl)
    {
        ArgumentNullException.ThrowIfNull(acl);
        _acl = acl;
    }

    /// <summary>The policy for every directory of a layout, the root first.</summary>
    /// <param name="layout">The directories.</param>
    /// <returns>One policy per directory.</returns>
    public static IReadOnlyList<DirectoryPolicy> PolicyFor(DataDirectories layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        AclGrant[] administratorsOnly =
        [
            new AclGrant(WellKnownAccounts.LocalSystem, Full),
            new AclGrant(WellKnownAccounts.Administrators, Full),
        ];

        AclGrant[] withUserRead = [.. administratorsOnly, new AclGrant(WellKnownAccounts.Users, ReadAndExecute)];

        return
        [
            new DirectoryPolicy(layout.Root, DirectoryRole.Root, administratorsOnly, StandardUsersMayRead: false),
            new DirectoryPolicy(layout.Config, DirectoryRole.Config, administratorsOnly, StandardUsersMayRead: false),
            new DirectoryPolicy(layout.Scripts, DirectoryRole.Scripts, withUserRead, StandardUsersMayRead: true),
            new DirectoryPolicy(layout.Secrets, DirectoryRole.Secrets, administratorsOnly, StandardUsersMayRead: false),
            new DirectoryPolicy(layout.Data, DirectoryRole.Data, administratorsOnly, StandardUsersMayRead: false),
            new DirectoryPolicy(layout.Logs, DirectoryRole.Logs, administratorsOnly, StandardUsersMayRead: false),
        ];
    }

    /// <summary>
    /// Creates every directory that is missing and sets its permissions, replacing the permissions of the ones that
    /// exist. This is what <c>service install</c> does; it needs an administrator.
    /// </summary>
    /// <param name="layout">The directories.</param>
    /// <exception cref="DataDirectorySecurityException">The root is a drive root, which must never be re-protected.</exception>
    public void Provision(DataDirectories layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        RejectDriveRoot(layout);

        foreach (DirectoryPolicy policy in PolicyFor(layout))
        {
            Directory.CreateDirectory(policy.Path);
            _acl.Protect(policy.Path, policy.Grants);
        }
    }

    /// <summary>Checks every directory against the policy.</summary>
    /// <param name="layout">The directories.</param>
    /// <returns>The deviations; empty when everything is as it must be.</returns>
    public IReadOnlyList<AclProblem> Verify(DataDirectories layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var problems = new List<AclProblem>();
        foreach (DirectoryPolicy policy in PolicyFor(layout))
        {
            AclSnapshot? acl = _acl.Read(policy.Path);
            if (acl is null)
            {
                problems.Add(new AclProblem(policy.Path, "The directory does not exist."));
                continue;
            }

            IReadOnlyList<string> writers = AclEvaluator.UntrustedWriters(acl, includeInheritOnly: true);
            if (writers.Count > 0)
            {
                problems.Add(new AclProblem(policy.Path, "Principals other than SYSTEM and Administrators can change it: " + Describe(writers) + "."));
            }
            else if (!policy.StandardUsersMayRead)
            {
                IReadOnlyList<string> others = AclEvaluator.UntrustedPrincipals(acl);
                if (others.Count > 0)
                {
                    problems.Add(new AclProblem(policy.Path, "It grants access to principals other than SYSTEM and Administrators: " + Describe(others) + "."));
                }
            }

            if (!AclEvaluator.IsOwnerTrusted(acl))
            {
                problems.Add(new AclProblem(policy.Path, $"Its owner ({acl.OwnerSid ?? "unknown"}) is not SYSTEM or Administrators, so the owner can change its permissions."));
            }
        }

        return problems;
    }

    /// <summary>
    /// Makes sure that the directories exist and are protected, as the agent does at every start. With
    /// <paramref name="mayProvision"/> the directories that are missing are created with the right permissions (an
    /// existing directory is never re-protected here, only checked); then everything is verified.
    /// </summary>
    /// <param name="layout">The directories.</param>
    /// <param name="mayProvision">True when the process has the rights to create and protect directories (administrator or SYSTEM).</param>
    /// <exception cref="DataDirectorySecurityException">A directory is missing or too open.</exception>
    public void EnsureSecure(DataDirectories layout, bool mayProvision)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (mayProvision)
        {
            RejectDriveRoot(layout);
            foreach (DirectoryPolicy policy in PolicyFor(layout))
            {
                if (!Directory.Exists(policy.Path))
                {
                    Directory.CreateDirectory(policy.Path);
                    _acl.Protect(policy.Path, policy.Grants);
                }
            }
        }

        IReadOnlyList<AclProblem> problems = Verify(layout);
        if (problems.Count > 0)
        {
            throw new DataDirectorySecurityException(FormatProblems(problems, mayProvision));
        }
    }

    private static void RejectDriveRoot(DataDirectories layout)
    {
        string full = Path.GetFullPath(layout.Root);
        if (string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase))
        {
            throw new DataDirectorySecurityException("The data directory must not be the root of a drive; its permissions would be replaced.");
        }
    }

    private static string FormatProblems(IReadOnlyList<AclProblem> problems, bool mayProvision)
    {
        string remedy = mayProvision
            ? "Run 'qoffeeautomat service install' again from an elevated prompt to repair the permissions, or choose another data directory."
            : "Start the agent as an administrator once, or run 'qoffeeautomat service install' from an elevated prompt.";
        string details = string.Join(" ", problems.Select(problem => problem.Path + ": " + problem.Message));
        return "The data directories are not protected as required (they run scripts as LocalSystem). " + details + " " + remedy;
    }

    private static string Describe(IReadOnlyList<string> sids) => string.Join(", ", sids);
}
