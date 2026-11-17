using System.Globalization;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Commands.Conditions;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.History;
using QoffeeAutomat.Core.Manifest;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Tests.Config;
using QoffeeAutomat.Core.Tests.Validation;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Tests.Commands;

/// <summary>
/// Command conditions (session state, time window), the schema migration from version 1 to 2 and the maintenance mode
/// (PLAN.md S06, T705).
/// </summary>
[Trait("Category", "Conditions")]
public sealed class CommandConditionTests
{
    // 2026-11-16 is a Monday, 2026-11-17 a Tuesday and 2026-11-21 a Saturday.
    private const int Monday = 16;
    private const int Tuesday = 17;
    private const int Saturday = 21;

    private static SessionState Session(bool loggedIn, bool locked) =>
        new(loggedIn, loggedIn ? "sim-user" : null, locked, loggedIn);

    private static DateTimeOffset At(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, TimeSpan.Zero);

    private static string[]? Days(string? days) => days?.Split(',');

    private static ExecutionResult Ok(GateCall call) => new(0, "ok\n", string.Empty);

    // ------------------------------------------------------------------
    // The evaluator
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null, false, false, true)]
    [InlineData(SessionRequirements.Any, false, false, true)]
    [InlineData(SessionRequirements.Any, true, true, true)]
    [InlineData(SessionRequirements.LoggedIn, true, false, true)]
    [InlineData(SessionRequirements.LoggedIn, true, true, true)]
    [InlineData(SessionRequirements.LoggedIn, false, false, false)]
    [InlineData(SessionRequirements.Unlocked, true, false, true)]
    [InlineData(SessionRequirements.Unlocked, true, true, false)]
    [InlineData(SessionRequirements.Unlocked, false, false, false)]
    [InlineData(SessionRequirements.Locked, true, true, true)]
    [InlineData(SessionRequirements.Locked, true, false, false)]
    [InlineData(SessionRequirements.Locked, false, false, false)]
    public void The_session_requirement_is_checked_against_the_session_state(string? requirement, bool loggedIn, bool locked, bool expected)
    {
        ConditionVerdict verdict = CommandConditionEvaluator.Evaluate(new CommandConditions(requirement), Session(loggedIn, locked), At(Monday, 12, 0));

        Assert.Equal(expected, verdict.IsMet);
        Assert.Equal(expected, verdict.Reason is null);
    }

    [Fact]
    public void A_command_without_conditions_always_passes()
    {
        Assert.True(CommandConditionEvaluator.Evaluate(null, SessionState.None, At(Monday, 3, 0)).IsMet);
        Assert.True(CommandConditionEvaluator.Evaluate(new CommandConditions(), SessionState.None, At(Monday, 3, 0)).IsMet);
    }

    [Theory]
    [InlineData("08:00", "18:00", null, Monday, 12, 0, true)]
    [InlineData("08:00", "18:00", null, Monday, 8, 0, true)]
    [InlineData("08:00", "18:00", null, Monday, 7, 59, false)]
    [InlineData("08:00", "18:00", null, Monday, 18, 0, false)]
    [InlineData("22:00", "06:00", null, Monday, 23, 30, true)]
    [InlineData("22:00", "06:00", null, Tuesday, 5, 59, true)]
    [InlineData("22:00", "06:00", null, Tuesday, 6, 0, false)]
    [InlineData("22:00", "06:00", null, Monday, 12, 0, false)]
    [InlineData("22:00", "06:00", "mon", Tuesday, 2, 0, true)]
    [InlineData("22:00", "06:00", "mon", Monday, 2, 0, false)]
    [InlineData("22:00", "06:00", "mon", Tuesday, 23, 0, false)]
    [InlineData("08:00", "18:00", "sat,sun", Monday, 12, 0, false)]
    [InlineData("08:00", "18:00", "sat,sun", Saturday, 12, 0, true)]
    [InlineData("00:00", "00:00", null, Monday, 3, 0, true)]
    public void A_time_window_opens_at_its_start_closes_at_its_end_and_may_cross_midnight(
        string from,
        string to,
        string? days,
        int day,
        int hour,
        int minute,
        bool expected)
    {
        var window = new TimeWindow(from, to, Days(days));

        Assert.Equal(expected, CommandConditionEvaluator.IsInside(window, At(day, hour, minute)));
    }

    [Fact]
    public void A_blocked_run_says_why_and_never_names_variable_values()
    {
        var conditions = new CommandConditions(SessionRequirements.Unlocked, new TimeWindow("08:00", "18:00", ["mon"]));

        ConditionVerdict locked = CommandConditionEvaluator.Evaluate(conditions, Session(true, true), At(Monday, 12, 0));
        ConditionVerdict early = CommandConditionEvaluator.Evaluate(conditions, Session(true, false), At(Monday, 6, 0));

        Assert.Contains("locked", locked.Reason, StringComparison.Ordinal);
        Assert.Contains("08:00", early.Reason, StringComparison.Ordinal);
        Assert.Contains("mon", early.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("00:00", true)]
    [InlineData("23:59", true)]
    [InlineData("24:00", false)]
    [InlineData("7:30", false)]
    [InlineData("07:60", false)]
    [InlineData("07-30", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Times_are_read_as_hh_mm(string? text, bool valid)
    {
        Assert.Equal(valid, CommandConditionEvaluator.TryParseTime(text, out _));
    }

    // ------------------------------------------------------------------
    // Validation
    // ------------------------------------------------------------------

    [Fact]
    public void A_command_with_valid_conditions_is_valid()
    {
        CommandDocument command = TestDocuments.Command() with
        {
            SchemaVersion = DocumentSchema.CommandVersion,
            Conditions = new CommandConditions(SessionRequirements.Locked, new TimeWindow("22:00", "06:00", ["mon", "tue"])),
        };

        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(command));
    }

    [Fact]
    public void Invalid_conditions_are_reported_with_json_pointers()
    {
        CommandDocument command = TestDocuments.Command() with
        {
            Conditions = new CommandConditions("sleeping", new TimeWindow("25:00", string.Empty, ["mon", "mon", "funday"])),
        };

        ValidationResult result = DocumentValidator.ValidateCommand(command);

        Assert.Contains(result.Errors, error => error.Path == "/conditions/session" && error.Code == ValidationCodes.Invalid);
        Assert.Contains(result.Errors, error => error.Path == "/conditions/timeWindow/from" && error.Code == ValidationCodes.Invalid);
        Assert.Contains(result.Errors, error => error.Path == "/conditions/timeWindow/to" && error.Code == ValidationCodes.Required);
        Assert.Contains(result.Errors, error => error.Path == "/conditions/timeWindow/days/1" && error.Code == ValidationCodes.Duplicate);
        Assert.Contains(result.Errors, error => error.Path == "/conditions/timeWindow/days/2" && error.Code == ValidationCodes.Invalid);
    }

    [Fact]
    public void Schema_version_2_is_understood_and_version_3_is_not()
    {
        Assert.True(DocumentValidator.ValidateCommand(TestDocuments.Command() with { SchemaVersion = 1 }).IsValid);
        Assert.True(DocumentValidator.ValidateCommand(TestDocuments.Command() with { SchemaVersion = 2 }).IsValid);

        ValidationResult future = DocumentValidator.ValidateCommand(TestDocuments.Command() with { SchemaVersion = 3 });

        Assert.Contains(future.Errors, error => error.Path == "/schemaVersion" && error.Code == ValidationCodes.Unsupported);
    }

    [Fact]
    public void The_id_of_a_system_command_is_reserved()
    {
        ValidationResult result = DocumentValidator.ValidateCommand(TestDocuments.Command() with { Id = SystemCommands.MaintenanceId });

        Assert.Contains(result.Errors, error => error.Path == "/id" && error.Code == ValidationCodes.Conflict);
    }

    [Fact]
    public void The_conditions_fixture_is_valid()
    {
        CommandDocument command = TestData.ReadCommand("command.conditions.json");

        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(command));
        Assert.Equal(SessionRequirements.Locked, command.Conditions!.Session);
        Assert.Equal(5, command.Conditions.TimeWindow!.Days!.Count);
    }

    // ------------------------------------------------------------------
    // Migration from schema version 1
    // ------------------------------------------------------------------

    private const string VersionOneCommand = """
        {
          "schemaVersion": 1,
          "id": "old_command",
          "name": "Old command",
          "enabled": true,
          "exposeToHa": false,
          "runAs": "system",
          "action": { "type": "powershell", "source": "inline", "code": "Write-Output 'hi'" },
          "timeoutSeconds": 30,
          "captureOutput": true
        }
        """;

    [Fact]
    public async Task A_schema_version_1_command_is_migrated_automatically_and_the_old_file_is_kept()
    {
        using var temp = new TempDirectory();
        string path = ConfigTestSupport.CommandPath(temp, "old_command");
        ConfigTestSupport.WriteFile(path, VersionOneCommand);

        using ConfigStore store = await ConfigTestSupport.OpenAsync(ConfigTestSupport.Options(temp));

        Assert.Empty(store.Problems);
        CommandDocument command = store.Effective.GetDocument<CommandDocument>("old_command")!;
        Assert.Equal(DocumentSchema.CommandVersion, command.SchemaVersion);
        Assert.Equal("Old command", command.Name);
        Assert.Equal(30, command.TimeoutSeconds);
        Assert.Null(command.Conditions);
        Assert.Null(command.Secrets);

        string upgraded = await File.ReadAllTextAsync(path);
        Assert.Contains("\"schemaVersion\": 2", upgraded, StringComparison.Ordinal);
        string backup = temp.Combine("config", ".history", "commands", "old_command", "000000.v1.bak.json");
        Assert.True(File.Exists(backup));
        Assert.Contains("\"schemaVersion\": 1", await File.ReadAllTextAsync(backup), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_migrated_command_keeps_working()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync(
            seed: temp => ConfigTestSupport.WriteFile(ConfigTestSupport.CommandPath(temp, "old_command"), VersionOneCommand));
        h.Gate!.AutoComplete = Ok;

        RunResult result = await h.RunAsync("old_command");

        Assert.True(result.Succeeded);
        Assert.Equal("ok\n", result.StandardOutput);
    }

    [Fact]
    public async Task Sensors_and_settings_stay_at_schema_version_1()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await ConfigTestSupport.OpenAsync(ConfigTestSupport.Options(temp));

        SaveResult<SensorDocument> sensor = await store.Local.SaveAsync(ConfigTestSupport.Sensor(), null, CancellationToken.None);
        SaveResult<CommandDocument> command = await store.Local.SaveAsync(ConfigTestSupport.Command(), null, CancellationToken.None);

        Assert.Equal(DocumentSchema.CurrentVersion, sensor.Document!.SchemaVersion);
        Assert.Equal(DocumentSchema.CommandVersion, command.Document!.SchemaVersion);
    }

    [Fact]
    public async Task Conditions_survive_a_save_and_a_reload()
    {
        using var temp = new TempDirectory();
        CommandDocument saved = ConfigTestSupport.Command() with
        {
            Conditions = new CommandConditions(SessionRequirements.Unlocked, new TimeWindow("08:00", "18:00", ["mon", "fri"])),
        };
        using (ConfigStore first = await ConfigTestSupport.OpenAsync(ConfigTestSupport.Options(temp)))
        {
            Assert.True((await first.Local.SaveAsync(saved, null, CancellationToken.None)).IsApplied);
        }

        using ConfigStore second = await ConfigTestSupport.OpenAsync(ConfigTestSupport.Options(temp));

        CommandConditions conditions = second.Effective.GetDocument<CommandDocument>(saved.Id)!.Conditions!;
        Assert.Equal(SessionRequirements.Unlocked, conditions.Session);
        Assert.Equal("08:00", conditions.TimeWindow!.From);
        string[] expectedDays = ["mon", "fri"];
        Assert.Equal(expectedDays, conditions.TimeWindow.Days);
    }

    // ------------------------------------------------------------------
    // The engine
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_command_with_condition_unlocked_is_rejected_while_the_session_is_locked()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Gate!.AutoComplete = Ok;
        await h.SaveAsync(Cmd.Gated("c") with { Conditions = new CommandConditions(SessionRequirements.Unlocked) });

        h.Platform.Session.SetLocked(true);
        RunResult rejected = await h.RunAsync("c");

        Assert.False(rejected.Started);
        Assert.Equal(ErrorCodes.ConditionNotMet, rejected.ErrorCode);
        Assert.Contains("locked", rejected.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(h.Gate.Calls);
        RunRecord recorded = Assert.Single(await h.History.QueryAsync(new RunQuery(), CancellationToken.None));
        Assert.Equal(ErrorCodes.ConditionNotMet, recorded.ErrorCode);

        h.Platform.Session.SetLocked(false);
        Assert.True((await h.RunAsync("c")).Succeeded);
    }

    [Fact]
    public async Task A_command_that_needs_a_logged_in_user_is_rejected_when_nobody_is_logged_in()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Gate!.AutoComplete = Ok;
        await h.SaveAsync(Cmd.Gated("c") with { Conditions = new CommandConditions(SessionRequirements.LoggedIn) });

        h.Platform.Session.SimulateLogoff();
        RunResult rejected = await h.RunAsync("c");

        Assert.Equal(ErrorCodes.ConditionNotMet, rejected.ErrorCode);
        Assert.Empty(h.Gate.Calls);
    }

    [Fact]
    public async Task A_time_window_that_contains_now_lets_the_command_run_and_one_that_does_not_blocks_it()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Gate!.AutoComplete = Ok;
        DateTimeOffset local = h.Clock.GetLocalNow();
        string Time(int hours) => local.AddHours(hours).ToString("HH:mm", CultureInfo.InvariantCulture);
        await h.SaveAsync(
            Cmd.Gated("open") with { Conditions = new CommandConditions(null, new TimeWindow(Time(-1), Time(1))) },
            Cmd.Gated("closed") with { Conditions = new CommandConditions(null, new TimeWindow(Time(1), Time(2))) });

        RunResult open = await h.RunAsync("open");
        RunResult closed = await h.RunAsync("closed");

        Assert.True(open.Succeeded);
        Assert.Equal(ErrorCodes.ConditionNotMet, closed.ErrorCode);
        Assert.False(closed.Started);
    }

    [Fact]
    public async Task The_conditions_apply_to_the_steps_of_a_sequence_as_well()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Gate!.AutoComplete = Ok;
        await h.SaveAsync(
            Cmd.Gated("inner") with { Conditions = new CommandConditions(SessionRequirements.Locked) },
            Cmd.Sequence("outer", SequenceErrorModes.Stop, Cmd.Step("inner")));

        RunResult result = await h.RunAsync("outer");

        Assert.Equal(ErrorCodes.ConditionNotMet, result.ErrorCode);
        Assert.Empty(h.Gate.Calls);
    }

    [Fact]
    public async Task The_pause_is_reported_before_a_condition()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Gate!.AutoComplete = Ok;
        await h.SaveAsync(Cmd.Gated("c") with { Conditions = new CommandConditions(SessionRequirements.Locked) });

        h.Pause.SetPaused(true);
        RunResult rejected = await h.RunAsync("c");

        Assert.Equal(ErrorCodes.Paused, rejected.ErrorCode);
    }

    // ------------------------------------------------------------------
    // Maintenance mode
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_maintenance_switch_pauses_the_agent_and_is_the_only_command_that_runs_while_paused()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Gate!.AutoComplete = Ok;
        await h.SaveAsync(Cmd.Gated("c"));
        Assert.NotNull(h.Registry.TryGet(SystemCommands.MaintenanceId));

        RunResult on = await h.RunAsync(SystemCommands.MaintenanceId, (SystemCommands.MaintenanceVariable, true));
        Assert.True(on.Succeeded);
        Assert.True(h.Pause.IsPaused);
        Assert.Equal(ErrorCodes.Paused, (await h.RunAsync("c")).ErrorCode);

        RunResult off = await h.RunAsync(SystemCommands.MaintenanceId, (SystemCommands.MaintenanceVariable, false));
        Assert.True(off.Succeeded);
        Assert.False(h.Pause.IsPaused);
        Assert.True((await h.RunAsync("c")).Succeeded);
    }

    [Fact]
    public async Task The_maintenance_switch_needs_its_variable()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();

        RunResult result = await h.RunAsync(SystemCommands.MaintenanceId);

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.False(h.Pause.IsPaused);
    }

    [Fact]
    public void The_maintenance_switch_is_announced_as_a_switch_with_the_pause_metric_as_its_state()
    {
        CommandDescriptor descriptor = ManifestBuilder.Describe(SystemCommands.Maintenance);

        Assert.Equal(SystemCommands.MaintenanceId, descriptor.Id);
        Assert.NotNull(descriptor.Control);
        Assert.Equal(HaControlTypes.Switch, descriptor.Control.Type);
        Assert.Equal(SystemCommands.MaintenanceVariable, descriptor.Control.Var);
        Assert.Equal(AgentMetricIds.Paused, descriptor.Control.StateMetric);
        Assert.Equal(ParameterTypes.Flag, Assert.Single(descriptor.Vars!).Type);
    }

    [Fact]
    public async Task The_pause_flag_is_reported_as_a_metric()
    {
        var pause = new AgentPause();
        var provider = new AgentMetricProvider(pause);

        MetricDescriptor descriptor = Assert.Single(provider.Describe());
        Assert.Equal(AgentMetricIds.Paused, descriptor.Id);
        Assert.Equal("binary_sensor", descriptor.Kind);
        Assert.Equal("bool", descriptor.ValueType);

        string[] pausedOnly = [AgentMetricIds.Paused];
        string[] unrelated = ["cpu.usage"];
        IReadOnlyDictionary<string, object?> before = await provider.SampleAsync(pausedOnly, CancellationToken.None);
        pause.SetPaused(true);
        IReadOnlyDictionary<string, object?> after = await provider.SampleAsync(pausedOnly, CancellationToken.None);
        IReadOnlyDictionary<string, object?> other = await provider.SampleAsync(unrelated, CancellationToken.None);

        Assert.False(Assert.IsType<bool>(before[AgentMetricIds.Paused]));
        Assert.True(Assert.IsType<bool>(after[AgentMetricIds.Paused]));
        Assert.Empty(other);
    }
}
