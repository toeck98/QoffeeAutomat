using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Sessions;

namespace QoffeeAutomat.Platform.Windows.Tests.Sessions;

/// <summary>
/// Running commands in the user's context through the SessionHost (PLAN.md S06, S11, T303): the agent's bridge and the
/// SessionHost's executor talk through a real named pipe inside the test process. Process runs, PowerShell runs and
/// built-in actions, streamed output, cancellation, timeouts and <c>waitForExit: false</c>.
/// </summary>
[Trait("Category", "SessionExec")]
public sealed class UserContextExecutionTests
{
    private static readonly IReadOnlyDictionary<string, string> NoEnvironment = new Dictionary<string, string>();

    private static ProcessLaunchRequest Request(
        string file = "tool",
        string[]? arguments = null,
        bool waitForExit = true,
        Action<ProcessOutputChunk>? onOutput = null,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null)
    {
        return new ProcessLaunchRequest(
            file,
            arguments ?? ["--x"],
            workingDirectory,
            environment ?? NoEnvironment,
            timeout ?? TimeSpan.FromSeconds(30),
            waitForExit,
            CaptureOutput: true,
            onOutput);
    }

    private static async Task<ReadySession> ReadyAsync(
        IProcessRunner? runner = null,
        IBuiltinActionProvider? builtins = null,
        IPowerShellEngineLocator? locator = null,
        TimeSpan? cancelGrace = null)
    {
        var harness = new SessionHarness(runner, builtins, locator, cancelGrace);
        await harness.StartAsync();
        RunningHost host = harness.StartHost();
        await harness.NextSessionReadyAsync();
        return new ReadySession(harness, host);
    }

    /// <summary>A program that does nothing for the given number of seconds: <c>ping</c> on Windows, <c>sleep</c> elsewhere.</summary>
    private static (string File, string[] Arguments) LongRunning(int seconds)
    {
        string count = seconds.ToString(CultureInfo.InvariantCulture);
        return OperatingSystem.IsWindows()
            ? ("ping.exe", new[] { "-n", count, "127.0.0.1" })
            : ("sleep", new[] { count });
    }

    /// <summary>A program that starts a child that lives for minutes, prints the id of the child and waits for it.</summary>
    private static (string File, string[] Arguments) ChildSpawner()
    {
        if (OperatingSystem.IsWindows())
        {
            return (
                "powershell.exe",
                new[]
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "$p = Start-Process -FilePath ping.exe -ArgumentList '-n','300','127.0.0.1' -PassThru -WindowStyle Hidden; [Console]::Out.WriteLine($p.Id); [Console]::Out.Flush(); Wait-Process -Id $p.Id",
                });
        }

        return ("/bin/sh", new[] { "-c", "sleep 300 & echo $!; wait" });
    }

    private static bool IsAlive(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        string stat = $"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat";
        if (!File.Exists(stat))
        {
            return false;
        }

        string text = File.ReadAllText(stat);
        return text[text.LastIndexOf(')') + 2] is not ('Z' or 'X');
    }

    private static async Task AssertGoneAsync(int pid)
    {
        await TestWait.UntilAsync(() => !IsAlive(pid), $"process {pid.ToString(CultureInfo.InvariantCulture)} to be gone");
    }

    private static async Task<SessionMessage> ReadAsync(Stream stream)
    {
        SessionMessage? message = await SessionFrameCodec.ReadAsync(stream).AsTask().WaitAsync(TestWait.Patience);
        return message ?? throw new InvalidOperationException("The stream ended.");
    }

    [Fact]
    public async Task A_process_run_streams_its_output_in_order_and_reports_the_result_in_the_common_shape()
    {
        var runner = new FakeProcessRunner();
        using ReadySession session = await ReadyAsync(runner);
        var received = new List<ProcessOutputChunk>();

        Task<ExecutionResult> run = session.Harness.Bridge.RunAsync(
            Request(onOutput: chunk =>
            {
                lock (received)
                {
                    received.Add(chunk);
                }
            }),
            CancellationToken.None).AsTask();
        FakeProcessRun process = await runner.WaitForRunAsync();
        process.Emit(ProcessOutputSource.StandardOutput, "one\n");
        process.Emit(ProcessOutputSource.StandardError, "warn\n");
        process.Emit(ProcessOutputSource.StandardOutput, "two\n");
        await TestWait.UntilAsync(() => { lock (received) { return received.Count >= 3; } }, "the output to arrive while the process runs");

        Assert.False(run.IsCompleted);
        process.Finish(5, "one\ntwo\n", "warn\n");
        ExecutionResult result = await run.WaitAsync(TestWait.Patience);

        lock (received)
        {
            Assert.Equal(["one\n", "warn\n", "two\n"], received.Select(chunk => chunk.Text).ToArray());
            Assert.Equal(
                [ProcessOutputSource.StandardOutput, ProcessOutputSource.StandardError, ProcessOutputSource.StandardOutput],
                received.Select(chunk => chunk.Source).ToArray());
        }

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("one\ntwo\n", result.StandardOutput);
        Assert.Equal("warn\n", result.StandardError);
        Assert.Null(result.ErrorCode);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task The_process_request_arrives_unchanged_with_separate_arguments_environment_directory_and_timeout()
    {
        var runner = new FakeProcessRunner(autoFinish: true);
        using ReadySession session = await ReadyAsync(runner);
        string[] arguments = ["a b", "\"quoted\"", "{{Name}}", "$(whoami)", "x; y", string.Empty];
        var environment = new Dictionary<string, string> { ["QA_RUN_ID"] = "run-1", ["QA_VAR_NAME"] = "x y" };

        ExecutionResult result = await session.Harness.Bridge.RunAsync(
            Request("C:\\Tools\\tool.exe", arguments, timeout: TimeSpan.FromSeconds(90), environment: environment, workingDirectory: "C:\\Work"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        ProcessLaunchRequest received = Assert.Single(runner.Runs).Request;
        Assert.Equal("C:\\Tools\\tool.exe", received.FileName);
        Assert.Equal(arguments, received.Arguments.ToArray());
        Assert.Equal("C:\\Work", received.WorkingDirectory);
        Assert.Equal(environment.OrderBy(pair => pair.Key).ToArray(), received.EnvironmentVariables.OrderBy(pair => pair.Key).ToArray());
        Assert.Equal(TimeSpan.FromSeconds(90), received.Timeout);
        Assert.True(received.WaitForExit);
        Assert.True(received.CaptureOutput);
    }

    [Fact]
    public async Task A_program_that_is_not_awaited_is_started_with_waitForExit_false_and_the_answer_comes_at_once()
    {
        var runner = new FakeProcessRunner(autoFinish: true);
        using ReadySession session = await ReadyAsync(runner);

        ExecutionResult result = await session.Harness.Bridge.RunAsync(Request("notepad.exe", waitForExit: false), CancellationToken.None)
            .AsTask().WaitAsync(TestWait.Patience);

        Assert.True(result.Succeeded);
        Assert.False(Assert.Single(runner.Runs).Request.WaitForExit);
    }

    [Fact]
    public async Task A_program_that_cannot_be_started_reports_a_failure_and_no_exit_code_of_the_program()
    {
        using ReadySession session = await ReadyAsync(new ProcessRunner());

        ExecutionResult result = await session.Harness.Bridge.RunAsync(
            Request(Path.Combine(Path.GetTempPath(), "qa-no-such-program-" + Guid.NewGuid().ToString("N"))),
            CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(45));

        Assert.False(result.Succeeded);
        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Contains("Could not start", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_from_the_agent_cancels_the_run_in_the_session()
    {
        var runner = new FakeProcessRunner();
        using ReadySession session = await ReadyAsync(runner);
        using var cancel = new CancellationTokenSource();

        Task<ExecutionResult> run = session.Harness.Bridge.RunAsync(Request(), cancel.Token).AsTask();
        FakeProcessRun process = await runner.WaitForRunAsync();
        Assert.False(process.WasCancelled);
        await cancel.CancelAsync();
        ExecutionResult result = await run.WaitAsync(TestWait.Patience);

        Assert.Equal(ErrorCodes.Cancelled, result.ErrorCode);
        Assert.True(process.WasCancelled);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task A_token_that_is_cancelled_before_the_run_starts_nothing()
    {
        var runner = new FakeProcessRunner(autoFinish: true);
        using ReadySession session = await ReadyAsync(runner);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        ExecutionResult result = await session.Harness.Bridge.RunAsync(Request(), cancel.Token);

        Assert.Equal(ErrorCodes.Cancelled, result.ErrorCode);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task Losing_the_connection_ends_the_run_with_no_session_and_the_session_host_kills_the_process()
    {
        var runner = new FakeProcessRunner();
        using ReadySession session = await ReadyAsync(runner);

        Task<ExecutionResult> run = session.Harness.Bridge.RunAsync(Request(), CancellationToken.None).AsTask();
        FakeProcessRun process = await runner.WaitForRunAsync();
        session.Harness.Manager.GetConnection(SessionHarness.SessionId)!.Dispose();
        ExecutionResult result = await run.WaitAsync(TestWait.Patience);

        Assert.Equal(ErrorCodes.NoSession, result.ErrorCode);
        await TestWait.UntilAsync(() => process.WasCancelled, "the session host to cancel the run of the lost connection");
    }

    [Fact]
    public async Task Several_runs_at_the_same_time_are_kept_apart()
    {
        var runner = new FakeProcessRunner();
        using ReadySession session = await ReadyAsync(runner);

        Task<ExecutionResult> first = session.Harness.Bridge.RunAsync(Request("first"), CancellationToken.None).AsTask();
        Task<ExecutionResult> second = session.Harness.Bridge.RunAsync(Request("second"), CancellationToken.None).AsTask();
        await runner.WaitForRunAsync(2);
        FakeProcessRun firstProcess = runner.Runs.Single(run => run.Request.FileName == "first");
        FakeProcessRun secondProcess = runner.Runs.Single(run => run.Request.FileName == "second");

        secondProcess.Finish(2, "second", string.Empty);
        ExecutionResult secondResult = await second.WaitAsync(TestWait.Patience);
        Assert.False(first.IsCompleted);
        firstProcess.Finish(1, "first", string.Empty);
        ExecutionResult firstResult = await first.WaitAsync(TestWait.Patience);

        Assert.Equal((2, "second"), (secondResult.ExitCode, secondResult.StandardOutput));
        Assert.Equal((1, "first"), (firstResult.ExitCode, firstResult.StandardOutput));
    }

    [Fact]
    public async Task A_session_host_that_does_not_confirm_a_cancellation_is_given_up_after_the_grace_period()
    {
        using var harness = new SessionHarness(cancelGrace: TimeSpan.FromMilliseconds(300));
        await harness.StartAsync();

        // A session host that takes the run and never answers.
        await using var raw = new NamedPipeClientStream(".", harness.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(10_000);
        await SessionFrameCodec.WriteAsync(
            raw,
            new SessionHelloMessage(SessionIpc.ProtocolVersion, SessionRoles.SessionHost, Environment.ProcessId, SessionHarness.SessionId, "0.1.0", "alice"));
        Assert.IsType<SessionHelloMessage>(await ReadAsync(raw));
        await harness.NextSessionReadyAsync();

        using var cancel = new CancellationTokenSource();
        Task<ExecutionResult> run = harness.Bridge.RunAsync(Request(), cancel.Token).AsTask();
        var sent = Assert.IsType<SessionRunMessage>(await ReadAsync(raw));
        await cancel.CancelAsync();
        var cancellation = Assert.IsType<SessionCancelMessage>(await ReadAsync(raw));
        ExecutionResult result = await run.WaitAsync(TestWait.Patience);

        Assert.Equal(sent.RunId, cancellation.RunId);
        Assert.Equal(ErrorCodes.Cancelled, result.ErrorCode);
        Assert.Contains("did not confirm", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PowerShell_of_a_user_command_runs_through_the_wrapper_in_the_session_with_bound_parameters()
    {
        var runner = new FakeProcessRunner();
        using ReadySession session = await ReadyAsync(runner, locator: new FakeEngineLocator());
        const string Evil = "a'b $(evil) \"q\"; x";
        var variables = new Dictionary<string, object> { ["Name"] = Evil, ["Level"] = 40L, ["Ratio"] = 0.5, ["Mute"] = true };
        var invocation = new PowerShellInvocation(
            PowerShellSources.Inline,
            "param($Name, $Level, $Ratio, $Mute) $Name",
            null,
            PowerShellEngines.Auto,
            variables,
            new Dictionary<string, string> { ["QA_RUN_ID"] = "r1", ["QA_VAR_NAME"] = Evil },
            TimeSpan.FromSeconds(30),
            RunAsValues.User);

        Task<ExecutionResult> run = session.Harness.Bridge.RunAsync(invocation, CancellationToken.None).AsTask();
        FakeProcessRun process = await runner.WaitForRunAsync();

        // While the engine runs, its files exist in the private directory of the user's temporary files.
        IReadOnlyDictionary<string, string> environment = process.Request.EnvironmentVariables;
        string parametersPath = environment[PowerShellWrapper.ParametersFileVariable];
        string targetPath = environment[PowerShellWrapper.TargetScriptVariable];
        string parameters = await File.ReadAllTextAsync(parametersPath);
        string script = await File.ReadAllTextAsync(targetPath);
        string workspace = Path.GetDirectoryName(parametersPath)!;

        Assert.Equal("pwsh", process.Request.FileName);
        Assert.Contains(process.Request.Arguments, argument => argument.EndsWith("wrapper.ps1", StringComparison.Ordinal));
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(workspace), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(PowerShellRunner.WorkspacePrefix, Path.GetFileName(workspace), StringComparison.Ordinal);
        Assert.Equal("param($Name, $Level, $Ratio, $Mute) $Name", script);
        Assert.DoesNotContain("evil", script, StringComparison.Ordinal);
        Assert.DoesNotContain(process.Request.Arguments, argument => argument.Contains("evil", StringComparison.Ordinal));
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(Evil)), parameters, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"int\"", parameters, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"float\"", parameters, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"bool\"", parameters, StringComparison.Ordinal);
        Assert.Equal("r1", environment["QA_RUN_ID"]);
        Assert.Equal(Evil, environment["QA_VAR_NAME"]);

        process.Finish(0, "ok\n", string.Empty);
        ExecutionResult result = await run.WaitAsync(TestWait.Patience);

        Assert.True(result.Succeeded);
        Assert.Equal("ok\n", result.StandardOutput);
        Assert.False(Directory.Exists(workspace));
    }

    [Fact]
    public async Task A_script_file_comes_from_the_scripts_directory_that_the_agent_announced()
    {
        var runner = new FakeProcessRunner(autoFinish: true);
        using ReadySession session = await ReadyAsync(runner, locator: new FakeEngineLocator());
        Directory.CreateDirectory(session.Harness.ScriptsDirectory);
        try
        {
            string script = Path.Combine(session.Harness.ScriptsDirectory, "hello.ps1");
            await File.WriteAllTextAsync(script, "param() 'hi'");

            ExecutionResult found = await session.Harness.Bridge.RunAsync(
                new PowerShellInvocation(PowerShellSources.File, null, "hello.ps1", PowerShellEngines.Pwsh, new Dictionary<string, object>(), NoEnvironment, TimeSpan.FromSeconds(30), RunAsValues.User),
                CancellationToken.None);
            ExecutionResult missing = await session.Harness.Bridge.RunAsync(
                new PowerShellInvocation(PowerShellSources.File, null, "missing.ps1", PowerShellEngines.Pwsh, new Dictionary<string, object>(), NoEnvironment, TimeSpan.FromSeconds(30), RunAsValues.User),
                CancellationToken.None);

            Assert.True(found.Succeeded);
            Assert.Equal(
                Path.GetFullPath(script),
                Path.GetFullPath(Assert.Single(runner.Runs).Request.EnvironmentVariables[PowerShellWrapper.TargetScriptVariable]));
            Assert.Equal(ErrorCodes.Internal, missing.ErrorCode);
            Assert.Contains("does not exist", missing.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(session.Harness.ScriptsDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task A_variable_with_an_invalid_name_is_refused_without_starting_anything()
    {
        var runner = new FakeProcessRunner(autoFinish: true);
        using ReadySession session = await ReadyAsync(runner, locator: new FakeEngineLocator());
        var invocation = new PowerShellInvocation(
            PowerShellSources.Inline,
            "param() 1",
            null,
            PowerShellEngines.Auto,
            new Dictionary<string, object> { ["bad name"] = "x" },
            NoEnvironment,
            TimeSpan.FromSeconds(30),
            RunAsValues.User);

        ExecutionResult result = await session.Harness.Bridge.RunAsync(invocation, CancellationToken.None);

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task The_runner_of_the_agent_hands_user_scripts_to_the_session_and_keeps_system_scripts()
    {
        var hostRunner = new FakeProcessRunner(autoFinish: true);
        using ReadySession session = await ReadyAsync(hostRunner, locator: new FakeEngineLocator());
        using var agentTemp = new TempDirectory();
        var agentRunner = new PowerShellRunner(
            new FakeEngineLocator(),
            new PowerShellOptions { TempRoot = agentTemp.Path, UserSession = session.Harness.Bridge });
        var agentLauncher = new RecordingLauncher();
        PowerShellInvocation For(string runAs) => new(
            PowerShellSources.Inline,
            "param() 1",
            null,
            PowerShellEngines.Auto,
            new Dictionary<string, object>(),
            NoEnvironment,
            TimeSpan.FromSeconds(30),
            runAs);

        ExecutionResult user = await agentRunner.RunAsync(For(RunAsValues.User), agentLauncher, CancellationToken.None);

        Assert.True(user.Succeeded);
        Assert.Single(hostRunner.Runs);
        Assert.Equal(0, agentLauncher.Launches);
        Assert.False(Directory.Exists(agentTemp.Path) && Directory.EnumerateFileSystemEntries(agentTemp.Path).Any());

        ExecutionResult system = await agentRunner.RunAsync(For(RunAsValues.System), agentLauncher, CancellationToken.None);

        Assert.True(system.Succeeded);
        Assert.Single(hostRunner.Runs);
        Assert.Equal(1, agentLauncher.Launches);
    }

    [Fact]
    public async Task A_user_script_without_a_session_fails_with_no_session()
    {
        using var harness = new SessionHarness(locator: new FakeEngineLocator());
        await harness.StartAsync();
        var agentRunner = new PowerShellRunner(new FakeEngineLocator(), new PowerShellOptions { UserSession = harness.Bridge });

        ExecutionResult result = await agentRunner.RunAsync(
            new PowerShellInvocation(PowerShellSources.Inline, "param() 1", null, PowerShellEngines.Auto, new Dictionary<string, object>(), NoEnvironment, TimeSpan.FromSeconds(30), RunAsValues.User),
            new RecordingLauncher(),
            CancellationToken.None);

        Assert.Equal(ErrorCodes.NoSession, result.ErrorCode);
    }

    [Fact]
    public async Task A_built_in_action_runs_in_the_session_with_typed_variables()
    {
        var builtins = new FakeBuiltinActions
        {
            Handler = (name, variables, _) => Task.FromResult(new ExecutionResult(0, $"{name}:{variables["Level"]}", string.Empty)),
        };
        using ReadySession session = await ReadyAsync(builtins: builtins);

        ExecutionResult result = await session.Harness.Bridge.RunBuiltinAsync(
            "audio.set_volume",
            new Dictionary<string, object> { ["Level"] = 40L, ["Mute"] = true, ["Name"] = "speakers", ["Ratio"] = 0.5 },
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("audio.set_volume:40", result.StandardOutput);
        (string name, IReadOnlyDictionary<string, object> received) = Assert.Single(builtins.Calls);
        Assert.Equal("audio.set_volume", name);
        Assert.Equal(40L, Assert.IsType<long>(received["Level"]));
        Assert.True(Assert.IsType<bool>(received["Mute"]));
        Assert.Equal("speakers", Assert.IsType<string>(received["Name"]));
        Assert.Equal(0.5, Assert.IsType<double>(received["Ratio"]));
    }

    [Fact]
    public async Task An_unknown_built_in_action_fails_with_bad_request()
    {
        using ReadySession session = await ReadyAsync();

        ExecutionResult result = await session.Harness.Bridge.RunBuiltinAsync(
            "does.not_exist",
            new Dictionary<string, object>(),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(ErrorCodes.BadRequest, result.ErrorCode);
    }

    [Fact]
    public async Task A_built_in_action_that_takes_too_long_ends_with_a_timeout()
    {
        var builtins = new FakeBuiltinActions
        {
            Handler = async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new ExecutionResult(0, string.Empty, string.Empty);
            },
        };
        using ReadySession session = await ReadyAsync(builtins: builtins);

        ExecutionResult result = await session.Harness.Bridge.RunBuiltinAsync(
            "slow.action",
            new Dictionary<string, object>(),
            TimeSpan.FromMilliseconds(300),
            CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(45));

        Assert.Equal(ErrorCodes.Timeout, result.ErrorCode);
    }

    [Fact]
    public async Task A_built_in_action_can_be_cancelled_from_the_agent()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builtins = new FakeBuiltinActions
        {
            Handler = async (_, _, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new ExecutionResult(0, string.Empty, string.Empty);
            },
        };
        using ReadySession session = await ReadyAsync(builtins: builtins);
        using var cancel = new CancellationTokenSource();

        Task<ExecutionResult> run = session.Harness.Bridge.RunBuiltinAsync("slow.action", new Dictionary<string, object>(), TimeSpan.FromMinutes(5), cancel.Token).AsTask();
        await started.Task.WaitAsync(TestWait.Patience);
        await cancel.CancelAsync();
        ExecutionResult result = await run.WaitAsync(TestWait.Patience);

        Assert.Equal(ErrorCodes.Cancelled, result.ErrorCode);
    }

    [Fact]
    public async Task Built_in_actions_of_the_user_go_to_the_session_and_the_others_stay_in_the_service()
    {
        var hostBuiltins = new FakeBuiltinActions { Handler = (name, _, _) => Task.FromResult(new ExecutionResult(0, "session:" + name, string.Empty)) };
        using ReadySession session = await ReadyAsync(builtins: hostBuiltins);
        var inner = new FakeBuiltinActions
        {
            Actions =
            [
                new BuiltinActionDescriptor("power.sleep", "Sleep", RunAsValues.System, []),
                new BuiltinActionDescriptor("audio.set_mute", "Mute", RunAsValues.User, []),
            ],
            Handler = (name, _, _) => Task.FromResult(new ExecutionResult(0, "service:" + name, string.Empty)),
        };
        var actions = new UserContextBuiltinActions(inner, session.Harness.Bridge);
        var none = new Dictionary<string, object>();

        ExecutionResult system = await actions.ExecuteAsync("power.sleep", none, CancellationToken.None);
        ExecutionResult user = await actions.ExecuteAsync("audio.set_mute", none, CancellationToken.None);

        Assert.Equal("service:power.sleep", system.StandardOutput);
        Assert.Equal("session:audio.set_mute", user.StandardOutput);
        Assert.Equal(["power.sleep", "audio.set_mute"], actions.Actions.Select(action => action.Name).ToArray());
    }

    [Fact]
    public async Task A_built_in_action_of_the_user_without_a_session_fails_with_no_session()
    {
        using var harness = new SessionHarness();
        await harness.StartAsync();
        var inner = new FakeBuiltinActions { Actions = [new BuiltinActionDescriptor("audio.set_mute", "Mute", RunAsValues.User, [])] };
        var actions = new UserContextBuiltinActions(inner, harness.Bridge);

        ExecutionResult result = await actions.ExecuteAsync("audio.set_mute", new Dictionary<string, object>(), CancellationToken.None);

        Assert.Equal(ErrorCodes.NoSession, result.ErrorCode);
        Assert.Empty(inner.Calls);
    }

    [WindowsFact]
    public async Task A_user_command_reports_the_interactive_user_and_not_system()
    {
        using ReadySession session = await ReadyAsync(new ProcessRunner());

        ExecutionResult result = await session.Harness.Bridge.RunAsync(
            Request("cmd.exe", ["/c", "echo %USERNAME%"]),
            CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(45));

        Assert.True(result.Succeeded);
        Assert.Equal(Environment.UserName, result.StandardOutput.Trim(), ignoreCase: true);
        Assert.NotEqual("SYSTEM", result.StandardOutput.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_timeout_in_the_session_kills_the_process_and_reports_timeout()
    {
        using ReadySession session = await ReadyAsync(new ProcessRunner());
        (string file, string[] arguments) = LongRunning(60);
        var stopwatch = Stopwatch.StartNew();

        ExecutionResult result = await session.Harness.Bridge.RunAsync(
            Request(file, arguments, timeout: TimeSpan.FromMilliseconds(600)),
            CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(45));

        Assert.Equal(ErrorCodes.Timeout, result.ErrorCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Cancelling_from_the_agent_terminates_the_whole_process_tree_in_the_session()
    {
        using ReadySession session = await ReadyAsync(new ProcessRunner());

        // The program starts a child, reports its process id on standard output and waits for it.
        (string file, string[] arguments) = ChildSpawner();
        var output = new StringBuilder();
        using var cancel = new CancellationTokenSource();

        Task<ExecutionResult> run = session.Harness.Bridge.RunAsync(
            Request(file, arguments, timeout: TimeSpan.FromMinutes(5), onOutput: chunk =>
            {
                if (chunk.Source == ProcessOutputSource.StandardOutput)
                {
                    lock (output)
                    {
                        output.Append(chunk.Text);
                    }
                }
            }),
            cancel.Token).AsTask();
        int child = 0;
        await TestWait.UntilAsync(
            () =>
            {
                lock (output)
                {
                    string text = output.ToString();
                    return text.EndsWith('\n') && int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out child);
                }
            },
            "the child process id",
            TimeSpan.FromSeconds(45));
        Assert.True(IsAlive(child));
        Assert.False(run.IsCompleted);

        await cancel.CancelAsync();
        ExecutionResult result = await run.WaitAsync(TestWait.Patience);

        Assert.Equal(ErrorCodes.Cancelled, result.ErrorCode);
        await AssertGoneAsync(child);
    }

    [Fact]
    public async Task A_program_started_with_waitForExit_false_returns_immediately_while_it_keeps_running()
    {
        using ReadySession session = await ReadyAsync(new ProcessRunner());
        (string file, string[] arguments) = LongRunning(8);
        var stopwatch = Stopwatch.StartNew();

        ExecutionResult result = await session.Harness.Bridge.RunAsync(
            Request(file, arguments, waitForExit: false),
            CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(45));

        Assert.True(result.Succeeded);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"The run took {stopwatch.Elapsed}.");
    }

    /// <summary>The agent and the session host of a test, ready to run commands.</summary>
    private sealed class ReadySession : IDisposable
    {
        public ReadySession(SessionHarness harness, RunningHost host)
        {
            Harness = harness;
            Host = host;
        }

        public SessionHarness Harness { get; }

        public RunningHost Host { get; }

        public void Dispose()
        {
            Host.Dispose();
            Harness.Dispose();
        }
    }
}
