using System.Text;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Tests.Commands;
using static QoffeeAutomat.Core.Tests.Execution.PowerShell.PowerShellTestSupport;

namespace QoffeeAutomat.Core.Tests.Execution.PowerShell;

/// <summary>
/// The wrapper run without a real engine: what the runner puts on the command line, in the environment and
/// into the temporary files, and that the files are private and gone afterwards (PLAN.md T208).
/// </summary>
[Trait("Category", "PowerShell")]
public sealed class PowerShellRunnerTests
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private static async Task<ExecutionResult> RunAsync(RunnerHarness harness, PowerShellInvocation invocation)
    {
        return await harness.Runner.RunAsync(invocation, harness.Launcher, CancellationToken.None);
    }

    [Fact]
    public async Task The_engine_gets_the_wrapper_and_the_variables_only_through_files()
    {
        using var harness = new RunnerHarness();
        List<LaunchSnapshot> snapshots = harness.CaptureSnapshots();

        ExecutionResult result = await RunAsync(harness, harness.Invocation(
            variables: Vars(("Value", WrapperValue.Hostile), ("Count", 3L), ("Flag", true))));

        Assert.True(result.Succeeded);
        (string runAs, ProcessLaunchRequest request) = Assert.Single(harness.Launcher.Launches);
        LaunchSnapshot snapshot = Assert.Single(snapshots);
        Assert.Equal(RunAsValues.System, runAs);
        Assert.Equal("/fake/bin/pwsh", request.FileName);
        Assert.Equal(PowerShellWrapper.BuildArguments(snapshot.WrapperPath), request.Arguments);
        Assert.Equal(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"], request.Arguments.Take(5));
        Assert.True(request.WaitForExit);
        Assert.True(request.CaptureOutput);
        Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);

        // No value reaches the command line or the script text.
        Assert.All(request.Arguments, argument => Assert.DoesNotContain("whoami", argument, StringComparison.Ordinal));
        Assert.DoesNotContain("whoami", snapshot.WrapperText, StringComparison.Ordinal);
        Assert.Equal(PowerShellWrapper.Script, snapshot.WrapperText.TrimStart('\uFEFF'));

        // The values travel in the parameter file, the context in the environment.
        Dictionary<string, (string Type, string Value)> parameters = ReadParameters(snapshot.ParameterBytes);
        Assert.Equal(WrapperValue.Hostile, Encoding.UTF8.GetString(Convert.FromBase64String(parameters["Value"].Value)));
        Assert.Equal(("int", "3"), parameters["Count"]);
        Assert.Equal(("bool", "true"), parameters["Flag"]);
        Assert.Equal("run1", request.EnvironmentVariables["QA_RUN_ID"]);
        Assert.Equal(snapshot.WrapperPath, Path.Combine(snapshot.RunDirectory, "wrapper.ps1"));
    }

    [Fact]
    public async Task The_wrapper_file_has_a_byte_order_mark_so_windows_powershell_reads_it_as_utf8()
    {
        using var harness = new RunnerHarness();
        List<LaunchSnapshot> snapshots = harness.CaptureSnapshots();

        await RunAsync(harness, harness.Invocation());

        Assert.Equal(Bom, Assert.Single(snapshots).WrapperBytes.Take(3));
    }

    [Fact]
    public async Task Inline_code_is_written_to_a_script_file_with_a_byte_order_mark_and_run_from_there()
    {
        using var harness = new RunnerHarness();
        List<LaunchSnapshot> snapshots = harness.CaptureSnapshots();
        const string code = "param($Name)\n'h\u00e9llo ' + $Name";

        await RunAsync(harness, harness.Invocation(code: code));

        LaunchSnapshot snapshot = Assert.Single(snapshots);
        Assert.Equal(Path.Combine(snapshot.RunDirectory, "inline.ps1"), snapshot.TargetPath);
        Assert.Equal(Bom, snapshot.TargetBytes.Take(3));
        Assert.Equal(code, Encoding.UTF8.GetString(snapshot.TargetBytes.AsSpan(3)));
        Assert.Equal(["inline.ps1", "parameters.json", "wrapper.ps1"], snapshot.FilesInRunDirectory);
    }

    [Fact]
    public async Task Each_run_gets_its_own_directory_below_the_temp_root()
    {
        using var harness = new RunnerHarness();
        List<LaunchSnapshot> snapshots = harness.CaptureSnapshots();

        await RunAsync(harness, harness.Invocation());
        await RunAsync(harness, harness.Invocation());

        Assert.Equal(2, snapshots.Count);
        Assert.NotEqual(snapshots[0].RunDirectory, snapshots[1].RunDirectory);
        Assert.All(snapshots, snapshot => Assert.Equal(harness.TempRoot, Path.GetDirectoryName(snapshot.RunDirectory)));
        Assert.All(snapshots, snapshot => Assert.StartsWith(PowerShellRunner.WorkspacePrefix, Path.GetFileName(snapshot.RunDirectory), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Temporary_files_are_deleted_after_a_successful_run()
    {
        using var harness = new RunnerHarness();

        ExecutionResult result = await RunAsync(harness, harness.Invocation(variables: Vars(("Secret", "hunter2"))));

        Assert.True(result.Succeeded);
        Assert.Empty(harness.RunDirectories);
    }

    [Fact]
    public async Task Temporary_files_are_deleted_after_a_failing_run()
    {
        using var harness = new RunnerHarness();
        harness.Launcher.OnLaunch = _ => new ExecutionResult(3, string.Empty, "boom");

        ExecutionResult result = await RunAsync(harness, harness.Invocation());

        Assert.Equal(3, result.ExitCode);
        Assert.Null(result.ErrorCode);
        Assert.Empty(harness.RunDirectories);
    }

    [Fact]
    public async Task Temporary_files_are_deleted_when_the_launch_throws_or_is_cancelled()
    {
        using var harness = new RunnerHarness();

        harness.Launcher.OnLaunch = _ => throw new InvalidOperationException("the launcher broke");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await RunAsync(harness, harness.Invocation()));
        Assert.Empty(harness.RunDirectories);

        harness.Launcher.OnLaunch = _ => throw new OperationCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await RunAsync(harness, harness.Invocation()));
        Assert.Empty(harness.RunDirectories);
    }

    [Fact]
    public async Task A_cancelled_token_stops_before_anything_is_started_and_leaves_nothing_behind()
    {
        using var harness = new RunnerHarness();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await harness.Runner.RunAsync(harness.Invocation(), harness.Launcher, cancelled.Token));

        Assert.Empty(harness.Launcher.Launches);
        Assert.Empty(harness.RunDirectories);
    }

    [Fact]
    public async Task A_failing_write_is_reported_and_the_partial_files_are_removed()
    {
        using var harness = new RunnerHarness(options: root => new PowerShellOptions
        {
            ScriptsDirectory = Path.Combine(root, "scripts"),
            TempRoot = Path.Combine(root, "run"),
            FileOperations = new FaultInjectingFileOperations { TruncateWrite = true },
        });

        ExecutionResult result = await RunAsync(harness, harness.Invocation(variables: Vars(("Value", "secret"))));

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Contains("temporary files", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(harness.Launcher.Launches);
        Assert.Empty(harness.RunDirectories);
    }

    [UnixFact]
    public async Task The_run_directory_and_its_files_are_private_to_the_executing_account()
    {
        using var harness = new RunnerHarness();
        var modes = new List<(UnixFileMode Directory, UnixFileMode[] Files)>();
        harness.Launcher.OnLaunch = request =>
        {
            if (!OperatingSystem.IsWindows())
            {
                string runDirectory = Path.GetDirectoryName(request.Arguments[^1])!;
                string[] runFiles = Directory.GetFiles(runDirectory);
                var fileModes = new UnixFileMode[runFiles.Length];
                for (int i = 0; i < runFiles.Length; i++)
                {
                    fileModes[i] = File.GetUnixFileMode(runFiles[i]);
                }

                modes.Add((File.GetUnixFileMode(runDirectory), fileModes));
            }

            return new ExecutionResult(0, string.Empty, string.Empty);
        };

        await RunAsync(harness, harness.Invocation(variables: Vars(("Secret", "hunter2"))));

        (UnixFileMode directory, UnixFileMode[] files) = Assert.Single(modes);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, directory);
        Assert.Equal(3, files.Length);
        Assert.All(files, mode => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode));
    }

    [Fact]
    public async Task A_missing_engine_is_a_clear_failure_and_nothing_is_written_or_started()
    {
        const string reason = "PowerShell 7 (pwsh) was not found on this computer.";
        using var harness = new RunnerHarness(new FakeEngineLocator(reason));

        ExecutionResult result = await RunAsync(harness, harness.Invocation(engine: PowerShellEngines.Pwsh));

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(reason, result.StandardError);
        Assert.Empty(harness.Launcher.Launches);
        Assert.False(Directory.Exists(harness.TempRoot));
    }

    [Fact]
    public async Task The_engine_name_of_the_action_is_what_the_locator_is_asked_for()
    {
        FakeEngineLocator locator = FakeEngineLocator.CreatePwsh();
        using var harness = new RunnerHarness(locator);

        await RunAsync(harness, harness.Invocation(engine: PowerShellEngines.PowerShell));

        Assert.Equal([PowerShellEngines.PowerShell], locator.Requested);
    }

    [Fact]
    public async Task A_script_file_runs_in_place_for_pwsh()
    {
        using var harness = new RunnerHarness();
        string script = harness.WriteScript("show.ps1", Encoding.UTF8.GetBytes("param($Name)\n'h\u00e9llo'"));
        List<LaunchSnapshot> snapshots = harness.CaptureSnapshots();

        await RunAsync(harness, harness.Invocation(PowerShellSources.File, code: null, script: "show.ps1"));

        LaunchSnapshot snapshot = Assert.Single(snapshots);
        Assert.Equal(Path.GetFullPath(script), snapshot.TargetPath);
        Assert.Equal(["parameters.json", "wrapper.ps1"], snapshot.FilesInRunDirectory);
    }

    [Fact]
    public async Task A_non_ascii_script_without_byte_order_mark_runs_as_a_utf8_copy_for_windows_powershell()
    {
        using var harness = new RunnerHarness(FakeEngineLocator.CreateWindowsPowerShell());
        byte[] content = Encoding.UTF8.GetBytes("'h\u00e9llo \u2713'");
        string script = harness.WriteScript("show.ps1", content);
        List<LaunchSnapshot> snapshots = harness.CaptureSnapshots();

        await RunAsync(harness, harness.Invocation(PowerShellSources.File, code: null, script: "show.ps1"));

        LaunchSnapshot snapshot = Assert.Single(snapshots);
        Assert.NotEqual(Path.GetFullPath(script), snapshot.TargetPath);
        Assert.Equal(Path.Combine(snapshot.RunDirectory, "show.ps1"), snapshot.TargetPath);
        Assert.Equal([.. Bom, .. content], snapshot.TargetBytes);
        Assert.Equal(content, File.ReadAllBytes(script));
    }

    [Fact]
    public async Task Scripts_that_are_ascii_or_already_have_a_byte_order_mark_run_in_place_for_windows_powershell()
    {
        using var harness = new RunnerHarness(FakeEngineLocator.CreateWindowsPowerShell());
        string ascii = harness.WriteScript("plain.ps1", Encoding.ASCII.GetBytes("'hello'"));
        string marked = harness.WriteScript("marked.ps1", [.. Bom, .. Encoding.UTF8.GetBytes("'h\u00e9llo'")]);
        List<LaunchSnapshot> snapshots = harness.CaptureSnapshots();

        await RunAsync(harness, harness.Invocation(PowerShellSources.File, code: null, script: "plain.ps1"));
        await RunAsync(harness, harness.Invocation(PowerShellSources.File, code: null, script: "marked.ps1"));

        Assert.Equal([Path.GetFullPath(ascii), Path.GetFullPath(marked)], snapshots.Select(snapshot => snapshot.TargetPath).ToArray());
    }

    [Fact]
    public async Task A_missing_script_file_is_a_failure_and_nothing_is_started()
    {
        using var harness = new RunnerHarness();

        ExecutionResult result = await RunAsync(harness, harness.Invocation(PowerShellSources.File, code: null, script: "missing.ps1"));

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Contains("missing.ps1", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(harness.Launcher.Launches);
    }

    [Theory]
    [InlineData("..\\outside.ps1")]
    [InlineData("../outside.ps1")]
    [InlineData("sub/inner.ps1")]
    [InlineData("C:\\Windows\\x.ps1")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public async Task A_script_name_that_leaves_the_scripts_directory_is_rejected(string name)
    {
        using var harness = new RunnerHarness();
        File.WriteAllText(Path.Combine(harness.Temp.FullPath, "outside.ps1"), "'x'");

        ExecutionResult result = await RunAsync(harness, harness.Invocation(PowerShellSources.File, code: null, script: name));

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Empty(harness.Launcher.Launches);
    }

    [Fact]
    public async Task A_script_file_without_a_scripts_directory_is_a_failure()
    {
        using var harness = new RunnerHarness(options: root => new PowerShellOptions { TempRoot = Path.Combine(root, "run") });

        ExecutionResult result = await RunAsync(harness, harness.Invocation(PowerShellSources.File, code: null, script: "x.ps1"));

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Contains("scripts directory", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PowerShellSources.Inline, "")]
    [InlineData(PowerShellSources.Inline, "   ")]
    [InlineData("download", "'x'")]
    public async Task Code_that_is_empty_or_of_an_unknown_source_is_a_failure(string source, string code)
    {
        using var harness = new RunnerHarness();

        ExecutionResult result = await RunAsync(harness, harness.Invocation(source, code));

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Empty(harness.Launcher.Launches);
    }

    [Fact]
    public async Task A_variable_with_an_invalid_name_is_rejected_without_starting_anything()
    {
        using var harness = new RunnerHarness();

        ExecutionResult result = await RunAsync(harness, harness.Invocation(variables: Vars(("-ExecutionPolicy", "x"))));

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(harness.Launcher.Launches);
        Assert.Empty(harness.RunDirectories);
    }

    [Fact]
    public async Task The_run_context_is_passed_to_the_launcher()
    {
        using var harness = new RunnerHarness();

        await RunAsync(harness, harness.Invocation(runAs: RunAsValues.UserElevated));

        Assert.Equal(RunAsValues.UserElevated, Assert.Single(harness.Launcher.Launches).RunAs);
    }

    [Fact]
    public void Stale_directories_are_swept_but_recent_ones_and_foreign_ones_are_kept()
    {
        using var temp = new TempDirectory();
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        string old = Directory.CreateDirectory(temp.Combine(PowerShellRunner.WorkspacePrefix + "old")).FullName;
        string recent = Directory.CreateDirectory(temp.Combine(PowerShellRunner.WorkspacePrefix + "recent")).FullName;
        string foreign = Directory.CreateDirectory(temp.Combine("other-old")).FullName;
        File.WriteAllText(Path.Combine(old, "parameters.json"), "{}");
        Directory.SetLastWriteTimeUtc(old, now.UtcDateTime - TimeSpan.FromDays(3));
        Directory.SetLastWriteTimeUtc(recent, now.UtcDateTime - TimeSpan.FromHours(1));
        Directory.SetLastWriteTimeUtc(foreign, now.UtcDateTime - TimeSpan.FromDays(30));

        int removed = PowerShellRunner.SweepStale(temp.FullPath, TimeSpan.FromHours(48), now);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(foreign));
    }

    [Fact]
    public void Sweeping_a_directory_that_does_not_exist_does_nothing()
    {
        using var temp = new TempDirectory();

        Assert.Equal(0, PowerShellRunner.SweepStale(temp.Combine("missing"), TimeSpan.Zero, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task The_first_run_sweeps_the_leftovers_of_crashed_runs()
    {
        using var harness = new RunnerHarness();
        string crashed = Directory.CreateDirectory(Path.Combine(harness.TempRoot, PowerShellRunner.WorkspacePrefix + "crashed")).FullName;
        File.WriteAllText(Path.Combine(crashed, "parameters.json"), "{\"secret\":true}");
        Directory.SetLastWriteTimeUtc(crashed, DateTime.UtcNow - TimeSpan.FromDays(10));

        await RunAsync(harness, harness.Invocation());

        Assert.False(Directory.Exists(crashed));
    }

    [Fact]
    public async Task The_handler_passes_the_command_context_to_the_runner()
    {
        using var harness = new RunnerHarness();
        List<LaunchSnapshot> snapshots = harness.CaptureSnapshots();
        CommandDocument command = Command(
            new PowerShellAction(PowerShellSources.Inline, Code: "param($Count)\n$Count", Engine: PowerShellEngines.Pwsh),
            runAs: RunAsValues.User,
            captureOutput: false);
        ActionContext context = Context(
            command,
            harness.Launcher,
            Vars(("Count", 5L)),
            TimeSpan.FromSeconds(12),
            new Dictionary<string, string> { ["QA_COMMAND_ID"] = "ps_test", ["QA_VAR_COUNT"] = "5" });

        ExecutionResult result = await harness.Handler.ExecuteAsync(context, CancellationToken.None);

        Assert.True(result.Succeeded);
        (string runAs, ProcessLaunchRequest request) = Assert.Single(harness.Launcher.Launches);
        Assert.Equal(RunAsValues.User, runAs);
        Assert.False(request.CaptureOutput);
        Assert.Equal(TimeSpan.FromSeconds(12), request.Timeout);
        Assert.Equal("ps_test", request.EnvironmentVariables["QA_COMMAND_ID"]);
        Assert.Equal("5", request.EnvironmentVariables["QA_VAR_COUNT"]);
        Assert.Equal(("int", "5"), ReadParameters(Assert.Single(snapshots).ParameterBytes)["Count"]);
        Assert.Equal(ActionTypes.PowerShell, harness.Handler.ActionType);
    }

    [Fact]
    public async Task The_handler_refuses_an_action_of_another_type()
    {
        using var harness = new RunnerHarness();
        ActionContext context = Context(Command(new BuiltinAction("power.shutdown")), harness.Launcher);

        ExecutionResult result = await harness.Handler.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Empty(harness.Launcher.Launches);
    }

    [Fact]
    public async Task A_powershell_command_runs_through_the_command_engine_and_the_handler()
    {
        using var harness = new RunnerHarness();
        await using CommandHarness commands = await CommandHarness.CreateAsync(
            powerShell: harness.Handler,
            seed: null);
        commands.Runner.Answer = request => new ProcessRunResult(3, "out", "err", TimeSpan.Zero, false, false);
        await commands.SaveAsync(Cmd.Gated("ps_run", timeoutSeconds: 45, parameters: [Cmd.Text("Name", required: true)]));

        RunResult result = await commands.RunAsync("ps_run", ("Name", WrapperValue.Hostile));

        Assert.True(result.Started);
        Assert.Equal(3, result.ExitCode);
        Assert.Null(result.ErrorCode);
        Assert.Equal("out", result.StandardOutput);
        ProcessLaunchRequest request = Assert.Single(commands.Runner.Requests);
        Assert.Equal("/fake/bin/pwsh", request.FileName);
        Assert.Equal(TimeSpan.FromSeconds(45), request.Timeout);
        Assert.Equal("ps_run", request.EnvironmentVariables["QA_COMMAND_ID"]);
        Assert.Equal(WrapperValue.Hostile, request.EnvironmentVariables["QA_VAR_NAME"]);
        Assert.All(request.Arguments, argument => Assert.DoesNotContain("whoami", argument, StringComparison.Ordinal));
        Assert.Empty(harness.RunDirectories);
    }
}

/// <summary>The hostile value shared by the PowerShell tests.</summary>
internal static class WrapperValue
{
    public const string Hostile = PowerShellWrapperTests.Hostile;
}
