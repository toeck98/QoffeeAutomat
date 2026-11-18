using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Execution.Bash;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Platform;
using static QoffeeAutomat.Core.Tests.Execution.Bash.BashTestSupport;

namespace QoffeeAutomat.Core.Tests.Execution.Bash;

/// <summary>
/// The runner and the handler of the <c>bash</c> action without a real shell (PLAN.md S06, S17, D-012, T803): what is put on the
/// command line, into the environment and into the temporary files, that the files are private and gone afterwards, and how script files,
/// the script guard and the user session are treated.
/// </summary>
[Trait("Category", "Bash")]
public sealed class BashRunnerTests
{
    private static async Task<ExecutionResult> RunAsync(BashHarness harness, BashInvocation invocation)
    {
        return await harness.Runner.RunAsync(invocation, harness.Launcher, CancellationToken.None);
    }

    [Fact]
    public async Task The_shell_gets_the_script_and_the_values_only_as_arguments_and_environment()
    {
        using var harness = new BashHarness();
        List<BashLaunchSnapshot> snapshots = harness.CaptureSnapshots();
        string[] arguments = [Hostile, "42", "true"];
        var environment = new Dictionary<string, string> { ["QA_RUN_ID"] = "run1", ["QA_VAR_VALUE"] = Hostile };

        ExecutionResult result = await RunAsync(harness, harness.Invocation(code: "printf '%s' \"$1\"", arguments: arguments, environment: environment));

        Assert.True(result.Succeeded);
        (string runAs, ProcessLaunchRequest request) = Assert.Single(harness.Launcher.Launches);
        BashLaunchSnapshot snapshot = Assert.Single(snapshots);
        Assert.Equal(RunAsValues.System, runAs);
        Assert.Equal(BashHarness.FakeShell, request.FileName);
        string[] expected = ["--noprofile", "--norc", snapshot.ScriptPath, .. arguments];
        Assert.Equal(expected, request.Arguments);
        Assert.True(request.WaitForExit);
        Assert.True(request.CaptureOutput);
        Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);

        // No value reaches the script text, and none reaches the options of the shell.
        Assert.Equal("printf '%s' \"$1\"", snapshot.ScriptText);
        Assert.All(request.Arguments.Take(3), argument => Assert.DoesNotContain("whoami", argument, StringComparison.Ordinal));
        Assert.DoesNotContain("whoami", snapshot.ScriptText, StringComparison.Ordinal);

        // The values travel as arguments and as environment variables.
        Assert.Equal(Hostile, request.EnvironmentVariables["QA_VAR_VALUE"]);
        Assert.Equal("run1", request.EnvironmentVariables["QA_RUN_ID"]);
        Assert.Equal(Path.Combine(snapshot.RunDirectory, BashRunner.InlineFileName), snapshot.ScriptPath);
    }

    [Fact]
    public async Task Inline_code_is_written_to_a_private_script_file_and_run_from_there()
    {
        using var harness = new BashHarness();
        List<BashLaunchSnapshot> snapshots = harness.CaptureSnapshots();
        const string code = "set -eu\necho \"héllo $1\"";

        await RunAsync(harness, harness.Invocation(code: code, arguments: ["world"]));

        BashLaunchSnapshot snapshot = Assert.Single(snapshots);
        Assert.Equal(code, snapshot.ScriptText);
        Assert.Equal([BashRunner.InlineFileName], snapshot.FilesInRunDirectory);
        Assert.Equal(harness.TempRoot, Path.GetDirectoryName(snapshot.RunDirectory));
        Assert.StartsWith(BashRunner.WorkspacePrefix, Path.GetFileName(snapshot.RunDirectory), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, snapshot.FileMode);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, snapshot.DirectoryMode);
        }
    }

    [Fact]
    public async Task The_script_file_has_no_byte_order_mark_and_windows_line_endings_become_line_feeds()
    {
        using var harness = new BashHarness();
        List<BashLaunchSnapshot> snapshots = harness.CaptureSnapshots();

        await RunAsync(harness, harness.Invocation(code: "echo one\r\necho two\r\n"));

        BashLaunchSnapshot snapshot = Assert.Single(snapshots);
        Assert.NotEqual(0xEF, snapshot.ScriptBytes[0]);
        Assert.Equal("echo one\necho two\n", snapshot.ScriptText);
    }

    [Fact]
    public async Task Each_run_gets_its_own_directory()
    {
        using var harness = new BashHarness();
        List<BashLaunchSnapshot> snapshots = harness.CaptureSnapshots();

        await RunAsync(harness, harness.Invocation());
        await RunAsync(harness, harness.Invocation());

        Assert.Equal(2, snapshots.Count);
        Assert.NotEqual(snapshots[0].RunDirectory, snapshots[1].RunDirectory);
    }

    [Fact]
    public async Task Temporary_files_are_deleted_after_a_successful_and_after_a_failing_run()
    {
        using var harness = new BashHarness();

        ExecutionResult success = await RunAsync(harness, harness.Invocation(arguments: ["secret-value"]));
        harness.Launcher.OnLaunch = _ => new ExecutionResult(3, string.Empty, "boom");
        ExecutionResult failure = await RunAsync(harness, harness.Invocation());

        Assert.True(success.Succeeded);
        Assert.Equal(3, failure.ExitCode);
        Assert.Empty(harness.RunDirectories);
    }

    [Fact]
    public async Task Temporary_files_are_deleted_when_the_launch_fails_with_an_exception()
    {
        using var harness = new BashHarness();
        harness.Launcher.OnLaunch = _ => throw new InvalidOperationException("the launcher broke");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await RunAsync(harness, harness.Invocation()));

        Assert.Empty(harness.RunDirectories);
    }

    [Fact]
    public async Task BASH_ENV_is_emptied_so_that_the_shell_reads_no_startup_file_unless_the_invocation_sets_it()
    {
        using var harness = new BashHarness();

        await RunAsync(harness, harness.Invocation());
        await RunAsync(harness, harness.Invocation(environment: new Dictionary<string, string> { ["BASH_ENV"] = "/own/file" }));

        Assert.Equal(string.Empty, harness.Launcher.Launches[0].Request.EnvironmentVariables["BASH_ENV"]);
        Assert.Equal("/own/file", harness.Launcher.Launches[1].Request.EnvironmentVariables["BASH_ENV"]);
    }

    [Fact]
    public async Task A_value_with_a_NUL_character_is_refused_before_anything_is_started()
    {
        using var harness = new BashHarness();

        ExecutionResult argument = await RunAsync(harness, harness.Invocation(arguments: ["a\0b"]));
        ExecutionResult environment = await RunAsync(harness, harness.Invocation(environment: new Dictionary<string, string> { ["QA_VAR_X"] = "a\0b" }));

        Assert.Equal(ErrorCodes.InvalidVars, argument.ErrorCode);
        Assert.Equal(ErrorCodes.InvalidVars, environment.ErrorCode);
        Assert.DoesNotContain("a\0b", argument.StandardError + environment.StandardError, StringComparison.Ordinal);
        Assert.Empty(harness.Launcher.Launches);
        Assert.Empty(harness.RunDirectories);
    }

    [Fact]
    public async Task A_missing_shell_is_a_failure_with_the_reason_and_nothing_is_written()
    {
        using var harness = new BashHarness(FakeBashLocator.Missing("bash was not found."));

        ExecutionResult result = await RunAsync(harness, harness.Invocation());

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Contains("bash was not found", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(harness.Launcher.Launches);
        Assert.Empty(harness.RunDirectories);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_inline_script_without_code_is_refused(string? code)
    {
        using var harness = new BashHarness();

        ExecutionResult result = await RunAsync(harness, harness.Invocation(code: code));

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Empty(harness.Launcher.Launches);
    }

    [Fact]
    public async Task An_unknown_source_is_refused()
    {
        using var harness = new BashHarness();

        ExecutionResult result = await RunAsync(harness, harness.Invocation(source: "remote"));

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Empty(harness.Launcher.Launches);
    }

    [Fact]
    public async Task A_script_file_runs_from_the_scripts_directory_without_a_copy()
    {
        using var harness = new BashHarness();
        string path = harness.WriteScript("backup.sh", "echo backup\n");

        ExecutionResult result = await RunAsync(harness, harness.Invocation(BashSources.File, code: null, script: "backup.sh", arguments: ["one"]));

        Assert.True(result.Succeeded);
        ProcessLaunchRequest request = Assert.Single(harness.Launcher.Launches).Request;
        Assert.Equal(["--noprofile", "--norc", Path.GetFullPath(path), "one"], request.Arguments);
        Assert.Empty(harness.RunDirectories);
    }

    [Theory]
    [InlineData("../outside.sh")]
    [InlineData("sub/inner.sh")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("a:b.sh")]
    public async Task A_script_name_that_is_not_a_plain_file_name_is_refused(string name)
    {
        using var harness = new BashHarness();
        File.WriteAllText(Path.Combine(harness.Temp.FullPath, "outside.sh"), "echo no\n");

        ExecutionResult result = await RunAsync(harness, harness.Invocation(BashSources.File, code: null, script: name));

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Empty(harness.Launcher.Launches);
    }

    [Fact]
    public async Task A_script_file_that_does_not_exist_is_refused_with_its_name()
    {
        using var harness = new BashHarness();

        ExecutionResult result = await RunAsync(harness, harness.Invocation(BashSources.File, code: null, script: "missing.sh"));

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Contains("missing.sh", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(harness.Launcher.Launches);
    }

    [Fact]
    public async Task Without_a_scripts_directory_file_scripts_cannot_run()
    {
        using var harness = new BashHarness(options: _ => new BashOptions());

        ExecutionResult result = await RunAsync(harness, harness.Invocation(BashSources.File, code: null, script: "a.sh"));

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Empty(harness.Launcher.Launches);
    }

    [Fact]
    public async Task The_script_guard_is_asked_for_a_system_script_and_can_refuse_it()
    {
        var guard = new FakeGuard((_, _) => ScriptAccessDecision.Refused("Others can write this file."));
        using var harness = new BashHarness(options: root => new BashOptions
        {
            ScriptsDirectory = Path.Combine(root, "scripts"),
            TempRoot = Path.Combine(root, "run"),
            ScriptGuard = guard,
        });
        string path = harness.WriteScript("danger.sh", "echo hi\n");

        ExecutionResult result = await RunAsync(harness, harness.Invocation(BashSources.File, code: null, script: "danger.sh"));

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Contains("Others can write this file.", result.StandardError, StringComparison.Ordinal);
        (string guarded, string runAs) = Assert.Single(guard.Calls);
        Assert.Equal(Path.GetFullPath(path), guarded);
        Assert.Equal(RunAsValues.System, runAs);
        Assert.Empty(harness.Launcher.Launches);
    }

    [Fact]
    public async Task The_script_guard_is_not_asked_for_a_script_that_runs_as_the_user()
    {
        var guard = new FakeGuard((_, _) => ScriptAccessDecision.Refused("never"));
        using var harness = new BashHarness(options: root => new BashOptions
        {
            ScriptsDirectory = Path.Combine(root, "scripts"),
            TempRoot = Path.Combine(root, "run"),
            ScriptGuard = guard,
        });
        harness.WriteScript("mine.sh", "echo hi\n");

        ExecutionResult result = await RunAsync(harness, harness.Invocation(BashSources.File, code: null, script: "mine.sh", runAs: RunAsValues.User));

        Assert.True(result.Succeeded);
        Assert.Empty(guard.Calls);
    }

    [Fact]
    public async Task A_user_script_file_goes_to_the_session_helper_as_inline_code_because_the_helper_cannot_read_the_scripts_directory()
    {
        var user = new RecordingUserBash();
        using var harness = new BashHarness(options: root => new BashOptions
        {
            ScriptsDirectory = Path.Combine(root, "scripts"),
            TempRoot = Path.Combine(root, "run"),
            UserSession = user,
        });
        harness.WriteScript("greet.sh", "echo \"hello $1\"\n");

        ExecutionResult result = await RunAsync(
            harness,
            harness.Invocation(BashSources.File, code: null, script: "greet.sh", arguments: [Hostile], runAs: RunAsValues.User));

        Assert.Equal("in the session", result.StandardOutput);
        BashInvocation sent = Assert.Single(user.Invocations);
        Assert.Equal(BashSources.Inline, sent.Source);
        Assert.Equal("echo \"hello $1\"\n", sent.Code);
        Assert.Null(sent.Script);
        Assert.Equal([Hostile], sent.Arguments);
        Assert.Empty(harness.Launcher.Launches);
        Assert.Empty(harness.RunDirectories);
    }

    [Fact]
    public async Task An_inline_user_script_goes_to_the_session_helper_unchanged()
    {
        var user = new RecordingUserBash();
        using var harness = new BashHarness(options: root => new BashOptions
        {
            ScriptsDirectory = Path.Combine(root, "scripts"),
            TempRoot = Path.Combine(root, "run"),
            UserSession = user,
        });

        await RunAsync(harness, harness.Invocation(code: "notify-send \"$1\"", arguments: ["text"], runAs: RunAsValues.User));

        BashInvocation sent = Assert.Single(user.Invocations);
        Assert.Equal("notify-send \"$1\"", sent.Code);
        Assert.Equal(["text"], sent.Arguments);
        Assert.Empty(harness.Launcher.Launches);
    }

    [Fact]
    public async Task A_system_run_never_goes_to_the_session_helper()
    {
        var user = new RecordingUserBash();
        using var harness = new BashHarness(options: root => new BashOptions
        {
            ScriptsDirectory = Path.Combine(root, "scripts"),
            TempRoot = Path.Combine(root, "run"),
            UserSession = user,
        });

        await RunAsync(harness, harness.Invocation(runAs: RunAsValues.System));

        Assert.Empty(user.Invocations);
        Assert.Single(harness.Launcher.Launches);
    }

    [Fact]
    public async Task A_script_larger_than_a_megabyte_is_refused()
    {
        using var harness = new BashHarness();

        ExecutionResult result = await RunAsync(harness, harness.Invocation(code: new string('#', BashRunner.MaxScriptBytes + 1)));

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Empty(harness.Launcher.Launches);
    }

    [Fact]
    public void Directories_of_crashed_runs_are_swept_by_age()
    {
        using var temp = new TempDirectory();
        string old = Directory.CreateDirectory(temp.Combine(BashRunner.WorkspacePrefix + "old")).FullName;
        string fresh = Directory.CreateDirectory(temp.Combine(BashRunner.WorkspacePrefix + "fresh")).FullName;
        string other = Directory.CreateDirectory(temp.Combine("not-ours")).FullName;
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        Directory.SetLastWriteTimeUtc(old, now.UtcDateTime.AddDays(-5));
        Directory.SetLastWriteTimeUtc(fresh, now.UtcDateTime.AddHours(-1));
        Directory.SetLastWriteTimeUtc(other, now.UtcDateTime.AddDays(-5));

        int removed = BashRunner.SweepStale(temp.FullPath, TimeSpan.FromHours(48), now);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public void The_positional_arguments_follow_the_order_of_the_parameters_and_an_absent_value_is_empty()
    {
        IReadOnlyList<ParameterDefinition> parameters = [Text("Name"), Whole("Count"), Real("Ratio"), Flag("Flag"), Text("Optional"), Flag("Off")];
        Dictionary<string, object> variables = Vars(("Ratio", 1.5), ("Name", "x"), ("Flag", true), ("Count", 42L), ("Off", false));

        IReadOnlyList<string> arguments = BashActionHandler.PositionalArguments(parameters, variables);

        Assert.Equal(["x", "42", "1.5", "true", string.Empty, "false"], arguments);
    }

    [Fact]
    public void A_command_without_parameters_has_no_positional_arguments()
    {
        Assert.Empty(BashActionHandler.PositionalArguments(null, new Dictionary<string, object>()));
    }

    [Fact]
    public async Task The_handler_passes_the_values_as_positional_arguments_and_as_environment_and_keeps_the_run_context()
    {
        using var harness = new BashHarness();
        CommandDocument command = Command(
            new BashAction(BashSources.Inline, Code: "echo \"$1 $2\""),
            runAs: RunAsValues.System,
            captureOutput: false,
            parameters: [Text("Name"), Whole("Count")]);
        ActionContext context = Context(command, harness.Launcher, Vars(("Name", Hostile), ("Count", 3L)), TimeSpan.FromSeconds(7));

        ExecutionResult result = await harness.Handler.ExecuteAsync(context, CancellationToken.None);

        Assert.True(result.Succeeded);
        ProcessLaunchRequest request = Assert.Single(harness.Launcher.Launches).Request;
        Assert.Equal([Hostile, "3"], request.Arguments.Skip(3));
        Assert.Equal(Hostile, request.EnvironmentVariables["QA_VAR_NAME"]);
        Assert.Equal("3", request.EnvironmentVariables["QA_VAR_COUNT"]);
        Assert.Equal(TimeSpan.FromSeconds(7), request.Timeout);
        Assert.False(request.CaptureOutput);
    }

    [Fact]
    public async Task The_handler_refuses_a_command_with_another_action_type()
    {
        using var harness = new BashHarness();
        ActionContext context = Context(Command(new ProcessAction("/bin/true")), harness.Launcher);

        ExecutionResult result = await harness.Handler.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Equal(ActionTypes.Bash, harness.Handler.ActionType);
        Assert.Empty(harness.Launcher.Launches);
    }

    private sealed class FakeGuard : IScriptAccessGuard
    {
        private readonly Func<string, string, ScriptAccessDecision> _decide;

        public FakeGuard(Func<string, string, ScriptAccessDecision> decide)
        {
            _decide = decide;
        }

        public List<(string Path, string RunAs)> Calls { get; } = [];

        public ScriptAccessDecision Check(string scriptPath, string runAs)
        {
            Calls.Add((scriptPath, runAs));
            return _decide(scriptPath, runAs);
        }
    }
}
