using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Execution.Bash;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Linux.Actions;
using QoffeeAutomat.Platform.Linux.Metrics.Session;
using QoffeeAutomat.Platform.Linux.Metrics.SystemMetrics;
using QoffeeAutomat.Platform.Linux.Service;
using QoffeeAutomat.Platform.Linux.Sessions;

namespace QoffeeAutomat.Platform.Linux.Tests.Service;

/// <summary>The Linux platform as the agent registers it: facts about the computer, the session and the service registrations (T802).</summary>
[Trait("Category", "Service")]
public sealed class LinuxPlatformServicesTests
{
    [Fact]
    public async Task The_agent_id_is_created_once_and_kept_across_restarts()
    {
        using var root = new FixtureRoot();
        string file = Path.Combine(root.Root, "var/lib/qoffeeautomat/agent-id.txt");

        using var first = CreateInfo(file, root);
        string id = await first.GetOrCreateAgentIdAsync(CancellationToken.None);
        using var second = CreateInfo(file, root);

        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal(id, await first.GetOrCreateAgentIdAsync(CancellationToken.None));
        Assert.Equal(id, await second.GetOrCreateAgentIdAsync(CancellationToken.None));
        Assert.Equal(id + "\n", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task A_damaged_agent_id_file_is_replaced()
    {
        using var root = new FixtureRoot();
        root.Write("agent-id.txt", "not a guid");

        using var info = CreateInfo(Path.Combine(root.Root, "agent-id.txt"), root);
        string id = await info.GetOrCreateAgentIdAsync(CancellationToken.None);

        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal(id + "\n", await File.ReadAllTextAsync(Path.Combine(root.Root, "agent-id.txt")));
    }

    [Fact]
    public void The_mac_addresses_are_those_of_the_physical_interfaces_in_upper_case()
    {
        using var root = new FixtureRoot();
        root.Write("sys/class/net/enp3s0/address", "aa:bb:cc:dd:ee:01\n");
        root.CreateDirectory("sys/class/net/enp3s0/device");
        root.Write("sys/class/net/wlan0/address", "aa:bb:cc:dd:ee:02\n");
        root.CreateDirectory("sys/class/net/wlan0/device");
        root.Write("sys/class/net/lo/address", "00:00:00:00:00:00\n");
        root.Write("sys/class/net/docker0/address", "02:42:ac:11:00:01\n");
        root.Write("sys/class/net/usb0/address", "00:00:00:00:00:00\n");
        root.CreateDirectory("sys/class/net/usb0/device");
        root.Write("sys/class/net/broken0/address", "not an address\n");
        root.CreateDirectory("sys/class/net/broken0/device");

        using var info = CreateInfo(Path.Combine(root.Root, "agent-id.txt"), root);

        Assert.Equal(["AA:BB:CC:DD:EE:01", "AA:BB:CC:DD:EE:02"], info.MacAddresses);
    }

    [Fact]
    public void A_computer_without_network_interfaces_has_no_mac_addresses()
    {
        using var root = new FixtureRoot();

        using var info = CreateInfo(Path.Combine(root.Root, "agent-id.txt"), root);

        Assert.Empty(info.MacAddresses);
    }

    [Fact]
    public void The_facts_about_the_computer_come_from_the_sources_of_the_system_metrics()
    {
        using var root = new FixtureRoot();
        var sources = new FakeLinuxSources { OsVersion = "Ubuntu 24.04.1 LTS (Linux 6.8.0)", BootId = "0f5b-1" };
        var boot = new LinuxBootTime(new ManualTime(), sources.ReadUptime, sources.ReadKernelBootId());

        using var info = new LinuxPlatformInfo(Path.Combine(root.Root, "agent-id.txt"), sources, boot, root.Root, hostname: "workbench");

        Assert.Equal("workbench", info.Hostname);
        Assert.Equal("linux-0f5b-1", info.BootId);
        Assert.Equal("linux", info.Os.Name);
        Assert.Equal("Ubuntu 24.04.1 LTS (Linux 6.8.0)", info.Os.Version);
        Assert.NotEmpty(info.Os.Architecture);
    }

    [Fact]
    public async Task The_session_state_follows_logind_and_is_never_ready_without_the_session_helper()
    {
        var source = new FakeSessionSource([SystemMetricTestData.Session("2", user: "tim", locked: false)]);
        using var bridge = new LinuxSessionBridge(new LinuxSessionMetricProvider(source), NullLogger.Instance);
        var changes = new List<SessionState>();
        bridge.StateChanged += (_, e) => changes.Add(e.State);

        await bridge.RefreshAsync(CancellationToken.None);
        await bridge.RefreshAsync(CancellationToken.None);

        Assert.Equal(new SessionState(false, "tim", false, false), bridge.State);
        Assert.Equal([new SessionState(false, "tim", false, false)], changes);
    }

    [Fact]
    public async Task A_lock_and_a_logout_are_published_as_changes()
    {
        var source = new FakeSessionSource([SystemMetricTestData.Session("2", user: "tim", locked: false)]);
        using var bridge = new LinuxSessionBridge(new LinuxSessionMetricProvider(source), NullLogger.Instance);
        var changes = new List<SessionState>();
        bridge.StateChanged += (_, e) => changes.Add(e.State);
        await bridge.RefreshAsync(CancellationToken.None);

        source.Sessions = [SystemMetricTestData.Session("2", user: "tim", locked: true)];
        await bridge.RefreshAsync(CancellationToken.None);
        source.Sessions = [];
        await bridge.RefreshAsync(CancellationToken.None);

        Assert.Equal(
            [
                new SessionState(false, "tim", false, false),
                new SessionState(false, "tim", true, false),
                SessionState.None,
            ],
            changes);
    }

    [Fact]
    public async Task The_session_metrics_are_answered_from_the_last_read_and_are_unavailable_without_a_login()
    {
        var source = new FakeSessionSource([SystemMetricTestData.Session("2", user: "tim", locked: true)]);
        using var bridge = new LinuxSessionBridge(new LinuxSessionMetricProvider(source), NullLogger.Instance);
        string[] ids = [SessionMetricIds.Ready, SessionMetricIds.User, SessionMetricIds.Locked];

        IReadOnlyDictionary<string, object?> before = await bridge.SampleUserMetricsAsync(ids, CancellationToken.None);
        await bridge.RefreshAsync(CancellationToken.None);
        IReadOnlyDictionary<string, object?> loggedIn = await bridge.SampleUserMetricsAsync(ids, CancellationToken.None);
        source.Sessions = [];
        await bridge.RefreshAsync(CancellationToken.None);
        IReadOnlyDictionary<string, object?> loggedOut = await bridge.SampleUserMetricsAsync(ids, CancellationToken.None);

        Assert.Null(before[SessionMetricIds.User]);
        Assert.False(Assert.IsType<bool>(loggedIn[SessionMetricIds.Ready]));
        Assert.Equal("tim", loggedIn[SessionMetricIds.User]);
        Assert.True(Assert.IsType<bool>(loggedIn[SessionMetricIds.Locked]));
        Assert.Null(loggedOut[SessionMetricIds.User]);
        Assert.Null(loggedOut[SessionMetricIds.Locked]);
        Assert.Equal(SessionMetricCatalog.AgentSide, bridge.DescribeUserMetrics());
    }

    [Fact]
    public async Task A_command_in_the_users_context_is_refused_with_no_session_without_a_helper()
    {
        using var bridge = new LinuxSessionBridge(new LinuxSessionMetricProvider(new FakeSessionSource([])), NullLogger.Instance);
        var request = new ProcessLaunchRequest("/bin/true", [], null, new Dictionary<string, string>(), TimeSpan.FromSeconds(5));

        ExecutionResult result = await bridge.RunAsync(request, CancellationToken.None);

        Assert.Equal(ErrorCodes.NoSession, result.ErrorCode);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task The_actions_that_belong_to_windows_do_not_exist_on_linux_and_the_others_do()
    {
        var actions = new LinuxBuiltinActions(new FakeCommandRunner());
        var launcher = new LinuxElevatedLauncher();
        var request = new ProcessLaunchRequest("/bin/true", [], null, new Dictionary<string, string>(), TimeSpan.FromSeconds(5));

        Assert.Contains(actions.Actions, action => action.Name == "power.shutdown");
        Assert.DoesNotContain(actions.Actions, action => action.Name is "power.keep_awake" or "audio.set_default_output" or "audio.set_default_input");
        Assert.Equal(ErrorCodes.BadRequest, (await actions.ExecuteAsync("power.keep_awake", new Dictionary<string, object>(), CancellationToken.None)).ErrorCode);
        Assert.Equal(ErrorCodes.ElevationUnavailable, (await launcher.StartAsync(request, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public void The_platform_replaces_the_simulated_registrations_and_keeps_everything_else()
    {
        using var root = new FixtureRoot();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new ManualTime());
        services.AddSingleton<ILinuxSystemSources>(new FakeLinuxSources());
        services.AddSingleton<ILogindSessionSource>(new FakeSessionSource([]));
        services.AddSingleton<ILogindSignalSource>(new FakeSignalSource());
        services.AddSingleton<ILogindInhibitor>(new FakeInhibitor());
        services.AddSingleton<IMetricProvider>(new MarkerMetricProvider());
        services.AddSingleton<IPowerEvents>(new MarkerPowerEvents());
        services.AddSingleton<IPlatformInfo>(new MarkerPlatformInfo());

        services.AddLinuxPlatform(_ => Path.Combine(root.Root, "agent-id.txt"));

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.IsType<LinuxPlatformInfo>(provider.GetRequiredService<IPlatformInfo>());
        Assert.IsType<LinuxPowerEvents>(provider.GetRequiredService<IPowerEvents>());
        Assert.IsType<LinuxSessionBridge>(provider.GetRequiredService<ISessionBridge>());
        Assert.IsType<LinuxBuiltinActions>(provider.GetRequiredService<IBuiltinActionProvider>());
        Assert.IsType<LinuxElevatedLauncher>(provider.GetRequiredService<IElevatedLauncher>());

        // The session helper (T803): the bridge is the user session, the user-context bash runner and the id source of the session.
        Assert.Same(provider.GetRequiredService<ISessionBridge>(), provider.GetRequiredService<ILinuxUserSession>());
        Assert.Same(provider.GetRequiredService<ISessionBridge>(), provider.GetRequiredService<IUserContextBash>());
        Assert.IsType<LinuxScriptAccessGuard>(provider.GetRequiredService<IScriptAccessGuard>());
        CommandPlatformSupport support = provider.GetRequiredService<CommandPlatformSupport>();
        Assert.Equal(PlatformActionSupport.LinuxOs, support.Os);
        Assert.Contains(provider.GetRequiredService<ISessionBridge>().DescribeUserMetrics(), metric => metric.Id == SessionMetricIds.IdleSeconds);
        Assert.IsType<LinuxSystemMetricProvider>(Assert.Single(provider.GetServices<IMetricProvider>()));
        Assert.Contains(provider.GetServices<IHostedService>(), service => service is LinuxPowerEvents);
        Assert.Contains(provider.GetServices<IHostedService>(), service => service is LinuxSessionBridge);
    }

    [Fact]
    public void The_systemd_lifetime_replaces_the_console_lifetime_only_for_a_systemd_service()
    {
        using var application = new FakeApplicationLifetime();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ILinuxSystemSources>(new FakeLinuxSources());
        services.AddSingleton<IHostApplicationLifetime>(application);
        services.AddSingleton<IHostLifetime>(new MarkerLifetime());

        services.AddLinuxPlatform(_ => "/nonexistent/agent-id.txt");

        // The test runner is not a systemd service; if it is run by one (a CI unit), the lifetime is the systemd one.
        Type expected = LinuxPlatformServices.IsRunningAsService() ? typeof(SystemdHostLifetime) : typeof(MarkerLifetime);
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.IsType(expected, provider.GetRequiredService<IHostLifetime>());
    }

    private static LinuxPlatformInfo CreateInfo(string agentIdFile, FixtureRoot root)
    {
        var sources = new FakeLinuxSources { OsVersion = "Test Linux", BootId = "boot-1" };
        var boot = new LinuxBootTime(new ManualTime(), sources.ReadUptime, sources.ReadKernelBootId());
        return new LinuxPlatformInfo(agentIdFile, sources, boot, root.Root, hostname: "test-host");
    }

    private sealed class MarkerMetricProvider : IMetricProvider
    {
        public IReadOnlyList<MetricDescriptor> Describe() => [];

        public ValueTask<IReadOnlyDictionary<string, object?>> SampleAsync(IReadOnlyCollection<string> metricIds, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
    }

    private sealed class MarkerPowerEvents : IPowerEvents
    {
        public event EventHandler<PowerEventArgs>? PowerChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class MarkerPlatformInfo : IPlatformInfo
    {
        public string Hostname => "marker";

        public IReadOnlyList<string> MacAddresses => [];

        public OsInfo Os => new("simulated", "0", "x64");

        public string BootId => "marker";

        public ValueTask<string> GetOrCreateAgentIdAsync(CancellationToken cancellationToken) => ValueTask.FromResult("marker");
    }

    private sealed class MarkerLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
