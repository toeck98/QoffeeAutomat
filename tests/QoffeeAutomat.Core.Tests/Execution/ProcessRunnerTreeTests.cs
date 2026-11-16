using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Platform.Simulated;
using static QoffeeAutomat.Core.Tests.Execution.ProcessTestSupport;

namespace QoffeeAutomat.Core.Tests.Execution;

/// <summary>Timeout, cancellation and the kill of the whole process tree (PLAN.md T206).</summary>
[Trait("Category", "ProcessRunner")]
public sealed class ProcessRunnerTreeTests
{
    /// <summary>A shell that starts a grandchild (<c>sleep</c>), writes its id to <c>$1</c> and waits for it.</summary>
    private const string ChildWithGrandchild = "sleep 300 & echo $! > \"$1\"; wait";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [UnixFact]
    public async Task A_child_that_spawns_a_grandchild_is_fully_terminated_on_timeout()
    {
        using var temp = new TempDirectory();
        string pidFile = temp.Combine("grandchild.pid");
        var clock = new SimulatedClock();
        var runner = new ProcessRunner(clock);

        await using IProcessRun run = runner.Start(Sh(ChildWithGrandchild, [pidFile], Timeout));
        int grandchild = await ReadPidAsync(pidFile);
        Assert.True(IsAlive(grandchild));
        Assert.False(run.Completion.IsCompleted);

        clock.Advance(Timeout);
        ProcessRunResult result = await run.Completion.WaitAsync(Patience);

        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.False(result.Succeeded);
        Assert.Equal(Timeout, result.Duration);
        Assert.Equal(ErrorCodes.Timeout, result.ToExecutionResult().ErrorCode);
        await AssertGoneAsync(grandchild);
        await AssertGoneAsync(result.ProcessId!.Value);
    }

    [UnixFact]
    public async Task A_timeout_on_the_system_clock_kills_the_process()
    {
        var runner = new ProcessRunner();

        ProcessRunResult result = await runner.RunAsync(Sh("sleep 300", timeout: TimeSpan.FromMilliseconds(400)));

        Assert.True(result.TimedOut);
        Assert.True(result.Duration < TimeSpan.FromSeconds(30));
        await AssertGoneAsync(result.ProcessId!.Value);
    }

    [UnixFact]
    public async Task A_process_that_ends_before_the_timeout_is_not_killed_or_flagged()
    {
        var clock = new SimulatedClock();
        var runner = new ProcessRunner(clock);

        ProcessRunResult result = await runner.RunAsync(Sh("exit 0", timeout: Timeout));

        Assert.True(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.Equal(TimeSpan.Zero, result.Duration);
    }

    [UnixFact]
    public async Task Descendants_that_lost_their_parent_are_terminated_too()
    {
        var clock = new SimulatedClock();
        var runner = new ProcessRunner(clock);
        if (runner.Containment.Name != "process-group")
        {
            // Without setsid and kill only the tree walk is available, and it cannot see an orphan.
            return;
        }

        using var temp = new TempDirectory();
        string pidFile = temp.Combine("orphan.pid");

        // The subshell exits right after it started the background job, so that job is no longer a
        // child of anything in the tree; only the process group still ties it to the run.
        await using IProcessRun run = runner.Start(Sh("(sleep 300 & echo $! > \"$1\"); sleep 300", [pidFile], Timeout));
        int orphan = await ReadPidAsync(pidFile);
        Assert.True(IsAlive(orphan));

        clock.Advance(Timeout);
        ProcessRunResult result = await run.Completion.WaitAsync(Patience);

        Assert.True(result.TimedOut);
        await AssertGoneAsync(orphan);
    }

    [UnixFact]
    public async Task Cancelling_the_token_kills_the_tree()
    {
        using var temp = new TempDirectory();
        string pidFile = temp.Combine("grandchild.pid");
        using var cancellation = new CancellationTokenSource();
        var runner = new ProcessRunner();

        await using IProcessRun run = runner.Start(Sh(ChildWithGrandchild, [pidFile]), cancellation.Token);
        int grandchild = await ReadPidAsync(pidFile);
        await cancellation.CancelAsync();
        ProcessRunResult result = await run.Completion.WaitAsync(Patience);

        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.Equal(ErrorCodes.Cancelled, result.ToExecutionResult().ErrorCode);
        await AssertGoneAsync(grandchild);
    }

    [UnixFact]
    public async Task Cancelling_the_run_kills_the_tree()
    {
        using var temp = new TempDirectory();
        string pidFile = temp.Combine("grandchild.pid");
        var runner = new ProcessRunner();

        await using IProcessRun run = runner.Start(Sh(ChildWithGrandchild, [pidFile]));
        int grandchild = await ReadPidAsync(pidFile);
        run.Cancel();
        ProcessRunResult result = await run.Completion.WaitAsync(Patience);

        Assert.True(result.Cancelled);
        await AssertGoneAsync(grandchild);

        // Cancelling a finished run is harmless.
        run.Cancel();
    }

    [UnixFact]
    public async Task A_token_that_is_cancelled_before_the_start_starts_nothing()
    {
        using var temp = new TempDirectory();
        string marker = temp.Combine("started");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var runner = new ProcessRunner();

        ProcessRunResult result = await runner.RunAsync(Sh("touch \"$1\"", [marker]), cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Null(result.ProcessId);
        Assert.False(File.Exists(marker));
    }

    [UnixFact]
    public async Task Disposing_a_running_run_cancels_it()
    {
        using var temp = new TempDirectory();
        string pidFile = temp.Combine("grandchild.pid");
        var runner = new ProcessRunner();
        IProcessRun run = runner.Start(Sh(ChildWithGrandchild, [pidFile]));
        int grandchild = await ReadPidAsync(pidFile);

        await run.DisposeAsync();

        Assert.True(run.Completion.IsCompleted);
        Assert.True((await run.Completion).Cancelled);
        await AssertGoneAsync(grandchild);
    }

    [UnixFact]
    public async Task The_containment_hook_sees_prepare_attach_kill_and_dispose_on_timeout()
    {
        var clock = new SimulatedClock();
        var containment = new RecordingContainment();
        var runner = new ProcessRunner(clock, new ProcessRunnerOptions { Containment = containment });

        await using IProcessRun run = runner.Start(Sh("sleep 300", timeout: Timeout));
        clock.Advance(Timeout);
        ProcessRunResult result = await run.Completion.WaitAsync(Patience);

        Assert.True(result.TimedOut);
        Assert.Equal(["prepare:/bin/sh", "attach", "kill", "dispose"], containment.Calls);
        Assert.Same(containment, runner.Containment);
    }

    [UnixFact]
    public async Task The_containment_hook_does_not_kill_a_process_that_ends_by_itself()
    {
        var containment = new RecordingContainment();
        var runner = new ProcessRunner(options: new ProcessRunnerOptions { Containment = containment });

        ProcessRunResult result = await runner.RunAsync(Sh("exit 0"));

        Assert.True(result.Succeeded);
        Assert.Equal(["prepare:/bin/sh", "attach", "dispose"], containment.Calls);
    }

    [UnixFact]
    public async Task The_process_that_the_containment_prepared_is_the_one_that_runs()
    {
        var containment = new RecordingContainment
        {
            Rewrite = command => new ProcessCommand("/bin/sh", ["-c", "printf 'ran:%s' \"$1\"", "sh", command.FileName]),
        };
        var runner = new ProcessRunner(options: new ProcessRunnerOptions { Containment = containment });

        ProcessRunResult result = await runner.RunAsync(Request("/original/program"));

        Assert.Equal("ran:/original/program", result.StandardOutput);
    }

    [UnixFact]
    public async Task A_containment_that_cannot_attach_falls_back_to_killing_the_tree()
    {
        var clock = new SimulatedClock();
        var containment = new RecordingContainment { FailToAttach = true };
        var runner = new ProcessRunner(clock, new ProcessRunnerOptions { Containment = containment });

        await using IProcessRun run = runner.Start(Sh("sleep 300", timeout: Timeout));
        clock.Advance(Timeout);
        ProcessRunResult result = await run.Completion.WaitAsync(Patience);

        Assert.True(result.TimedOut);
        Assert.Equal(["prepare:/bin/sh", "attach-failed"], containment.Calls);
        await AssertGoneAsync(result.ProcessId!.Value);
    }

    [UnixFact]
    public void The_default_containment_is_a_process_group_when_setsid_and_kill_exist_and_a_tree_walk_otherwise()
    {
        IProcessContainment containment = ProcessContainments.CreateDefault();

        Assert.True(containment.Name is "process-group" or "process-tree", containment.Name);
        ProcessCommand prepared = containment.Prepare(new ProcessCommand("/bin/sh", ["-c", "true"]));
        if (containment.Name == "process-group")
        {
            Assert.EndsWith("setsid", prepared.FileName, StringComparison.Ordinal);
            Assert.Equal(["/bin/sh", "-c", "true"], prepared.Arguments);
        }
        else
        {
            Assert.Equal("/bin/sh", prepared.FileName);
        }
    }

    [Fact]
    public void The_process_tree_containment_leaves_the_command_alone()
    {
        var containment = new ProcessTreeContainment();
        var command = new ProcessCommand("tool", ["a", "b c"]);

        Assert.Same(command, containment.Prepare(command));
        Assert.Equal("process-tree", containment.Name);
    }

    [WindowsFact]
    public async Task Windows_kills_a_command_that_runs_past_its_timeout()
    {
        var clock = new SimulatedClock();
        var runner = new ProcessRunner(clock);

        await using IProcessRun run = runner.Start(Request("ping.exe", ["-n", "60", "127.0.0.1"], Timeout));
        clock.Advance(Timeout);
        ProcessRunResult result = await run.Completion.WaitAsync(Patience);

        Assert.True(result.TimedOut);
        int pid = result.ProcessId!.Value;
        Assert.Throws<ArgumentException>(() =>
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
        });
    }
}
