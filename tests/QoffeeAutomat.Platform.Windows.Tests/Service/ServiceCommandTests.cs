using QoffeeAutomat.Platform.Windows.Security;
using QoffeeAutomat.Platform.Windows.Service;

namespace QoffeeAutomat.Platform.Windows.Tests.Service;

/// <summary>The verb <c>service install|uninstall|start|stop|status</c> (PLAN.md S11), against fakes of the service control manager and the Event Log.</summary>
[Trait("Category", "Service")]
public sealed class ServiceCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeScm _scm = new();
    private readonly FakeEventLog _eventLog = new();
    private readonly FakeAcl _acl = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    private string Executable => Path.Combine(_temp.Path, "bin dir", "QoffeeAutomat.Agent.exe");

    public void Dispose()
    {
        _output.Dispose();
        _error.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task Install_creates_the_directories_the_event_source_and_the_service()
    {
        int code = await RunAsync("install", "--data-dir", _temp.Path);

        Assert.Equal(ServiceExitCodes.Success, code);
        Assert.True(Directory.Exists(Path.Combine(_temp.Path, "secrets")));
        Assert.Equal(6, _acl.Protections.Count);
        Assert.Equal(("QoffeeAutomat", "Application"), Assert.Single(_eventLog.Created));
        ServiceDefinition definition = Assert.Single(_scm.Created);
        Assert.Equal("QoffeeAutomat", definition.Name);
        Assert.StartsWith("\"" + Executable + "\" run --data-dir ", definition.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_without_administrator_rights_changes_nothing()
    {
        int code = await RunCoreAsync(["install"], privileged: false, dataDirectory: null);

        Assert.Equal(ServiceExitCodes.NoPermission, code);
        Assert.Contains("administrator", _error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_scm.Created);
        Assert.Empty(_acl.Protections);
        Assert.Empty(_eventLog.Created);
    }

    [Fact]
    public async Task Install_twice_is_refused_instead_of_changing_the_service()
    {
        Assert.Equal(ServiceExitCodes.Success, await RunAsync("install", "--data-dir", _temp.Path));
        _acl.Protections.Clear();

        int code = await RunAsync("install", "--data-dir", _temp.Path);

        Assert.Equal(ServiceExitCodes.Failure, code);
        Assert.Contains("installed already", _error.ToString(), StringComparison.Ordinal);
        Assert.Single(_scm.Created);
        Assert.Empty(_acl.Protections);
    }

    [Fact]
    public async Task Install_hands_the_ports_and_the_service_name_on()
    {
        int code = await RunAsync("install", "--name", "QoffeeAutomatTest", "--data-dir", _temp.Path, "--ui-port", "9480", "--link-port=9481");

        Assert.Equal(ServiceExitCodes.Success, code);
        ServiceDefinition definition = Assert.Single(_scm.Created);
        Assert.Equal("QoffeeAutomatTest", definition.Name);
        Assert.EndsWith(" --ui-port 9480 --link-port 9481", definition.CommandLine, StringComparison.Ordinal);
        Assert.Equal("QoffeeAutomatTest", Assert.Single(_eventLog.Created).Source);
    }

    [Fact]
    public async Task The_global_data_directory_option_is_used_when_the_action_has_none()
    {
        int code = await RunCoreAsync(["install"], privileged: true, dataDirectory: _temp.Path);

        Assert.Equal(ServiceExitCodes.Success, code);
        Assert.Contains("--data-dir", Assert.Single(_scm.Created).CommandLine, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(_temp.Path, "config")));
    }

    [Fact]
    public async Task A_path_with_spaces_is_quoted_in_the_command_line()
    {
        string spaced = Path.Combine(_temp.Path, "my data");

        await RunAsync("install", "--data-dir", spaced);

        Assert.Contains("\"" + spaced + "\"", Assert.Single(_scm.Created).CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_stops_a_running_service_removes_it_and_keeps_the_data()
    {
        await RunAsync("install", "--data-dir", _temp.Path);
        await RunAsync("start");
        _output.GetStringBuilder().Clear();

        int code = await RunAsync("uninstall");

        Assert.Equal(ServiceExitCodes.Success, code);
        string[] expectedCalls = ["stop", "delete"];
        Assert.Equal(expectedCalls, _scm.Calls.Skip(_scm.Calls.IndexOf("start") + 1));
        Assert.Equal("QoffeeAutomat", Assert.Single(_eventLog.Deleted));
        Assert.True(Directory.Exists(Path.Combine(_temp.Path, "secrets")));
        Assert.Contains("kept", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_of_a_service_that_is_not_installed_succeeds()
    {
        int code = await RunAsync("uninstall");

        Assert.Equal(ServiceExitCodes.Success, code);
        Assert.Contains("not installed", _output.ToString(), StringComparison.Ordinal);
        Assert.Empty(_scm.Calls);
    }

    [Fact]
    public async Task Start_and_stop_follow_the_state_of_the_service()
    {
        await RunAsync("install", "--data-dir", _temp.Path);

        Assert.Equal(ServiceExitCodes.Success, await RunAsync("start"));
        Assert.Equal(ServiceRunState.Running, (await _scm.QueryAsync("QoffeeAutomat", CancellationToken.None))!.State);
        Assert.Equal(ServiceExitCodes.Success, await RunAsync("stop"));
        Assert.Equal(ServiceRunState.Stopped, (await _scm.QueryAsync("QoffeeAutomat", CancellationToken.None))!.State);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    public async Task Start_and_stop_report_a_service_that_is_not_installed(string action)
    {
        int code = await RunAsync(action);

        Assert.Equal(ServiceExitCodes.NotInstalled, code);
        Assert.Contains("not installed", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_start_is_reported_with_its_reason()
    {
        await RunAsync("install", "--data-dir", _temp.Path);
        _scm.StartFailure = "The service did not respond to the start or control request in a timely fashion.";

        int code = await RunAsync("start");

        Assert.Equal(ServiceExitCodes.Failure, code);
        Assert.Contains("timely fashion", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_shows_the_name_the_account_and_the_start_type()
    {
        await RunAsync("install", "--data-dir", _temp.Path);
        await RunAsync("start");
        _output.GetStringBuilder().Clear();

        int code = await RunAsync("status");

        string text = _output.ToString();
        Assert.Equal(ServiceExitCodes.Success, code);
        Assert.Contains("QoffeeAutomat", text, StringComparison.Ordinal);
        Assert.Contains("Running", text, StringComparison.Ordinal);
        Assert.Contains("LocalSystem", text, StringComparison.Ordinal);
        Assert.Contains("automatic (delayed)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_of_a_service_that_is_not_installed_says_so()
    {
        int code = await RunAsync("status");

        Assert.Equal(ServiceExitCodes.NotInstalled, code);
        Assert.Contains("not installed", _output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("install|--nonsense")]
    [InlineData("install|--name|bad name")]
    [InlineData("install|--name|9lives")]
    [InlineData("install|--ui-port|70000")]
    [InlineData("install|--ui-port|abc")]
    [InlineData("install|--data-dir")]
    public async Task Wrong_arguments_exit_with_the_usage_code_and_change_nothing(string arguments)
    {
        int code = await RunAsync(arguments.Split('|'));

        Assert.Equal(ServiceExitCodes.Usage, code);
        Assert.NotEmpty(_error.ToString());
        Assert.Empty(_scm.Created);
    }

    [Fact]
    public async Task A_missing_action_is_a_usage_error()
    {
        Assert.Equal(ServiceExitCodes.Usage, await RunAsync());
    }

    [Fact]
    public async Task Help_lists_the_actions()
    {
        int code = await RunAsync("--help");

        Assert.Equal(ServiceExitCodes.Success, code);
        foreach (string action in new[] { "install", "uninstall", "start", "stop", "status" })
        {
            Assert.Contains(action, _output.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("C:\\dir with space\\", "\"C:\\dir with space\\\\\"")]
    [InlineData("C:\\plain\\dir", "C:\\plain\\dir")]
    public void Arguments_are_quoted_the_way_the_c_runtime_reads_them_back(string argument, string expected)
    {
        Assert.Equal(expected, ServiceCommand.Quote(argument));
    }

    [Fact]
    public void The_command_line_starts_the_host_executable_with_its_leading_arguments_before_run()
    {
        var context = new ServiceCommandContext(
            [],
            null,
            _ => throw new InvalidOperationException(),
            "C:\\Program Files\\dotnet\\dotnet.exe",
            ["C:\\agent dir\\QoffeeAutomat.Agent.dll"]);

        string commandLine = ServiceCommand.BuildCommandLine(context, null, null, null);

        Assert.Equal("\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\agent dir\\QoffeeAutomat.Agent.dll\" run", commandLine);
    }

    private Task<int> RunAsync(params string[] arguments) => RunCoreAsync(arguments, privileged: true, dataDirectory: null);

    private DataDirectories DirectoriesBelow(string? directory)
    {
        string root = directory ?? Path.Combine(_temp.Path, "default");
        return new DataDirectories(
            root,
            Path.Combine(root, "config"),
            Path.Combine(root, "scripts"),
            Path.Combine(root, "secrets"),
            Path.Combine(root, "data"),
            Path.Combine(root, "logs"));
    }

    private async Task<int> RunCoreAsync(string[] arguments, bool privileged, string? dataDirectory)
    {
        var command = new ServiceCommand(_scm, new DataDirectoryProvisioner(_acl), _eventLog, () => privileged);
        var context = new ServiceCommandContext(arguments, dataDirectory, DirectoriesBelow, Executable, []);
        return await command.RunAsync(context, _output, _error);
    }

    private sealed class FakeEventLog : IEventLogSources
    {
        public List<(string Source, string Log)> Created { get; } = [];

        public List<string> Deleted { get; } = [];

        public bool Exists(string source) => Created.Any(entry => entry.Source == source);

        public void Create(string source, string logName) => Created.Add((source, logName));

        public void Delete(string source) => Deleted.Add(source);
    }

    private sealed class FakeScm : IScmClient
    {
        private readonly Dictionary<string, ServiceInfo> _services = [];

        public List<ServiceDefinition> Created { get; } = [];

        public List<string> Calls { get; } = [];

        public string? StartFailure { get; set; }

        public Task<ServiceInfo?> QueryAsync(string name, CancellationToken cancellationToken)
        {
            return Task.FromResult(_services.TryGetValue(name, out ServiceInfo? info) ? info : null);
        }

        public Task CreateAsync(ServiceDefinition definition, CancellationToken cancellationToken)
        {
            Created.Add(definition);
            Calls.Add("create");
            _services[definition.Name] = new ServiceInfo(
                definition.Name,
                ServiceRunState.Stopped,
                ServiceDefaults.Account,
                StartsAutomatically: true,
                DelayedStart: true,
                definition.CommandLine);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            Calls.Add("delete");
            _services.Remove(name);
            return Task.CompletedTask;
        }

        public Task StartAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add("start");
            if (StartFailure is not null)
            {
                throw new ServiceControlException(StartFailure);
            }

            _services[name] = _services[name] with { State = ServiceRunState.Running };
            return Task.CompletedTask;
        }

        public Task StopAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add("stop");
            _services[name] = _services[name] with { State = ServiceRunState.Stopped };
            return Task.CompletedTask;
        }
    }
}
