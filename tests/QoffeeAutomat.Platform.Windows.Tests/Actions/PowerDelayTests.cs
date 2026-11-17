using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Actions;
using QoffeeAutomat.Platform.Windows.Actions.Power;

namespace QoffeeAutomat.Platform.Windows.Tests.Actions;

/// <summary>
/// Delayed power actions (PLAN.md S11, S16, T715): the pending action, the countdown, the cancel and the event. The clock is a
/// <see cref="FakeClock"/> and the power calls go to <see cref="FakePowerNativeApi"/>: no test waits for real time and no test can
/// shut down, restart, suspend or log off the computer that runs it.
/// </summary>
[Trait("Category", "PowerDelay")]
public sealed class PowerDelayTests : IDisposable
{
    private readonly FakeClock _clock = new();
    private readonly FakePowerNativeApi _power = new();
    private readonly FakeCountdownPresenter _presenter = new();
    private readonly EventBus _bus;
    private readonly EventSubscription _cancelled;
    private readonly PowerActionScheduler _scheduler;
    private readonly WindowsBuiltinActions _actions;

    public PowerDelayTests()
    {
        _bus = new EventBus(_clock);
        _cancelled = _bus.Subscribe([EventTypes.PowerActionCancelled]);
        _scheduler = new PowerActionScheduler(_clock, _presenter, _bus);
        _actions = new WindowsBuiltinActions(_power, _scheduler);
    }

    public void Dispose()
    {
        _cancelled.Dispose();
        _bus.Dispose();
    }

    private static Dictionary<string, object> Delayed(long seconds, string? message = null, bool force = false)
    {
        var variables = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [PowerActionSpec.DelaySeconds] = seconds,
        };
        if (message is not null)
        {
            variables[PowerActionSpec.Message] = message;
        }

        if (force)
        {
            variables[WindowsActionParameters.Force] = true;
        }

        return variables;
    }

    private Task<ExecutionResult> Start(string action, IReadOnlyDictionary<string, object> variables, CancellationToken cancellationToken = default)
    {
        return _actions.ExecuteAsync(action, variables, cancellationToken).AsTask();
    }

    private AgentEvent ReadCancelledEvent()
    {
        bool read = _cancelled.Events.TryRead(out AgentEvent? published);
        Assert.True(read, "The event power_action_cancelled was not published.");
        return published!;
    }

    private static JsonElement DataOf(AgentEvent published) => published.Data!.Value;

    private void AssertNoCancelledEvent()
    {
        Assert.False(_cancelled.Events.TryRead(out _), "No event power_action_cancelled was expected.");
    }

    [Fact]
    public async Task A_delayed_shutdown_waits_for_the_time_and_then_shuts_down_once()
    {
        Task<ExecutionResult> run = Start(WindowsActionNames.Shutdown, Delayed(300));

        Assert.False(run.IsCompleted);
        Assert.Empty(_power.Calls);
        Assert.NotNull(_scheduler.Current);

        _clock.Advance(TimeSpan.FromSeconds(299));
        Assert.False(run.IsCompleted);
        Assert.Empty(_power.Calls);

        _clock.Advance(TimeSpan.FromSeconds(1));
        ExecutionResult result = await run.WaitAsync(TestTimeout);

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(_power.Calls, "shutdown");
        Assert.Null(_scheduler.Current);
        AssertNoCancelledEvent();
    }

    [Theory]
    [InlineData(WindowsActionNames.Shutdown, "shutdown")]
    [InlineData(WindowsActionNames.Restart, "restart")]
    [InlineData(WindowsActionNames.Sleep, "sleep")]
    [InlineData(WindowsActionNames.Hibernate, "hibernate")]
    public async Task Every_power_action_can_be_delayed(string action, string expectedCall)
    {
        Task<ExecutionResult> run = Start(action, Delayed(10));
        _clock.Advance(TimeSpan.FromSeconds(10));

        ExecutionResult result = await run.WaitAsync(TestTimeout);

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(_power.Calls, expectedCall);
    }

    [Fact]
    public async Task Force_is_kept_through_the_delay()
    {
        Task<ExecutionResult> run = Start(WindowsActionNames.Restart, Delayed(5, force: true));
        _clock.Advance(TimeSpan.FromSeconds(5));

        await run.WaitAsync(TestTimeout);

        ActionTestData.AssertCalls(_power.Calls, "restart:force");
    }

    [Fact]
    public async Task Without_a_delay_or_with_zero_the_action_acts_at_once_and_shows_no_dialog()
    {
        ExecutionResult none = await Start(WindowsActionNames.Shutdown, ActionTestData.Vars());
        ExecutionResult zero = await Start(WindowsActionNames.Restart, Delayed(0));

        Assert.True(none.Succeeded);
        Assert.True(zero.Succeeded);
        ActionTestData.AssertCalls(_power.Calls, "shutdown", "restart");
        Assert.Empty(_presenter.Requests);
        Assert.Null(_scheduler.Current);
    }

    [Fact]
    public async Task The_dialog_gets_the_title_the_message_and_the_whole_delay()
    {
        Task<ExecutionResult> run = Start(WindowsActionNames.Shutdown, Delayed(120, "Home Assistant is turning the PC off for the night."));

        CountdownRequest request = Assert.Single(_presenter.Requests);
        Assert.Equal(WindowsActionNames.Shutdown, request.Action);
        Assert.Equal("Shut down", request.Title);
        Assert.Equal("Home Assistant is turning the PC off for the night.", request.Message);
        Assert.Equal(120, request.Seconds);
        Assert.True(_presenter.IsOpen);

        _clock.Advance(TimeSpan.FromSeconds(120));
        await run.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task A_blank_message_is_no_message()
    {
        Task<ExecutionResult> run = Start(WindowsActionNames.Sleep, Delayed(5, "   "));

        Assert.Null(Assert.Single(_presenter.Requests).Message);

        _clock.Advance(TimeSpan.FromSeconds(5));
        await run.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task When_the_time_is_over_the_dialog_is_closed_by_the_agent()
    {
        Task<ExecutionResult> run = Start(WindowsActionNames.Shutdown, Delayed(30));
        Assert.True(_presenter.IsOpen);

        _clock.Advance(TimeSpan.FromSeconds(30));
        await run.WaitAsync(TestTimeout);

        Assert.True(_presenter.ClosedByAgent);
        Assert.False(_presenter.IsOpen);
    }

    [Fact]
    public async Task Cancelling_in_the_dialog_ends_the_run_with_cancelled_and_nothing_is_shut_down()
    {
        Task<ExecutionResult> run = Start(WindowsActionNames.Shutdown, Delayed(300));

        _presenter.PressCancel();
        ExecutionResult result = await run.WaitAsync(TestTimeout);

        Assert.Equal(ErrorCodes.Cancelled, result.ErrorCode);
        Assert.False(result.Succeeded);
        Assert.Contains("cancelled", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(_power.Calls);
        Assert.Null(_scheduler.Current);

        // The time that was left no longer matters: the cancelled action must not act later.
        _clock.Advance(TimeSpan.FromSeconds(300));
        Assert.Empty(_power.Calls);
    }

    [Fact]
    public async Task Cancelling_in_the_dialog_publishes_power_action_cancelled_with_the_source_dialog()
    {
        Task<ExecutionResult> run = Start(WindowsActionNames.Restart, Delayed(90));

        _presenter.PressCancel();
        await run.WaitAsync(TestTimeout);

        AgentEvent published = ReadCancelledEvent();
        Assert.Equal("power_action_cancelled", published.Type);
        Assert.Equal("power.restart", DataOf(published).GetProperty("action").GetString());
        Assert.Equal("dialog", DataOf(published).GetProperty("source").GetString());
        Assert.Equal(90, DataOf(published).GetProperty("delay_seconds").GetInt32());
        AssertNoCancelledEvent();
    }

    [Fact]
    public async Task The_action_power_cancel_ends_the_pending_run_with_cancelled_and_publishes_the_event()
    {
        Task<ExecutionResult> run = Start(WindowsActionNames.Shutdown, Delayed(600));

        ExecutionResult cancel = await Start(WindowsActionNames.CancelPending, ActionTestData.Vars());
        ExecutionResult result = await run.WaitAsync(TestTimeout);

        Assert.True(cancel.Succeeded);
        Assert.Contains("cancelled", cancel.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(ErrorCodes.Cancelled, result.ErrorCode);
        Assert.Empty(_power.Calls);
        Assert.True(_presenter.ClosedByAgent); // the dialog goes away when the action was cancelled elsewhere
        AgentEvent published = ReadCancelledEvent();
        Assert.Equal("action", DataOf(published).GetProperty("source").GetString());
        Assert.Equal("power.shutdown", DataOf(published).GetProperty("action").GetString());
    }

    [Fact]
    public async Task Cancelling_when_nothing_is_pending_is_not_an_error_and_publishes_nothing()
    {
        ExecutionResult result = await Start(WindowsActionNames.CancelPending, ActionTestData.Vars());

        Assert.True(result.Succeeded);
        Assert.Contains("No delayed power action", result.StandardOutput, StringComparison.Ordinal);
        AssertNoCancelledEvent();
    }

    [Fact]
    public async Task Cancelling_twice_cancels_once()
    {
        Task<ExecutionResult> run = Start(WindowsActionNames.Sleep, Delayed(60));

        ExecutionResult first = await Start(WindowsActionNames.CancelPending, ActionTestData.Vars());
        ExecutionResult second = await Start(WindowsActionNames.CancelPending, ActionTestData.Vars());
        await run.WaitAsync(TestTimeout);

        Assert.Contains("was cancelled", first.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("No delayed power action", second.StandardOutput, StringComparison.Ordinal);
        ReadCancelledEvent();
        AssertNoCancelledEvent();
    }

    [Fact]
    public async Task A_second_delayed_action_is_refused_clearly_while_one_is_pending_and_the_first_is_not_disturbed()
    {
        Task<ExecutionResult> first = Start(WindowsActionNames.Shutdown, Delayed(300));

        ExecutionResult second = await Start(WindowsActionNames.Restart, Delayed(60));

        Assert.Equal(ErrorCodes.Busy, second.ErrorCode);
        Assert.Contains("already pending", second.StandardError, StringComparison.Ordinal);
        Assert.Contains("power.cancel", second.StandardError, StringComparison.Ordinal);
        Assert.Single(_presenter.Requests); // no second dialog
        Assert.Equal(WindowsActionNames.Shutdown, _scheduler.Current!.Action);

        _clock.Advance(TimeSpan.FromSeconds(300));
        ExecutionResult firstResult = await first.WaitAsync(TestTimeout);
        Assert.True(firstResult.Succeeded);
        ActionTestData.AssertCalls(_power.Calls, "shutdown");
        AssertNoCancelledEvent();
    }

    [Fact]
    public async Task The_same_action_delayed_twice_is_refused_too()
    {
        Task<ExecutionResult> first = Start(WindowsActionNames.Sleep, Delayed(300));

        ExecutionResult second = await Start(WindowsActionNames.Sleep, Delayed(300));

        Assert.Equal(ErrorCodes.Busy, second.ErrorCode);
        _scheduler.Cancel(PowerCancelSources.Action);
        await first.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task After_a_delayed_action_ended_the_next_one_is_accepted()
    {
        Task<ExecutionResult> first = Start(WindowsActionNames.Shutdown, Delayed(10));
        _presenter.PressCancel();
        await first.WaitAsync(TestTimeout);

        Task<ExecutionResult> second = Start(WindowsActionNames.Restart, Delayed(10));
        Assert.NotNull(_scheduler.Current);
        _clock.Advance(TimeSpan.FromSeconds(10));
        ExecutionResult result = await second.WaitAsync(TestTimeout);

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(_power.Calls, "restart");
    }

    [Fact]
    public async Task An_immediate_action_is_not_blocked_by_a_pending_one_and_leaves_it_alone()
    {
        Task<ExecutionResult> pending = Start(WindowsActionNames.Shutdown, Delayed(300));

        ExecutionResult immediate = await Start(WindowsActionNames.Restart, ActionTestData.Vars());

        Assert.True(immediate.Succeeded);
        ActionTestData.AssertCalls(_power.Calls, "restart");
        Assert.NotNull(_scheduler.Current);
        _scheduler.Cancel(PowerCancelSources.Action);
        await pending.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Without_a_user_the_delay_simply_elapses()
    {
        _presenter.Immediate = CountdownDialogResult.Unavailable;

        Task<ExecutionResult> run = Start(WindowsActionNames.Shutdown, Delayed(60));

        Assert.False(run.IsCompleted);
        Assert.Empty(_power.Calls);
        _clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(_power.Calls);
        _clock.Advance(TimeSpan.FromSeconds(1));
        ExecutionResult result = await run.WaitAsync(TestTimeout);

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(_power.Calls, "shutdown");
    }

    [Fact]
    public async Task Without_any_dialog_at_all_the_delay_simply_elapses_and_it_can_still_be_cancelled_by_the_action()
    {
        var scheduler = new PowerActionScheduler(_clock);
        var actions = new WindowsBuiltinActions(_power, scheduler);

        Task<ExecutionResult> elapsing = actions.ExecuteAsync(WindowsActionNames.Sleep, Delayed(5), CancellationToken.None).AsTask();
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True((await elapsing.WaitAsync(TestTimeout)).Succeeded);

        Task<ExecutionResult> cancelled = actions.ExecuteAsync(WindowsActionNames.Shutdown, Delayed(5), CancellationToken.None).AsTask();
        ExecutionResult cancel = await actions.ExecuteAsync(WindowsActionNames.CancelPending, ActionTestData.Vars(), CancellationToken.None);
        ExecutionResult result = await cancelled.WaitAsync(TestTimeout);

        Assert.True(cancel.Succeeded);
        Assert.Equal(ErrorCodes.Cancelled, result.ErrorCode);
        ActionTestData.AssertCalls(_power.Calls, "sleep");
    }

    [Fact]
    public async Task A_dialog_that_fails_never_stops_the_countdown()
    {
        _presenter.Failure = new InvalidOperationException("The session helper is gone.");

        Task<ExecutionResult> run = Start(WindowsActionNames.Restart, Delayed(20));
        _clock.Advance(TimeSpan.FromSeconds(20));
        ExecutionResult result = await run.WaitAsync(TestTimeout);

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(_power.Calls, "restart");
    }

    [Fact]
    public async Task A_dialog_that_ends_by_itself_does_not_cancel_and_the_clock_decides()
    {
        Task<ExecutionResult> run = Start(WindowsActionNames.Hibernate, Delayed(40));

        _presenter.EndByItself();
        Assert.False(run.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(40));
        ExecutionResult result = await run.WaitAsync(TestTimeout);

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(_power.Calls, "hibernate");
    }

    [Fact]
    public async Task A_cancelled_run_publishes_the_event_with_the_source_run_and_frees_the_slot()
    {
        using var cancellation = new CancellationTokenSource();
        Task<ExecutionResult> run = Start(WindowsActionNames.Shutdown, Delayed(300), cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TestTimeout));
        Assert.Empty(_power.Calls);
        Assert.Null(_scheduler.Current);
        Assert.Equal("run", DataOf(ReadCancelledEvent()).GetProperty("source").GetString());
        Assert.True(_presenter.ClosedByAgent);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(PowerActionSpec.MaxDelaySeconds + 1L)]
    public async Task A_delay_outside_the_range_is_invalid_vars_and_nothing_starts(long seconds)
    {
        ExecutionResult result = await Start(WindowsActionNames.Shutdown, Delayed(seconds));

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(_power.Calls);
        Assert.Empty(_presenter.Requests);
        Assert.Null(_scheduler.Current);
    }

    [Fact]
    public async Task The_longest_delay_of_the_plan_is_accepted()
    {
        Task<ExecutionResult> run = Start(WindowsActionNames.Shutdown, Delayed(PowerActionSpec.MaxDelaySeconds));

        Assert.Equal(3600, _scheduler.Current!.DelaySeconds);
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.True((await run.WaitAsync(TestTimeout)).Succeeded);
    }

    [Fact]
    public async Task A_message_that_is_too_long_is_invalid_vars()
    {
        ExecutionResult result = await Start(WindowsActionNames.Shutdown, Delayed(10, new string('x', PowerActionSpec.MaxMessageLength + 1)));

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(_power.Calls);
    }

    [Fact]
    public async Task A_delay_that_is_not_a_whole_number_is_invalid_vars()
    {
        var variables = new Dictionary<string, object> { [PowerActionSpec.DelaySeconds] = "soon" };

        ExecutionResult result = await Start(WindowsActionNames.Shutdown, variables);

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(_power.Calls);
    }

    [Fact]
    public async Task A_delayed_sleep_is_refused_at_once_when_the_computer_cannot_sleep_instead_of_announcing_it()
    {
        _power.CanSleep = false;

        ExecutionResult result = await Start(WindowsActionNames.Sleep, Delayed(300));

        Assert.False(result.Succeeded);
        Assert.Contains("cannot sleep", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(_presenter.Requests);
        Assert.Null(_scheduler.Current);
    }

    [Fact]
    public async Task A_delayed_hibernate_is_refused_at_once_when_hibernation_is_off()
    {
        _power.CanHibernate = false;

        ExecutionResult result = await Start(WindowsActionNames.Hibernate, Delayed(300));

        Assert.False(result.Succeeded);
        Assert.Contains("Hibernation is not enabled", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(_presenter.Requests);
    }

    [Fact]
    public async Task A_failure_of_windows_when_the_time_is_over_is_reported_and_frees_the_slot()
    {
        _power.Failure = new System.ComponentModel.Win32Exception(5, "Access is denied.");
        Task<ExecutionResult> run = Start(WindowsActionNames.Shutdown, Delayed(15));

        _clock.Advance(TimeSpan.FromSeconds(15));
        ExecutionResult result = await run.WaitAsync(TestTimeout);

        Assert.False(result.Succeeded);
        Assert.Contains("failed", result.StandardError, StringComparison.Ordinal);
        Assert.Null(_scheduler.Current);
    }

    [Fact]
    public void The_pending_action_tells_what_waits_and_until_when()
    {
        DateTimeOffset start = _clock.GetUtcNow();
        Task<ExecutionResult> run = Start(WindowsActionNames.Restart, Delayed(45));

        PendingPowerAction pending = _scheduler.Current!;

        Assert.Equal(WindowsActionNames.Restart, pending.Action);
        Assert.Equal("Restart", pending.Title);
        Assert.Equal(45, pending.DelaySeconds);
        Assert.Equal(start, pending.StartedAt);
        Assert.Equal(start.AddSeconds(45), pending.DueAt);
        Assert.False(run.IsCompleted);

        _scheduler.Cancel(PowerCancelSources.Action);
    }

    [Fact]
    public void The_delayable_actions_offer_DelaySeconds_from_0_to_3600_and_Message()
    {
        string[] delayable =
        [
            WindowsActionNames.Shutdown,
            WindowsActionNames.Restart,
            WindowsActionNames.Sleep,
            WindowsActionNames.Hibernate,
            WindowsActionNames.Logoff,
        ];

        foreach (string action in delayable)
        {
            BuiltinActionDescriptor descriptor = WindowsActionCatalog.Find(action)!;
            ParameterDefinition delay = descriptor.Parameters.Single(parameter => parameter.Name == "DelaySeconds");
            ParameterDefinition message = descriptor.Parameters.Single(parameter => parameter.Name == "Message");

            Assert.Equal(ParameterTypes.WholeNumber, delay.Type);
            Assert.Equal(0d, delay.Min);
            Assert.Equal(3600d, delay.Max);
            Assert.Equal(0, delay.Default!.Value.GetInt32());
            Assert.False(delay.Required);
            Assert.Equal(ParameterTypes.Text, message.Type);
            Assert.False(message.Required);
            Assert.Equal(PowerActionSpec.MaxMessageLength, message.MaxLength);
        }
    }

    [Fact]
    public void The_other_actions_do_not_offer_a_delay()
    {
        foreach (BuiltinActionDescriptor descriptor in WindowsActionCatalog.All.Where(action => action.Name is not (
            WindowsActionNames.Shutdown or WindowsActionNames.Restart or WindowsActionNames.Sleep or WindowsActionNames.Hibernate or WindowsActionNames.Logoff)))
        {
            Assert.DoesNotContain(descriptor.Parameters, parameter => parameter.Name == "DelaySeconds");
        }
    }

    [Fact]
    public void The_cancel_action_runs_in_the_service_and_has_no_variables()
    {
        BuiltinActionDescriptor descriptor = WindowsActionCatalog.Find(WindowsActionNames.CancelPending)!;

        Assert.Equal("power.cancel", descriptor.Name);
        Assert.Equal(RunAsValues.System, descriptor.RunAs);
        Assert.Empty(descriptor.Parameters);
    }

    [Fact]
    public void The_event_is_part_of_the_catalog_of_events()
    {
        Assert.True(EventCatalog.Contains("power_action_cancelled"));
        Assert.Equal("power_action_cancelled", EventTypes.PowerActionCancelled);
    }

    [Fact]
    public void The_default_commands_that_can_wait_carry_the_variables_and_wait_longer_than_the_longest_delay()
    {
        foreach (string id in new[] { "power_shutdown", "power_restart", "power_sleep", "power_hibernate", "session_logoff" })
        {
            CommandDocument command = DefaultCommandSeeder.DefaultCommands.Single(candidate => candidate.Id == id);

            Assert.Contains(command.Parameters!, parameter => parameter.Name == "DelaySeconds");
            Assert.Contains(command.Parameters!, parameter => parameter.Name == "Message");
            Assert.True(command.TimeoutSeconds > PowerActionSpec.MaxDelaySeconds, id + " would time out during a countdown.");
        }
    }

    [Fact]
    public async Task The_scheduler_carries_the_action_out_only_when_the_time_is_over_and_never_when_it_was_cancelled()
    {
        // The scheduler only waits; carrying the action out is the job of the delegate it is given.
        int executed = 0;
        Func<CancellationToken, ValueTask<ExecutionResult>> execute = _ =>
        {
            executed++;
            return ValueTask.FromResult(new ExecutionResult(0, "done", string.Empty));
        };

        Task<ExecutionResult> cancelled = _scheduler.RunDelayedAsync("power.shutdown", "Shut down", 5, null, execute, CancellationToken.None).AsTask();
        Assert.NotNull(_scheduler.Cancel(PowerCancelSources.Dialog));
        ExecutionResult cancelledResult = await cancelled.WaitAsync(TestTimeout);
        Assert.Equal(ErrorCodes.Cancelled, cancelledResult.ErrorCode);
        Assert.Equal(0, executed);

        Task<ExecutionResult> elapsing = _scheduler.RunDelayedAsync("power.shutdown", "Shut down", 5, null, execute, CancellationToken.None).AsTask();
        _clock.Advance(TimeSpan.FromSeconds(5));
        ExecutionResult elapsedResult = await elapsing.WaitAsync(TestTimeout);
        Assert.True(elapsedResult.Succeeded);
        Assert.Equal(1, executed);
        Assert.Empty(_power.Calls);
    }

    [Fact]
    public async Task The_scheduler_itself_rejects_a_delay_outside_the_range()
    {
        ExecutionResult zero = await _scheduler.RunDelayedAsync("power.shutdown", "Shut down", 0, null, _ => ValueTask.FromResult(ExecutionResult.Failure(ErrorCodes.Internal, "no")), CancellationToken.None);
        ExecutionResult huge = await _scheduler.RunDelayedAsync("power.shutdown", "Shut down", 3601, null, _ => ValueTask.FromResult(ExecutionResult.Failure(ErrorCodes.Internal, "no")), CancellationToken.None);

        Assert.Equal(ErrorCodes.InvalidVars, zero.ErrorCode);
        Assert.Equal(ErrorCodes.InvalidVars, huge.ErrorCode);
        Assert.Null(_scheduler.Current);
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);
}
