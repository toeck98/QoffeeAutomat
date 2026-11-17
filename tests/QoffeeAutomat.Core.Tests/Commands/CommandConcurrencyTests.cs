using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;

namespace QoffeeAutomat.Core.Tests.Commands;

/// <summary>
/// The concurrency modes behave like the Home Assistant script modes of the same name: one test per
/// mode (PLAN.md T207 acceptance), plus the limits and the bookkeeping around them.
/// </summary>
[Trait("Category", "Commands")]
public sealed class CommandConcurrencyTests
{
    private static ParameterDefinition Name => Cmd.Text("Name");

    private static string NameOf(GateCall call) => (string)call.Variables["Name"];

    [Fact]
    public async Task Mode_single_rejects_a_new_run_while_one_is_active()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        await h.SaveAsync(Cmd.Gated("c", ConcurrencyModes.Exclusive, parameters: Name));

        RunHandle first = await h.StartAsync("c", ("Name", "first"));
        GateCall firstCall = await h.Gate!.NextStartAsync();
        RunHandle second = await h.StartAsync("c", ("Name", "second"));

        Assert.True(first.IsAccepted);
        Assert.False(second.IsAccepted);
        RunResult rejected = await CommandHarness.Finished(second);
        Assert.Equal(ErrorCodes.Busy, rejected.ErrorCode);
        Assert.False(rejected.Started);

        firstCall.Finish();
        Assert.True((await CommandHarness.Finished(first)).Succeeded);

        // The slot is free again.
        RunHandle third = await h.StartAsync("c", ("Name", "third"));
        Assert.True(third.IsAccepted);
        (await h.Gate.NextStartAsync()).Finish();
        Assert.True((await CommandHarness.Finished(third)).Succeeded);
        Assert.Equal(["first", "third"], h.Gate.Calls.Select(NameOf));
    }

    [Fact]
    public async Task Mode_queue_runs_the_requests_one_after_the_other_in_the_order_they_arrived()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        await h.SaveAsync(Cmd.Gated("c", ConcurrencyModes.Queue, parameters: Name));

        RunHandle a = await h.StartAsync("c", ("Name", "a"));
        RunHandle b = await h.StartAsync("c", ("Name", "b"));
        RunHandle c = await h.StartAsync("c", ("Name", "c"));
        Assert.All([a, b, c], handle => Assert.True(handle.IsAccepted));

        GateCall first = await h.Gate!.NextStartAsync();
        Assert.Equal("a", NameOf(first));
        await Task.Delay(100);
        Assert.Single(h.Gate.Calls);
        Assert.Equal(
            [ActiveRunState.Running, ActiveRunState.Waiting, ActiveRunState.Waiting],
            h.Engine.ActiveRuns.OrderByDescending(run => run.State).Select(run => run.State));

        first.Finish();
        GateCall second = await h.Gate.NextStartAsync();
        Assert.Equal("b", NameOf(second));
        Assert.True((await CommandHarness.Finished(a)).Succeeded);
        Assert.False(c.Completion.IsCompleted);

        second.Finish();
        GateCall third = await h.Gate.NextStartAsync();
        Assert.Equal("c", NameOf(third));
        third.Finish();

        Assert.True((await CommandHarness.Finished(b)).Succeeded);
        Assert.True((await CommandHarness.Finished(c)).Succeeded);
        Assert.Empty(h.Engine.ActiveRuns);
    }

    [Fact]
    public async Task Mode_queue_rejects_requests_beyond_its_limit_with_busy()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync(new CommandEngineOptions { QueueLimit = 1 });
        await h.SaveAsync(Cmd.Gated("c", ConcurrencyModes.Queue, parameters: Name));

        RunHandle running = await h.StartAsync("c", ("Name", "running"));
        await h.Gate!.NextStartAsync();
        RunHandle waiting = await h.StartAsync("c", ("Name", "waiting"));
        RunHandle overflow = await h.StartAsync("c", ("Name", "overflow"));

        Assert.True(running.IsAccepted);
        Assert.True(waiting.IsAccepted);
        Assert.False(overflow.IsAccepted);
        Assert.Equal(ErrorCodes.Busy, (await CommandHarness.Finished(overflow)).ErrorCode);
    }

    [Fact]
    public async Task Mode_queue_drops_a_waiting_run_that_is_cancelled_and_carries_on()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        await h.SaveAsync(Cmd.Gated("c", ConcurrencyModes.Queue, parameters: Name));
        RunHandle a = await h.StartAsync("c", ("Name", "a"));
        GateCall first = await h.Gate!.NextStartAsync();
        RunHandle b = await h.StartAsync("c", ("Name", "b"));
        RunHandle c = await h.StartAsync("c", ("Name", "c"));

        b.Cancel();
        first.Finish();

        RunResult cancelled = await CommandHarness.Finished(b);
        Assert.Equal(ErrorCodes.Cancelled, cancelled.ErrorCode);
        Assert.False(cancelled.Started);
        GateCall next = await h.Gate.NextStartAsync();
        Assert.Equal("c", NameOf(next));
        next.Finish();
        Assert.True((await CommandHarness.Finished(a)).Succeeded);
        Assert.True((await CommandHarness.Finished(c)).Succeeded);
    }

    [Fact]
    public async Task Mode_parallel_lets_the_runs_overlap()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        await h.SaveAsync(Cmd.Gated("c", ConcurrencyModes.Parallel, parameters: Name));

        RunHandle[] handles =
        [
            await h.StartAsync("c", ("Name", "a")),
            await h.StartAsync("c", ("Name", "b")),
            await h.StartAsync("c", ("Name", "c")),
        ];
        var calls = new List<GateCall>();
        for (int index = 0; index < 3; index++)
        {
            calls.Add(await h.Gate!.NextStartAsync());
        }

        // All three are inside the handler at the same time, before any of them finished.
        Assert.All(handles, handle => Assert.True(handle.IsAccepted));
        Assert.All(handles, handle => Assert.False(handle.Completion.IsCompleted));
        Assert.Equal(["a", "b", "c"], calls.Select(NameOf).Order());
        Assert.All(h.Engine.ActiveRuns, run => Assert.Equal(ActiveRunState.Running, run.State));

        // They finish independently, in any order.
        GateCall first = calls.Single(call => NameOf(call) == "a");
        GateCall second = calls.Single(call => NameOf(call) == "b");
        GateCall third = calls.Single(call => NameOf(call) == "c");
        third.Finish();
        Assert.True((await CommandHarness.Finished(handles[2])).Succeeded);
        Assert.False(handles[0].Completion.IsCompleted);
        first.Finish();
        second.Finish();
        foreach (RunHandle handle in handles)
        {
            Assert.True((await CommandHarness.Finished(handle)).Succeeded);
        }
    }

    [Fact]
    public async Task Mode_parallel_rejects_runs_beyond_its_limit_with_busy()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync(new CommandEngineOptions { ParallelLimit = 2 });
        await h.SaveAsync(Cmd.Gated("c", ConcurrencyModes.Parallel, parameters: Name));

        RunHandle a = await h.StartAsync("c", ("Name", "a"));
        RunHandle b = await h.StartAsync("c", ("Name", "b"));
        RunHandle c = await h.StartAsync("c", ("Name", "c"));

        Assert.True(a.IsAccepted);
        Assert.True(b.IsAccepted);
        Assert.False(c.IsAccepted);
        Assert.Equal(ErrorCodes.Busy, (await CommandHarness.Finished(c)).ErrorCode);
    }

    [Fact]
    public async Task Mode_restart_cancels_the_running_run_and_then_starts_the_new_one()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        await h.SaveAsync(Cmd.Gated("c", ConcurrencyModes.Restart, parameters: Name));

        RunHandle old = await h.StartAsync("c", ("Name", "old"));
        GateCall oldCall = await h.Gate!.NextStartAsync();
        RunHandle replacement = await h.StartAsync("c", ("Name", "new"));

        RunResult oldResult = await CommandHarness.Finished(old);
        Assert.Equal(ErrorCodes.Cancelled, oldResult.ErrorCode);
        Assert.True(oldResult.Started);
        Assert.True(oldCall.WasCancelled);

        // The new run starts only after the old one has ended, so the two never overlap.
        GateCall newCall = await h.Gate.NextStartAsync();
        Assert.Equal("new", NameOf(newCall));
        Assert.True(old.Completion.IsCompleted);
        newCall.Finish();
        Assert.True((await CommandHarness.Finished(replacement)).Succeeded);
        Assert.False(newCall.WasCancelled);
    }

    [Fact]
    public async Task Mode_restart_replaces_a_run_that_has_not_started_yet()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        await h.SaveAsync(Cmd.Gated("c", ConcurrencyModes.Restart, parameters: Name));

        RunHandle a = await h.StartAsync("c", ("Name", "a"));
        await h.Gate!.NextStartAsync();
        RunHandle b = await h.StartAsync("c", ("Name", "b"));
        RunHandle c = await h.StartAsync("c", ("Name", "c"));

        // a was running, b was waiting for a: both are cancelled, only c runs.
        Assert.Equal(ErrorCodes.Cancelled, (await CommandHarness.Finished(a)).ErrorCode);
        RunResult dropped = await CommandHarness.Finished(b);
        Assert.Equal(ErrorCodes.Cancelled, dropped.ErrorCode);
        Assert.False(dropped.Started);

        GateCall last = await h.Gate.NextStartAsync();
        Assert.Equal("c", NameOf(last));
        last.Finish();
        Assert.True((await CommandHarness.Finished(c)).Succeeded);
        Assert.Equal(["a", "c"], h.Gate.Calls.Select(NameOf));
    }

    [Fact]
    public async Task Different_commands_never_block_each_other()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        await h.SaveAsync(Cmd.Gated("one"), Cmd.Gated("two"));

        RunHandle first = await h.StartAsync("one");
        RunHandle second = await h.StartAsync("two");
        GateCall a = await h.Gate!.NextStartAsync();
        GateCall b = await h.Gate.NextStartAsync();

        Assert.True(first.IsAccepted);
        Assert.True(second.IsAccepted);
        a.Finish();
        b.Finish();
        Assert.True((await CommandHarness.Finished(first)).Succeeded);
        Assert.True((await CommandHarness.Finished(second)).Succeeded);
    }

    [Fact]
    public async Task A_cancelled_run_frees_its_slot()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        await h.SaveAsync(Cmd.Gated("c", ConcurrencyModes.Exclusive));
        RunHandle running = await h.StartAsync("c");
        await h.Gate!.NextStartAsync();

        running.Cancel();
        Assert.Equal(ErrorCodes.Cancelled, (await CommandHarness.Finished(running)).ErrorCode);

        RunHandle next = await h.StartAsync("c");
        Assert.True(next.IsAccepted);
    }

    [Fact]
    public async Task Active_runs_are_listed_until_they_finish_and_can_be_cancelled_by_id()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        await h.SaveAsync(Cmd.Gated("c"));
        RunHandle handle = await h.StartAsync("c");
        await h.Gate!.NextStartAsync();

        ActiveRunInfo active = Assert.Single(h.Engine.ActiveRuns);
        Assert.Equal(handle.RunId, active.RunId);
        Assert.Equal("c", active.CommandId);
        Assert.Equal(RunSources.Ui, active.Source);
        Assert.Equal(ActiveRunState.Running, active.State);

        Assert.True(h.Engine.Cancel(handle.RunId));
        Assert.Equal(ErrorCodes.Cancelled, (await CommandHarness.Finished(handle)).ErrorCode);
        Assert.Empty(h.Engine.ActiveRuns);
        Assert.False(h.Engine.Cancel(handle.RunId));
        Assert.False(h.Engine.Cancel("no-such-run"));
    }
}
