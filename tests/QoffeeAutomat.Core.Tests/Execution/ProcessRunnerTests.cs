using System.Text;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Platform;
using static QoffeeAutomat.Core.Tests.Execution.ProcessTestSupport;

namespace QoffeeAutomat.Core.Tests.Execution;

/// <summary>Starting, argument passing, capture and caps of the process runner (PLAN.md T206).</summary>
[Trait("Category", "ProcessRunner")]
public sealed class ProcessRunnerTests
{
    private const int Cap = 64 * 1024;

    private static readonly string[] NastyArguments =
    [
        "plain",
        "with space",
        "tab\there",
        "double \"quoted\" text",
        "single 'quoted' text",
        "$HOME",
        "$(echo pwned)",
        "`echo pwned`",
        "a;b|c&d>e<f",
        "*",
        "?",
        "~",
        "back\\slash",
        "trailing backslash\\",
        "--flag=value",
        "-n",
        string.Empty,
        "  leading and trailing  ",
        "line1\nline2",
        "ünï ✓ 日本語",
        "%PATH%",
        "!history",
        "#comment",
        "'",
        "\"",
    ];

    [UnixFact]
    public async Task Arguments_with_spaces_quotes_and_shell_metacharacters_arrive_unchanged()
    {
        var runner = new ProcessRunner();

        ProcessRunResult result = await runner.RunAsync(
            Sh("for a in \"$@\"; do printf '%s\\0' \"$a\"; done", NastyArguments));

        Assert.True(result.Succeeded, result.StandardError);
        // The output ends with a separator, so the last element after the split is empty.
        IEnumerable<string> received = result.StandardOutput.Split('\0').SkipLast(1);
        Assert.Equal(NastyArguments, received);
    }

    [UnixFact]
    public async Task Exit_code_output_and_duration_are_reported()
    {
        var runner = new ProcessRunner();

        ProcessRunResult result = await runner.RunAsync(Sh("echo out; echo err >&2; exit 3"));

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out\n", result.StandardOutput);
        Assert.Equal("err\n", result.StandardError);
        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.False(result.OutputTruncated);
        Assert.Null(result.StartError);
        Assert.True(result.ProcessId > 0);
        Assert.True(result.Duration >= TimeSpan.Zero);

        ExecutionResult converted = result.ToExecutionResult();
        Assert.Equal((3, "out\n", "err\n"), (converted.ExitCode, converted.StandardOutput, converted.StandardError));
        Assert.Null(converted.ErrorCode);
    }

    [UnixFact]
    public async Task A_successful_process_succeeds()
    {
        var runner = new ProcessRunner();

        ProcessRunResult result = await runner.RunAsync(Sh("printf done"));

        Assert.True(result.Succeeded);
        Assert.Equal("done", result.StandardOutput);
        Assert.True(result.ToExecutionResult().Succeeded);
    }

    [UnixFact]
    public async Task Environment_variables_and_working_directory_are_applied()
    {
        using var temp = new TempDirectory();
        var runner = new ProcessRunner();
        var environment = new Dictionary<string, string>
        {
            ["QA_VAR_NAME"] = "va lue $x \"q\"",
            ["QA_RUN_ID"] = "run-1",
        };

        ProcessRunResult result = await runner.RunAsync(
            Sh("printf '%s|%s|%s' \"$QA_VAR_NAME\" \"$QA_RUN_ID\" \"$(pwd -P)\"", workingDirectory: temp.FullPath, environment: environment));

        Assert.True(result.Succeeded, result.StandardError);
        string[] parts = result.StandardOutput.Split('|');
        Assert.Equal("va lue $x \"q\"", parts[0]);
        Assert.Equal("run-1", parts[1]);
        Assert.Equal(Path.GetFileName(temp.FullPath), Path.GetFileName(parts[2]));
    }

    [UnixFact]
    public async Task Standard_input_is_closed_so_a_reading_process_does_not_hang()
    {
        var runner = new ProcessRunner();

        ProcessRunResult result = await runner.RunAsync(Sh("cat; echo end", timeout: TimeSpan.FromSeconds(60)));

        Assert.True(result.Succeeded);
        Assert.Equal("end\n", result.StandardOutput);
    }

    [UnixFact]
    public async Task Output_beyond_64_KiB_is_dropped_and_flagged_for_standard_output()
    {
        var runner = new ProcessRunner();

        ProcessRunResult result = await runner.RunAsync(Sh("head -c 102400 /dev/zero | tr '\\0' a"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Cap, result.StandardOutput.Length);
        Assert.All(result.StandardOutput, character => Assert.Equal('a', character));
        Assert.Equal(string.Empty, result.StandardError);
        Assert.True(result.OutputTruncated);
    }

    [UnixFact]
    public async Task Output_beyond_64_KiB_is_dropped_and_flagged_for_standard_error()
    {
        var runner = new ProcessRunner();

        ProcessRunResult result = await runner.RunAsync(Sh("head -c 70000 /dev/zero | tr '\\0' b >&2; echo small"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("small\n", result.StandardOutput);
        Assert.Equal(Cap, result.StandardError.Length);
        Assert.True(result.OutputTruncated);
    }

    [UnixFact]
    public async Task Exactly_64_KiB_is_kept_whole_and_not_flagged()
    {
        var runner = new ProcessRunner();

        ProcessRunResult exact = await runner.RunAsync(Sh("head -c 65536 /dev/zero | tr '\\0' a"));
        ProcessRunResult over = await runner.RunAsync(Sh("head -c 65537 /dev/zero | tr '\\0' a"));

        Assert.Equal(Cap, exact.StandardOutput.Length);
        Assert.False(exact.OutputTruncated);
        Assert.Equal(Cap, over.StandardOutput.Length);
        Assert.True(over.OutputTruncated);
    }

    [UnixFact]
    public async Task The_cap_is_measured_in_bytes_and_never_keeps_half_a_character()
    {
        var runner = new ProcessRunner();

        // 65535 ASCII bytes and then é (two bytes): the second byte of é falls beyond the cap.
        ProcessRunResult result = await runner.RunAsync(Sh("head -c 65535 /dev/zero | tr '\\0' a; printf '\\303\\251'"));

        Assert.Equal(Cap - 1, result.StandardOutput.Length);
        Assert.DoesNotContain('�', result.StandardOutput);
        Assert.True(result.OutputTruncated);
    }

    [UnixFact]
    public async Task A_custom_cap_is_honoured()
    {
        var runner = new ProcessRunner(options: new ProcessRunnerOptions { OutputLimitBytes = 10 });

        ProcessRunResult result = await runner.RunAsync(Sh("printf 0123456789abcdef"));

        Assert.Equal("0123456789", result.StandardOutput);
        Assert.True(result.OutputTruncated);
    }

    [UnixFact]
    public async Task Output_is_decoded_as_UTF8()
    {
        var runner = new ProcessRunner();

        ProcessRunResult result = await runner.RunAsync(Sh("printf 'h\\303\\251llo \\342\\234\\223'; printf '\\303\\251' >&2"));

        Assert.Equal("héllo ✓", result.StandardOutput);
        Assert.Equal("é", result.StandardError);
    }

    [UnixFact]
    public async Task A_character_split_between_two_reads_is_decoded_whole()
    {
        var runner = new ProcessRunner();

        ProcessRunResult result = await runner.RunAsync(Sh("printf '\\303'; sleep 0.3; printf '\\251'"));

        Assert.Equal("é", result.StandardOutput);
        Assert.False(result.OutputTruncated);
    }

    [UnixFact]
    public async Task Output_that_is_not_captured_is_drained_and_dropped_without_blocking_the_process()
    {
        var runner = new ProcessRunner();

        ProcessRunResult result = await runner.RunAsync(
            Sh("head -c 300000 /dev/zero | tr '\\0' a; echo done >&2", captureOutput: false));

        Assert.True(result.Succeeded);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.False(result.OutputTruncated);
    }

    [UnixFact]
    public async Task A_missing_executable_is_reported_not_thrown()
    {
        var runner = new ProcessRunner();

        ProcessRunResult result = await runner.RunAsync(Request("/definitely/not/here/tool"));

        Assert.NotNull(result.StartError);
        Assert.Contains("/definitely/not/here/tool", result.StartError, StringComparison.Ordinal);
        Assert.Equal(-1, result.ExitCode);
        Assert.Null(result.ProcessId);
        Assert.False(result.Succeeded);
        ExecutionResult converted = result.ToExecutionResult();
        Assert.Equal(ErrorCodes.Internal, converted.ErrorCode);
        Assert.False(converted.Succeeded);
    }

    [UnixFact]
    public async Task A_file_that_is_not_executable_is_reported_not_thrown()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("not-executable.sh");
        await File.WriteAllTextAsync(path, "#!/bin/sh\necho hi\n", Encoding.UTF8);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var runner = new ProcessRunner();

        ProcessRunResult result = await runner.RunAsync(Request(path));

        Assert.NotNull(result.StartError);
        Assert.Equal(-1, result.ExitCode);
    }

    [UnixFact]
    public async Task A_process_that_is_not_waited_for_is_started_and_left_running()
    {
        var runner = new ProcessRunner();

        ProcessRunResult result = await runner.RunAsync(Sh("sleep 30", waitForExit: false, timeout: TimeSpan.FromSeconds(1)));

        try
        {
            Assert.Equal(0, result.ExitCode);
            Assert.NotNull(result.ProcessId);
            Assert.Null(result.StartError);
            Assert.False(result.TimedOut);
            Assert.True(IsAlive(result.ProcessId.Value));
        }
        finally
        {
            if (result.ProcessId is { } pid)
            {
                KillQuietly(pid);
            }
        }
    }

    [UnixFact]
    public async Task A_process_that_is_not_waited_for_still_reports_a_missing_executable()
    {
        var runner = new ProcessRunner();

        ProcessRunResult result = await runner.RunAsync(Request("/definitely/not/here/tool", waitForExit: false));

        Assert.NotNull(result.StartError);
    }

    [Fact]
    public async Task An_invalid_request_throws()
    {
        var runner = new ProcessRunner();

        Assert.Throws<ArgumentNullException>(() => runner.Start(null!));
        Assert.Throws<ArgumentException>(() => runner.Start(Request(" ")));
        Assert.Throws<ArgumentOutOfRangeException>(() => runner.Start(Request("tool", timeout: TimeSpan.Zero)));
        Assert.Throws<ArgumentOutOfRangeException>(() => runner.Start(Request("tool", timeout: TimeSpan.FromDays(400))));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(Request(string.Empty)));
    }

    [Fact]
    public void An_invalid_cap_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessRunner(options: new ProcessRunnerOptions { OutputLimitBytes = 0 }));
    }

    [Fact]
    public void The_default_cap_is_64_KiB()
    {
        Assert.Equal(Cap, ProcessRunnerOptions.DefaultOutputLimitBytes);
        Assert.Equal(Cap, new ProcessRunnerOptions().OutputLimitBytes);
    }

    [Theory]
    [InlineData(false, false, null, null)]
    [InlineData(true, false, ErrorCodes.Timeout, null)]
    [InlineData(false, true, ErrorCodes.Cancelled, null)]
    [InlineData(false, false, ErrorCodes.Internal, "no such file")]
    public void The_result_maps_to_an_execution_result_with_the_matching_error_code(
        bool timedOut,
        bool cancelled,
        string? expectedCode,
        string? startError)
    {
        var result = new ProcessRunResult(5, "o", "e", TimeSpan.FromSeconds(2), timedOut, false, cancelled, startError);

        ExecutionResult converted = result.ToExecutionResult();

        Assert.Equal(expectedCode, converted.ErrorCode);
        Assert.False(result.Succeeded);
        if (startError is null)
        {
            Assert.Equal((5, "o", "e"), (converted.ExitCode, converted.StandardOutput, converted.StandardError));
        }
        else
        {
            Assert.Equal(-1, converted.ExitCode);
            Assert.Equal(startError, converted.StandardError);
        }
    }

    [WindowsFact]
    public async Task Windows_reports_the_exit_code_and_output_of_a_command()
    {
        var runner = new ProcessRunner();

        ProcessRunResult failed = await runner.RunAsync(Request("cmd.exe", ["/c", "exit 3"]));
        ProcessRunResult echoed = await runner.RunAsync(Request("cmd.exe", ["/c", "echo hello"]));

        Assert.Equal(3, failed.ExitCode);
        Assert.True(echoed.Succeeded);
        Assert.Equal("hello", echoed.StandardOutput.Trim());
    }
}
