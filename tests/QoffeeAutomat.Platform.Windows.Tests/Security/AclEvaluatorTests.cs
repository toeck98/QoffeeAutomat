using System.Security.AccessControl;
using QoffeeAutomat.Platform.Windows.Security;

namespace QoffeeAutomat.Platform.Windows.Tests.Security;

/// <summary>The decisions of <see cref="AclEvaluator"/> (PLAN.md S09 items 5 and 6); they need no Windows API.</summary>
[Trait("Category", "Service")]
public sealed class AclEvaluatorTests
{
    private static AclSnapshot Snapshot(string? owner, params AclRule[] rules) => new(owner, rules);

    private static void AssertOnly(string expected, IReadOnlyList<string> actual)
    {
        string single = Assert.Single(actual);
        Assert.Equal(expected, single);
    }

    [Fact]
    public void Only_system_administrators_and_trusted_installer_are_trusted()
    {
        Assert.True(WellKnownAccounts.IsTrusted(WellKnownAccounts.LocalSystem));
        Assert.True(WellKnownAccounts.IsTrusted(WellKnownAccounts.Administrators));
        Assert.True(WellKnownAccounts.IsTrusted(WellKnownAccounts.TrustedInstaller));
        Assert.False(WellKnownAccounts.IsTrusted(WellKnownAccounts.Users));
        Assert.False(WellKnownAccounts.IsTrusted(WellKnownAccounts.AuthenticatedUsers));
        Assert.False(WellKnownAccounts.IsTrusted(WellKnownAccounts.Everyone));
        Assert.False(WellKnownAccounts.IsTrusted("S-1-5-21-1-2-3-1001"));
    }

    [Theory]
    [InlineData(FileSystemRights.Modify)]
    [InlineData(FileSystemRights.FullControl)]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    public void Write_rights_of_standard_users_are_reported(FileSystemRights rights)
    {
        AclSnapshot acl = Snapshot(WellKnownAccounts.Administrators, new AclRule(WellKnownAccounts.Users, true, rights));

        AssertOnly(WellKnownAccounts.Users, AclEvaluator.UntrustedWriters(acl));
    }

    [Fact]
    public void Read_and_execute_is_not_a_write_right()
    {
        AclSnapshot acl = Snapshot(
            WellKnownAccounts.Administrators,
            new AclRule(WellKnownAccounts.Users, true, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize));

        Assert.Empty(AclEvaluator.UntrustedWriters(acl));
        Assert.Single(AclEvaluator.UntrustedPrincipals(acl));
    }

    [Fact]
    public void Generic_write_and_generic_all_count_as_write()
    {
        AclSnapshot acl = Snapshot(
            WellKnownAccounts.Administrators,
            new AclRule("S-1-5-21-1-2-3-1001", true, (FileSystemRights)0x40000000),
            new AclRule("S-1-5-21-1-2-3-1002", true, (FileSystemRights)0x10000000));

        Assert.Equal(2, AclEvaluator.UntrustedWriters(acl).Count);
    }

    [Fact]
    public void Trusted_principals_and_deny_entries_are_ignored()
    {
        AclSnapshot acl = Snapshot(
            WellKnownAccounts.LocalSystem,
            new AclRule(WellKnownAccounts.LocalSystem, true, FileSystemRights.FullControl),
            new AclRule(WellKnownAccounts.Administrators, true, FileSystemRights.FullControl),
            new AclRule(WellKnownAccounts.CreatorOwner, true, FileSystemRights.FullControl),
            new AclRule(WellKnownAccounts.Users, false, FileSystemRights.FullControl));

        Assert.Empty(AclEvaluator.UntrustedWriters(acl));
        Assert.Empty(AclEvaluator.UntrustedPrincipals(acl));
    }

    [Fact]
    public void An_entry_that_only_children_inherit_matters_when_asked()
    {
        AclSnapshot acl = Snapshot(
            WellKnownAccounts.Administrators,
            new AclRule(WellKnownAccounts.AuthenticatedUsers, true, FileSystemRights.Modify, AppliesToSelf: false));

        Assert.Empty(AclEvaluator.UntrustedWriters(acl));
        AssertOnly(WellKnownAccounts.AuthenticatedUsers, AclEvaluator.UntrustedWriters(acl, includeInheritOnly: true));
        AssertOnly(WellKnownAccounts.AuthenticatedUsers, AclEvaluator.UntrustedPrincipals(acl));
    }

    [Fact]
    public void Replacing_a_directory_needs_delete_or_permission_rights_not_just_create_rights()
    {
        // The rules of C:\ProgramData: users may create entries but cannot rename or delete the folder.
        AclSnapshot programData = Snapshot(
            WellKnownAccounts.LocalSystem,
            new AclRule(WellKnownAccounts.Users, true, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories));
        AclSnapshot open = Snapshot(
            WellKnownAccounts.LocalSystem,
            new AclRule(WellKnownAccounts.Users, true, FileSystemRights.DeleteSubdirectoriesAndFiles));

        Assert.Empty(AclEvaluator.UntrustedReplacers(programData));
        AssertOnly(WellKnownAccounts.Users, AclEvaluator.UntrustedReplacers(open));
    }

    [Fact]
    public void The_owner_must_be_trusted_because_the_owner_can_change_the_permissions()
    {
        Assert.True(AclEvaluator.IsOwnerTrusted(Snapshot(WellKnownAccounts.Administrators)));
        Assert.True(AclEvaluator.IsOwnerTrusted(Snapshot(WellKnownAccounts.LocalSystem)));
        Assert.False(AclEvaluator.IsOwnerTrusted(Snapshot("S-1-5-21-1-2-3-1001")));
        Assert.False(AclEvaluator.IsOwnerTrusted(Snapshot(null)));
    }

    [Fact]
    public void Each_principal_is_listed_once()
    {
        AclSnapshot acl = Snapshot(
            WellKnownAccounts.Administrators,
            new AclRule(WellKnownAccounts.Users, true, FileSystemRights.WriteData),
            new AclRule(WellKnownAccounts.Users, true, FileSystemRights.Delete));

        Assert.Single(AclEvaluator.UntrustedWriters(acl));
    }
}
