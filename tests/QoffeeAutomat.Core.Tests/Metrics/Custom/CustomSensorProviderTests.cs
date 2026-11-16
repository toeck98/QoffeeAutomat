using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Core.Metrics.Custom;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Tests.Execution.PowerShell;
using static QoffeeAutomat.Core.Tests.Metrics.Custom.Sensors;

namespace QoffeeAutomat.Core.Tests.Metrics.Custom;

/// <summary>The custom sensor provider on its own: how sensors run, how output is parsed and how failures are contained (PLAN.md T215).</summary>
[Trait("Category", "CustomSensors")]
public sealed class CustomSensorProviderTests
{
    private static readonly TimeSpan Five = TimeSpan.FromSeconds(5);

    // ------------------------------------------------------------------
    // Catalog
    // ------------------------------------------------------------------

    [Fact]
    public async Task Sensors_appear_as_custom_metrics_with_the_interval_that_applies()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(
            Script("fast_script", interval: 1),
            Script("default_script", interval: null),
            Script("event_script", interval: 0),
            Script("slow_script", interval: 60),
            Process("fast_process", "notepad", interval: 1));

        MetricDescriptor[] descriptors = [.. harness.Provider.Describe()];
        await harness.Engine.PollAsync(CancellationToken.None);

        Assert.Equal(
            ["custom.default_script", "custom.event_script", "custom.fast_process", "custom.fast_script", "custom.slow_script"],
            descriptors.Select(descriptor => descriptor.Id));
        Assert.Equal(
            [5d, 0d, 1d, 5d, 60d],
            descriptors.Select(descriptor => descriptor.IntervalSeconds!.Value));
        Assert.Equal("system", descriptors[0].Context);

        // The engine announces the same interval.
        Assert.Equal(
            [5d, 0d, 1d, 5d, 60d],
            harness.Engine.Catalog.Select(entry => entry.IntervalSeconds));
        Assert.All(harness.Engine.Catalog, entry => Assert.True(entry.IsCustom));
    }

    [Fact]
    public async Task The_minimum_interval_of_script_sensors_can_be_tuned()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync(engineOptions: new MetricEngineOptions { MinimumScriptIntervalSeconds = 10 });
        await harness.SaveAsync(Script("a", interval: 5), Script("b", interval: 30), Process("c", "x", interval: 5));

        Assert.Equal([10d, 30d, 5d], harness.Provider.Describe().Select(descriptor => descriptor.IntervalSeconds!.Value));
    }

    // ------------------------------------------------------------------
    // Scripts run through the PowerShell action
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_script_sensor_runs_through_the_powershell_wrapper_as_system()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("temperature", code: "Write-Output 21.5", engine: PowerShellEngines.Pwsh));
        harness.Launcher.Output("temperature", "21.5\n");

        IReadOnlyDictionary<string, object?> values = await harness.SampleAsync("temperature");

        Assert.Equal(21.5, values["custom.temperature"]);
        SensorCall call = Assert.Single(harness.Launcher.Calls);
        Assert.Equal(RunAsValues.System, call.RunAs);
        Assert.Equal("/fake/bin/pwsh", call.Request.FileName);
        Assert.Equal(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"], call.Request.Arguments.Take(5));
        Assert.EndsWith("wrapper.ps1", call.Request.Arguments[^1], StringComparison.Ordinal);
        Assert.Equal("Write-Output 21.5", call.Code);
        Assert.Equal("temperature", call.Request.EnvironmentVariables["QA_SENSOR_ID"]);
        Assert.True(call.Request.EnvironmentVariables.ContainsKey(PowerShellWrapper.ParametersFileVariable));
        Assert.True(call.Request.CaptureOutput);
        Assert.True(call.Request.WaitForExit);
        Assert.Equal(["pwsh"], harness.Locator.Requested);
        Assert.Empty(harness.RunDirectories);
    }

    [Fact]
    public async Task The_engine_choice_of_the_sensor_reaches_the_locator()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("a", engine: PowerShellEngines.PowerShell), Script("b"));

        await harness.SampleAsync("a", "b");

        Assert.Equal([PowerShellEngines.Auto, PowerShellEngines.PowerShell], harness.Locator.Requested.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_script_file_sensor_runs_the_file_from_the_scripts_directory_in_place()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        ScriptResult saved = await harness.Store.Local.SaveScriptAsync("level.ps1", "Write-Output 80", null, CancellationToken.None);
        Assert.True(saved.IsApplied);
        await harness.SaveAsync(Script("from_file") with { Source = new PowerShellSensorSource(PowerShellSources.File, Script: "level.ps1") });
        harness.Launcher.Output("from_file", "80");

        IReadOnlyDictionary<string, object?> values = await harness.SampleAsync("from_file");

        Assert.Equal(80.0, values["custom.from_file"]);
        SensorCall call = Assert.Single(harness.Launcher.Calls);
        Assert.Equal("Write-Output 80", call.Code);
        Assert.EndsWith("level.ps1", call.Request.EnvironmentVariables[PowerShellWrapper.TargetScriptVariable], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_script_file_that_does_not_exist_makes_only_that_sensor_unavailable()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(
            Script("from_file") with { Source = new PowerShellSensorSource(PowerShellSources.File, Script: "missing.ps1") },
            Script("good"));
        harness.Launcher.Output("good", "1");

        IReadOnlyDictionary<string, object?> values = await harness.SampleAsync("from_file", "good");

        Assert.Null(values["custom.from_file"]);
        Assert.Equal(1.0, values["custom.good"]);
        Assert.Contains("missing.ps1", harness.Provider.GetStatus("from_file")!.LastError, StringComparison.Ordinal);
        Assert.Equal(["good"], harness.Launcher.Calls.Select(call => call.SensorId));
    }

    [Fact]
    public async Task A_missing_engine_makes_the_sensor_unavailable_with_the_reason()
    {
        const string reason = "PowerShell 7 (pwsh) was not found on this computer.";
        await using SensorHarness harness = await SensorHarness.CreateAsync(locator: new FakeEngineLocator(reason));
        await harness.SaveAsync(Script("a"));

        IReadOnlyDictionary<string, object?> values = await harness.SampleAsync("a");

        Assert.Null(values["custom.a"]);
        CustomSensorStatus status = harness.Provider.GetStatus("a")!;
        Assert.False(status.IsAvailable);
        Assert.Equal(reason, status.LastError);
        Assert.Empty(harness.Launcher.Calls);
    }

    // ------------------------------------------------------------------
    // Parsing by value type
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_last_non_empty_line_is_parsed_according_to_the_value_type()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(
            Script("number"),
            Script("flag", "bool"),
            Script("text", "string"),
            Script("mode", "enum", options: ["Idle", "Busy"]),
            Script("moment", "timestamp"));
        harness.Launcher.Output("number", "starting\n42\r\n\r\n");
        harness.Launcher.Output("flag", "False\n");
        harness.Launcher.Output("text", "ignored\n  Now playing  \n");
        harness.Launcher.Output("mode", "busy\n");
        harness.Launcher.Output("moment", "2026-03-04T05:06:07+02:00\n");

        IReadOnlyDictionary<string, object?> values = await harness.SampleAsync("number", "flag", "text", "mode", "moment");

        Assert.Equal(42.0, values["custom.number"]);
        Assert.Equal(false, values["custom.flag"]);
        Assert.Equal("Now playing", values["custom.text"]);
        Assert.Equal("Busy", values["custom.mode"]);
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 3, 6, 7, TimeSpan.Zero), values["custom.moment"]);
        Assert.All(harness.Provider.GetStatuses(), status => Assert.True(status.IsAvailable));
    }

    [Theory]
    [InlineData("", "no output")]
    [InlineData("   \r\n\r\n", "no output")]
    [InlineData("hello", "is not a number")]
    [InlineData("12 degrees", "is not a number")]
    public async Task Unusable_output_makes_the_metric_unavailable_and_records_why(string output, string expectedError)
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("a"));
        harness.Launcher.Output("a", output);

        IReadOnlyDictionary<string, object?> values = await harness.SampleAsync("a");

        Assert.Null(values["custom.a"]);
        CustomSensorStatus status = harness.Provider.GetStatus("a")!;
        Assert.False(status.IsAvailable);
        Assert.Contains(expectedError, status.LastError, StringComparison.Ordinal);
        Assert.Equal(1, status.ConsecutiveFailures);
        Assert.NotNull(status.LastErrorAt);
        Assert.Null(status.LastSuccessAt);
    }

    [Fact]
    public async Task Output_that_was_cut_at_the_cap_is_not_trusted()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("a"));
        harness.Launcher.Output("a", "42", truncated: true);

        IReadOnlyDictionary<string, object?> values = await harness.SampleAsync("a");

        Assert.Null(values["custom.a"]);
        Assert.Contains("64 KiB", harness.Provider.GetStatus("a")!.LastError, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Failures are contained
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_non_zero_exit_code_makes_the_metric_unavailable_and_keeps_the_error_text()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("a"));
        harness.Launcher.Output("a", "42\n", exitCode: 3, standardError: "Cannot reach the device\nsecond line");

        IReadOnlyDictionary<string, object?> values = await harness.SampleAsync("a");

        Assert.Null(values["custom.a"]);
        Assert.Equal("The script exited with code 3: Cannot reach the device", harness.Provider.GetStatus("a")!.LastError);
    }

    [Fact]
    public async Task A_failing_sensor_does_not_affect_the_others_in_the_same_sample()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(
            Script("parse_error"),
            Script("exit_code"),
            Script("crash"),
            Script("good"),
            Process("app", "notepad"));
        harness.Launcher.Output("parse_error", "not a number");
        harness.Launcher.Output("exit_code", "1", exitCode: 1);
        harness.Launcher.Throw("crash", new InvalidOperationException("the launcher broke"));
        harness.Launcher.Output("good", "7");
        harness.Processes.Names.Add("notepad");

        IReadOnlyDictionary<string, object?> values = await harness.SampleAsync("parse_error", "exit_code", "crash", "good", "app");

        Assert.Null(values["custom.parse_error"]);
        Assert.Null(values["custom.exit_code"]);
        Assert.Null(values["custom.crash"]);
        Assert.Equal(7.0, values["custom.good"]);
        Assert.Equal(true, values["custom.app"]);
        Assert.Contains("failed unexpectedly", harness.Provider.GetStatus("crash")!.LastError, StringComparison.Ordinal);
        Assert.True(harness.Provider.GetStatus("good")!.IsAvailable);
        Assert.True(harness.Provider.GetStatus("app")!.IsAvailable);
    }

    [Fact]
    public async Task A_timeout_reported_by_the_process_launcher_makes_the_metric_unavailable()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("a"));
        harness.Launcher.Output("a", string.Empty, exitCode: -1, errorCode: ErrorCodes.Timeout);

        IReadOnlyDictionary<string, object?> values = await harness.SampleAsync("a");

        Assert.Null(values["custom.a"]);
        Assert.Equal("The script did not finish within 5 seconds.", harness.Provider.GetStatus("a")!.LastError);
    }

    [Fact]
    public async Task A_script_that_runs_longer_than_its_timeout_is_stopped_and_unavailable_while_the_others_deliver()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("slow"), Script("fast"));
        harness.Launcher.Hang("slow");
        harness.Launcher.Output("fast", "3");

        Task<IReadOnlyDictionary<string, object?>> sample = harness.SampleAsync("slow", "fast");
        await harness.Launcher.WaitForCallsAsync(2);
        harness.Clock.Advance(Five);
        IReadOnlyDictionary<string, object?> values = await sample.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(values["custom.slow"]);
        Assert.Equal(3.0, values["custom.fast"]);
        Assert.Equal("The script did not finish within 5 seconds.", harness.Provider.GetStatus("slow")!.LastError);
        Assert.True(harness.Provider.GetStatus("fast")!.IsAvailable);
        Assert.Empty(harness.RunDirectories);
    }

    [Fact]
    public async Task A_sample_that_is_cancelled_returns_unavailable_without_blaming_the_sensor()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("slow"));
        harness.Launcher.Hang("slow");
        using var cancel = new CancellationTokenSource();

        Task<IReadOnlyDictionary<string, object?>> sample = harness.Provider.SampleAsync([MetricId("slow")], cancel.Token).AsTask();
        await harness.Launcher.WaitForCallsAsync();
        await cancel.CancelAsync();
        IReadOnlyDictionary<string, object?> values = await sample.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(values["custom.slow"]);
        Assert.Null(harness.Provider.GetStatus("slow"));
        Assert.Empty(harness.RunDirectories);
    }

    [Theory]
    [InlineData(5d, 5d)]
    [InlineData(7.5, 7.5)]
    [InlineData(10d, 10d)]
    [InlineData(25d, 25d)]
    [InlineData(26d, 25d)]
    [InlineData(60d, 25d)]
    [InlineData(3600d, 25d)]
    [InlineData(0d, 25d)]
    [InlineData(1d, 5d)]
    public async Task The_timeout_is_never_longer_than_the_interval_nor_than_the_maximum(double interval, double expectedTimeoutSeconds)
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("a", interval: interval));

        await harness.SampleAsync("a");

        SensorCall call = Assert.Single(harness.Launcher.Calls);
        Assert.Equal(TimeSpan.FromSeconds(expectedTimeoutSeconds), call.Request.Timeout);
        if (interval > 0)
        {
            Assert.True(call.Request.Timeout <= TimeSpan.FromSeconds(Math.Max(interval, 5)));
        }
    }

    [Fact]
    public async Task The_maximum_timeout_can_be_lowered()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync(new CustomSensorOptions { MaxScriptTimeout = TimeSpan.FromSeconds(3) });
        await harness.SaveAsync(Script("a", interval: 60));

        await harness.SampleAsync("a");

        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(harness.Launcher.Calls).Request.Timeout);
    }

    // ------------------------------------------------------------------
    // Status
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_status_records_failures_and_recovery()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("a"));
        Assert.Null(harness.Provider.GetStatus("a"));

        harness.Launcher.Output("a", "oops");
        await harness.SampleAsync("a");
        harness.Clock.Advance(Five);
        await harness.SampleAsync("a");
        CustomSensorStatus failing = harness.Provider.GetStatus("a")!;

        harness.Launcher.Output("a", "5");
        harness.Clock.Advance(Five);
        await harness.SampleAsync("a");
        CustomSensorStatus recovered = harness.Provider.GetStatus("a")!;

        Assert.False(failing.IsAvailable);
        Assert.Equal(2, failing.ConsecutiveFailures);
        Assert.Equal("a", failing.SensorId);
        Assert.Equal("custom.a", failing.MetricId);
        Assert.True(recovered.IsAvailable);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Null(recovered.LastError);
        Assert.Null(recovered.LastErrorAt);
        Assert.Equal(recovered.LastSampleAt, recovered.LastSuccessAt);
        Assert.True(recovered.LastSampleAt > failing.LastSampleAt);
    }

    [Fact]
    public async Task The_status_keeps_the_time_of_the_last_success_while_a_sensor_fails()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("a"));
        harness.Launcher.Output("a", "1");
        await harness.SampleAsync("a");
        DateTimeOffset success = harness.Provider.GetStatus("a")!.LastSuccessAt!.Value;

        harness.Clock.Advance(Five);
        harness.Launcher.Output("a", "bad");
        await harness.SampleAsync("a");

        CustomSensorStatus status = harness.Provider.GetStatus("a")!;
        Assert.Equal(success, status.LastSuccessAt);
        Assert.Equal(success + Five, status.LastErrorAt);
    }

    [Fact]
    public async Task The_status_records_how_long_the_sample_took()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("a"));
        harness.Launcher.Answer("a", (_, _) =>
        {
            harness.Clock.Advance(TimeSpan.FromSeconds(1.5));
            return Task.FromResult(new ExecutionResult(0, "1", string.Empty));
        });

        await harness.SampleAsync("a");

        Assert.Equal(TimeSpan.FromSeconds(1.5), harness.Provider.GetStatus("a")!.LastDuration);
    }

    [Fact]
    public async Task The_statuses_are_sorted_and_forget_deleted_sensors()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("b"), Script("a"), Script("c"));
        await harness.SampleAsync("c", "a", "b");

        Assert.Equal(["a", "b", "c"], harness.Provider.GetStatuses().Select(status => status.SensorId));

        await harness.Store.Local.DeleteAsync<SensorDocument>("b", null, CancellationToken.None);

        Assert.Equal(["a", "c"], harness.Provider.GetStatuses().Select(status => status.SensorId));
        Assert.Null(harness.Provider.GetStatus("b"));
    }

    [Fact]
    public async Task Edits_to_a_sensor_apply_at_the_next_sample_without_a_restart()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("a", code: "'first'"));
        await harness.SampleAsync("a");

        await harness.SaveAsync(Script("a", code: "'second'", interval: 20));
        await harness.SampleAsync("a");

        Assert.Equal(["'first'", "'second'"], harness.Launcher.Calls.Select(call => call.Code));
        Assert.Equal(TimeSpan.FromSeconds(20), harness.Launcher.Calls[1].Request.Timeout);
    }

    // ------------------------------------------------------------------
    // Process sensors and everything else
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_process_sensor_reports_whether_the_process_runs()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Process("notepad_running", "notepad"), Process("exe_name", "Notepad.exe"), Process("other", "calc"));
        harness.Processes.Names.Add("notepad");

        IReadOnlyDictionary<string, object?> running = await harness.SampleAsync("notepad_running", "exe_name", "other");
        harness.Processes.Names.Clear();
        IReadOnlyDictionary<string, object?> gone = await harness.SampleAsync("notepad_running");

        Assert.Equal(true, running["custom.notepad_running"]);
        Assert.Equal(true, running["custom.exe_name"]);
        Assert.Equal(false, running["custom.other"]);
        Assert.Equal(false, gone["custom.notepad_running"]);
        Assert.Empty(harness.Launcher.Calls);
        Assert.True(harness.Provider.GetStatus("notepad_running")!.IsAvailable);
    }

    [Fact]
    public async Task The_process_list_is_read_once_per_sample_however_many_process_sensors_there_are()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Process("a", "one"), Process("b", "two"), Process("c", "three"), Script("script"));

        await harness.SampleAsync("a", "b", "c", "script");
        Assert.Equal(1, harness.Processes.Reads);

        await harness.SampleAsync("script");
        Assert.Equal(1, harness.Processes.Reads);
    }

    [Fact]
    public async Task An_unreadable_process_list_makes_only_the_process_sensors_unavailable()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Process("app", "notepad"), Script("script"));
        harness.Processes.Failure = new InvalidOperationException("access denied");
        harness.Launcher.Output("script", "9");

        IReadOnlyDictionary<string, object?> values = await harness.SampleAsync("app", "script");

        Assert.Null(values["custom.app"]);
        Assert.Equal(9.0, values["custom.script"]);
        Assert.Contains("process list could not be read", harness.Provider.GetStatus("app")!.LastError, StringComparison.Ordinal);
        Assert.Contains("access denied", harness.Provider.GetStatus("app")!.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sensor_types_that_need_the_operating_system_are_unavailable_with_an_explanation()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        SensorDocument counter = Process("counter", "x") with
        {
            ValueType = "number",
            Kind = "sensor",
            Source = new PerfCounterSensorSource("Processor", "% Processor Time", "_Total"),
        };
        await harness.SaveAsync(counter);

        IReadOnlyDictionary<string, object?> values = await harness.SampleAsync("counter");

        Assert.Null(values["custom.counter"]);
        Assert.Contains("'perf_counter' is not supported", harness.Provider.GetStatus("counter")!.LastError, StringComparison.Ordinal);
        Assert.Empty(harness.Launcher.Calls);
    }

    [Fact]
    public async Task Ids_that_are_not_existing_custom_sensors_are_ignored()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("real"));
        harness.Launcher.Output("real", "1");

        IReadOnlyDictionary<string, object?> values = await harness.Provider.SampleAsync(
            ["cpu.usage", "custom.ghost", "custom.", "real", MetricId("real")],
            CancellationToken.None);

        Assert.Equal(["custom.real"], values.Keys);
        Assert.Equal(1.0, values["custom.real"]);
    }

    [Fact]
    public async Task Sampling_nothing_starts_nothing()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("a"), Process("b", "x"));

        IReadOnlyDictionary<string, object?> values = await harness.Provider.SampleAsync([], CancellationToken.None);

        Assert.Empty(values);
        Assert.Empty(harness.Launcher.Calls);
        Assert.Equal(0, harness.Processes.Reads);
    }

    [Fact]
    public async Task Scripts_of_one_sample_run_concurrently()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("a"), Script("b"));
        harness.Launcher.Hang("a");
        harness.Launcher.Hang("b");

        Task<IReadOnlyDictionary<string, object?>> sample = harness.SampleAsync("a", "b");

        // Both were started before either finished.
        await harness.Launcher.WaitForCallsAsync(2);
        harness.Clock.Advance(Five);
        IReadOnlyDictionary<string, object?> values = await sample.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(values["custom.a"]);
        Assert.Null(values["custom.b"]);
    }

    [Fact]
    public async Task The_constructor_and_options_reject_invalid_arguments()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        var runner = new PowerShellRunner(FakeEngineLocator.CreatePwsh());

        Assert.Throws<ArgumentNullException>(() => new CustomSensorProvider(null!, runner, harness.Launcher, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CustomSensorProvider(harness.Store, null!, harness.Launcher, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CustomSensorProvider(harness.Store, runner, null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CustomSensorProvider(harness.Store, runner, harness.Launcher, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CustomSensorProvider(
            harness.Store,
            runner,
            harness.Launcher,
            TimeProvider.System,
            options: new CustomSensorOptions { MaxScriptTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetricEngine(
            harness.Store,
            [],
            TimeProvider.System,
            new MetricEngineOptions { MinimumScriptIntervalSeconds = 0 }));
    }
}
