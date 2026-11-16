using System.Security.AccessControl;
using QoffeeAutomat.Platform.Windows.Security;

namespace QoffeeAutomat.Platform.Windows.Tests.Security;

/// <summary>
/// The data directories of PLAN.md S05 and S09: what install creates and what start verifies. The logic runs against
/// <see cref="FakeAcl"/> on every system; <see cref="RealAclTests"/> repeats the key facts on a real NTFS volume.
/// </summary>
[Trait("Category", "Service")]
public sealed class DataDirectoryProvisionerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private DataDirectories Layout => new(
        _temp.Path,
        Path.Combine(_temp.Path, "config"),
        Path.Combine(_temp.Path, "scripts"),
        Path.Combine(_temp.Path, "secrets"),
        Path.Combine(_temp.Path, "data"),
        Path.Combine(_temp.Path, "logs"));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Provision_creates_every_directory_and_protects_it()
    {
        var acl = new FakeAcl();

        new DataDirectoryProvisioner(acl).Provision(Layout);

        foreach (string path in new[] { Layout.Root, Layout.Config, Layout.Scripts, Layout.Secrets, Layout.Data, Layout.Logs })
        {
            Assert.True(Directory.Exists(path), path);
        }

        Assert.Equal(6, acl.Protections.Count);
        Assert.Equal(Path.GetFullPath(Layout.Root), acl.Protections[0].Path);
    }

    [Fact]
    public void The_secrets_directory_grants_access_to_system_and_administrators_only()
    {
        var acl = new FakeAcl();

        new DataDirectoryProvisioner(acl).Provision(Layout);

        IReadOnlyList<AclGrant> grants = acl.Protections.Single(entry => entry.Path == Path.GetFullPath(Layout.Secrets)).Grants;
        Assert.Equal(2, grants.Count);
        Assert.Contains(grants, grant => grant.Sid == WellKnownAccounts.LocalSystem);
        Assert.Contains(grants, grant => grant.Sid == WellKnownAccounts.Administrators);
        Assert.All(grants, grant => Assert.Equal(FileSystemRights.FullControl, grant.Rights));
    }

    [Fact]
    public void Standard_users_may_read_scripts_but_nothing_else()
    {
        var acl = new FakeAcl();

        new DataDirectoryProvisioner(acl).Provision(Layout);

        foreach ((string path, IReadOnlyList<AclGrant> grants) in acl.Protections)
        {
            bool usersGranted = grants.Any(grant => grant.Sid == WellKnownAccounts.Users);
            Assert.Equal(path == Path.GetFullPath(Layout.Scripts), usersGranted);
        }

        AclGrant users = acl.Protections.Single(entry => entry.Path == Path.GetFullPath(Layout.Scripts)).Grants.Single(grant => grant.Sid == WellKnownAccounts.Users);
        Assert.Equal(0, (int)(users.Rights & AclEvaluator.WriteMask));
    }

    [Fact]
    public void A_provisioned_layout_verifies_clean()
    {
        var acl = new FakeAcl();
        var provisioner = new DataDirectoryProvisioner(acl);
        provisioner.Provision(Layout);

        Assert.Empty(provisioner.Verify(Layout));
    }

    [Fact]
    public void Verify_reports_standard_users_in_the_secrets_directory()
    {
        var acl = new FakeAcl();
        var provisioner = new DataDirectoryProvisioner(acl);
        provisioner.Provision(Layout);
        acl.Set(
            Layout.Secrets,
            new AclSnapshot(
                WellKnownAccounts.Administrators,
                [
                    new AclRule(WellKnownAccounts.LocalSystem, true, FileSystemRights.FullControl),
                    new AclRule(WellKnownAccounts.Administrators, true, FileSystemRights.FullControl),
                    new AclRule(WellKnownAccounts.Users, true, FileSystemRights.ReadAndExecute),
                ]));

        AclProblem problem = Assert.Single(provisioner.Verify(Layout));
        Assert.Equal(Layout.Secrets, problem.Path);
        Assert.Contains(WellKnownAccounts.Users, problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_reports_write_access_for_standard_users_to_the_scripts_directory()
    {
        var acl = new FakeAcl();
        var provisioner = new DataDirectoryProvisioner(acl);
        provisioner.Provision(Layout);
        acl.Set(
            Layout.Scripts,
            new AclSnapshot(
                WellKnownAccounts.Administrators,
                [
                    new AclRule(WellKnownAccounts.Administrators, true, FileSystemRights.FullControl),
                    new AclRule(WellKnownAccounts.AuthenticatedUsers, true, FileSystemRights.Modify),
                ]));

        AclProblem problem = Assert.Single(provisioner.Verify(Layout));
        Assert.Equal(Layout.Scripts, problem.Path);
        Assert.Contains("change", problem.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_reports_an_owner_who_is_not_trusted()
    {
        var acl = new FakeAcl();
        var provisioner = new DataDirectoryProvisioner(acl);
        provisioner.Provision(Layout);
        acl.Set(Layout.Config, new AclSnapshot("S-1-5-21-1-2-3-1001", FakeAcl.Protected.Rules));

        AclProblem problem = Assert.Single(provisioner.Verify(Layout));
        Assert.Contains("owner", problem.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_reports_a_missing_directory()
    {
        var acl = new FakeAcl { ReadAll = false };
        var provisioner = new DataDirectoryProvisioner(acl);

        IReadOnlyList<AclProblem> problems = provisioner.Verify(Layout);

        Assert.Equal(6, problems.Count);
        Assert.All(problems, problem => Assert.Contains("does not exist", problem.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void EnsureSecure_creates_missing_directories_when_the_process_may_provision()
    {
        var acl = new FakeAcl { ReadAll = false };

        new DataDirectoryProvisioner(acl).EnsureSecure(Layout, mayProvision: true);

        Assert.True(Directory.Exists(Layout.Secrets));
        Assert.Equal(6, acl.Protections.Count);
    }

    [Fact]
    public void EnsureSecure_refuses_missing_directories_without_the_rights_to_create_them()
    {
        var acl = new FakeAcl { ReadAll = false };

        var exception = Assert.Throws<DataDirectorySecurityException>(() => new DataDirectoryProvisioner(acl).EnsureSecure(Layout, mayProvision: false));

        Assert.Contains("service install", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Layout.Secrets));
        Assert.Empty(acl.Protections);
    }

    [Fact]
    public void EnsureSecure_refuses_an_existing_directory_that_is_too_open_and_does_not_change_it()
    {
        var acl = new FakeAcl();
        Directory.CreateDirectory(Layout.Secrets);
        acl.Set(
            Layout.Secrets,
            new AclSnapshot(
                WellKnownAccounts.Administrators,
                [new AclRule(WellKnownAccounts.Everyone, true, FileSystemRights.FullControl)]));

        var exception = Assert.Throws<DataDirectorySecurityException>(() => new DataDirectoryProvisioner(acl).EnsureSecure(Layout, mayProvision: true));

        Assert.Contains(Layout.Secrets, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(acl.Protections, entry => entry.Path == Path.GetFullPath(Layout.Secrets));
    }

    [Fact]
    public void A_drive_root_is_never_re_protected()
    {
        string driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var layout = new DataDirectories(driveRoot, driveRoot + "config", driveRoot + "scripts", driveRoot + "secrets", driveRoot + "data", driveRoot + "logs");
        var acl = new FakeAcl();

        Assert.Throws<DataDirectorySecurityException>(() => new DataDirectoryProvisioner(acl).Provision(layout));
        Assert.Empty(acl.Protections);
    }
}
