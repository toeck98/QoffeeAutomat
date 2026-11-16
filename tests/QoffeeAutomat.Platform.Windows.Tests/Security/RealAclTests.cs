using System.Security.AccessControl;
using System.Security.Principal;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Platform.Windows.Security;

namespace QoffeeAutomat.Platform.Windows.Tests.Security;

/// <summary>The acceptance facts of T301 on a real NTFS volume: they need Windows, and the ones that set permissions an elevated process.</summary>
[Trait("Category", "Service")]
public sealed class RealAclTests : IDisposable
{
    private static readonly string[] AllowedOnSecrets = [WellKnownAccounts.LocalSystem, WellKnownAccounts.Administrators];

    private readonly TempDirectory _temp = new();

    private DataDirectories Layout => new(
        _temp.Path,
        Path.Combine(_temp.Path, "config"),
        Path.Combine(_temp.Path, "scripts"),
        Path.Combine(_temp.Path, "secrets"),
        Path.Combine(_temp.Path, "data"),
        Path.Combine(_temp.Path, "logs"));

    public void Dispose() => _temp.Dispose();

    [ElevatedWindowsFact]
    public void The_secrets_directory_grants_access_to_system_and_administrators_only()
    {
        var acl = new WindowsFileSystemAcl();
        new DataDirectoryProvisioner(acl).Provision(Layout);

        AclSnapshot secrets = acl.Read(Layout.Secrets)!;

        Assert.NotEmpty(secrets.Rules);
        Assert.All(secrets.Rules, rule => Assert.Contains(rule.Sid, AllowedOnSecrets));
        Assert.Empty(AclEvaluator.UntrustedPrincipals(secrets));
        Assert.True(AclEvaluator.IsOwnerTrusted(secrets), "Owner: " + secrets.OwnerSid);
    }

    [ElevatedWindowsFact]
    public void Inheritance_from_the_parent_is_switched_off()
    {
        var acl = new WindowsFileSystemAcl();
        new DataDirectoryProvisioner(acl).Provision(Layout);

        DirectorySecurity security = new DirectoryInfo(Layout.Secrets).GetAccessControl();

        Assert.True(security.AreAccessRulesProtected);
    }

    [ElevatedWindowsFact]
    public void A_provisioned_layout_verifies_clean_and_a_file_in_it_is_protected_too()
    {
        var acl = new WindowsFileSystemAcl();
        var provisioner = new DataDirectoryProvisioner(acl);
        provisioner.Provision(Layout);
        string file = Path.Combine(Layout.Secrets, "secrets.json");
        File.WriteAllText(file, "{}");

        Assert.Empty(provisioner.Verify(Layout));
        Assert.Empty(AclEvaluator.UntrustedPrincipals(acl.Read(file)!));
    }

    [ElevatedWindowsFact]
    public void A_directory_that_standard_users_can_write_is_found_by_the_check_at_start()
    {
        var acl = new WindowsFileSystemAcl();
        var provisioner = new DataDirectoryProvisioner(acl);
        provisioner.Provision(Layout);

        var directory = new DirectoryInfo(Layout.Scripts);
        DirectorySecurity security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directory.SetAccessControl(security);

        AclProblem problem = Assert.Single(provisioner.Verify(Layout));
        Assert.Equal(Layout.Scripts, problem.Path);
        Assert.Throws<DataDirectorySecurityException>(() => provisioner.EnsureSecure(Layout, mayProvision: true));
    }

    [WindowsFact]
    public void A_system_script_below_a_directory_of_the_current_user_is_refused_on_the_real_file_system()
    {
        if (WindowsIdentity.GetCurrent().IsSystem)
        {
            return;
        }

        Directory.CreateDirectory(_temp.Path);
        string script = Path.Combine(_temp.Path, "job.ps1");
        File.WriteAllText(script, "Write-Output 'x'");
        var logger = new ListLogger();

        ScriptAccessDecisionAssert.Refused(new SystemScriptAclGuard(new WindowsFileSystemAcl(), logger).Check(script, RunAsValues.System));

        Assert.Single(logger.Entries);
    }
}

/// <summary>Assertion helper for the decision of the script guard.</summary>
internal static class ScriptAccessDecisionAssert
{
    public static void Refused(QoffeeAutomat.Core.Execution.PowerShell.ScriptAccessDecision decision)
    {
        Assert.False(decision.IsAllowed);
        Assert.False(string.IsNullOrEmpty(decision.Reason));
    }
}
