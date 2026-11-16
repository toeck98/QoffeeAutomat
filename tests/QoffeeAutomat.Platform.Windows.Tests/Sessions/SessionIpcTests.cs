using System.Diagnostics;
using System.IO.Pipes;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Sessions;
using QoffeeAutomat.SessionHost;

namespace QoffeeAutomat.Platform.Windows.Tests.Sessions;

/// <summary>
/// The handshake, the mutual checks and the session state (PLAN.md S09 item 7, S11, T302). The agent's pipe server, the
/// session manager and the bridge talk to the SessionHost's client through a real named pipe inside the test process.
/// </summary>
[Trait("Category", "SessionIpc")]
public sealed class SessionIpcTests
{
    private static ProcessLaunchRequest Request(string file = "tool") =>
        new(file, ["--x", "a b"], null, new Dictionary<string, string>(), TimeSpan.FromSeconds(30));

    private static async Task WaitForWarningAsync(SessionHarness harness, string text)
    {
        await TestWait.UntilAsync(
            () => harness.Logger.Entries.Any(entry => entry.Level == LogLevel.Warning && entry.Message.Contains(text, StringComparison.Ordinal)),
            $"the warning '{text}'");
    }

    [Fact]
    public async Task A_session_host_in_the_installed_image_completes_the_handshake_and_the_session_becomes_ready()
    {
        using var harness = new SessionHarness();
        await harness.StartAsync();
        Assert.False(harness.Bridge.State.IsReady);
        Assert.Equal("alice", harness.Bridge.State.UserName);

        using RunningHost host = harness.StartHost();
        AgentEvent ready = await harness.NextSessionReadyAsync();
        await host.Connected.WaitAsync(TestWait.Patience);

        Assert.Equal(EventTypes.SessionReady, ready.Type);
        Assert.True(harness.Bridge.State.IsReady);
        Assert.True(harness.Bridge.IsReady);
        SessionConnection connection = Assert.IsType<SessionConnection>(harness.Bridge.ActiveConnection);
        Assert.Equal(SessionHarness.SessionId, connection.SessionId);
        Assert.Equal("alice", connection.UserName);
        Assert.False(harness.Bridge.State.IsUserAdministrator);
    }

    [Fact]
    public async Task The_connection_keeps_the_hello_of_the_session_host()
    {
        using var harness = new SessionHarness();
        await harness.StartAsync();

        using RunningHost host = harness.StartHost();
        await harness.NextSessionReadyAsync();

        SessionConnection connection = Assert.IsType<SessionConnection>(harness.Bridge.ActiveConnection);
        Assert.Equal(SessionRoles.SessionHost, connection.Hello.Role);
        Assert.Equal(SessionIpc.ProtocolVersion, connection.Hello.ProtocolVersion);
        Assert.Equal(SessionHarness.SessionId, connection.Hello.SessionId);
    }

    [Fact]
    public async Task A_client_whose_image_is_not_the_installed_session_host_is_rejected()
    {
        using var harness = new SessionHarness();
        harness.Inspector.Peer = new PipePeer(
            Environment.ProcessId,
            SessionHarness.SessionId,
            Path.Combine(Path.GetTempPath(), "somewhere-else", "QoffeeAutomat.SessionHost.exe"));
        await harness.StartAsync();

        using RunningHost host = harness.StartHost();
        await WaitForWarningAsync(harness, "not the installed session helper");

        Assert.Null(harness.Manager.GetConnection(SessionHarness.SessionId));
        Assert.False(harness.Bridge.State.IsReady);
        Assert.False(harness.Ready.Events.TryRead(out _));
        Assert.False(host.Connected.IsCompleted);
    }

    [Fact]
    public async Task A_client_whose_image_is_unknown_is_rejected()
    {
        using var harness = new SessionHarness();
        harness.Inspector.Peer = new PipePeer(Environment.ProcessId, SessionHarness.SessionId, null);
        await harness.StartAsync();

        using RunningHost host = harness.StartHost();
        await WaitForWarningAsync(harness, "not the installed session helper");

        Assert.Null(harness.Manager.GetConnection(SessionHarness.SessionId));
    }

    [Fact]
    public async Task A_client_in_the_services_session_is_rejected()
    {
        using var harness = new SessionHarness();
        harness.Inspector.Peer = new PipePeer(Environment.ProcessId, 0, SessionHarness.SessionHostPath);
        await harness.StartAsync();

        using RunningHost host = harness.StartHost(sessionId: 0);
        await WaitForWarningAsync(harness, "services session");

        Assert.Null(harness.Manager.GetConnection(0));
        Assert.False(harness.Bridge.State.IsReady);
    }

    [Fact]
    public async Task A_client_that_claims_another_session_than_the_one_it_runs_in_is_rejected()
    {
        using var harness = new SessionHarness();
        await harness.StartAsync();

        using RunningHost host = harness.StartHost(sessionId: SessionHarness.SessionId + 1);
        await WaitForWarningAsync(harness, "claims session");

        Assert.Null(harness.Manager.GetConnection(SessionHarness.SessionId));
        Assert.Null(harness.Manager.GetConnection(SessionHarness.SessionId + 1));
        Assert.False(harness.Ready.Events.TryRead(out _));
    }

    [Fact]
    public async Task A_client_that_does_not_start_with_hello_is_dropped()
    {
        using var harness = new SessionHarness();
        await harness.StartAsync();

        await using var raw = new NamedPipeClientStream(".", harness.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(10_000);
        await SessionFrameCodec.WriteAsync(raw, new SessionCancelMessage("r1"));
        await WaitForWarningAsync(harness, "first message was not hello");

        Assert.Null(harness.Manager.GetConnection(SessionHarness.SessionId));
    }

    [Fact]
    public async Task A_client_that_stays_silent_is_dropped_after_the_handshake_timeout()
    {
        using var harness = new SessionHarness(handshakeTimeout: TimeSpan.FromMilliseconds(300));
        await harness.StartAsync();

        await using var raw = new NamedPipeClientStream(".", harness.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(10_000);
        await WaitForWarningAsync(harness, "did not complete the handshake in time");

        Assert.Null(harness.Manager.GetConnection(SessionHarness.SessionId));
    }

    [Fact]
    public async Task A_client_that_speaks_another_protocol_version_is_rejected()
    {
        using var harness = new SessionHarness();
        await harness.StartAsync();

        await using var raw = new NamedPipeClientStream(".", harness.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(10_000);
        await SessionFrameCodec.WriteAsync(
            raw,
            new SessionHelloMessage(SessionIpc.ProtocolVersion + 1, SessionRoles.SessionHost, Environment.ProcessId, SessionHarness.SessionId, "9.9.9", "alice"));
        await WaitForWarningAsync(harness, "speaks protocol");

        Assert.Null(harness.Manager.GetConnection(SessionHarness.SessionId));
    }

    [Fact]
    public async Task A_client_that_claims_to_be_an_agent_is_rejected()
    {
        using var harness = new SessionHarness();
        await harness.StartAsync();

        await using var raw = new NamedPipeClientStream(".", harness.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(10_000);
        await SessionFrameCodec.WriteAsync(
            raw,
            new SessionHelloMessage(SessionIpc.ProtocolVersion, SessionRoles.Agent, Environment.ProcessId, SessionHarness.SessionId, "0.1.0"));
        await WaitForWarningAsync(harness, "is not 'session_host'");

        Assert.Null(harness.Manager.GetConnection(SessionHarness.SessionId));
    }

    [Fact]
    public async Task A_session_host_that_does_not_trust_the_server_never_says_hello()
    {
        using var harness = new SessionHarness();
        harness.Verifier.Refusal = "the server process 4711 is not the agent.";
        await harness.StartAsync();

        using RunningHost host = harness.StartHost();
        await Task.Delay(500);

        Assert.False(host.Connected.IsCompleted);
        Assert.Null(harness.Manager.GetConnection(SessionHarness.SessionId));
        Assert.False(harness.Ready.Events.TryRead(out _));
    }

    [Fact]
    public async Task Session_ready_is_published_once_per_connection_and_a_user_command_sent_directly_after_it_succeeds()
    {
        using var harness = new SessionHarness();
        await harness.StartAsync();

        using RunningHost first = harness.StartHost();
        AgentEvent ready = await harness.NextSessionReadyAsync();
        ExecutionResult result = await harness.Bridge.RunAsync(Request(), CancellationToken.None).AsTask().WaitAsync(TestWait.Patience);

        Assert.Equal(EventTypes.SessionReady, ready.Type);
        Assert.True(result.Succeeded);
        Assert.Equal("done\n", result.StandardOutput);

        // The same connection does not publish the event again.
        await Task.Delay(300);
        Assert.False(harness.Ready.Events.TryRead(out _));

        // When the SessionHost goes away and a new one connects, the event is published once more.
        await first.StopAsync();
        await TestWait.UntilAsync(() => !harness.Bridge.State.IsReady, "the session to stop being ready");
        using RunningHost second = harness.StartHost();
        await harness.NextSessionReadyAsync();
        ExecutionResult again = await harness.Bridge.RunAsync(Request(), CancellationToken.None).AsTask().WaitAsync(TestWait.Patience);

        Assert.True(again.Succeeded);
        await Task.Delay(300);
        Assert.False(harness.Ready.Events.TryRead(out _));
    }

    [Fact]
    public async Task A_session_host_that_replaces_its_predecessor_publishes_session_ready_again()
    {
        using var harness = new SessionHarness();
        await harness.StartAsync();

        using RunningHost first = harness.StartHost();
        await harness.NextSessionReadyAsync();
        using RunningHost second = harness.StartHost();
        await harness.NextSessionReadyAsync();

        // The first one was told to go (replaced) and ends; the second one stays ready.
        Assert.Equal(SessionHostExitCodes.Ok, await first.Exit.WaitAsync(TestWait.Patience));
        Assert.True(harness.Bridge.State.IsReady);
        ExecutionResult result = await harness.Bridge.RunAsync(Request(), CancellationToken.None).AsTask().WaitAsync(TestWait.Patience);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task The_state_changes_are_raised_when_the_session_host_connects_and_disconnects()
    {
        using var harness = new SessionHarness();
        await harness.StartAsync();
        var states = new List<SessionState>();
        harness.Bridge.StateChanged += (_, e) =>
        {
            lock (states)
            {
                states.Add(e.State);
            }
        };

        using RunningHost host = harness.StartHost();
        await harness.NextSessionReadyAsync();
        await host.StopAsync();
        await TestWait.UntilAsync(() => { lock (states) { return states.Count >= 2; } }, "the state to change twice");

        lock (states)
        {
            Assert.True(states[0].IsReady);
            Assert.Equal("alice", states[0].UserName);
            Assert.False(states[1].IsReady);
            Assert.Equal("alice", states[1].UserName);
        }
    }

    [Fact]
    public async Task Without_a_session_host_user_commands_fail_with_no_session_and_nothing_else_breaks()
    {
        using var harness = new SessionHarness();
        await harness.StartAsync();

        ExecutionResult result = await harness.Bridge.RunAsync(Request(), CancellationToken.None);
        IReadOnlyDictionary<string, object?> metrics = await harness.Bridge.SampleUserMetricsAsync(["session.idle_time"], CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ErrorCodes.NoSession, result.ErrorCode);
        Assert.Null(metrics["session.idle_time"]);
        Assert.False(harness.Bridge.State.IsReady);
        Assert.Null(harness.Bridge.ActiveConnection);
    }

    [Fact]
    public async Task Nobody_logged_in_is_a_normal_state()
    {
        using var harness = new SessionHarness();
        harness.Monitor.Handle(SessionChangeReason.SessionLogoff, SessionHarness.SessionId);
        harness.Lookup.Console = null;
        harness.Lookup.User = null;
        await harness.StartAsync();

        Assert.Equal(SessionState.None, harness.Bridge.State);
        ExecutionResult result = await harness.Bridge.RunAsync(Request(), CancellationToken.None);
        Assert.Equal(ErrorCodes.NoSession, result.ErrorCode);
    }

    [Fact]
    public async Task Only_the_session_host_of_the_active_console_session_makes_the_session_ready()
    {
        using var harness = new SessionHarness();
        await harness.StartAsync();
        harness.Lookup.Console = SessionHarness.SessionId + 2;

        using RunningHost host = harness.StartHost();
        await host.Connected.WaitAsync(TestWait.Patience);
        await TestWait.UntilAsync(() => harness.Manager.GetConnection(SessionHarness.SessionId) is not null, "the connection");

        Assert.False(harness.Bridge.State.IsReady);
        Assert.Equal(ErrorCodes.NoSession, (await harness.Bridge.RunAsync(Request(), CancellationToken.None)).ErrorCode);

        harness.Lookup.Console = SessionHarness.SessionId;

        Assert.True(harness.Bridge.State.IsReady);
        Assert.True((await harness.Bridge.RunAsync(Request(), CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task Logging_off_ends_the_session_host_and_resets_the_ready_state()
    {
        using var harness = new SessionHarness();
        await harness.StartAsync();
        using RunningHost host = harness.StartHost();
        await harness.NextSessionReadyAsync();

        harness.Monitor.Handle(SessionChangeReason.SessionLogoff, SessionHarness.SessionId);
        harness.Manager.Handle(SessionChangeReason.SessionLogoff, SessionHarness.SessionId);

        Assert.Equal(SessionHostExitCodes.Ok, await host.Exit.WaitAsync(TestWait.Patience));
        await TestWait.UntilAsync(() => !harness.Bridge.State.IsReady, "the session to stop being ready");
        Assert.Null(harness.Manager.GetConnection(SessionHarness.SessionId));
    }

    [Fact]
    public async Task Stopping_the_manager_tells_the_session_hosts_to_end()
    {
        using var harness = new SessionHarness();
        await harness.StartAsync();
        using RunningHost host = harness.StartHost();
        await harness.NextSessionReadyAsync();

        await harness.Manager.StopAsync(CancellationToken.None);

        Assert.Equal(SessionHostExitCodes.Ok, await host.Exit.WaitAsync(TestWait.Patience));
        Assert.Empty(harness.Manager.SupervisedSessions);
    }

    [Fact]
    public async Task A_session_host_reconnects_when_the_connection_is_lost()
    {
        using var harness = new SessionHarness();
        await harness.StartAsync();
        using RunningHost host = harness.StartHost();
        await harness.NextSessionReadyAsync();

        // The agent drops the connection (as a restart of the service would); the session host connects again.
        harness.Manager.GetConnection(SessionHarness.SessionId)!.Dispose();
        await TestWait.UntilAsync(() => !harness.Bridge.State.IsReady, "the session to stop being ready");
        await harness.NextSessionReadyAsync();

        Assert.True(harness.Bridge.State.IsReady);
        Assert.False(host.Exit.IsCompleted);
    }

    [WindowsFact]
    public async Task The_native_inspector_reports_the_process_the_session_and_the_image_of_the_client()
    {
        string name = "qa-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(10_000)).WaitAsync(TestWait.Patience);

        PipePeer peer = new NativePipePeerInspector().Inspect(server);

        using Process current = Process.GetCurrentProcess();
        Assert.Equal(current.Id, peer.ProcessId);
        Assert.Equal(current.SessionId, peer.SessionId);
        Assert.True(ImagePaths.AreSame(peer.ImagePath, Environment.ProcessPath));
        Assert.False(ImagePaths.AreSame(peer.ImagePath, SessionHarness.SessionHostPath));
    }

    [Theory]
    [InlineData(1, WtsConnectState.Active, "alice", true)]
    [InlineData(2, WtsConnectState.Disconnected, "bob", true)]
    [InlineData(3, WtsConnectState.Connected, "carol", true)]
    [InlineData(0, WtsConnectState.Active, "SYSTEM", false)]
    [InlineData(4, WtsConnectState.Connected, null, false)]
    [InlineData(5, WtsConnectState.Active, "  ", false)]
    [InlineData(6, WtsConnectState.Down, "dave", false)]
    [InlineData(7, WtsConnectState.Listen, "erin", false)]
    [InlineData(8, WtsConnectState.Idle, "frank", false)]
    public void Only_a_session_with_a_user_that_is_not_session_zero_is_interactive(int id, WtsConnectState state, string? user, bool expected)
    {
        Assert.Equal(expected, new WtsSessionInfo(id, state, user).IsInteractive);
    }

    [Fact]
    public void The_session_host_command_line_is_built_with_quoting_that_the_c_runtime_reads_back()
    {
        string line = WtsSessionNativeApi.BuildCommandLine(
            "C:\\Program Files\\QoffeeAutomat\\QoffeeAutomat.SessionHost.exe",
            ["--session", "3", "plain", "with space", "say \"hi\"", "ends with slash\\", string.Empty, "C:\\dir\\"]);

        Assert.Equal(
            "\"C:\\Program Files\\QoffeeAutomat\\QoffeeAutomat.SessionHost.exe\" --session 3 plain \"with space\" \"say \\\"hi\\\"\" \"ends with slash\\\\\" \"\" C:\\dir\\",
            line);
    }

    [Fact]
    public void The_command_line_options_of_the_session_host_are_parsed_strictly()
    {
        SessionHostOptions? defaults = SessionHostOptions.Parse([], 5, "C:\\Install", out string? noError);
        SessionHostOptions? full = SessionHostOptions.Parse(["--session", "9", "--pipe", "test-pipe", "--agent-image", "C:\\x\\agent.exe"], 5, "C:\\Install", out _);

        Assert.Null(noError);
        Assert.Equal(5, defaults!.SessionId);
        Assert.Equal(SessionIpc.PipeName, defaults.PipeName);
        Assert.Equal(Path.Combine("C:\\Install", SessionIpc.AgentFileName), defaults.AgentImagePath);
        Assert.Equal(new SessionHostOptions(9, "test-pipe", "C:\\x\\agent.exe"), full);

        foreach (string[] wrong in new string[][]
        {
            ["--session"],
            ["--session", "zero"],
            ["--session", "0"],
            ["--session", "-4"],
            ["--pipe", "\\\\.\\pipe\\evil"],
            ["--pipe", "a/b"],
            ["--unknown", "x"],
        })
        {
            Assert.Null(SessionHostOptions.Parse(wrong, 5, "C:\\Install", out string? error));
            Assert.False(string.IsNullOrEmpty(error));
        }
    }
}
