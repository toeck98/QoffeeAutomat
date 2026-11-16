using QoffeeAutomat.Platform.Windows.Service;

namespace QoffeeAutomat.Platform.Windows.Tests.Service;

/// <summary>The configuration that <c>service install</c> gives the service (PLAN.md S11): checked through the arguments of sc.exe.</summary>
[Trait("Category", "Service")]
public sealed class ScmClientTests
{
    private const string QueryConfigOutput = """
        [SC] QueryServiceConfig SUCCESS

        SERVICE_NAME: QoffeeAutomat
                TYPE               : 10  WIN32_OWN_PROCESS
                START_TYPE         : 2   AUTO_START  (DELAYED)
                ERROR_CONTROL      : 1   NORMAL
                BINARY_PATH_NAME   : "C:\Program Files\QoffeeAutomat\QoffeeAutomat.Agent.exe" run
                LOAD_ORDER_GROUP   :
                TAG                : 0
                DISPLAY_NAME       : QoffeeAutomat Agent
                DEPENDENCIES       :
                SERVICE_START_NAME : LocalSystem
        """;

    private static readonly ServiceDefinition Definition = new(
        "QoffeeAutomat",
        "QoffeeAutomat Agent",
        "Lets Home Assistant monitor this PC.",
        "\"C:\\Program Files\\QoffeeAutomat\\QoffeeAutomat.Agent.exe\" run");

    [Fact]
    public void The_service_is_created_as_local_system_with_delayed_automatic_start()
    {
        IReadOnlyList<string> create = ScmClient.CreateCommands(Definition)[0];

        Assert.Equal("create", create[0]);
        Assert.Equal("QoffeeAutomat", create[1]);
        AssertOption(create, "obj=", "LocalSystem");
        AssertOption(create, "start=", "delayed-auto");
        AssertOption(create, "type=", "own");
        AssertOption(create, "DisplayName=", "QoffeeAutomat Agent");
        AssertOption(create, "binPath=", Definition.CommandLine);
    }

    [Fact]
    public void The_command_line_is_one_argument_entry_and_never_joined_with_the_option_name()
    {
        IReadOnlyList<string> create = ScmClient.CreateCommands(Definition)[0];

        Assert.Contains(Definition.CommandLine, create);
        Assert.DoesNotContain(create, entry => entry.StartsWith("binPath=\"", StringComparison.Ordinal));
    }

    [Fact]
    public void The_service_restarts_after_a_failure_and_also_after_a_non_zero_exit_code()
    {
        IReadOnlyList<IReadOnlyList<string>> commands = ScmClient.CreateCommands(Definition);

        IReadOnlyList<string> failure = Assert.Single(commands, command => command[0] == "failure");
        AssertOption(failure, "actions=", "restart/5000/restart/5000/restart/60000");
        AssertOption(failure, "reset=", "86400");

        IReadOnlyList<string> flag = Assert.Single(commands, command => command[0] == "failureflag");
        Assert.Equal("1", flag[2]);
    }

    [Fact]
    public void The_service_gets_a_description()
    {
        IReadOnlyList<string> description = Assert.Single(ScmClient.CreateCommands(Definition), command => command[0] == "description");

        Assert.Equal(Definition.Description, description[2]);
    }

    [Fact]
    public async Task Create_runs_every_command_in_order_and_stops_at_the_first_failure()
    {
        string[] all = ["create", "description", "failure", "failureflag"];
        string[] untilFailure = ["create", "description"];

        var sc = new RecordingSc();
        await new ScmClient(sc).CreateAsync(Definition, CancellationToken.None);
        Assert.Equal(all, sc.Calls.Select(call => call[0]));

        var failing = new RecordingSc { FailOn = "description" };
        var exception = await Assert.ThrowsAsync<ServiceControlException>(() => new ScmClient(failing).CreateAsync(Definition, CancellationToken.None));
        Assert.Contains("exit code 5", exception.Message, StringComparison.Ordinal);
        Assert.Equal(untilFailure, failing.Calls.Select(call => call[0]));
    }

    [Fact]
    public async Task Delete_asks_sc_to_delete_the_named_service()
    {
        var sc = new RecordingSc();

        await new ScmClient(sc).DeleteAsync("QoffeeAutomat", CancellationToken.None);

        string[] expected = ["delete", "QoffeeAutomat"];
        string[] call = Assert.Single(sc.Calls);
        Assert.Equal(expected, call);
    }

    [Fact]
    public void The_configuration_of_the_service_is_read_from_sc_qc()
    {
        ServiceInfo info = ScmClient.ParseConfiguration("QoffeeAutomat", ServiceRunState.Running, QueryConfigOutput);

        Assert.Equal("LocalSystem", info.Account);
        Assert.True(info.StartsAutomatically);
        Assert.True(info.DelayedStart);
        Assert.Equal("\"C:\\Program Files\\QoffeeAutomat\\QoffeeAutomat.Agent.exe\" run", info.CommandLine);
        Assert.Equal(ServiceRunState.Running, info.State);
    }

    [Fact]
    public void A_manual_service_is_not_reported_as_automatic()
    {
        ServiceInfo info = ScmClient.ParseConfiguration("X", ServiceRunState.Stopped, "START_TYPE         : 3   DEMAND_START\nSERVICE_START_NAME : LocalSystem");

        Assert.False(info.StartsAutomatically);
        Assert.False(info.DelayedStart);
    }

    [Fact]
    public void An_automatic_service_without_delay_is_not_reported_as_delayed()
    {
        ServiceInfo info = ScmClient.ParseConfiguration("X", ServiceRunState.Stopped, "START_TYPE         : 2   AUTO_START");

        Assert.True(info.StartsAutomatically);
        Assert.False(info.DelayedStart);
    }

    [WindowsFact]
    public async Task A_service_that_does_not_exist_is_reported_as_not_installed()
    {
        var sc = new RecordingSc();

        ServiceInfo? info = await new ScmClient(sc).QueryAsync("QoffeeAutomatNoSuchService" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);

        Assert.Null(info);
        Assert.Empty(sc.Calls);
    }

    private static void AssertOption(IReadOnlyList<string> arguments, string option, string expected)
    {
        int index = arguments.ToList().IndexOf(option);
        Assert.True(index >= 0, $"{option} is missing");
        Assert.Equal(expected, arguments[index + 1]);
    }

    private sealed class RecordingSc : IScRunner
    {
        public List<string[]> Calls { get; } = [];

        public string? FailOn { get; init; }

        public Task<ScResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add([.. arguments]);
            return Task.FromResult(arguments[0] == FailOn ? new ScResult(5, "Access is denied.") : new ScResult(0, "[SC] SUCCESS"));
        }
    }
}
