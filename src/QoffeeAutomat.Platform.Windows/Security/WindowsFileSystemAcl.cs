using System.Security.AccessControl;
using System.Security.Principal;

namespace QoffeeAutomat.Platform.Windows.Security;

/// <summary>The <see cref="IFileSystemAcl"/> of a Windows computer, on top of the NTFS access control lists.</summary>
public sealed class WindowsFileSystemAcl : IFileSystemAcl
{
    /// <inheritdoc />
    public AclSnapshot? Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        FileSystemSecurity security;
        try
        {
            if (Directory.Exists(path))
            {
                security = new DirectoryInfo(path).GetAccessControl();
            }
            else if (File.Exists(path))
            {
                security = new FileInfo(path).GetAccessControl();
            }
            else
            {
                return null;
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        string? owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
        var rules = new List<AclRule>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            rules.Add(new AclRule(
                rule.IdentityReference.Value,
                rule.AccessControlType == AccessControlType.Allow,
                rule.FileSystemRights,
                AppliesToSelf: (rule.PropagationFlags & PropagationFlags.InheritOnly) == PropagationFlags.None));
        }

        return new AclSnapshot(owner, rules);
    }

    /// <inheritdoc />
    public void Protect(string path, IReadOnlyList<AclGrant> grants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(grants);

        var directory = new DirectoryInfo(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (AclGrant grant in grants)
        {
            InheritanceFlags inheritance = grant.InheritToChildren
                ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                : InheritanceFlags.None;
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(grant.Sid),
                grant.Rights,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        directory.SetAccessControl(security);

        // The owner is changed in a second step: a caller that may not hand the directory to the group (for example a
        // process without the restore privilege) still gets the permissions, and the start-up check names the owner.
        try
        {
            var ownerOnly = new DirectorySecurity();
            ownerOnly.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            directory.SetAccessControl(ownerOnly);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            // Reported by DataDirectoryProvisioner.Verify.
        }
    }
}
