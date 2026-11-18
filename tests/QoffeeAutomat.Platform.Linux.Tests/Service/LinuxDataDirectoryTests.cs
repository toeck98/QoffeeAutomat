using QoffeeAutomat.Platform.Linux.Service;

namespace QoffeeAutomat.Platform.Linux.Tests.Service;

/// <summary>The data directories of PLAN.md S05 and their permissions (S09): 0700 for directories, 0600 for secrets (T802).</summary>
[Trait("Category", "Service")]
public sealed class LinuxDataDirectoryTests
{
    private const UnixFileMode Mode0700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode Mode0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode Mode0755 = Mode0700 | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode Mode0644 = Mode0600 | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    [Fact]
    public void The_default_layout_protects_five_directories_and_never_a_common_parent()
    {
        IReadOnlyList<LinuxDirectoryPolicy> policies = LinuxDataDirectoryProvisioner.PolicyFor(TestLayouts.System());

        Assert.Equal(
            ["/etc/qoffeeautomat", "/etc/qoffeeautomat/scripts", "/var/lib/qoffeeautomat", "/var/lib/qoffeeautomat/secrets", "/var/log/qoffeeautomat"],
            policies.Select(policy => policy.Path));
        Assert.All(policies, policy => Assert.Equal(Mode0700, policy.Mode));
        Assert.DoesNotContain(policies, policy => policy.Role == LinuxDirectoryRole.Root);
    }

    [Fact]
    public void A_layout_below_one_directory_protects_that_directory_too()
    {
        IReadOnlyList<LinuxDirectoryPolicy> policies = LinuxDataDirectoryProvisioner.PolicyFor(TestLayouts.Below("/srv/qoffee"));

        Assert.Equal(6, policies.Count);
        Assert.Equal(new LinuxDirectoryPolicy("/srv/qoffee", LinuxDirectoryRole.Root, Mode0700), policies[0]);
    }

    [Fact]
    public void Provision_creates_every_directory_with_mode_0700_and_gives_it_to_root()
    {
        var files = new FakeUnixFileSystem();
        var provisioner = new LinuxDataDirectoryProvisioner(files);

        provisioner.Provision(TestLayouts.System(), makeOwnedByRoot: true);

        Assert.Equal(5, files.Directories.Count);
        Assert.All(files.Directories.Values, mode => Assert.Equal(Mode0700, mode));
        Assert.Equal(5, files.Chowned.Count);
    }

    [Fact]
    public void Provision_repairs_a_directory_that_exists_with_wider_permissions_and_secret_files_that_are_readable()
    {
        LinuxDataDirectories layout = TestLayouts.System();
        var files = new FakeUnixFileSystem();
        files.Directories[layout.Config] = Mode0755;
        files.Directories[layout.Secrets] = Mode0755;
        files.Files[layout.Secrets + "/secrets.key"] = Mode0644;
        files.Files[layout.Secrets + "/setup-token.txt"] = Mode0600;

        new LinuxDataDirectoryProvisioner(files).Provision(layout, makeOwnedByRoot: false);

        Assert.Equal(Mode0700, files.Directories[layout.Config]);
        Assert.Equal(Mode0700, files.Directories[layout.Secrets]);
        Assert.Equal(Mode0600, files.Files[layout.Secrets + "/secrets.key"]);
        Assert.Equal(Mode0600, files.Files[layout.Secrets + "/setup-token.txt"]);
        Assert.Empty(files.Chowned);
    }

    [Fact]
    public void Provision_fails_when_the_owner_cannot_be_set()
    {
        var files = new FakeUnixFileSystem { ChownFails = true };

        var exception = Assert.Throws<LinuxDataDirectorySecurityException>(() => new LinuxDataDirectoryProvisioner(files).Provision(TestLayouts.System(), makeOwnedByRoot: true));

        Assert.Contains("owner", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_root_of_the_file_system_is_never_re_protected()
    {
        var provisioner = new LinuxDataDirectoryProvisioner(new FakeUnixFileSystem());

        Assert.Throws<LinuxDataDirectorySecurityException>(() => provisioner.Provision(TestLayouts.Below("/"), makeOwnedByRoot: true));
        Assert.Throws<LinuxDataDirectorySecurityException>(() => provisioner.EnsureSecure(TestLayouts.Below("/"), requireRootOwner: true));
    }

    [Fact]
    public void Verify_accepts_what_Provision_creates()
    {
        var files = new FakeUnixFileSystem();
        var provisioner = new LinuxDataDirectoryProvisioner(files);
        LinuxDataDirectories layout = TestLayouts.System();
        provisioner.Provision(layout, makeOwnedByRoot: true);
        files.Files[layout.Secrets + "/secrets.key"] = Mode0600;
        files.Owners[layout.Secrets + "/secrets.key"] = 0;

        Assert.Empty(provisioner.Verify(layout, requireRootOwner: true));
    }

    [Fact]
    public void Verify_reports_a_directory_that_other_users_can_enter_or_read()
    {
        LinuxDataDirectories layout = TestLayouts.System();
        var files = new FakeUnixFileSystem();
        var provisioner = new LinuxDataDirectoryProvisioner(files);
        provisioner.Provision(layout, makeOwnedByRoot: false);
        files.Directories[layout.Scripts] = Mode0755;

        IReadOnlyList<LinuxPermissionProblem> problems = provisioner.Verify(layout, requireRootOwner: false);

        LinuxPermissionProblem problem = Assert.Single(problems);
        Assert.Equal(layout.Scripts, problem.Path);
        Assert.Contains("0755", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_reports_a_secret_file_that_is_readable_by_others()
    {
        LinuxDataDirectories layout = TestLayouts.System();
        var files = new FakeUnixFileSystem();
        var provisioner = new LinuxDataDirectoryProvisioner(files);
        provisioner.Provision(layout, makeOwnedByRoot: false);
        files.Files[layout.Secrets + "/secrets.json"] = Mode0644;

        LinuxPermissionProblem problem = Assert.Single(provisioner.Verify(layout, requireRootOwner: false));

        Assert.Equal(layout.Secrets + "/secrets.json", problem.Path);
        Assert.Contains("0644", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_reports_a_directory_that_another_user_owns_when_the_agent_runs_as_root()
    {
        LinuxDataDirectories layout = TestLayouts.System();
        var files = new FakeUnixFileSystem();
        var provisioner = new LinuxDataDirectoryProvisioner(files);
        provisioner.Provision(layout, makeOwnedByRoot: true);
        files.Owners[layout.Scripts] = 1000;

        LinuxPermissionProblem problem = Assert.Single(provisioner.Verify(layout, requireRootOwner: true));
        Assert.Equal(layout.Scripts, problem.Path);
        Assert.Contains("1000", problem.Message, StringComparison.Ordinal);

        // An agent that does not run as root only protects its own files, so the owner is not examined.
        Assert.Empty(provisioner.Verify(layout, requireRootOwner: false));
    }

    [Fact]
    public void Verify_reports_a_missing_directory()
    {
        var provisioner = new LinuxDataDirectoryProvisioner(new FakeUnixFileSystem());

        IReadOnlyList<LinuxPermissionProblem> problems = provisioner.Verify(TestLayouts.System(), requireRootOwner: false);

        Assert.Equal(5, problems.Count);
        Assert.All(problems, problem => Assert.Contains("does not exist", problem.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void EnsureSecure_creates_what_is_missing_and_leaves_an_existing_directory_alone()
    {
        LinuxDataDirectories layout = TestLayouts.System();
        var files = new FakeUnixFileSystem();
        files.Directories[layout.Data] = Mode0700;
        var provisioner = new LinuxDataDirectoryProvisioner(files);

        provisioner.EnsureSecure(layout, requireRootOwner: false);

        Assert.Equal(5, files.Directories.Count);
        Assert.All(files.Directories.Values, mode => Assert.Equal(Mode0700, mode));
        Assert.Empty(files.Chowned);
    }

    [Fact]
    public void EnsureSecure_refuses_to_start_with_an_open_directory_and_says_how_to_repair_it()
    {
        LinuxDataDirectories layout = TestLayouts.System();
        var files = new FakeUnixFileSystem();
        files.Directories[layout.Config] = Mode0755;

        var exception = Assert.Throws<LinuxDataDirectorySecurityException>(() => new LinuxDataDirectoryProvisioner(files).EnsureSecure(layout, requireRootOwner: true));

        Assert.Contains(layout.Config, exception.Message, StringComparison.Ordinal);
        Assert.Contains("service install", exception.Message, StringComparison.Ordinal);
    }

    [LinuxHostFact]
    public void The_real_file_system_ends_up_with_0700_directories_and_0600_secrets()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new FixtureRoot();
        LinuxDataDirectories layout = TestLayouts.Below(Path.Combine(root.Root, "qoffee"));
        Directory.CreateDirectory(layout.Secrets);
        string key = Path.Combine(layout.Secrets, "secrets.key");
        File.WriteAllText(key, "not a real key");
        File.SetUnixFileMode(key, Mode0644);
        var provisioner = new LinuxDataDirectoryProvisioner(new UnixFileSystem());

        provisioner.Provision(layout, makeOwnedByRoot: false);

        foreach (LinuxDirectoryPolicy policy in LinuxDataDirectoryProvisioner.PolicyFor(layout))
        {
            Assert.Equal(Mode0700, File.GetUnixFileMode(policy.Path));
        }

        Assert.Equal(Mode0600, File.GetUnixFileMode(key));
        Assert.Empty(provisioner.Verify(layout, requireRootOwner: false));
    }

    [LinuxHostFact]
    public void The_real_file_system_tells_an_open_directory_from_a_closed_one()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new FixtureRoot();
        LinuxDataDirectories layout = TestLayouts.Below(Path.Combine(root.Root, "qoffee"));
        var provisioner = new LinuxDataDirectoryProvisioner(new UnixFileSystem());
        provisioner.EnsureSecure(layout, requireRootOwner: false);
        File.SetUnixFileMode(layout.Logs, Mode0755);

        var exception = Assert.Throws<LinuxDataDirectorySecurityException>(() => provisioner.EnsureSecure(layout, requireRootOwner: false));

        Assert.Contains(layout.Logs, exception.Message, StringComparison.Ordinal);
    }

    [LinuxHostFact]
    public void The_owner_of_a_file_is_read_with_stat()
    {
        using var root = new FixtureRoot();
        string file = Path.Combine(root.Root, "owned");
        File.WriteAllText(file, "x");

        uint? owner = new UnixFileSystem().GetOwner(file);

        // Skipped silently only where stat does not exist (a minimal image): then the answer is unknown, never a wrong one.
        if (owner is { } uid)
        {
            Assert.Equal(Environment.IsPrivilegedProcess, uid == 0);
        }
    }
}
