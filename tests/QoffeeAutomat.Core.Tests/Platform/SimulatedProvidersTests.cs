using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Validation;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Core.Tests.Platform;

[Trait("Category", "Platform")]
public sealed class SimulatedProvidersTests
{
    private static readonly string[] ExpectedActionNames = ["sim.echo", "sim.fail"];
    private static readonly string[] ExpectedEchoProperties = ["Count", "Enabled", "Message", "Ratio"];
    private static readonly string[] ValidKinds = ["sensor", "binary_sensor"];
    private static readonly string[] ValidValueTypes = ["number", "string", "bool", "timestamp", "enum"];

    private static ProcessLaunchRequest Launch(string file = "notepad.exe", params string[] arguments) =>
        new(file, arguments, null, new Dictionary<string, string>(), TimeSpan.FromSeconds(5));

    // ------------------------------------------------------------------
    // Built-in actions
    // ------------------------------------------------------------------

    [Fact]
    public void The_action_provider_lists_sim_echo_and_sim_fail()
    {
        var actions = new SimulatedBuiltinActions();

        Assert.Equal(
            ExpectedActionNames,
            actions.Actions.Select(action => action.Name).Order(StringComparer.Ordinal));
        Assert.All(actions.Actions, action =>
        {
            Assert.Equal(RunAsValues.System, action.RunAs);
            Assert.False(string.IsNullOrWhiteSpace(action.Title));
        });
    }

    [Fact]
    public async Task Sim_echo_returns_its_variables_as_json()
    {
        var actions = new SimulatedBuiltinActions();
        BuiltinActionDescriptor echo = actions.Actions.Single(action => action.Name == SimulatedBuiltinActions.EchoName);
        var variables = new Dictionary<string, JsonElement>
        {
            ["Message"] = JsonSerializer.SerializeToElement("héllo \"quoted\""),
            ["Count"] = JsonSerializer.SerializeToElement(3),
            ["Ratio"] = JsonSerializer.SerializeToElement(1.5),
            ["Enabled"] = JsonSerializer.SerializeToElement(true),
        };

        // The values go through the real binder, so the declared parameters are checked too.
        BindResult bound = VariableBinder.Bind(echo.Parameters, variables);
        Assert.True(bound.IsValid, string.Join("; ", bound.Errors.Select(error => error.Message)));
        ExecutionResult result = await actions.ExecuteAsync(SimulatedBuiltinActions.EchoName, bound.Values, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        using JsonDocument output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("héllo \"quoted\"", output.RootElement.GetProperty("Message").GetString());
        Assert.Equal(3, output.RootElement.GetProperty("Count").GetInt64());
        Assert.Equal(1.5, output.RootElement.GetProperty("Ratio").GetDouble());
        Assert.True(output.RootElement.GetProperty("Enabled").GetBoolean());
        Assert.Equal(ExpectedEchoProperties, output.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task Sim_echo_without_variables_returns_an_empty_object()
    {
        var actions = new SimulatedBuiltinActions();

        ExecutionResult result = await actions.ExecuteAsync(SimulatedBuiltinActions.EchoName, new Dictionary<string, object>(), CancellationToken.None);

        Assert.Equal("{}", result.StandardOutput);
    }

    [Fact]
    public async Task Sim_fail_exits_non_zero()
    {
        var actions = new SimulatedBuiltinActions();
        BuiltinActionDescriptor fail = actions.Actions.Single(action => action.Name == SimulatedBuiltinActions.FailName);

        ExecutionResult plain = await actions.ExecuteAsync(SimulatedBuiltinActions.FailName, new Dictionary<string, object>(), CancellationToken.None);
        BindResult bound = VariableBinder.Bind(fail.Parameters, new Dictionary<string, JsonElement> { ["ExitCode"] = JsonSerializer.SerializeToElement(42) });
        ExecutionResult custom = await actions.ExecuteAsync(SimulatedBuiltinActions.FailName, bound.Values, CancellationToken.None);

        Assert.False(plain.Succeeded);
        Assert.Equal(1, plain.ExitCode);
        Assert.Null(plain.ErrorCode);
        Assert.False(string.IsNullOrEmpty(plain.StandardError));
        Assert.Equal(42, custom.ExitCode);
        Assert.False(custom.Succeeded);
    }

    [Fact]
    public async Task An_unknown_action_is_a_bad_request()
    {
        var actions = new SimulatedBuiltinActions();

        ExecutionResult result = await actions.ExecuteAsync("power.shutdown", new Dictionary<string, object>(), CancellationToken.None);

        Assert.Equal(ErrorCodes.BadRequest, result.ErrorCode);
        Assert.False(result.Succeeded);
    }

    // ------------------------------------------------------------------
    // Session bridge
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_session_bridge_runs_user_commands_while_a_session_is_ready()
    {
        var bridge = new SimulatedSessionBridge(new SimulatedClock());
        var session = bridge;

        ExecutionResult result = await session.RunAsync(Launch("notepad.exe", "a b", "c"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("notepad.exe a b c", result.StandardOutput);
        ProcessLaunchRequest recorded = Assert.Single(bridge.Runs);
        string[] expectedArguments = ["a b", "c"];
        Assert.Equal(expectedArguments, recorded.Arguments);
        Assert.True(session.State.IsReady);
        Assert.Equal("sim-user", session.State.UserName);
    }

    [Fact]
    public async Task The_session_bridge_can_be_made_unavailable()
    {
        var bridge = new SimulatedSessionBridge(new SimulatedClock());
        var session = bridge;

        bridge.SetReady(false);
        ExecutionResult notReady = await session.RunAsync(Launch(), CancellationToken.None);
        bridge.SetReady(true);
        bridge.SimulateLogoff();
        ExecutionResult loggedOff = await session.RunAsync(Launch(), CancellationToken.None);
        bridge.SimulateLogon("alice", isAdministrator: false);
        ExecutionResult back = await session.RunAsync(Launch(), CancellationToken.None);

        Assert.Equal(ErrorCodes.NoSession, notReady.ErrorCode);
        Assert.Equal(ErrorCodes.NoSession, loggedOff.ErrorCode);
        Assert.False(notReady.Succeeded);
        Assert.True(back.Succeeded);
        Assert.Equal(new SessionState(true, "alice", false, false), session.State);
        Assert.Single(bridge.Runs);
    }

    [Fact]
    public void The_state_changed_event_fires_once_per_real_change()
    {
        var bridge = new SimulatedSessionBridge(new SimulatedClock());
        var seen = new List<SessionState>();
        bridge.StateChanged += (_, args) => seen.Add(args.State);

        bridge.SetReady(true); // already ready: no change
        bridge.SetLocked(true);
        bridge.SetLocked(true);
        bridge.SimulateLogoff();

        Assert.Equal(2, seen.Count);
        Assert.True(seen[0].IsLocked);
        Assert.Equal(SessionState.None, seen[1]);
    }

    [Fact]
    public async Task User_metrics_follow_the_session_and_the_clock()
    {
        var clock = new SimulatedClock();
        var bridge = new SimulatedSessionBridge(clock);
        var session = bridge;
        string[] ids = [SimulatedSessionBridge.UserMetricId, SimulatedSessionBridge.LockedMetricId, SimulatedSessionBridge.IdleMetricId];

        clock.Advance(TimeSpan.FromSeconds(42));
        IReadOnlyDictionary<string, object?> idle = await session.SampleUserMetricsAsync(ids, CancellationToken.None);
        bridge.SimulateInput();
        clock.Advance(TimeSpan.FromSeconds(5));
        bridge.SetLocked(true);
        IReadOnlyDictionary<string, object?> afterInput = await session.SampleUserMetricsAsync(ids, CancellationToken.None);
        bridge.SimulateLogoff();
        IReadOnlyDictionary<string, object?> loggedOff = await session.SampleUserMetricsAsync(ids, CancellationToken.None);

        Assert.Equal("sim-user", idle[SimulatedSessionBridge.UserMetricId]);
        Assert.False(Assert.IsType<bool>(idle[SimulatedSessionBridge.LockedMetricId]));
        Assert.Equal(42d, idle[SimulatedSessionBridge.IdleMetricId]);
        Assert.Equal(5d, afterInput[SimulatedSessionBridge.IdleMetricId]);
        Assert.True(Assert.IsType<bool>(afterInput[SimulatedSessionBridge.LockedMetricId]));
        Assert.All(ids, id => Assert.Null(loggedOff[id]));
        Assert.All(session.DescribeUserMetrics(), metric => Assert.Equal("user", metric.Context));
        Assert.Equal(3, session.DescribeUserMetrics().Count);
    }

    // ------------------------------------------------------------------
    // Elevated launcher, power events, service advertiser
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_elevated_launcher_starts_processes_for_an_administrator()
    {
        var bridge = new SimulatedSessionBridge(new SimulatedClock());
        var launcher = new SimulatedElevatedLauncher(bridge);

        ExecutionResult result = await ((IElevatedLauncher)launcher).StartAsync(Launch("regedit.exe"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("regedit.exe", Assert.Single(launcher.Launches).FileName);
    }

    [Fact]
    public async Task The_elevated_launcher_can_report_elevation_unavailable()
    {
        var bridge = new SimulatedSessionBridge(new SimulatedClock());
        var launcher = new SimulatedElevatedLauncher(bridge);

        launcher.ElevationAvailable = false;
        ExecutionResult switchedOff = await launcher.StartAsync(Launch(), CancellationToken.None);
        launcher.ElevationAvailable = true;
        bridge.SimulateLogon("bob", isAdministrator: false);
        ExecutionResult standardUser = await launcher.StartAsync(Launch(), CancellationToken.None);

        Assert.Equal(ErrorCodes.ElevationUnavailable, switchedOff.ErrorCode);
        Assert.Equal(ErrorCodes.ElevationUnavailable, standardUser.ErrorCode);
        Assert.Empty(launcher.Launches);
    }

    [Fact]
    public async Task The_elevated_launcher_needs_a_session()
    {
        var bridge = new SimulatedSessionBridge(new SimulatedClock());
        var launcher = new SimulatedElevatedLauncher(bridge);
        bridge.SimulateLogoff();

        ExecutionResult result = await launcher.StartAsync(Launch(), CancellationToken.None);

        Assert.Equal(ErrorCodes.NoSession, result.ErrorCode);
    }

    [Fact]
    public void Power_events_reach_subscribers()
    {
        var power = new SimulatedPowerEvents();
        var events = power;
        var seen = new List<PowerEventKind>();
        events.PowerChanged += (sender, args) =>
        {
            Assert.Same(power, sender);
            seen.Add(args.Kind);
        };

        power.Simulate(PowerEventKind.Sleep);
        power.Simulate(PowerEventKind.Resume);

        PowerEventKind[] expected = [PowerEventKind.Sleep, PowerEventKind.Resume];
        Assert.Equal(expected, seen);
    }

    [Fact]
    public async Task The_service_advertiser_remembers_what_it_would_announce()
    {
        var advertiser = new SimulatedServiceAdvertiser();
        var service = advertiser;
        var advertisement = new ServiceAdvertisement(
            "_qoffeeautomat._tcp.local.",
            "Gaming-PC",
            8481,
            new Dictionary<string, string> { ["id"] = "abc", ["proto"] = "1" });

        Assert.Null(advertiser.Current);
        await service.StartAsync(advertisement, CancellationToken.None);
        Assert.Same(advertisement, advertiser.Current);
        await service.StartAsync(advertisement with { Port = 9000 }, CancellationToken.None);
        Assert.Equal(9000, advertiser.Current!.Port);
        Assert.Equal(2, advertiser.StartCount);

        await service.StopAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Null(advertiser.Current);
    }

    // ------------------------------------------------------------------
    // Platform info
    // ------------------------------------------------------------------

    [Fact]
    public async Task Platform_info_reports_fixed_facts()
    {
        using var info = new SimulatedPlatformInfo("GAMING-PC", ["AA:BB:CC:00:11:22"]);
        var platform = info;

        Assert.Equal("GAMING-PC", platform.Hostname);
        Assert.Equal("AA:BB:CC:00:11:22", Assert.Single(platform.MacAddresses));
        Assert.Equal("simulated", platform.Os.Name);
        Assert.False(string.IsNullOrEmpty(platform.Os.Version));
        Assert.False(string.IsNullOrEmpty(platform.Os.Architecture));
        Assert.Equal(SimulatedPlatformInfo.DefaultAgentId, await platform.GetOrCreateAgentIdAsync(CancellationToken.None));
    }

    [Fact]
    public void The_boot_id_changes_with_an_operating_system_start_but_not_with_an_agent_restart()
    {
        using var first = new SimulatedPlatformInfo();
        string before = first.BootId;
        string again = first.BootId;

        using var restartedAgent = new SimulatedPlatformInfo(); // a new object stands for a restarted agent
        first.SimulateReboot();

        Assert.Equal(before, again);
        Assert.Equal(before, restartedAgent.BootId);
        Assert.NotEqual(before, first.BootId);
        Assert.StartsWith("sim-boot-", first.BootId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_agent_id_is_persisted_in_a_file()
    {
        using var temp = new TempDirectory();
        string file = temp.Combine("data", "agent-id.txt");
        string created;
        using (var first = new SimulatedPlatformInfo(agentIdFile: file))
        {
            created = await first.GetOrCreateAgentIdAsync(CancellationToken.None);
            Assert.Equal(created, await first.GetOrCreateAgentIdAsync(CancellationToken.None));
        }

        using var second = new SimulatedPlatformInfo(agentIdFile: file);

        Assert.True(Guid.TryParse(created, out _));
        Assert.NotEqual(SimulatedPlatformInfo.DefaultAgentId, created);
        Assert.Equal(created, await second.GetOrCreateAgentIdAsync(CancellationToken.None));
        Assert.Equal(created, (await File.ReadAllTextAsync(file)).Trim());
    }

    [Fact]
    public async Task A_damaged_agent_id_file_is_replaced_by_a_new_id()
    {
        using var temp = new TempDirectory();
        string file = temp.Combine("agent-id.txt");
        await File.WriteAllTextAsync(file, "not a guid");
        using var info = new SimulatedPlatformInfo(agentIdFile: file);

        string id = await info.GetOrCreateAgentIdAsync(CancellationToken.None);

        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal(id, (await File.ReadAllTextAsync(file)).Trim());
    }

    // ------------------------------------------------------------------
    // The platform as a whole
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_simulated_platform_implements_every_core_interface_on_one_clock()
    {
        var clock = new SimulatedClock(DateTimeOffset.UnixEpoch);
        using var platform = new SimulatedPlatform(clock);

        var metrics = platform.Metrics;
        var actions = platform.Actions;
        var session = platform.Session;
        var power = platform.Power;
        var advertiser = platform.Advertiser;
        var launcher = platform.ElevatedLauncher;
        var info = platform.Info;

        Assert.Same(clock, platform.Time);
        Assert.NotEmpty(metrics.Describe());
        Assert.NotEmpty(actions.Actions);
        Assert.True(session.State.IsReady);
        Assert.NotNull(power);
        Assert.NotNull(advertiser);
        Assert.NotNull(launcher);
        Assert.False(string.IsNullOrEmpty(info.BootId));

        // Moving the one clock moves the metrics and the idle time together.
        clock.Advance(TimeSpan.FromSeconds(15));
        IReadOnlyDictionary<string, object?> values = await metrics.SampleAsync([SimulatedMetricProvider.CpuId], CancellationToken.None);
        IReadOnlyDictionary<string, object?> user = await session.SampleUserMetricsAsync([SimulatedSessionBridge.IdleMetricId], CancellationToken.None);
        Assert.Equal(90d, values[SimulatedMetricProvider.CpuId]);
        Assert.Equal(15d, user[SimulatedSessionBridge.IdleMetricId]);
    }

    [Fact]
    public void Metric_ids_of_all_providers_are_unique_and_valid()
    {
        using var platform = new SimulatedPlatform(new SimulatedClock());

        MetricDescriptor[] all = [.. platform.Metrics.Describe(), .. platform.Session.DescribeUserMetrics()];

        Assert.Equal(all.Length, all.Select(metric => metric.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(all, metric =>
        {
            Assert.Matches("^[a-z][a-z0-9_]*(\\.[a-z0-9_-]+)*$", metric.Id);
            Assert.Contains(metric.Kind, ValidKinds);
            Assert.Contains(metric.ValueType, ValidValueTypes);
            if (metric.ValueType == "enum")
            {
                Assert.NotEmpty(metric.Options!);
            }
        });
    }
}
