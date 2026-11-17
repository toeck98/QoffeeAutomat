using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Actions;
using QoffeeAutomat.Platform.Windows.Actions.Power;

namespace QoffeeAutomat.Platform.Windows.Tests.Actions;

/// <summary>
/// Keeping the computer awake (PLAN.md S11, S16, T715): the service, the action <c>power.keep_awake</c>, the metric and the default
/// switch. The power request goes to <see cref="FakePowerRequestApi"/> and time to <see cref="FakeClock"/>: nothing here holds the
/// computer awake or waits for real time.
/// </summary>
[Trait("Category", "PowerDelay")]
public sealed class KeepAwakeTests
{
    private readonly FakeClock _clock = new();
    private readonly FakePowerRequestApi _api = new();

    private KeepAwakeService NewService() => new(_api, _clock);

    private static WindowsBuiltinActions NewActions(KeepAwakeService? service) => new(new FakePowerNativeApi(), keepAwake: service);

    private static Task<ExecutionResult> RunAsync(WindowsBuiltinActions actions, params (string Name, object Value)[] variables)
    {
        return actions.ExecuteAsync(WindowsActionNames.KeepAwake, ActionTestData.Vars(variables), CancellationToken.None).AsTask();
    }

    private static bool Flag(IReadOnlyDictionary<string, object?> values)
    {
        return Assert.IsType<bool>(values[PowerActionSpec.KeepAwakeMetric]);
    }

    [Fact]
    public void Switching_on_holds_a_request_until_it_is_switched_off()
    {
        using KeepAwakeService service = NewService();

        KeepAwakeState state = service.Switch(on: true, keepDisplayOn: false, minutes: 0);

        Assert.True(state.IsOn);
        Assert.False(state.KeepDisplayOn);
        Assert.Null(state.Until);
        Assert.True(_api.Held);
        Assert.False(_api.DisplayHeld);
        Assert.True(service.IsOn);

        service.Switch(on: false, keepDisplayOn: false, minutes: 0);

        Assert.False(service.IsOn);
        Assert.False(_api.Held);
    }

    [Fact]
    public void The_display_is_kept_on_only_when_asked_for()
    {
        using KeepAwakeService service = NewService();

        KeepAwakeState state = service.Switch(on: true, keepDisplayOn: true, minutes: 0);

        Assert.True(state.KeepDisplayOn);
        Assert.True(_api.DisplayHeld);
        ActionTestData.AssertCalls(_api.Calls, "acquire:display");
    }

    [Fact]
    public void Without_a_time_it_lasts_until_it_is_switched_off()
    {
        using KeepAwakeService service = NewService();
        service.Switch(on: true, keepDisplayOn: false, minutes: 0);

        _clock.Advance(TimeSpan.FromDays(30));

        Assert.True(service.IsOn);
        Assert.True(_api.Held);
        Assert.Equal(0, _clock.ArmedTimers);
    }

    [Fact]
    public void With_minutes_the_request_ends_by_itself_when_the_time_is_over()
    {
        using KeepAwakeService service = NewService();
        int changes = 0;
        service.Changed += (_, _) => changes++;
        DateTimeOffset start = _clock.GetUtcNow();

        KeepAwakeState state = service.Switch(on: true, keepDisplayOn: false, minutes: 30);
        Assert.Equal(start.AddMinutes(30), state.Until);

        _clock.Advance(TimeSpan.FromMinutes(29));
        Assert.True(service.IsOn);
        Assert.True(_api.Held);

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(service.IsOn);
        Assert.False(_api.Held);
        Assert.Equal(2, changes); // on, then off when the time was over
    }

    [Fact]
    public void Switching_on_again_replaces_the_time_and_the_old_time_no_longer_ends_it()
    {
        using KeepAwakeService service = NewService();
        service.Switch(on: true, keepDisplayOn: false, minutes: 10);
        _clock.Advance(TimeSpan.FromMinutes(5));

        service.Switch(on: true, keepDisplayOn: true, minutes: 60);
        _clock.Advance(TimeSpan.FromMinutes(10)); // the first time would have ended at minute 10; now it is minute 15

        Assert.True(service.IsOn);
        Assert.True(service.State.KeepDisplayOn);

        _clock.Advance(TimeSpan.FromMinutes(50)); // minute 65: 60 minutes after the second switch-on
        Assert.False(service.IsOn);
    }

    [Fact]
    public void Switching_off_stops_the_timer()
    {
        using KeepAwakeService service = NewService();
        service.Switch(on: true, keepDisplayOn: false, minutes: 10);

        service.Switch(on: false, keepDisplayOn: false, minutes: 0);

        Assert.Equal(0, _clock.ArmedTimers);
        int releases = _api.Calls.Count(call => call == "release");
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(releases, _api.Calls.Count(call => call == "release"));
    }

    [Fact]
    public void A_change_is_reported_only_when_the_state_really_changed()
    {
        using KeepAwakeService service = NewService();
        int changes = 0;
        service.Changed += (_, _) => changes++;

        service.Switch(on: false, keepDisplayOn: false, minutes: 0); // already off
        service.Switch(on: true, keepDisplayOn: false, minutes: 0);
        service.Switch(on: true, keepDisplayOn: false, minutes: 0); // the same again
        service.Switch(on: false, keepDisplayOn: false, minutes: 0);

        Assert.Equal(2, changes);
    }

    [Fact]
    public void A_request_that_windows_refuses_leaves_the_state_off()
    {
        using KeepAwakeService service = NewService();
        _api.Refusal = FakePowerRequestApi.Denied();

        Assert.Throws<System.ComponentModel.Win32Exception>(() => service.Switch(on: true, keepDisplayOn: false, minutes: 5));

        Assert.False(service.IsOn);
        Assert.Equal(0, _clock.ArmedTimers);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(PowerActionSpec.MaxKeepAwakeMinutes + 1)]
    public void A_time_outside_the_range_is_refused(int minutes)
    {
        using KeepAwakeService service = NewService();

        Assert.Throws<ArgumentOutOfRangeException>(() => service.Switch(on: true, keepDisplayOn: false, minutes));
        Assert.False(_api.Held);
    }

    [Fact]
    public void Disposing_the_service_gives_the_request_back_and_a_new_service_starts_off_so_nothing_is_restored_after_a_restart()
    {
        KeepAwakeService service = NewService();
        service.Switch(on: true, keepDisplayOn: true, minutes: 0);

        service.Dispose(); // the agent ends

        Assert.False(_api.Held);

        var restarted = new FakePowerRequestApi();
        using var afterRestart = new KeepAwakeService(restarted, _clock);
        Assert.False(afterRestart.IsOn);
        Assert.Empty(restarted.Calls); // nothing was read from disk, nothing was acquired
    }

    [Fact]
    public void After_the_service_is_disposed_it_refuses_to_switch_on_again()
    {
        KeepAwakeService service = NewService();
        service.Dispose();

        Assert.Throws<ObjectDisposedException>(() => service.Switch(on: true, keepDisplayOn: false, minutes: 0));
    }

    [Fact]
    public async Task The_action_switches_on_with_the_defaults_On_true_Display_false_and_no_time()
    {
        using KeepAwakeService service = NewService();

        ExecutionResult result = await RunAsync(NewActions(service));

        Assert.True(result.Succeeded);
        Assert.True(service.IsOn);
        Assert.False(service.State.KeepDisplayOn);
        Assert.Null(service.State.Until);
    }

    [Fact]
    public async Task The_action_passes_Display_and_Minutes_on_and_switches_off_with_On_false()
    {
        using KeepAwakeService service = NewService();
        WindowsBuiltinActions actions = NewActions(service);

        ExecutionResult on = await RunAsync(
            actions,
            (PowerActionSpec.On, true),
            (PowerActionSpec.Display, true),
            (PowerActionSpec.Minutes, 45L));
        Assert.True(on.Succeeded);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(45), service.State.Until);
        Assert.True(service.State.KeepDisplayOn);

        ExecutionResult off = await RunAsync(actions, (PowerActionSpec.On, false));
        Assert.True(off.Succeeded);
        Assert.False(service.IsOn);
        Assert.False(_api.Held);
    }

    [Theory]
    [InlineData(-5L)]
    [InlineData(PowerActionSpec.MaxKeepAwakeMinutes + 1L)]
    public async Task The_action_refuses_minutes_outside_the_range_with_invalid_vars_and_changes_nothing(long minutes)
    {
        using KeepAwakeService service = NewService();

        ExecutionResult result = await RunAsync(NewActions(service), (PowerActionSpec.Minutes, minutes));

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.False(service.IsOn);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task The_action_reports_a_refusal_of_windows_as_a_failed_run()
    {
        using KeepAwakeService service = NewService();
        _api.Refusal = FakePowerRequestApi.Denied();

        ExecutionResult result = await RunAsync(NewActions(service));

        Assert.False(result.Succeeded);
        Assert.Contains("failed", result.StandardError, StringComparison.Ordinal);
        Assert.False(service.IsOn);
    }

    [Fact]
    public async Task Without_a_keep_awake_service_the_action_fails_clearly_instead_of_doing_nothing()
    {
        ExecutionResult result = await RunAsync(NewActions(service: null));

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
    }

    [Fact]
    public async Task The_metric_is_the_real_state_and_follows_the_end_of_the_time()
    {
        using KeepAwakeService service = NewService();
        var provider = new KeepAwakeMetricProvider(service);
        string[] ask = [PowerActionSpec.KeepAwakeMetric];

        bool initial = Flag(await provider.SampleAsync(ask, CancellationToken.None));
        service.Switch(on: true, keepDisplayOn: false, minutes: 5);
        bool during = Flag(await provider.SampleAsync(ask, CancellationToken.None));
        _clock.Advance(TimeSpan.FromMinutes(5));
        bool after = Flag(await provider.SampleAsync(ask, CancellationToken.None));

        Assert.False(initial);
        Assert.True(during);
        Assert.False(after);
    }

    [Fact]
    public async Task The_metric_samples_only_what_is_asked_for()
    {
        using KeepAwakeService service = NewService();
        var provider = new KeepAwakeMetricProvider(service);

        IReadOnlyDictionary<string, object?> values = await provider.SampleAsync(["cpu.usage"], CancellationToken.None);

        Assert.Empty(values);
    }

    [Fact]
    public void The_metric_is_a_binary_sensor_of_the_system_context_that_is_pushed_when_it_changes()
    {
        using KeepAwakeService service = NewService();

        MetricDescriptor descriptor = Assert.Single(new KeepAwakeMetricProvider(service).Describe());

        Assert.Equal("power.keep_awake", descriptor.Id);
        Assert.Equal("binary_sensor", descriptor.Kind);
        Assert.Equal("bool", descriptor.ValueType);
        Assert.Equal("system", descriptor.Context);
        Assert.Equal(0d, descriptor.IntervalSeconds); // event-driven
        Assert.True(descriptor.EnabledDefault);
    }

    [Fact]
    public async Task The_relay_asks_for_a_fresh_sample_on_every_change_and_gives_the_request_back_when_the_agent_stops()
    {
        using KeepAwakeService service = NewService();
        var requested = new List<string>();
        var relay = new KeepAwakeMetricRelay(service, requested.Add);
        await relay.StartAsync(CancellationToken.None);

        service.Switch(on: true, keepDisplayOn: false, minutes: 1);
        _clock.Advance(TimeSpan.FromMinutes(1)); // ends by itself
        service.Switch(on: true, keepDisplayOn: false, minutes: 0);
        await relay.StopAsync(CancellationToken.None);

        Assert.Equal(["power.keep_awake", "power.keep_awake", "power.keep_awake"], requested);
        Assert.False(service.IsOn);
        Assert.False(_api.Held);
    }

    [Fact]
    public async Task The_relay_without_a_metric_engine_still_gives_the_request_back_on_stop()
    {
        using KeepAwakeService service = NewService();
        var relay = new KeepAwakeMetricRelay(service, requestSample: null);
        await relay.StartAsync(CancellationToken.None);
        service.Switch(on: true, keepDisplayOn: false, minutes: 0);

        await relay.StopAsync(CancellationToken.None);

        Assert.False(_api.Held);
    }

    [Fact]
    public void The_action_has_the_variables_of_the_plan()
    {
        BuiltinActionDescriptor descriptor = WindowsActionCatalog.Find(WindowsActionNames.KeepAwake)!;

        Assert.Equal(RunAsValues.System, descriptor.RunAs);
        Assert.Equal(["On", "Display", "Minutes"], descriptor.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(ParameterTypes.Flag, descriptor.Parameters[0].Type);
        Assert.Equal(ParameterTypes.Flag, descriptor.Parameters[1].Type);
        ParameterDefinition minutes = descriptor.Parameters[2];
        Assert.Equal(ParameterTypes.WholeNumber, minutes.Type);
        Assert.Equal(0d, minutes.Min);
        Assert.Equal(0, minutes.Default!.Value.GetInt32());
        Assert.All(descriptor.Parameters, parameter => Assert.False(parameter.Required));
    }

    [Fact]
    public void The_default_command_is_a_switch_on_On_whose_state_is_the_keep_awake_metric()
    {
        CommandDocument command = DefaultCommandSeeder.DefaultCommands.Single(candidate => candidate.Id == "power_keep_awake");

        Assert.Equal(RunAsValues.System, command.RunAs);
        Assert.Equal(WindowsActionNames.KeepAwake, Assert.IsType<BuiltinAction>(command.Action).Name);
        Assert.Equal(HaControlTypes.Switch, command.HaControl!.Type);
        Assert.Equal("On", command.HaControl.Parameter);
        Assert.Equal("power.keep_awake", command.HaControl.StateMetric);
        Assert.Contains(command.Parameters!, parameter => parameter.Name == "On" && parameter.Type == ParameterTypes.Flag);
        Assert.False(command.Enabled);
        Assert.False(command.ExposeToHa);
    }
}
