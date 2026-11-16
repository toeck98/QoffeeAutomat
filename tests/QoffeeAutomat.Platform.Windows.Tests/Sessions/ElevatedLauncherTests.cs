using System.ComponentModel;
using System.Security.AccessControl;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Security;
using QoffeeAutomat.Platform.Windows.Sessions;

namespace QoffeeAutomat.Platform.Windows.Tests.Sessions;

/// <summary>
/// Desktop commands with administrator rights (PLAN.md S11, D-036, T311). The launcher takes the token of the console session
/// and its elevated linked token, and starts the process with it. All of that goes through <see cref="FakeElevatedNativeApi"/>:
/// the tests decide whether there is a session, a linked token or a process, and nothing is started on the computer that runs
/// them. What really happens on a desktop is checked by hand (T311, <c>human_verify</c>).
/// </summary>
[Trait("Category", "Elevated")]
public sealed class ElevatedLauncherTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly TempDirectory _temp = new();
    private readonly FakeElevatedNativeApi _native = new();
    private readonly FakeAcl _acl = new();

    public void Dispose() => _temp.Dispose();

    private ElevatedLauncher NewLauncher(IFileSystemAcl? acl = null, ElevatedLauncherOptions? options = null, ILogger? logger = null)
    {
        return new ElevatedLauncher(_native, acl ?? _acl, time: null, options, logger);
    }

    private static ProcessLaunchRequest Request(
        string file = "C:\\Tools\\admin.exe",
        string[]? arguments = null,
        TimeSpan? timeout = null,
        bool waitForExit = true,
        bool captureOutput = true,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        Action<ProcessOutputChunk>? onOutput = null)
    {
        return new ProcessLaunchRequest(
            file,
            arguments ?? ["--x"],
            workingDirectory,
            environment ?? new Dictionary<string, string>(),
            timeout ?? TimeSpan.FromSeconds(30),
            waitForExit,
            captureOutput,
            onOutput);
    }

    private static async Task<ExecutionResult> StartAsync(ElevatedLauncher launcher, ProcessLaunchRequest request, CancellationToken cancellationToken = default)
    {
        return await launcher.StartAsync(request, cancellationToken).AsTask().WaitAsync(Patience, CancellationToken.None);
    }

    /// <summary>Makes every started process end at once with the given exit code.</summary>
    private void EndImmediately(int exitCode = 0)
    {
        _native.OnStart = (process, _) => process.Exit(exitCode);
    }

    // ───────────────────────────── token handling ─────────────────────────────

    [Fact]
    public async Task Nobody_at_the_console_is_no_session_and_nothing_is_started()
    {
        _native.ConsoleSession = null;

        ExecutionResult result = await StartAsync(NewLauncher(), Request());

        Assert.Equal(ErrorCodes.NoSession, result.ErrorCode);
        Assert.Empty(_native.OpenedSessions);
        Assert.Empty(_native.Launches);
    }

    [Fact]
    public async Task A_session_without_a_logged_in_user_is_no_session_with_the_explanation_of_the_native_layer()
    {
        _native.TokenStatus = ElevatedTokenStatus.NoSession;
        _native.TokenMessage = "The sign-in screen is showing.";

        ExecutionResult result = await StartAsync(NewLauncher(), Request());

        Assert.Equal(ErrorCodes.NoSession, result.ErrorCode);
        Assert.Contains("sign-in screen", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(_native.Launches);
        Assert.Empty(_native.Tokens);
    }

    [Fact]
    public async Task A_user_without_an_elevated_linked_token_gets_elevation_unavailable_and_nothing_is_started()
    {
        _native.TokenStatus = ElevatedTokenStatus.NoLinkedToken;

        ExecutionResult result = await StartAsync(NewLauncher(), Request());

        Assert.Equal(ErrorCodes.ElevationUnavailable, result.ErrorCode);
        Assert.Contains("administrator", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(_native.ConsoleSession, Assert.Single(_native.OpenedSessions));
        Assert.Empty(_native.StartedWith);
        Assert.Empty(_native.Launches);
        Assert.Empty(_native.Processes);
    }

    [Fact]
    public async Task The_message_of_the_native_layer_replaces_the_default_for_a_missing_linked_token()
    {
        _native.TokenStatus = ElevatedTokenStatus.NoLinkedToken;
        _native.TokenMessage = "User Account Control is switched off.";

        ExecutionResult result = await StartAsync(NewLauncher(), Request());

        Assert.Equal(ErrorCodes.ElevationUnavailable, result.ErrorCode);
        Assert.Equal("User Account Control is switched off.", result.StandardError);
    }

    [Fact]
    public async Task Another_failure_to_get_the_token_is_an_internal_error()
    {
        _native.TokenStatus = ElevatedTokenStatus.Failed;
        _native.TokenMessage = "The agent does not run as LocalSystem.";

        ExecutionResult result = await StartAsync(NewLauncher(), Request());

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Contains("LocalSystem", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(_native.Launches);
    }

    [Fact]
    public async Task A_windows_error_while_getting_the_token_is_an_internal_error_and_never_an_exception()
    {
        Exception[] failures = [new Win32Exception(5, "Access is denied."), new InvalidOperationException("Broken."), new UnauthorizedAccessException("Denied.")];

        foreach (Exception failure in failures)
        {
            _native.OpenFailure = failure;

            ExecutionResult result = await StartAsync(NewLauncher(), Request());

            Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
            Assert.Contains(failure.Message, result.StandardError, StringComparison.Ordinal);
        }

        Assert.Empty(_native.Launches);
    }

    [Fact]
    public async Task The_token_of_the_console_session_is_the_one_that_is_used_for_the_process()
    {
        _native.ConsoleSession = 7;
        EndImmediately();

        ExecutionResult result = await StartAsync(NewLauncher(), Request());

        Assert.True(result.Succeeded);
        Assert.Equal(7, Assert.Single(_native.OpenedSessions));
        FakeElevatedToken token = Assert.Single(_native.Tokens);
        Assert.Same(token, Assert.Single(_native.StartedWith));
    }

    [Fact]
    public async Task The_token_is_released_after_every_kind_of_run()
    {
        EndImmediately();
        await StartAsync(NewLauncher(), Request());
        Assert.All(_native.Tokens, token => Assert.True(token.IsDisposed));

        _native.StartFailure = new ElevatedLaunchException("The file does not exist.");
        await StartAsync(NewLauncher(), Request());
        Assert.All(_native.Tokens, token => Assert.True(token.IsDisposed));

        _native.StartFailure = null;
        await StartAsync(NewLauncher(), Request(waitForExit: false));

        Assert.Equal(3, _native.Tokens.Count);
        Assert.All(_native.Tokens, token => Assert.True(token.IsDisposed));
    }

    // ───────────────────────────── process execution ─────────────────────────────

    [Fact]
    public async Task A_process_is_started_with_its_arguments_as_separate_entries_and_its_output_is_captured()
    {
        _native.OnStart = (process, _) =>
        {
            process.Output!.Feed("hello");
            process.Error!.Feed("oops");
            process.Exit(0);
        };
        string[] arguments = ["--name", "a b & c", "\"quoted\"", "%PATH%"];
        var environment = new Dictionary<string, string> { ["QA_RUN_ID"] = "run1" };

        ExecutionResult result = await StartAsync(NewLauncher(), Request(arguments: arguments, environment: environment, workingDirectory: "C:\\Work"));

        Assert.True(result.Succeeded);
        Assert.Equal("hello", result.StandardOutput);
        Assert.Equal("oops", result.StandardError);
        ElevatedLaunch launch = Assert.Single(_native.Launches);
        Assert.Equal("C:\\Tools\\admin.exe", launch.FileName);
        Assert.Equal(arguments, launch.Arguments);
        Assert.Equal("C:\\Work", launch.WorkingDirectory);
        Assert.Equal("run1", launch.Environment["QA_RUN_ID"]);
        Assert.True(launch.Attached);
    }

    [Fact]
    public async Task The_exit_code_of_the_process_is_the_exit_code_of_the_run()
    {
        EndImmediately(3);

        ExecutionResult result = await StartAsync(NewLauncher(), Request());

        Assert.Equal(3, result.ExitCode);
        Assert.Null(result.ErrorCode);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task The_process_is_released_but_not_killed_after_a_normal_run()
    {
        EndImmediately();

        await StartAsync(NewLauncher(), Request());

        FakeElevatedProcess process = Assert.Single(_native.Processes);
        Assert.True(process.IsDisposed);
        Assert.False(process.WasKilled);
    }

    [Fact]
    public async Task A_run_that_does_not_wait_starts_the_process_and_lets_go_of_it()
    {
        ExecutionResult result = await StartAsync(NewLauncher(), Request(waitForExit: false, timeout: TimeSpan.Zero));

        Assert.True(result.Succeeded);
        Assert.Equal(string.Empty, result.StandardOutput);
        ElevatedLaunch launch = Assert.Single(_native.Launches);
        Assert.False(launch.Attached);
        FakeElevatedProcess process = Assert.Single(_native.Processes);
        Assert.Null(process.StandardOutput);
        Assert.True(process.IsDisposed);
        Assert.False(process.WasKilled);
    }

    [Fact]
    public async Task Output_is_not_kept_when_the_command_does_not_ask_for_it()
    {
        _native.OnStart = (process, _) =>
        {
            process.Output!.Feed("noise");
            process.Exit(0);
        };

        ExecutionResult result = await StartAsync(NewLauncher(), Request(captureOutput: false));

        Assert.True(result.Succeeded);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Output_beyond_the_limit_is_dropped_and_the_result_says_so()
    {
        _native.OnStart = (process, _) =>
        {
            process.Output!.Feed("0123456789ABCDEF");
            process.Exit(0);
        };

        ExecutionResult result = await StartAsync(NewLauncher(options: new ElevatedLauncherOptions { OutputLimitBytes = 10 }), Request());

        Assert.Equal("0123456789", result.StandardOutput);
        Assert.True(result.OutputTruncated);
    }

    [Fact]
    public async Task Output_within_the_limit_is_not_marked_as_truncated()
    {
        _native.OnStart = (process, _) =>
        {
            process.Output!.Feed("short");
            process.Exit(0);
        };

        ExecutionResult result = await StartAsync(NewLauncher(), Request());

        Assert.False(result.OutputTruncated);
    }

    [Fact]
    public async Task Output_is_handed_to_the_receiver_while_it_is_read_and_a_failing_receiver_changes_nothing()
    {
        var chunks = new List<ProcessOutputChunk>();
        _native.OnStart = (process, _) =>
        {
            process.Output!.Feed("out");
            process.Error!.Feed("err");
            process.Exit(0);
        };

        ExecutionResult result = await StartAsync(NewLauncher(), Request(onOutput: chunk =>
        {
            lock (chunks)
            {
                chunks.Add(chunk);
            }

            throw new InvalidOperationException("The receiver is broken.");
        }));

        Assert.True(result.Succeeded);
        Assert.Equal("out", result.StandardOutput);
        Assert.Equal("err", result.StandardError);
        lock (chunks)
        {
            Assert.Contains(chunks, chunk => chunk.Source == ProcessOutputSource.StandardOutput && chunk.Text == "out");
            Assert.Contains(chunks, chunk => chunk.Source == ProcessOutputSource.StandardError && chunk.Text == "err");
        }
    }

    [Fact]
    public async Task A_descendant_that_keeps_the_pipes_open_cannot_hold_the_result_back()
    {
        _native.OnStart = (process, _) =>
        {
            process.Output!.Feed("partial");
            process.Exit(0, closePipes: false);
        };

        ExecutionResult result = await StartAsync(
            NewLauncher(options: new ElevatedLauncherOptions { OutputDrainTimeout = TimeSpan.FromMilliseconds(150) }),
            Request());

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("partial", result.StandardOutput);
    }

    [Fact]
    public async Task A_process_that_runs_too_long_is_killed_and_the_run_is_a_timeout()
    {
        ExecutionResult result = await StartAsync(NewLauncher(), Request(timeout: TimeSpan.FromMilliseconds(200)));

        Assert.Equal(ErrorCodes.Timeout, result.ErrorCode);
        FakeElevatedProcess process = Assert.Single(_native.Processes);
        Assert.True(process.WasKilled);
        Assert.True(process.IsDisposed);
        Assert.True(Assert.Single(_native.Tokens).IsDisposed);
    }

    [Fact]
    public async Task Cancelling_the_run_kills_the_process_and_the_run_is_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        _native.OnStart = (_, _) => cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        ExecutionResult result = await StartAsync(NewLauncher(), Request(), cancellation.Token);

        Assert.Equal(ErrorCodes.Cancelled, result.ErrorCode);
        Assert.True(Assert.Single(_native.Processes).WasKilled);
    }

    [Fact]
    public async Task A_run_that_was_cancelled_before_it_began_does_not_even_look_for_the_session()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        ExecutionResult result = await StartAsync(NewLauncher(), Request(), cancellation.Token);

        Assert.Equal(ErrorCodes.Cancelled, result.ErrorCode);
        Assert.Equal(0, _native.ConsoleLookups);
        Assert.Empty(_native.Launches);
    }

    [Fact]
    public async Task A_process_that_cannot_be_created_is_an_internal_error_that_names_the_program()
    {
        Exception[] failures =
        [
            new ElevatedLaunchException("The file does not exist."),
            new Win32Exception(2, "The system cannot find the file."),
            new IOException("The pipe broke."),
            new InvalidOperationException("No desktop."),
        ];

        foreach (Exception failure in failures)
        {
            _native.StartFailure = failure;

            ExecutionResult result = await StartAsync(NewLauncher(), Request(file: "C:\\Tools\\missing.exe"));

            Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
            Assert.Contains("Could not start 'C:\\Tools\\missing.exe' with administrator rights", result.StandardError, StringComparison.Ordinal);
            Assert.Contains(failure.Message, result.StandardError, StringComparison.Ordinal);
        }

        Assert.Empty(_native.Processes);
    }

    [Fact]
    public async Task A_variable_that_an_environment_block_cannot_hold_is_invalid_vars()
    {
        _native.StartFailure = new ArgumentException("The value of the environment variable 'X' must not contain NUL.");

        ExecutionResult result = await StartAsync(NewLauncher(), Request());

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
    }

    [Fact]
    public async Task A_request_without_a_program_is_refused_before_anything_is_looked_up()
    {
        ExecutionResult result = await StartAsync(NewLauncher(), Request(file: "  "));

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Equal(0, _native.ConsoleLookups);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(41 * 24 * 3600)]
    public async Task A_waited_run_needs_a_timeout_from_zero_exclusive_to_forty_days(int seconds)
    {
        ExecutionResult result = await StartAsync(NewLauncher(), Request(timeout: TimeSpan.FromSeconds(seconds)));

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Empty(_native.Launches);
    }

    [Fact]
    public async Task Every_elevated_start_is_logged_with_the_program_and_the_user_but_never_the_arguments()
    {
        var logger = new ListLogger();
        EndImmediately();

        await StartAsync(NewLauncher(logger: logger), Request(arguments: ["--password=hunter2"]));

        (LogLevel level, string message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, level);
        Assert.Contains("C:\\Tools\\admin.exe", message, StringComparison.Ordinal);
        Assert.Contains("PC\\alice", message, StringComparison.Ordinal);
        Assert.Contains("5000", message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_that_starts_nothing_logs_no_start()
    {
        var logger = new ListLogger();
        _native.TokenStatus = ElevatedTokenStatus.NoLinkedToken;

        await StartAsync(NewLauncher(logger: logger), Request());

        Assert.Empty(logger.Entries);
    }

    // ───────────────────────────── PowerShell: wrapper and parameter file ─────────────────────────────

    private string NewWorkspace(string name = "qa-ps-0123456789ab")
    {
        string directory = Path.Combine(_temp.Path, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "parameters.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "wrapper.ps1"), string.Empty);
        return directory;
    }

    private static Dictionary<string, string> WithParameterFile(string directory)
    {
        return new Dictionary<string, string> { [PowerShellWrapper.ParametersFileVariable] = Path.Combine(directory, "parameters.json") };
    }

    [Fact]
    public async Task The_directory_of_a_powershell_run_is_restricted_to_system_the_administrators_and_the_user()
    {
        string directory = NewWorkspace();
        EndImmediately();

        ExecutionResult result = await StartAsync(NewLauncher(), Request(file: "pwsh", environment: WithParameterFile(directory)));

        Assert.True(result.Succeeded);
        (string path, IReadOnlyList<AclGrant> grants) = Assert.Single(_acl.Protections);
        Assert.Equal(Path.GetFullPath(directory), path);
        Assert.Equal(3, grants.Count);
        Assert.Equal(FileSystemRights.FullControl, Assert.Single(grants, grant => grant.Sid == WellKnownAccounts.LocalSystem).Rights);
        Assert.Equal(FileSystemRights.FullControl, Assert.Single(grants, grant => grant.Sid == WellKnownAccounts.Administrators).Rights);
        Assert.All(grants, grant => Assert.True(grant.InheritToChildren, "The wrapper and the parameter file inherit the restriction."));
    }

    [Fact]
    public async Task The_interactive_user_may_read_and_run_the_files_of_the_run_but_not_change_them()
    {
        string directory = NewWorkspace();
        EndImmediately();

        await StartAsync(NewLauncher(), Request(file: "pwsh", environment: WithParameterFile(directory)));

        IReadOnlyList<AclGrant> grants = Assert.Single(_acl.Protections).Grants;
        AclGrant user = Assert.Single(grants, grant => grant.Sid == FakeElevatedNativeApi.UserSid);
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, user.Rights);
        const FileSystemRights Changes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        Assert.Equal((FileSystemRights)0, user.Rights & Changes);
    }

    [Fact]
    public async Task No_other_account_is_granted_access_to_the_files_of_the_run()
    {
        string directory = NewWorkspace();
        EndImmediately();

        await StartAsync(NewLauncher(), Request(file: "pwsh", environment: WithParameterFile(directory)));

        IReadOnlyList<AclGrant> grants = Assert.Single(_acl.Protections).Grants;
        string[] excluded = [WellKnownAccounts.Users, WellKnownAccounts.AuthenticatedUsers, WellKnownAccounts.Everyone];
        Assert.DoesNotContain(grants, grant => excluded.Contains(grant.Sid));
    }

    [Fact]
    public async Task The_directory_is_restricted_before_the_process_exists()
    {
        string directory = NewWorkspace();
        int protectionsAtStart = -1;
        _native.OnStart = (process, _) =>
        {
            protectionsAtStart = _acl.Protections.Count;
            process.Exit(0);
        };

        await StartAsync(NewLauncher(), Request(file: "pwsh", environment: WithParameterFile(directory)));

        Assert.Equal(1, protectionsAtStart);
    }

    [Fact]
    public async Task A_script_is_not_started_when_its_directory_cannot_be_restricted()
    {
        string directory = NewWorkspace();
        var acl = new RefusingAcl();
        EndImmediately();

        ExecutionResult result = await StartAsync(NewLauncher(acl: acl), Request(file: "pwsh", environment: WithParameterFile(directory)));

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Contains("not started", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, acl.Attempts);
        Assert.Empty(_native.Launches);
        Assert.True(Assert.Single(_native.Tokens).IsDisposed);
    }

    [Fact]
    public async Task A_process_run_without_a_parameter_file_changes_no_permissions()
    {
        EndImmediately();

        ExecutionResult result = await StartAsync(NewLauncher(), Request());

        Assert.True(result.Succeeded);
        Assert.Empty(_acl.Protections);
    }

    [Fact]
    public async Task A_parameter_file_outside_a_workspace_of_the_runner_is_never_given_new_permissions()
    {
        string other = NewWorkspace("documents");
        EndImmediately();

        ExecutionResult result = await StartAsync(NewLauncher(), Request(environment: WithParameterFile(other)));

        Assert.True(result.Succeeded);
        Assert.Empty(_acl.Protections);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_parameter_file_variable_is_ignored(string value)
    {
        EndImmediately();
        var environment = new Dictionary<string, string> { [PowerShellWrapper.ParametersFileVariable] = value };

        ExecutionResult result = await StartAsync(NewLauncher(), Request(environment: environment));

        Assert.True(result.Succeeded);
        Assert.Empty(_acl.Protections);
    }

    [Fact]
    public async Task A_workspace_that_does_not_exist_is_ignored()
    {
        EndImmediately();
        string missing = Path.Combine(_temp.Path, "qa-ps-missing0000");

        ExecutionResult result = await StartAsync(NewLauncher(), Request(environment: WithParameterFile(missing)));

        Assert.True(result.Succeeded);
        Assert.Empty(_acl.Protections);
    }

    [Fact]
    public async Task The_powershell_runner_hands_the_elevated_launcher_a_directory_that_is_restricted_before_the_engine_starts()
    {
        var runner = new PowerShellRunner(new FakeEngineLocator(), new PowerShellOptions { TempRoot = _temp.Path });
        var launcher = new RoutingProcessLauncher(new FakeProcessRunner(), session: null, elevated: NewLauncher());
        var seen = new List<string>();
        int protectionsAtStart = -1;
        _native.OnStart = (process, launch) =>
        {
            string parameterFile = launch.Environment[PowerShellWrapper.ParametersFileVariable];
            string directory = Path.GetDirectoryName(parameterFile)!;
            protectionsAtStart = _acl.Protections.Count;
            seen.Add(File.Exists(parameterFile) ? "parameters" : "no parameters");
            seen.Add(File.Exists(Path.Combine(directory, "wrapper.ps1")) ? "wrapper" : "no wrapper");
            process.Exit(0);
        };
        var invocation = new PowerShellInvocation(
            PowerShellSources.Inline,
            "Write-Output 'hi'",
            Script: null,
            PowerShellEngines.Pwsh,
            new Dictionary<string, object>(),
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(30),
            RunAsValues.UserElevated);

        ExecutionResult result = await runner.RunAsync(invocation, launcher, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, protectionsAtStart);
        Assert.Contains("parameters", seen);
        Assert.Contains("wrapper", seen);
        ElevatedLaunch launch = Assert.Single(_native.Launches);
        Assert.Equal("pwsh", launch.FileName);
        (string protectedDirectory, _) = Assert.Single(_acl.Protections);
        Assert.StartsWith(PowerShellRunner.WorkspacePrefix, Path.GetFileName(protectedDirectory), StringComparison.Ordinal);
        Assert.Contains(
            launch.Arguments,
            argument => argument.StartsWith(protectedDirectory, StringComparison.OrdinalIgnoreCase) && argument.EndsWith("wrapper.ps1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_user_who_is_not_an_administrator_gets_elevation_unavailable_for_a_powershell_run_and_nothing_is_written_to_the_acl()
    {
        var runner = new PowerShellRunner(new FakeEngineLocator(), new PowerShellOptions { TempRoot = _temp.Path });
        var launcher = new RoutingProcessLauncher(new FakeProcessRunner(), session: null, elevated: NewLauncher());
        _native.TokenStatus = ElevatedTokenStatus.NoLinkedToken;
        var invocation = new PowerShellInvocation(
            PowerShellSources.Inline,
            "Write-Output 'hi'",
            Script: null,
            PowerShellEngines.Pwsh,
            new Dictionary<string, object>(),
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(30),
            RunAsValues.UserElevated);

        ExecutionResult result = await runner.RunAsync(invocation, launcher, CancellationToken.None);

        Assert.Equal(ErrorCodes.ElevationUnavailable, result.ErrorCode);
        Assert.Empty(_acl.Protections);
        Assert.Empty(_native.Launches);
    }
}
