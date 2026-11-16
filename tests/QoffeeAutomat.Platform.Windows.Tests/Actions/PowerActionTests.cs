using System.ComponentModel;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Actions;

namespace QoffeeAutomat.Platform.Windows.Tests.Actions;

/// <summary>
/// The power actions (shutdown, restart, sleep, hibernate; PLAN.md S11, T306). They are destructive, so they run only against
/// <see cref="FakePowerNativeApi"/>: nothing in this class can end, restart or suspend the computer that runs the tests.
/// </summary>
[Trait("Category", "Actions")]
public sealed class PowerActionTests
{
    private static readonly IReadOnlyDictionary<string, object> NoVariables = new Dictionary<string, object>();

    private static async Task<ExecutionResult> RunAsync(WindowsBuiltinActions provider, string action, IReadOnlyDictionary<string, object>? variables = null)
    {
        return await provider.ExecuteAsync(action, variables ?? NoVariables, CancellationToken.None);
    }

    private static CommandDocument Command(string action) => new(
        DocumentSchema.CurrentVersion,
        "power_test",
        "Power test",
        Enabled: true,
        ExposeToHa: false,
        RunAsValues.System,
        new BuiltinAction(action));

    private static ActionContext Context(string action, IReadOnlyDictionary<string, object> variables)
    {
        return new ActionContext(
            Command(action),
            variables,
            "run1",
            RunSources.Ui,
            "Test-PC",
            TimeSpan.FromSeconds(30),
            new Dictionary<string, string>(),
            new RecordingLauncher());
    }

    [Fact]
    public async Task Shutdown_asks_for_a_shutdown_that_waits_for_programs_by_default()
    {
        var power = new FakePowerNativeApi();

        ExecutionResult result = await RunAsync(new WindowsBuiltinActions(power), WindowsActionNames.Shutdown);

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(power.Calls, "shutdown");
    }

    [Fact]
    public async Task Shutdown_with_force_asks_for_a_forced_shutdown()
    {
        var power = new FakePowerNativeApi();

        ExecutionResult result = await RunAsync(
            new WindowsBuiltinActions(power),
            WindowsActionNames.Shutdown,
            ActionTestData.Vars((WindowsActionParameters.Force, true)));

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(power.Calls, "shutdown:force");
    }

    [Fact]
    public async Task Restart_asks_for_a_restart_and_honours_force()
    {
        var power = new FakePowerNativeApi();
        var provider = new WindowsBuiltinActions(power);

        ExecutionResult plain = await RunAsync(provider, WindowsActionNames.Restart);
        ExecutionResult forced = await RunAsync(provider, WindowsActionNames.Restart, ActionTestData.Vars((WindowsActionParameters.Force, true)));

        Assert.True(plain.Succeeded);
        Assert.True(forced.Succeeded);
        ActionTestData.AssertCalls(power.Calls, "restart", "restart:force");
    }

    [Fact]
    public async Task A_force_value_that_is_not_a_boolean_means_off_and_never_forces()
    {
        var power = new FakePowerNativeApi();

        await RunAsync(
            new WindowsBuiltinActions(power),
            WindowsActionNames.Restart,
            ActionTestData.Vars((WindowsActionParameters.Force, "true")));

        ActionTestData.AssertCalls(power.Calls, "restart");
    }

    [Fact]
    public async Task Sleep_suspends_when_the_computer_can_sleep()
    {
        var power = new FakePowerNativeApi();

        ExecutionResult result = await RunAsync(new WindowsBuiltinActions(power), WindowsActionNames.Sleep);

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(power.Calls, "sleep");
    }

    [Fact]
    public async Task Sleep_is_refused_with_an_explanation_when_the_computer_cannot_sleep()
    {
        var power = new FakePowerNativeApi { CanSleep = false };

        ExecutionResult result = await RunAsync(new WindowsBuiltinActions(power), WindowsActionNames.Sleep);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("cannot sleep", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(power.Calls);
    }

    [Fact]
    public async Task Hibernate_suspends_to_disk_when_hibernation_is_enabled()
    {
        var power = new FakePowerNativeApi();

        ExecutionResult result = await RunAsync(new WindowsBuiltinActions(power), WindowsActionNames.Hibernate);

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(power.Calls, "hibernate");
    }

    [Fact]
    public async Task Hibernate_is_refused_and_tells_how_to_enable_it_when_hibernation_is_off()
    {
        var power = new FakePowerNativeApi { CanHibernate = false };

        ExecutionResult result = await RunAsync(new WindowsBuiltinActions(power), WindowsActionNames.Hibernate);

        Assert.False(result.Succeeded);
        Assert.Contains("powercfg /hibernate on", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(power.Calls);
    }

    [Theory]
    [InlineData(WindowsActionNames.Shutdown)]
    [InlineData(WindowsActionNames.Restart)]
    [InlineData(WindowsActionNames.Sleep)]
    [InlineData(WindowsActionNames.Hibernate)]
    public async Task A_failing_windows_call_becomes_a_failed_run_and_not_an_exception(string action)
    {
        var power = new FakePowerNativeApi { Failure = new Win32Exception(5, "Access is denied.") };

        ExecutionResult result = await RunAsync(new WindowsBuiltinActions(power), action);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Access is denied.", result.StandardError, StringComparison.Ordinal);
        Assert.Single(power.Calls);
    }

    [Fact]
    public async Task An_unknown_action_is_a_bad_request_and_changes_nothing()
    {
        var power = new FakePowerNativeApi();

        ExecutionResult result = await RunAsync(new WindowsBuiltinActions(power), "power.explode");

        Assert.Equal(ErrorCodes.BadRequest, result.ErrorCode);
        Assert.Empty(power.Calls);
    }

    [Theory]
    [InlineData(WindowsActionNames.LockSession)]
    [InlineData(WindowsActionNames.Logoff)]
    [InlineData(WindowsActionNames.DisplayOff)]
    [InlineData(WindowsActionNames.SetVolume)]
    [InlineData(WindowsActionNames.ProcessKill)]
    public async Task An_action_of_the_desktop_never_runs_in_the_service(string action)
    {
        var power = new FakePowerNativeApi();

        ExecutionResult result = await RunAsync(new WindowsBuiltinActions(power), action);

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Empty(power.Calls);
    }

    [Fact]
    public async Task A_cancelled_run_does_not_start_a_power_action()
    {
        var power = new FakePowerNativeApi();
        var provider = new WindowsBuiltinActions(power);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await provider.ExecuteAsync(WindowsActionNames.Shutdown, NoVariables, cancellation.Token));

        Assert.Empty(power.Calls);
    }

    [Fact]
    public async Task The_handler_binds_the_variables_before_a_power_action_runs()
    {
        var power = new FakePowerNativeApi();
        var handler = new BuiltinActionHandler(new WindowsBuiltinActions(power));

        ExecutionResult result = await handler.ExecuteAsync(
            Context(WindowsActionNames.Shutdown, ActionTestData.Vars((WindowsActionParameters.Force, true))),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(power.Calls, "shutdown:force");
    }

    [Fact]
    public async Task The_handler_refuses_a_force_value_that_the_binder_rejects_and_shuts_nothing_down()
    {
        var power = new FakePowerNativeApi();
        var handler = new BuiltinActionHandler(new WindowsBuiltinActions(power));

        ExecutionResult result = await handler.ExecuteAsync(
            Context(WindowsActionNames.Shutdown, ActionTestData.Vars((WindowsActionParameters.Force, "maybe"))),
            CancellationToken.None);

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(power.Calls);
    }

    [Fact]
    public async Task The_handler_passes_on_only_the_variables_that_the_action_declares()
    {
        var power = new FakePowerNativeApi();
        var handler = new BuiltinActionHandler(new WindowsBuiltinActions(power));

        ExecutionResult result = await handler.ExecuteAsync(
            Context(WindowsActionNames.Sleep, ActionTestData.Vars((WindowsActionParameters.Force, true), ("Extra", 1L))),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(power.Calls, "sleep");
    }
}
