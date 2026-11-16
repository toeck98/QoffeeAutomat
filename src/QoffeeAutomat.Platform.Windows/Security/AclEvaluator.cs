using System.Security.AccessControl;

namespace QoffeeAutomat.Platform.Windows.Security;

/// <summary>
/// Decides from an <see cref="AclSnapshot"/> who can change or read a file or directory (PLAN.md S09 items 5 and 6).
/// Only allow entries count; a deny entry never makes a directory look safer than its allow entries say. Entries
/// of trusted principals (<see cref="WellKnownAccounts.IsTrusted"/>) are ignored.
/// </summary>
public static class AclEvaluator
{
    /// <summary>Every right that lets a principal change the content, the attributes or the permissions of an object.</summary>
    public const FileSystemRights WriteMask =
        FileSystemRights.WriteData
        | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;

    /// <summary>
    /// The rights that let a principal replace a directory (rename or delete it, or take it over). Creating new
    /// entries inside a parent is not part of it, so the usual rules of ProgramData do not trip the check.
    /// </summary>
    public const FileSystemRights ReplaceMask =
        FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;

    // The generic rights can appear in an entry that was never mapped to specific rights.
    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    /// <summary>Lists the untrusted principals that can change the object.</summary>
    /// <param name="acl">The permissions.</param>
    /// <param name="includeInheritOnly">True to count entries that only the children inherit (the files below a directory).</param>
    /// <returns>The security identifiers, each once.</returns>
    public static IReadOnlyList<string> UntrustedWriters(AclSnapshot acl, bool includeInheritOnly = false)
    {
        return Principals(acl, rights => (rights & WriteMask) != 0 || HasGenericWrite(rights), includeInheritOnly);
    }

    /// <summary>Lists the untrusted principals that can rename, delete or take over the object.</summary>
    /// <param name="acl">The permissions.</param>
    /// <returns>The security identifiers, each once.</returns>
    public static IReadOnlyList<string> UntrustedReplacers(AclSnapshot acl)
    {
        return Principals(acl, rights => (rights & ReplaceMask) != 0 || HasGenericWrite(rights));
    }

    /// <summary>Lists the untrusted principals that have any access at all (the secrets directory allows none).</summary>
    /// <param name="acl">The permissions.</param>
    /// <returns>The security identifiers, each once.</returns>
    public static IReadOnlyList<string> UntrustedPrincipals(AclSnapshot acl)
    {
        // Entries that only the children inherit count here: a file below the directory would carry them.
        return Principals(acl, rights => (rights & ~FileSystemRights.Synchronize) != 0, includeInheritOnly: true);
    }

    /// <summary>Tells whether the owner is trusted. The owner can always change the permissions, so it counts like a writer.</summary>
    /// <param name="acl">The permissions.</param>
    /// <returns>True when the owner is SYSTEM, the Administrators group or TrustedInstaller.</returns>
    public static bool IsOwnerTrusted(AclSnapshot acl)
    {
        ArgumentNullException.ThrowIfNull(acl);
        return acl.OwnerSid is { } owner && WellKnownAccounts.IsTrusted(owner);
    }

    private static bool HasGenericWrite(FileSystemRights rights)
    {
        return (((int)rights) & (GenericAll | GenericWrite)) != 0;
    }

    private static List<string> Principals(AclSnapshot acl, Func<FileSystemRights, bool> matches, bool includeInheritOnly = false)
    {
        ArgumentNullException.ThrowIfNull(acl);

        var found = new List<string>();
        foreach (AclRule rule in acl.Rules)
        {
            if (rule.IsAllow
                && (rule.AppliesToSelf || includeInheritOnly)
                && !WellKnownAccounts.IsTrusted(rule.Sid)
                && matches(rule.Rights)
                && !found.Contains(rule.Sid, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(rule.Sid);
            }
        }

        return found;
    }
}
