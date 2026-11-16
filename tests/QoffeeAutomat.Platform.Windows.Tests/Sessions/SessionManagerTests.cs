using System.Globalization;
using System.ServiceProcess;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Platform.Windows.Sessions;

namespace QoffeeAutomat.Platform.Windows.Tests.Sessions;

/// <summary>
/// The session manager (PLAN.md S11, T302): one SessionHost per interactive session, started with the token of the user
/// (a fake of <see cref="ISessionNativeApi"/> stands in for <c>WTSQueryUserToken</c> and <c>CreateProcessAsUser</c>, so the
/// tests need neither SYSTEM nor a service), restarted with a backoff, stopped at logoff.
/// </summary>
[Trait("Category", "SessionIpc")]
public sealed class SessionManagerTests
{
    private static readonly WtsSessionInfo Services = new(0, WtsConnectState.Active, null);
    private static readonly WtsSessionInfo Alice = new(1, WtsConnectState.Active, "alice");
    private static readonly WtsSessionInfo Bob = new(2, WtsConnectState.Disconnected, "bob");
    private static readonly WtsSessionInfo LogonScreen = new(3, WtsConnectState.Connected, null);
    private static readonly WtsSessionInfo Closing = new(4, WtsConnectState.Down, "carol");

    [Fact]
    public async Task One_session_host_is_started_for_each_interactive_session_when_the_agent_starts()
    {
        using var harness = new SessionHarness();
        harness.Native.SetSessions(Services, Alice, Bob, LogonScreen, Closing);

        await harness.StartAsync();
        await TestWait.UntilAsync(() => harness.Native.Launches.Length >= 2, "both session hosts to be started");
        await Task.Delay(200);

        (int SessionId, SessionLaunch Launch)[] launches = harness.Native.Launches;
        Assert.Equal([1, 2], launches.Select(entry => entry.SessionId).Order().ToArray());
        Assert.Equal([1, 2], harness.Manager.SupervisedSessions.Order().ToArray());
        foreach ((int sessionId, SessionLaunch launch) in launches)
        {
            string[] expected = ["--session", sessionId.ToString(CultureInfo.InvariantCulture), "--pipe", harness.PipeName];
            Assert.Equal(SessionHarness.SessionHostPath, launch.ExecutablePath);
            Assert.Equal(expected, launch.Arguments.ToArray());
        }
    }

    [Fact]
    public async Task The_default_pipe_name_is_not_repeated_on_the_command_line()
    {
        using var harness = new SessionHarness();
        harness.Native.SetSessions(Alice);
        using var manager = new WindowsSessionManager(
            harness.Native,
            harness.Server,
            new SessionManagerOptions { SessionHostPath = SessionHarness.SessionHostPath, Delay = harness.Native.NoDelay },
            TimeProvider.System,
            harness.Logger);

        await manager.StartAsync(CancellationToken.None);
        await TestWait.UntilAsync(() => harness.Native.Launches.Length >= 1, "the session host to be started");

        Assert.Equal(["--session", "1"], harness.Native.Launches[0].Launch.Arguments.ToArray());
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_session_host_that_ends_is_started_again_after_a_growing_backoff()
    {
        using var harness = new SessionHarness();
        harness.Native.SetSessions(Alice);
        await harness.StartAsync();

        await TestWait.UntilAsync(() => harness.Native.Processes.Length >= 1, "the first start");
        harness.Native.Processes[0].Exit(1);
        await TestWait.UntilAsync(() => harness.Native.Processes.Length >= 2, "the second start");
        harness.Native.Processes[1].Exit(1);
        await TestWait.UntilAsync(() => harness.Native.Processes.Length >= 3, "the third start");
        harness.Native.Processes[2].Exit(1);
        await TestWait.UntilAsync(() => harness.Native.Processes.Length >= 4, "the fourth start");

        TimeSpan[] expected = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];
        Assert.Equal(expected, harness.Native.Delays.Take(3).ToArray());
    }

    [Fact]
    public async Task A_start_that_fails_is_retried_with_a_backoff_that_stops_growing_at_its_limit()
    {
        using var harness = new SessionHarness();
        harness.Native.SetSessions(Alice);
        harness.Native.LaunchFailure = new SessionLaunchException("nobody is logged on yet");
        await harness.StartAsync();

        await TestWait.UntilAsync(() => harness.Native.Delays.Length >= 9, "nine attempts");
        harness.Native.LaunchFailure = null;
        await TestWait.UntilAsync(() => harness.Native.Launches.Length >= 1, "a start after the failures");

        TimeSpan[] delays = harness.Native.Delays;
        Assert.Equal(TimeSpan.FromSeconds(1), delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(2), delays[1]);
        Assert.Equal(TimeSpan.FromSeconds(4), delays[2]);
        Assert.Equal(TimeSpan.FromSeconds(32), delays[5]);
        Assert.Equal(TimeSpan.FromSeconds(60), delays[6]);
        Assert.Equal(TimeSpan.FromSeconds(60), delays[8]);
        Assert.Contains(harness.Logger.Entries, entry => entry.Message.Contains("nobody is logged on yet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_session_host_that_finds_another_one_running_is_not_counted_as_a_crash()
    {
        using var harness = new SessionHarness();
        harness.Native.SetSessions(Alice);
        await harness.StartAsync();

        await TestWait.UntilAsync(() => harness.Native.Processes.Length >= 1, "the first start");
        harness.Native.Processes[0].Exit(SessionHostExitCodes.AlreadyRunning);
        await TestWait.UntilAsync(() => harness.Native.Processes.Length >= 2, "the second start");

        Assert.Equal(TimeSpan.FromSeconds(30), harness.Native.Delays[0]);
    }

    [Fact]
    public async Task Logging_off_stops_the_supervision_and_kills_the_session_host()
    {
        using var harness = new SessionHarness();
        harness.Native.SetSessions(Alice);
        await harness.StartAsync();
        await TestWait.UntilAsync(() => harness.Native.Processes.Length >= 1, "the first start");

        harness.Native.SetSessions();
        harness.Manager.Handle(SessionChangeReason.SessionLogoff, 1);

        await TestWait.UntilAsync(() => harness.Native.Processes[0].Killed, "the session host to be killed");
        Assert.Empty(harness.Manager.SupervisedSessions);
        await Task.Delay(100);
        Assert.Single(harness.Native.Launches);
    }

    [Fact]
    public async Task Logging_on_starts_a_session_host_for_a_session_that_did_not_exist_at_start()
    {
        using var harness = new SessionHarness();
        await harness.StartAsync();
        Assert.Empty(harness.Native.Launches);

        harness.Native.SetSessions(Alice);
        harness.Manager.Handle(SessionChangeReason.SessionLogon, 1);

        await TestWait.UntilAsync(() => harness.Native.Launches.Length >= 1, "the session host to be started");
        Assert.Equal([1], harness.Manager.SupervisedSessions.ToArray());
    }

    [Fact]
    public async Task Connecting_again_does_not_start_a_second_session_host()
    {
        using var harness = new SessionHarness();
        harness.Native.SetSessions(Alice);
        await harness.StartAsync();
        await TestWait.UntilAsync(() => harness.Native.Launches.Length >= 1, "the first start");

        harness.Manager.Handle(SessionChangeReason.SessionUnlock, 1);
        harness.Manager.Handle(SessionChangeReason.RemoteConnect, 1);
        await Task.Delay(200);

        Assert.Single(harness.Native.Launches);
    }

    [Fact]
    public async Task The_services_session_never_gets_a_session_host()
    {
        using var harness = new SessionHarness();
        harness.Native.SetSessions(Services);
        await harness.StartAsync();

        harness.Manager.Handle(SessionChangeReason.SessionLogon, 0);
        await Task.Delay(200);

        Assert.Empty(harness.Native.Launches);
        Assert.Empty(harness.Manager.SupervisedSessions);
    }

    [Fact]
    public async Task The_supervision_ends_when_the_session_is_gone_after_the_session_host_ended()
    {
        using var harness = new SessionHarness();
        harness.Native.SetSessions(Alice);
        await harness.StartAsync();
        await TestWait.UntilAsync(() => harness.Native.Processes.Length >= 1, "the first start");

        harness.Native.SetSessions();
        harness.Native.Processes[0].Exit(0);

        await TestWait.UntilAsync(() => harness.Manager.SupervisedSessions.Count == 0, "the supervision to end");
        await Task.Delay(100);
        Assert.Single(harness.Native.Launches);
    }

    [Fact]
    public async Task A_missing_session_host_file_and_a_missing_session_do_not_stop_the_agent()
    {
        using var harness = new SessionHarness();

        await harness.StartAsync();
        await harness.StartAsync();

        Assert.Empty(harness.Native.Launches);
        Assert.Contains(harness.Logger.Entries, entry => entry.Message.Contains("does not exist", StringComparison.Ordinal));
        Assert.False(harness.Bridge.State.IsReady);
        await harness.Manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stopping_the_manager_ends_the_supervisors_and_kills_the_session_hosts_that_ignore_shutdown()
    {
        using var harness = new SessionHarness();
        harness.Native.SetSessions(Alice, Bob);
        await harness.StartAsync();
        await TestWait.UntilAsync(() => harness.Native.Processes.Length >= 2, "both starts");

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await harness.Manager.StopAsync(cancel.Token);

        await TestWait.UntilAsync(() => harness.Native.Processes.All(process => process.Killed), "both session hosts to be killed");
        Assert.Empty(harness.Manager.SupervisedSessions);
    }
}
