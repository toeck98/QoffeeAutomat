using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Actions;
using QoffeeAutomat.Platform.Windows.Actions.Power;
using QoffeeAutomat.Platform.Windows.Sessions;
using QoffeeAutomat.Platform.Windows.Tests.Actions;
using QoffeeAutomat.SessionHost.Actions;

namespace QoffeeAutomat.Platform.Windows.Tests.Sessions;

/// <summary>
/// The countdown of a delayed power action through the session pipe (PLAN.md S11, T715): the agent's scheduler asks the SessionHost
/// to show the dialog (the internal action <c>power.countdown</c>), the user's cancel comes back as the error <c>cancelled</c>, and
/// <c>session.logoff</c> waits in the same scheduler as the power actions. The pipe is real, the clock is a fake and the desktop, the
/// dialog and the power calls are fakes: nothing here shows a window or ends a session, and no test waits for real time.
/// </summary>
[Trait("Category", "PowerDelay")]
public sealed class PowerCountdownFlowTests
{
    private static Dictionary<string, object> Delayed(long seconds, string? message = null, bool force = false)
    {
        var variables = new Dictionary<string, object>(StringComparer.Ordinal) { [PowerActionSpec.DelaySeconds] = seconds };
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

    private static async Task<Rig> StartAsync(bool withSessionHost = true)
    {
        var rig = new Rig();
        await rig.Harness.StartAsync();
        if (withSessionHost)
        {
            rig.Harness.StartHost();
            await rig.Harness.NextSessionReadyAsync();
        }

        return rig;
    }

    [Fact]
    public async Task A_delayed_logoff_shows_the_dialog_in_the_session_and_logs_off_when_the_time_is_over()
    {
        using Rig rig = await StartAsync();

        Task<ExecutionResult> run = rig.Run(WindowsActionNames.Logoff, Delayed(90, "See you tomorrow.", force: true));
        await TestWait.UntilAsync(() => rig.Dialog.Requests.Count == 1, "the countdown dialog of the session helper");

        Assert.Equal("Log off", rig.Dialog.Requests[0].Title);
        Assert.Equal("See you tomorrow.", rig.Dialog.Requests[0].Message);
        Assert.Equal(TimeSpan.FromSeconds(90), rig.Dialog.Requests[0].Duration);
        Assert.Empty(rig.Desktop.Calls);

        rig.Clock.Advance(TimeSpan.FromSeconds(90));
        ExecutionResult result = await run.WaitAsync(TestWait.Patience);

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(rig.Desktop.Calls, "logoff:force"); // the delay and the message did not go on, Force did
        await TestWait.UntilAsync(() => rig.Dialog.ClosedByAgent, "the agent to close the dialog");
    }

    [Fact]
    public async Task Cancelling_in_the_dialog_of_the_session_cancels_a_delayed_logoff()
    {
        using Rig rig = await StartAsync();
        Task<ExecutionResult> run = rig.Run(WindowsActionNames.Logoff, Delayed(60));
        await TestWait.UntilAsync(() => rig.Dialog.Requests.Count == 1, "the countdown dialog of the session helper");

        rig.Dialog.PressCancel();
        ExecutionResult result = await run.WaitAsync(TestWait.Patience);

        Assert.Equal(ErrorCodes.Cancelled, result.ErrorCode);
        Assert.Empty(rig.Desktop.Calls);
        Assert.Null(rig.Scheduler.Current);

        AgentEvent published = await rig.NextCancelledEventAsync();
        Assert.Equal("session.logoff", published.Data!.Value.GetProperty("action").GetString());
        Assert.Equal("dialog", published.Data!.Value.GetProperty("source").GetString());

        rig.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Empty(rig.Desktop.Calls);
    }

    [Fact]
    public async Task Cancelling_in_the_dialog_of_the_session_cancels_a_delayed_shutdown_and_nothing_is_shut_down()
    {
        using Rig rig = await StartAsync();
        Task<ExecutionResult> run = rig.Run(WindowsActionNames.Shutdown, Delayed(300, "Maintenance"));
        await TestWait.UntilAsync(() => rig.Dialog.Requests.Count == 1, "the countdown dialog of the session helper");
        Assert.Equal("Shut down", rig.Dialog.Requests[0].Title);

        rig.Dialog.PressCancel();
        ExecutionResult result = await run.WaitAsync(TestWait.Patience);

        Assert.Equal(ErrorCodes.Cancelled, result.ErrorCode);
        ActionTestData.AssertCalls(rig.Power.Calls);
        Assert.Equal("dialog", (await rig.NextCancelledEventAsync()).Data!.Value.GetProperty("source").GetString());
    }

    [Fact]
    public async Task The_action_power_cancel_closes_the_dialog_in_the_session_and_ends_the_run_with_cancelled()
    {
        using Rig rig = await StartAsync();
        Task<ExecutionResult> run = rig.Run(WindowsActionNames.Restart, Delayed(120));
        await TestWait.UntilAsync(() => rig.Dialog.Requests.Count == 1, "the countdown dialog of the session helper");

        ExecutionResult cancel = await rig.Run(WindowsActionNames.CancelPending, ActionTestData.Vars());
        ExecutionResult result = await run.WaitAsync(TestWait.Patience);

        Assert.True(cancel.Succeeded);
        Assert.Equal(ErrorCodes.Cancelled, result.ErrorCode);
        ActionTestData.AssertCalls(rig.Power.Calls);
        await TestWait.UntilAsync(() => rig.Dialog.ClosedByAgent, "the agent to close the dialog");
        Assert.Equal("action", (await rig.NextCancelledEventAsync()).Data!.Value.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Only_one_delayed_action_can_be_pending_across_the_power_actions_and_the_logoff()
    {
        using Rig rig = await StartAsync();
        Task<ExecutionResult> logoff = rig.Run(WindowsActionNames.Logoff, Delayed(60));
        await TestWait.UntilAsync(() => rig.Dialog.Requests.Count == 1, "the countdown dialog of the session helper");

        ExecutionResult shutdown = await rig.Run(WindowsActionNames.Shutdown, Delayed(30));
        ExecutionResult secondLogoff = await rig.Run(WindowsActionNames.Logoff, Delayed(30));

        Assert.Equal(ErrorCodes.Busy, shutdown.ErrorCode);
        Assert.Equal(ErrorCodes.Busy, secondLogoff.ErrorCode);
        Assert.Single(rig.Dialog.Requests);

        rig.Scheduler.Cancel(PowerCancelSources.Action);
        Assert.Equal(ErrorCodes.Cancelled, (await logoff.WaitAsync(TestWait.Patience)).ErrorCode);
    }

    [Fact]
    public async Task Without_a_session_helper_the_delay_of_a_power_action_simply_elapses()
    {
        using Rig rig = await StartAsync(withSessionHost: false);

        Task<ExecutionResult> run = rig.Run(WindowsActionNames.Shutdown, Delayed(45));
        Assert.False(run.IsCompleted);
        Assert.Empty(rig.Dialog.Requests);
        rig.Clock.Advance(TimeSpan.FromSeconds(45));
        ExecutionResult result = await run.WaitAsync(TestWait.Patience);

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(rig.Power.Calls, "shutdown");
    }

    [Fact]
    public async Task A_delayed_logoff_without_a_user_ends_with_no_session_at_once_instead_of_after_the_delay()
    {
        using Rig rig = await StartAsync(withSessionHost: false);

        ExecutionResult result = await rig.Run(WindowsActionNames.Logoff, Delayed(600));

        Assert.Equal(ErrorCodes.NoSession, result.ErrorCode);
        Assert.Null(rig.Scheduler.Current);
        Assert.Empty(rig.Desktop.Calls);
    }

    [Fact]
    public async Task A_logoff_without_a_delay_goes_straight_to_the_session_and_shows_no_dialog()
    {
        using Rig rig = await StartAsync();

        ExecutionResult result = await rig.Run(WindowsActionNames.Logoff, ActionTestData.Vars((WindowsActionParameters.Force, true)));

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(rig.Desktop.Calls, "logoff:force");
        Assert.Empty(rig.Dialog.Requests);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(3601L)]
    public async Task A_logoff_delay_outside_the_range_is_invalid_vars_and_nothing_starts(long seconds)
    {
        using Rig rig = await StartAsync();

        ExecutionResult result = await rig.Run(WindowsActionNames.Logoff, Delayed(seconds));

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(rig.Desktop.Calls);
        Assert.Empty(rig.Dialog.Requests);
    }

    /// <summary>The agent and the session helper of a test with all fakes in place.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly EventSubscription _cancelled;

        public Rig()
        {
            Desktop = new FakeDesktopNativeApi();
            Dialog = new FakeCountdownDialog();
            Power = new FakePowerNativeApi();
            Clock = new FakeClock();
            var hostActions = new UserBuiltinActions(Desktop, new FakeAudioEndpoint(), new FakeProcessControl(), countdown: Dialog);
            Harness = new SessionHarness(hostBuiltins: hostActions);
            Scheduler = new PowerActionScheduler(Clock, new SessionHostCountdownPresenter(Harness.Bridge), Harness.Bus);
            Actions = new UserContextBuiltinActions(
                new WindowsBuiltinActions(Power, Scheduler),
                Harness.Bridge,
                scheduler: Scheduler);
            _cancelled = Harness.Bus.Subscribe([EventTypes.PowerActionCancelled]);
        }

        public FakeDesktopNativeApi Desktop { get; }

        public FakeCountdownDialog Dialog { get; }

        public FakePowerNativeApi Power { get; }

        public FakeClock Clock { get; }

        public SessionHarness Harness { get; }

        public PowerActionScheduler Scheduler { get; }

        public UserContextBuiltinActions Actions { get; }

        public Task<ExecutionResult> Run(string action, IReadOnlyDictionary<string, object> variables)
        {
            return Actions.ExecuteAsync(action, variables, CancellationToken.None).AsTask();
        }

        public async Task<AgentEvent> NextCancelledEventAsync()
        {
            using var timeout = new CancellationTokenSource(TestWait.Patience);
            return await _cancelled.Events.ReadAsync(timeout.Token);
        }

        public void Dispose()
        {
            _cancelled.Dispose();
            Harness.Dispose();
        }
    }
}
