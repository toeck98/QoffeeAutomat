using QoffeeAutomat.Platform.Linux.Service;

namespace QoffeeAutomat.Platform.Linux.Tests.Service;

/// <summary>The verbs <c>service install|uninstall|start|stop|status</c> with a mocked <c>systemctl</c> and a temporary unit directory (T802).</summary>
[Trait("Category", "Service")]
public sealed class LinuxServiceCommandTests : IDisposable
{
    private const string Unit = "qoffeeautomat.service";
    private const string Agent = "/usr/lib/qoffeeautomat/qoffeeautomat";

    private readonly FixtureRoot _root = new();
    private readonly FakeSystemctl _systemctl = new();
    private readonly FakeUnixFileSystem _files = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private bool _privileged = true;

    public LinuxServiceCommandTests()
    {
        _root.CreateDirectory("root/run/systemd/system");
        _root.CreateDirectory("units");
    }

    private string UnitPath => Path.Combine(_root.Root, "units", Unit);

    public void Dispose()
    {
        _output.Dispose();
        _error.Dispose();
        _root.Dispose();
    }

    [Fact]
    public async Task Install_provisions_the_directories_writes_the_unit_enables_it_and_starts_it_in_that_order()
    {
        int code = await RunAsync("install");

        Assert.Equal(LinuxServiceExitCodes.Success, code);
        Assert.Equal(["show " + Unit, "daemon-reload", "enable " + Unit, "start " + Unit], _systemctl.Calls);
        Assert.Equal(5, _files.Directories.Count);
        Assert.Equal(5, _files.Chowned.Count);
        Assert.Contains("is installed", _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("is running", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, _error.ToString());
    }

    [Fact]
    public async Task Install_writes_the_unit_for_the_running_executable()
    {
        await RunAsync("install");

        string unit = await File.ReadAllTextAsync(UnitPath);
        Assert.Equal(SystemdUnitFile.Render(SystemdUnitOptions.Packaged), unit);
        Assert.Contains("\nExecStart=" + Agent + " run\n", unit, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root.Root, "units"), "*.tmp"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                File.GetUnixFileMode(UnitPath));
        }
    }

    [LinuxHostFact]
    public async Task Install_passes_the_options_to_the_unit_and_the_data_directory_to_the_provisioner()
    {
        int code = await RunAsync("install", "--data-dir", "/srv/qoffee", "--ui-port=9000", "--link-port", "9001", "--no-start");

        Assert.Equal(LinuxServiceExitCodes.Success, code);
        string unit = await File.ReadAllTextAsync(UnitPath);
        Assert.Contains("\nExecStart=" + Agent + " run --data-dir /srv/qoffee --ui-port 9000 --link-port 9001\n", unit, StringComparison.Ordinal);
        Assert.Contains("/srv/qoffee/config", _files.Directories.Keys);
        Assert.Contains("/srv/qoffee", _files.Directories.Keys);
        Assert.DoesNotContain("start " + Unit, _systemctl.Calls);
        Assert.Contains("Start it with", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_uses_the_name_for_the_unit_file_and_the_calls()
    {
        int code = await RunAsync("install", "--name", "qoffee-two");

        Assert.Equal(LinuxServiceExitCodes.Success, code);
        Assert.True(File.Exists(Path.Combine(_root.Root, "units", "qoffee-two.service")));
        Assert.Contains("enable qoffee-two.service", _systemctl.Calls);
        Assert.Contains("start qoffee-two.service", _systemctl.Calls);
    }

    [Fact]
    public async Task Install_over_a_running_service_replaces_the_unit_but_leaves_the_restart_to_the_user()
    {
        _systemctl.Status = new UnitStatus(Unit, "loaded", "active", "running", "enabled", 100, UnitPath);

        int code = await RunAsync("install");

        Assert.Equal(LinuxServiceExitCodes.Success, code);
        Assert.DoesNotContain("start " + Unit, _systemctl.Calls);
        Assert.Contains("systemctl restart " + Unit, _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_needs_root_and_changes_nothing_without_it()
    {
        _privileged = false;

        int code = await RunAsync("install");

        Assert.Equal(LinuxServiceExitCodes.NoPermission, code);
        Assert.Contains("sudo", _error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_systemctl.Calls);
        Assert.Empty(_files.Directories);
        Assert.False(File.Exists(UnitPath));
    }

    [Fact]
    public async Task Install_needs_systemd_as_the_init_system()
    {
        Directory.Delete(Path.Combine(_root.Root, "root", "run", "systemd", "system"));

        int code = await RunAsync("install");

        Assert.Equal(LinuxServiceExitCodes.NotInstalled, code);
        Assert.Contains("systemd is not the init system", _error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_systemctl.Calls);
        Assert.False(File.Exists(UnitPath));
    }

    [Fact]
    public async Task A_failing_systemctl_call_is_an_error_with_the_message_of_systemd()
    {
        _systemctl.FailOn = "start " + Unit;

        int code = await RunAsync("install");

        Assert.Equal(LinuxServiceExitCodes.Failure, code);
        Assert.Contains("simulated failure", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_stops_disables_and_removes_the_unit_and_keeps_the_data()
    {
        await RunAsync("install");
        _systemctl.Calls.Clear();
        _systemctl.Status = new UnitStatus(Unit, "loaded", "active", "running", "enabled", 100, UnitPath);
        int directories = _files.Directories.Count;

        int code = await RunAsync("uninstall");

        Assert.Equal(LinuxServiceExitCodes.Success, code);
        Assert.Equal(["show " + Unit, "stop " + Unit, "disable " + Unit, "daemon-reload"], _systemctl.Calls);
        Assert.False(File.Exists(UnitPath));
        Assert.Equal(directories, _files.Directories.Count);
        Assert.Contains("data directories were kept", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_does_not_stop_a_service_that_is_not_running()
    {
        await RunAsync("install");
        _systemctl.Calls.Clear();
        _systemctl.Status = new UnitStatus(Unit, "loaded", "inactive", "dead", "enabled", 0, UnitPath);

        await RunAsync("uninstall");

        Assert.Equal(["show " + Unit, "disable " + Unit, "daemon-reload"], _systemctl.Calls);
    }

    [Fact]
    public async Task Uninstall_of_a_service_that_is_not_installed_succeeds_and_says_so()
    {
        int code = await RunAsync("uninstall");

        Assert.Equal(LinuxServiceExitCodes.Success, code);
        Assert.Equal(["show " + Unit], _systemctl.Calls);
        Assert.Contains("is not installed", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_needs_root()
    {
        _privileged = false;

        Assert.Equal(LinuxServiceExitCodes.NoPermission, await RunAsync("uninstall"));
        Assert.Empty(_systemctl.Calls);
    }

    [Fact]
    public async Task Start_and_stop_go_to_systemctl_when_the_unit_exists()
    {
        _systemctl.Status = new UnitStatus(Unit, "loaded", "inactive", "dead", "enabled", 0, UnitPath);

        Assert.Equal(LinuxServiceExitCodes.Success, await RunAsync("start"));
        Assert.Equal(LinuxServiceExitCodes.Success, await RunAsync("stop"));

        Assert.Equal(["show " + Unit, "start " + Unit, "show " + Unit, "stop " + Unit], _systemctl.Calls);
        Assert.Contains("is running", _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("is stopped", _output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    public async Task Start_and_stop_say_that_the_service_is_not_installed(string action)
    {
        int code = await RunAsync(action);

        Assert.Equal(LinuxServiceExitCodes.NotInstalled, code);
        Assert.Contains("not installed", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(["show " + Unit], _systemctl.Calls);
    }

    [Fact]
    public async Task Status_shows_the_state_of_the_unit()
    {
        _systemctl.Status = new UnitStatus(Unit, "loaded", "active", "running", "enabled", 4711, "/etc/systemd/system/qoffeeautomat.service");

        int code = await RunAsync("status");

        Assert.Equal(LinuxServiceExitCodes.Success, code);
        string text = _output.ToString();
        Assert.Contains("Service:    " + Unit, text, StringComparison.Ordinal);
        Assert.Contains("State:      active (running)", text, StringComparison.Ordinal);
        Assert.Contains("Start type: enabled", text, StringComparison.Ordinal);
        Assert.Contains("Main PID:   4711", text, StringComparison.Ordinal);
        Assert.Contains("/etc/systemd/system/qoffeeautomat.service", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_of_a_service_that_is_not_installed_has_its_own_exit_code()
    {
        int code = await RunAsync("status");

        Assert.Equal(LinuxServiceExitCodes.NotInstalled, code);
        Assert.Contains("is not installed", _output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], "needs an action")]
    [InlineData(new[] { "restart" }, "Unknown service action 'restart'")]
    [InlineData(new[] { "install", "--bogus" }, "Unknown option '--bogus'")]
    [InlineData(new[] { "install", "--ui-port" }, "--ui-port needs a value")]
    [InlineData(new[] { "install", "--ui-port", "70000" }, "--ui-port must be a number")]
    [InlineData(new[] { "install", "--link-port", "-1" }, "--link-port must be a number")]
    [InlineData(new[] { "install", "--name", "Bad Name" }, "--name must start")]
    [InlineData(new[] { "install", "--name", "../x" }, "--name must start")]
    [InlineData(new[] { "install", "--data-dir", "/a\nb" }, "--data-dir needs")]
    [InlineData(new[] { "status", "--no-start=1" }, "Unknown option")]
    public async Task A_wrong_command_line_is_a_usage_error_and_touches_nothing(string[] arguments, string message)
    {
        int code = await RunAsync(arguments);

        Assert.Equal(LinuxServiceExitCodes.Usage, code);
        Assert.Contains(message, _error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_systemctl.Calls);
        Assert.Empty(_files.Directories);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("help")]
    [InlineData("-h")]
    public async Task Help_prints_the_usage(string argument)
    {
        int code = await RunAsync(argument);

        Assert.Equal(LinuxServiceExitCodes.Success, code);
        Assert.Contains("Usage: qoffeeautomat service", _output.ToString(), StringComparison.Ordinal);
        Assert.Empty(_systemctl.Calls);
    }

    [LinuxHostFact]
    public async Task The_global_data_directory_is_used_when_the_action_has_none()
    {
        var command = CreateCommand();
        var context = new LinuxServiceContext(["install", "--no-start"], "/srv/global", ResolveLayout, Agent, []);

        int code = await command.RunAsync(context, _output, _error);

        Assert.Equal(LinuxServiceExitCodes.Success, code);
        Assert.Contains("/srv/global/data", _files.Directories.Keys);
        Assert.Contains("--data-dir /srv/global", await File.ReadAllTextAsync(UnitPath), StringComparison.Ordinal);
    }

    private static LinuxDataDirectories ResolveLayout(string? dataDirectory) =>
        dataDirectory is null ? TestLayouts.System() : TestLayouts.Below(dataDirectory);

    private LinuxServiceCommand CreateCommand() => new(
        _systemctl,
        new LinuxDataDirectoryProvisioner(_files),
        new LinuxServiceCommandOptions
        {
            UnitDirectory = Path.Combine(_root.Root, "units"),
            RootDirectory = Path.Combine(_root.Root, "root"),
            IsPrivileged = () => _privileged,
        });

    private Task<int> RunAsync(params string[] arguments)
    {
        var context = new LinuxServiceContext(arguments, null, ResolveLayout, Agent, []);
        return CreateCommand().RunAsync(context, _output, _error);
    }
}
