using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Modes;

namespace QoffeeAutomat.Core.Tests.Modes;

/// <summary>The switch of a mode group (PLAN.md S06, T717): the order of the steps, onError, mode_changed and one switch at a time.</summary>
[Trait("Category", "Modes")]
public sealed class ModeSwitchTests
{
    [Fact]
    public async Task Switch_runs_the_exit_steps_of_the_old_mode_before_the_enter_steps_of_the_new_one()
    {
        await using ModeHarness h = await ModeHarness.CreateAsync();
        await h.SeedAsync(ModeDocs.Standard());

        ModeSwitchResult first = await h.SwitchAsync("room", "a");
        Assert.True(first.Succeeded);
        Assert.Equal(["a_enter"], h.Runner.Calls);

        ModeSwitchResult second = await h.SwitchAsync("room", "b");

        Assert.True(second.Succeeded);
        Assert.Equal("a", second.From);
        Assert.Equal(["a_enter", "a_exit", "b_enter1", "b_enter2"], h.Runner.Calls);
    }

    [Fact]
    public async Task State_changes_to_the_new_mode_only_after_the_enter_steps_finished()
    {
        await using ModeHarness h = await ModeHarness.CreateAsync();
        await h.SeedAsync(ModeDocs.Standard());
        await h.SwitchAsync("room", "a");

        await h.SwitchAsync("room", "b");

        // While the exit and enter steps ran, the old mode was still the current one.
        Assert.Equal("a", h.Runner.CurrentModeDuring("a_exit"));
        Assert.Equal("a", h.Runner.CurrentModeDuring("b_enter1"));
        Assert.Equal("a", h.Runner.CurrentModeDuring("b_enter2"));
        Assert.Equal("b", h.Engine.GetCurrent("room"));
        Assert.Equal("b", h.Engine.GetState("room").Current);
    }

    [Fact]
    public async Task Switch_fires_mode_changed_with_group_from_and_to()
    {
        await using ModeHarness h = await ModeHarness.CreateAsync();
        await h.SeedAsync(ModeDocs.Standard());

        await h.SwitchAsync("room", "a");
        await h.SwitchAsync("room", "b");

        Assert.Equal([("room", (string?)null, "a"), ("room", "a", "b")], h.TakeChangedEvents());
    }

    [Fact]
    public async Task Selecting_the_current_mode_runs_its_enter_steps_again_without_exit_steps_and_without_an_event()
    {
        await using ModeHarness h = await ModeHarness.CreateAsync();
        await h.SeedAsync(ModeDocs.Standard());
        await h.SwitchAsync("room", "b");
        h.TakeChangedEvents();
        int before = h.Runner.Calls.Count;

        ModeSwitchResult again = await h.SwitchAsync("room", "b");

        Assert.True(again.Succeeded);
        Assert.Equal(["b_enter1", "b_enter2"], h.Runner.Calls.Skip(before));
        Assert.Empty(h.TakeChangedEvents());
    }

    [Fact]
    public async Task Failing_step_with_on_error_stop_leaves_the_state_unknown_and_fires_no_event()
    {
        await using ModeHarness h = await ModeHarness.CreateAsync();
        await h.SeedAsync(ModeDocs.Standard(onError: SequenceErrorModes.Stop));
        await h.SwitchAsync("room", "a");
        h.TakeChangedEvents();
        h.Runner.Fail("b_enter1");

        ModeSwitchResult result = await h.SwitchAsync("room", "b");

        Assert.False(result.Completed);
        Assert.False(result.Succeeded);
        Assert.Equal(1, result.FailedSteps);
        Assert.DoesNotContain("b_enter2", h.Runner.Calls);
        Assert.Null(h.Engine.GetCurrent("room"));
        Assert.Empty(h.TakeChangedEvents());
    }

    [Fact]
    public async Task Failing_exit_step_with_on_error_stop_does_not_run_the_enter_steps()
    {
        await using ModeHarness h = await ModeHarness.CreateAsync();
        await h.SeedAsync(ModeDocs.Standard(onError: SequenceErrorModes.Stop));
        await h.SwitchAsync("room", "a");
        h.TakeChangedEvents();
        h.Runner.Fail("a_exit");

        ModeSwitchResult result = await h.SwitchAsync("room", "b");

        Assert.False(result.Completed);
        Assert.DoesNotContain("b_enter1", h.Runner.Calls);
        Assert.Null(h.Engine.GetCurrent("room"));
        Assert.Empty(h.TakeChangedEvents());
    }

    [Fact]
    public async Task Failing_step_with_on_error_continue_completes_the_switch()
    {
        await using ModeHarness h = await ModeHarness.CreateAsync();
        await h.SeedAsync(ModeDocs.Standard(onError: SequenceErrorModes.Continue));
        await h.SwitchAsync("room", "a");
        h.TakeChangedEvents();
        h.Runner.Fail("b_enter1");

        ModeSwitchResult result = await h.SwitchAsync("room", "b");

        Assert.True(result.Completed);
        Assert.False(result.Succeeded);
        Assert.Equal(1, result.FailedSteps);
        Assert.Contains("b_enter2", h.Runner.Calls);
        Assert.Equal("b", h.Engine.GetCurrent("room"));
        Assert.Equal([("room", (string?)"a", "b")], h.TakeChangedEvents());
    }

    [Fact]
    public async Task A_successful_switch_after_a_failed_one_makes_the_state_known_again()
    {
        await using ModeHarness h = await ModeHarness.CreateAsync();
        await h.SeedAsync(ModeDocs.Standard());
        await h.SwitchAsync("room", "a");
        h.Runner.Fail("b_enter1");
        await h.SwitchAsync("room", "b");
        Assert.Null(h.Engine.GetCurrent("room"));
        h.TakeChangedEvents();

        ModeSwitchResult result = await h.SwitchAsync("room", "a");

        Assert.True(result.Succeeded);
        Assert.Null(result.From);
        Assert.Equal("a", h.Engine.GetCurrent("room"));
        Assert.Equal([("room", (string?)null, "a")], h.TakeChangedEvents());
    }

    [Fact]
    public async Task Second_switch_during_a_running_switch_is_rejected_with_busy()
    {
        await using ModeHarness h = await ModeHarness.CreateAsync();
        await h.SeedAsync(ModeDocs.Standard());
        await h.SwitchAsync("room", "a");
        h.TakeChangedEvents();
        Task reached = h.Runner.Gate("b_enter1");

        Task<ModeSwitchResult> running = h.SwitchAsync("room", "b");
        await reached.WaitAsync(TimeSpan.FromSeconds(10));

        ModeState state = h.Engine.GetState("room");
        Assert.True(state.Switching);
        Assert.Equal("b", state.Target);

        ModeSwitchResult rejected = await h.SwitchAsync("room", "a");

        Assert.False(rejected.Started);
        Assert.Equal(ErrorCodes.Busy, rejected.ErrorCode);
        Assert.DoesNotContain("a_enter", h.Runner.Calls.Skip(1));

        h.Runner.Release("b_enter1");
        ModeSwitchResult finished = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(finished.Succeeded);
        Assert.Equal("b", h.Engine.GetCurrent("room"));
        Assert.False(h.Engine.GetState("room").Switching);

        // The slot is free again.
        Assert.True((await h.SwitchAsync("room", "a")).Succeeded);
    }

    [Fact]
    public async Task Switches_of_different_groups_do_not_block_each_other()
    {
        await using ModeHarness h = await ModeHarness.CreateAsync();
        await h.SeedAsync(ModeDocs.Standard());
        await h.SeedAsync(ModeDocs.Group("desk", "x", ModeBootBehaviors.Reset, SequenceErrorModes.Stop, ModeDocs.Mode("x", ["x_enter"])));
        Task reached = h.Runner.Gate("b_enter1");

        Task<ModeSwitchResult> running = h.SwitchAsync("room", "b");
        await reached.WaitAsync(TimeSpan.FromSeconds(10));

        ModeSwitchResult other = await h.SwitchAsync("desk", "x");
        Assert.True(other.Succeeded);

        h.Runner.Release("b_enter1");
        await running.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Unknown_group_and_unknown_mode_are_rejected_before_anything_runs()
    {
        await using ModeHarness h = await ModeHarness.CreateAsync();
        await h.SeedAsync(ModeDocs.Standard());

        ModeSwitchResult noGroup = await h.SwitchAsync("nope", "a");
        ModeSwitchResult noMode = await h.SwitchAsync("room", "nope");

        Assert.False(noGroup.Started);
        Assert.Equal(ErrorCodes.UnknownCommand, noGroup.ErrorCode);
        Assert.False(noMode.Started);
        Assert.Equal(ErrorCodes.InvalidVars, noMode.ErrorCode);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task Cancelled_switch_leaves_the_state_unknown_and_fires_no_event()
    {
        await using ModeHarness h = await ModeHarness.CreateAsync();
        await h.SeedAsync(ModeDocs.Standard());
        await h.SwitchAsync("room", "a");
        h.TakeChangedEvents();
        Task reached = h.Runner.Gate("b_enter1");
        using var cts = new CancellationTokenSource();

        Task<ModeSwitchResult> running = h.Engine.SwitchAsync("room", "b", h.Runner.RunAsync, cts.Token);
        await reached.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Null(h.Engine.GetCurrent("room"));
        Assert.Empty(h.TakeChangedEvents());
    }
}
