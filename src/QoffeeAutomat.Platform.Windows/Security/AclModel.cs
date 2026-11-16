using System.Security.AccessControl;

namespace QoffeeAutomat.Platform.Windows.Security;

/// <summary>
/// Security identifiers of the accounts that the file system rules of PLAN.md S05 and S09 talk about. They are
/// kept as strings so that the evaluation logic needs no Windows API and is tested on every operating system.
/// </summary>
public static class WellKnownAccounts
{
    /// <summary>NT AUTHORITY\SYSTEM, the account of the service.</summary>
    public const string LocalSystem = "S-1-5-18";

    /// <summary>BUILTIN\Administrators.</summary>
    public const string Administrators = "S-1-5-32-544";

    /// <summary>BUILTIN\Users.</summary>
    public const string Users = "S-1-5-32-545";

    /// <summary>NT AUTHORITY\Authenticated Users.</summary>
    public const string AuthenticatedUsers = "S-1-5-11";

    /// <summary>Everyone.</summary>
    public const string Everyone = "S-1-1-0";

    /// <summary>CREATOR OWNER, a placeholder that is replaced by the owner of a new object.</summary>
    public const string CreatorOwner = "S-1-3-0";

    /// <summary>NT SERVICE\TrustedInstaller, which owns the system files.</summary>
    public const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private static readonly HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase)
    {
        LocalSystem,
        Administrators,
        TrustedInstaller,
        CreatorOwner,
    };

    /// <summary>
    /// Tells whether a principal may change files that run as LocalSystem: SYSTEM, the Administrators group,
    /// TrustedInstaller and the CREATOR OWNER placeholder. Everybody else, including a named administrator
    /// account, is reported so that the owner can decide to grant access to the group instead.
    /// </summary>
    /// <param name="sid">The security identifier in string form.</param>
    /// <returns>True for a trusted principal.</returns>
    public static bool IsTrusted(string sid)
    {
        ArgumentNullException.ThrowIfNull(sid);
        return Trusted.Contains(sid);
    }
}

/// <summary>One access control entry, reduced to what the checks need.</summary>
/// <param name="Sid">The principal.</param>
/// <param name="IsAllow">True for an allow entry; deny entries are ignored by the checks (the check is conservative).</param>
/// <param name="Rights">The rights.</param>
/// <param name="AppliesToSelf">False for an entry that only the children inherit (inherit-only).</param>
public sealed record AclRule(string Sid, bool IsAllow, FileSystemRights Rights, bool AppliesToSelf = true);

/// <summary>The access control of a file or directory.</summary>
/// <param name="OwnerSid">The owner; the owner can always change the permissions.</param>
/// <param name="Rules">The explicit and inherited entries.</param>
public sealed record AclSnapshot(string? OwnerSid, IReadOnlyList<AclRule> Rules);

/// <summary>A permission that <see cref="IFileSystemAcl.Protect"/> sets.</summary>
/// <param name="Sid">The principal.</param>
/// <param name="Rights">The rights.</param>
/// <param name="InheritToChildren">True when files and sub-directories inherit the entry.</param>
public sealed record AclGrant(string Sid, FileSystemRights Rights, bool InheritToChildren = true);

/// <summary>Reads and writes file system permissions; the seam that lets the logic run without Windows.</summary>
public interface IFileSystemAcl
{
    /// <summary>Reads the permissions of a file or directory.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The permissions, or null when the path does not exist.</returns>
    AclSnapshot? Read(string path);

    /// <summary>
    /// Replaces the permissions of a directory by exactly <paramref name="grants"/>: inheritance from the parent is
    /// switched off, and the owner is set to the Administrators group when the caller may do so.
    /// </summary>
    /// <param name="path">The directory.</param>
    /// <param name="grants">The permissions to grant.</param>
    void Protect(string path, IReadOnlyList<AclGrant> grants);
}
