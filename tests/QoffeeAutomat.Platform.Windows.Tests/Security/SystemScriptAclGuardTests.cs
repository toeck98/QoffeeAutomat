using System.Security.AccessControl;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Platform.Windows.Security;

namespace QoffeeAutomat.Platform.Windows.Tests.Security;

/// <summary>PLAN.md S09 item 5: system scripts in places that standard users can write are refused with a logged security warning.</summary>
[Trait("Category", "Service")]
public sealed class SystemScriptAclGuardTests
{
    private static readonly string ScriptsDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "qa-guard", "scripts"));
    private static readonly string Script = Path.Combine(ScriptsDirectory, "backup.ps1");

    private static AclSnapshot WithUsers(FileSystemRights rights) => new(
        WellKnownAccounts.Administrators,
        [
            new AclRule(WellKnownAccounts.Administrators, true, FileSystemRights.FullControl),
            new AclRule(WellKnownAccounts.Users, true, rights),
        ]);

    [Fact]
    public void A_script_in_a_protected_directory_may_run_as_system()
    {
        var logger = new ListLogger();
        var guard = new SystemScriptAclGuard(new FakeAcl(), logger);

        ScriptAccessDecision decision = guard.Check(Script, RunAsValues.System);

        Assert.True(decision.IsAllowed);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void A_script_in_a_directory_that_standard_users_can_write_is_refused_with_a_security_warning()
    {
        var acl = new FakeAcl();
        acl.Set(ScriptsDirectory, WithUsers(FileSystemRights.Modify));
        var logger = new ListLogger();

        ScriptAccessDecision decision = new SystemScriptAclGuard(acl, logger).Check(Script, RunAsValues.System);

        Assert.False(decision.IsAllowed);
        Assert.Contains("standard users could change it", decision.Reason, StringComparison.Ordinal);
        (LogLevel level, string message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.StartsWith("Security:", message, StringComparison.Ordinal);
        Assert.Contains("backup.ps1", message, StringComparison.Ordinal);
        Assert.Contains(WellKnownAccounts.Users, message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_script_file_that_standard_users_can_write_is_refused_even_in_a_protected_directory()
    {
        var acl = new FakeAcl();
        acl.Set(Script, WithUsers(FileSystemRights.WriteData));

        ScriptAccessDecision decision = new SystemScriptAclGuard(acl, new ListLogger()).Check(Script, RunAsValues.System);

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void Elevated_desktop_scripts_are_checked_too()
    {
        var acl = new FakeAcl();
        acl.Set(ScriptsDirectory, WithUsers(FileSystemRights.Modify));

        Assert.False(new SystemScriptAclGuard(acl, new ListLogger()).Check(Script, RunAsValues.UserElevated).IsAllowed);
    }

    [Fact]
    public void Scripts_that_run_as_the_user_are_not_checked()
    {
        var acl = new FakeAcl();
        acl.Set(ScriptsDirectory, WithUsers(FileSystemRights.Modify));
        var logger = new ListLogger();

        Assert.True(new SystemScriptAclGuard(acl, logger).Check(Script, RunAsValues.User).IsAllowed);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void A_directory_above_the_scripts_that_users_can_rename_is_refused()
    {
        var acl = new FakeAcl();
        string parent = Path.GetDirectoryName(ScriptsDirectory)!;
        acl.Set(parent, WithUsers(FileSystemRights.DeleteSubdirectoriesAndFiles));

        ScriptAccessDecision decision = new SystemScriptAclGuard(acl, new ListLogger()).Check(Script, RunAsValues.System);

        Assert.False(decision.IsAllowed);
        Assert.Contains("renamed or deleted", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_directory_above_the_scripts_where_users_may_only_create_entries_is_fine()
    {
        var acl = new FakeAcl();
        string parent = Path.GetDirectoryName(ScriptsDirectory)!;
        acl.Set(parent, WithUsers(FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories));

        Assert.True(new SystemScriptAclGuard(acl, new ListLogger()).Check(Script, RunAsValues.System).IsAllowed);
    }

    [Fact]
    public void A_script_whose_permissions_cannot_be_read_is_refused()
    {
        var acl = new FakeAcl();
        acl.Set(Script, null);

        ScriptAccessDecision decision = new SystemScriptAclGuard(acl, new ListLogger()).Check(Script, RunAsValues.System);

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void A_script_owned_by_a_standard_user_is_refused()
    {
        var acl = new FakeAcl();
        acl.Set(Script, new AclSnapshot("S-1-5-21-1-2-3-1001", FakeAcl.Protected.Rules));

        Assert.False(new SystemScriptAclGuard(acl, new ListLogger()).Check(Script, RunAsValues.System).IsAllowed);
    }

    [Fact]
    public async Task The_runner_does_not_start_a_refused_system_script()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Path);
        string file = Path.Combine(temp.Path, "evil.ps1");
        await File.WriteAllTextAsync(file, "Write-Output 'x'");
        var acl = new FakeAcl();
        acl.Set(temp.Path, WithUsers(FileSystemRights.Modify));
        var logger = new ListLogger();
        var runner = new PowerShellRunner(
            new FakeEngineLocator(),
            new PowerShellOptions { ScriptsDirectory = temp.Path, ScriptGuard = new SystemScriptAclGuard(acl, logger) });
        var invocation = new PowerShellInvocation(
            PowerShellSources.File,
            null,
            "evil.ps1",
            PowerShellEngines.Pwsh,
            new Dictionary<string, object>(),
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(5),
            RunAsValues.System);

        var launcher = new RecordingLauncher();
        var result = await runner.RunAsync(invocation, launcher, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("standard users could change it", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, launcher.Launches);
        Assert.Single(logger.Entries);
    }
}
