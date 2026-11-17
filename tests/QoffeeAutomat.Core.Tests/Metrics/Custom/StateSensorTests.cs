using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.IO;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Core.Metrics.Custom;
using static QoffeeAutomat.Core.Tests.Metrics.Custom.Sensors;

namespace QoffeeAutomat.Core.Tests.Metrics.Custom;

/// <summary>
/// The custom sensor type <c>state</c> (PLAN.md S06, T714): a value that a script, the API or the UI sets from outside, validated
/// against the value type and the options, published at once as an event-driven metric, optionally kept across restarts.
/// </summary>
[Trait("Category", "StateSensor")]
public sealed class StateSensorTests
{
    private static readonly string[] Modes = ["Normal", "Eco"];

    private static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static Task<StateSetResult> SetAsync(SensorHarness harness, string id, string text) =>
        harness.States.SetTextAsync(id, text, CancellationToken.None);

    [Fact]
    public async Task A_number_is_set_from_text_and_read_back()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("level"));

        StateSetResult result = await SetAsync(harness, "level", "42.5");

        Assert.True(result.IsSuccess);
        Assert.Equal(42.5, result.Value);
        Assert.Equal(harness.Clock.GetUtcNow(), result.UpdatedAt);
        StateGetResult read = harness.States.Get("level");
        Assert.True(read.IsSuccess);
        Assert.Equal(42.5, read.Reading!.Value);
        Assert.Equal(harness.Clock.GetUtcNow(), read.Reading.UpdatedAt);
    }

    [Fact]
    public async Task Setting_an_undeclared_sensor_fails_with_a_clear_error()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();

        StateSetResult result = await SetAsync(harness, "nope", "1");

        Assert.False(result.IsSuccess);
        Assert.Equal(StateSensorErrors.UnknownSensor, result.ErrorCode);
        Assert.Contains("'nope'", result.Message, StringComparison.Ordinal);
        Assert.Contains("state", result.Message, StringComparison.Ordinal);
        Assert.Equal(StateSensorErrors.UnknownSensor, harness.States.Get("nope").ErrorCode);
    }

    [Fact]
    public async Task Setting_a_sensor_of_another_type_fails_and_names_the_type()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(Script("s"));

        StateSetResult result = await SetAsync(harness, "s", "1");

        Assert.Equal(StateSensorErrors.NotStateSensor, result.ErrorCode);
        Assert.Contains("powershell", result.Message, StringComparison.Ordinal);
        Assert.Equal(StateSensorErrors.NotStateSensor, harness.States.Get("s").ErrorCode);
    }

    [Theory]
    [InlineData("number", "warm")]
    [InlineData("number", "NaN")]
    [InlineData("number", "1e999")]
    [InlineData("number", "")]
    [InlineData("bool", "maybe")]
    [InlineData("timestamp", "yesterday")]
    [InlineData("enum", "Turbo")]
    public async Task A_value_that_does_not_fit_the_value_type_fails_and_changes_nothing(string valueType, string text)
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("s", valueType, options: valueType == "enum" ? Modes : null));
        var changes = new List<string>();
        harness.States.Changed += (_, change) => changes.Add(change.SensorId);

        StateSetResult result = await SetAsync(harness, "s", text);

        Assert.False(result.IsSuccess);
        Assert.Equal(StateSensorErrors.InvalidValue, result.ErrorCode);
        Assert.Contains("'s'", result.Message, StringComparison.Ordinal);
        Assert.Null(harness.States.Get("s").Reading!.Value);
        Assert.Empty(changes);
    }

    [Fact]
    public async Task The_error_for_an_option_lists_the_options()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("mode", "enum", options: Modes));

        StateSetResult result = await SetAsync(harness, "mode", "Turbo");

        Assert.Contains("Turbo", result.Message, StringComparison.Ordinal);
        Assert.Contains("Normal, Eco", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("No", false)]
    [InlineData("OFF", false)]
    public async Task A_bool_is_read_the_way_script_output_is(string text, bool expected)
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("flag", "bool"));

        StateSetResult result = await SetAsync(harness, "flag", text);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public async Task An_option_is_matched_without_regard_to_case_and_published_as_declared()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("mode", "enum", options: Modes));

        StateSetResult result = await SetAsync(harness, "mode", "eco");

        Assert.True(result.IsSuccess);
        Assert.Equal("Eco", result.Value);
    }

    [Fact]
    public async Task A_timestamp_is_published_in_utc()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("when", "timestamp"));

        StateSetResult result = await SetAsync(harness, "when", "2026-03-04T05:06:07+02:00");

        Assert.Equal("2026-03-04T03:06:07Z", result.Value);
    }

    [Fact]
    public async Task A_text_is_kept_as_it_is_but_not_beyond_what_home_assistant_accepts()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("note", "string"));

        Assert.Equal("  spaced  ", (await SetAsync(harness, "note", "  spaced  ")).Value);

        StateSetResult tooLong = await SetAsync(harness, "note", new string('x', StateSensorStore.MaxTextLength + 1));

        Assert.Equal(StateSensorErrors.InvalidValue, tooLong.ErrorCode);
        Assert.Equal("  spaced  ", harness.States.Get("note").Reading!.Value);
    }

    [Fact]
    public async Task The_published_number_is_rounded_to_the_precision_of_the_sensor()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("level", precision: 1));

        StateSetResult result = await SetAsync(harness, "level", "3.14159");

        Assert.Equal(3.1, result.Value);
    }

    [Fact]
    public async Task A_json_value_of_the_right_kind_is_accepted_and_a_string_is_read_like_command_line_text()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("level"), State("flag", "bool"), State("note", "string"));

        Assert.Equal(7.0, (await harness.States.SetAsync("level", Json("7"), CancellationToken.None)).Value);
        Assert.Equal(8.0, (await harness.States.SetAsync("level", Json("\"8\""), CancellationToken.None)).Value);
        Assert.True(Assert.IsType<bool>((await harness.States.SetAsync("flag", Json("true"), CancellationToken.None)).Value));
        Assert.Equal("on", (await harness.States.SetAsync("note", Json("\"on\""), CancellationToken.None)).Value);
    }

    [Theory]
    [InlineData("level", "true")]
    [InlineData("level", "{}")]
    [InlineData("level", "[1]")]
    [InlineData("level", "null")]
    [InlineData("flag", "5")]
    [InlineData("note", "5")]
    public async Task A_json_value_of_the_wrong_kind_is_refused(string sensorId, string json)
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("level"), State("flag", "bool"), State("note", "string"));

        StateSetResult result = await harness.States.SetAsync(sensorId, Json(json), CancellationToken.None);

        Assert.Equal(StateSensorErrors.InvalidValue, result.ErrorCode);
        Assert.Null(harness.States.Get(sensorId).Reading!.Value);
    }

    [Fact]
    public async Task A_missing_json_value_is_refused()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("level"));

        StateSetResult result = await harness.States.SetAsync("level", default, CancellationToken.None);

        Assert.Equal(StateSensorErrors.InvalidValue, result.ErrorCode);
        Assert.Contains("required", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sensor_shows_its_initial_value_until_one_is_set_and_none_when_it_has_neither()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("mode", "enum", initial: "Normal", options: Modes), State("level"));

        StateReading initial = harness.States.Get("mode").Reading!;
        Assert.Equal("Normal", initial.Value);
        Assert.Null(initial.UpdatedAt);
        Assert.Null(harness.States.Get("level").Reading!.Value);

        await SetAsync(harness, "mode", "Eco");

        Assert.Equal("Eco", harness.States.Get("mode").Reading!.Value);
    }

    [Fact]
    public async Task Setting_a_value_raises_changed_with_the_metric_id_of_the_sensor()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("level"));
        var changes = new List<StateChangedEventArgs>();
        harness.States.Changed += (_, change) => changes.Add(change);

        await SetAsync(harness, "level", "1");

        StateChangedEventArgs change = Assert.Single(changes);
        Assert.Equal("level", change.SensorId);
        Assert.Equal("custom.level", change.MetricId);
    }

    // The value of a state sensor is event-driven: subscribers see it at once, and nothing is ever sampled on a timer.

    [Fact]
    public async Task A_state_sensor_is_an_event_driven_metric_without_an_interval()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("level") with { IntervalSeconds = 30 });
        await harness.Engine.PollAsync(CancellationToken.None);

        MetricCatalogEntry entry = Assert.Single(harness.Engine.Catalog, candidate => candidate.Id == "custom.level");

        Assert.Equal(0, entry.IntervalSeconds);
        Assert.Equal(0, entry.Descriptor.IntervalSeconds);
        Assert.Equal(0, Assert.Single(harness.Provider.Describe()).IntervalSeconds);
    }

    [Fact]
    public async Task A_new_value_reaches_a_subscriber_at_once_and_no_script_runs()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("mode", "enum", initial: "Normal", options: Modes));
        MetricSubscription subscription = harness.Engine.Subscribe(MetricSelection.From(["custom.mode"]));
        await harness.Engine.PollAsync(CancellationToken.None);
        MetricUpdate snapshot = Assert.Single(SensorHarness.Drain(subscription));
        Assert.True(snapshot.IsSnapshot);
        Assert.Equal("Normal", snapshot.Values["custom.mode"]);

        // Nothing changes while time passes: there is no timer behind the sensor.
        await harness.TickAsync(TimeSpan.FromMinutes(1), 5);
        Assert.Empty(SensorHarness.Drain(subscription));

        await SetAsync(harness, "mode", "Eco");
        await harness.Engine.PollAsync(CancellationToken.None);

        MetricUpdate update = Assert.Single(SensorHarness.Drain(subscription));
        Assert.False(update.IsSnapshot);
        Assert.Equal("Eco", update.Values["custom.mode"]);
        Assert.Empty(harness.Launcher.Calls);
        Assert.Equal(0, harness.Processes.Reads);
    }

    [Fact]
    public async Task A_state_sensor_without_a_value_is_unavailable_until_one_is_set()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("level"));
        MetricSubscription subscription = harness.Engine.Subscribe(MetricSelection.From(["custom.level"]));
        await harness.Engine.PollAsync(CancellationToken.None);

        MetricUpdate snapshot = Assert.Single(SensorHarness.Drain(subscription));
        Assert.True(snapshot.Values.ContainsKey("custom.level"));
        Assert.Null(snapshot.Values["custom.level"]);

        await SetAsync(harness, "level", "3");
        await harness.TickAsync(TimeSpan.FromSeconds(1), 1);

        Assert.Equal(3.0, Assert.Single(SensorHarness.Drain(subscription)).Values["custom.level"]);
    }

    [Fact]
    public async Task A_state_sensor_without_a_value_is_not_reported_as_failing()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("level"));

        IReadOnlyDictionary<string, object?> values = await harness.SampleAsync("level");

        Assert.Null(values["custom.level"]);
        CustomSensorStatus status = Assert.Single(harness.Provider.GetStatuses());
        Assert.Null(status.LastError);
        Assert.Equal(0, status.ConsecutiveFailures);
    }

    [Fact]
    public async Task The_dry_run_of_a_draft_shows_the_initial_value_and_not_a_value_that_was_set()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("level", initial: 5));
        await SetAsync(harness, "level", "9");
        SensorDocument draft = State("level", initial: 1);

        SensorTestOutcome outcome = await harness.Provider.TestAsync(draft, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(1.0, outcome.Value);
        Assert.Equal(9.0, harness.States.Get("level").Reading!.Value);
    }

    [Fact]
    public async Task The_dry_run_of_a_draft_without_an_initial_value_succeeds_with_no_value()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();

        SensorTestOutcome outcome = await harness.Provider.TestAsync(State("level"), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Null(outcome.Value);
        Assert.Null(outcome.Error);
    }

    // Persistence: a value with persist survives a restart of the agent, any other returns to its initial value.

    [Fact]
    public async Task A_persisted_value_survives_an_agent_restart()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("mode", "enum", initial: "Normal", persist: true, options: Modes));
        StateSetResult result = await SetAsync(harness, "mode", "Eco");
        Assert.True(result.IsSuccess);

        using StateSensorStore restarted = harness.RestartedStates();

        StateReading reading = restarted.Get("mode").Reading!;
        Assert.Equal("Eco", reading.Value);
        Assert.Equal(result.UpdatedAt!.Value.ToUnixTimeMilliseconds(), reading.UpdatedAt!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task A_value_without_persist_returns_to_its_initial_value_after_a_restart()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("mode", "enum", initial: "Normal", persist: false, options: Modes), State("level"));
        await SetAsync(harness, "mode", "Eco");
        await SetAsync(harness, "level", "9");

        using StateSensorStore restarted = harness.RestartedStates();

        Assert.Equal("Normal", restarted.Get("mode").Reading!.Value);
        Assert.Null(restarted.Get("level").Reading!.Value);
        Assert.False(File.Exists(harness.StatePath));
    }

    [Fact]
    public async Task Only_the_sensors_with_persist_are_written_to_the_file()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("kept", persist: true), State("lost"));
        await SetAsync(harness, "lost", "1");
        await SetAsync(harness, "kept", "2");

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(harness.StatePath));

        JsonElement values = document.RootElement.GetProperty("values");
        Assert.Equal(["kept"], values.EnumerateObject().Select(property => property.Name));
        Assert.Equal(2, values.GetProperty("kept").GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task A_persisted_value_is_ignored_when_the_sensor_no_longer_asks_for_persistence()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("level", initial: 1, persist: true));
        await SetAsync(harness, "level", "9");

        await harness.SaveAsync(State("level", initial: 1, persist: false) with { Revision = null });
        using StateSensorStore restarted = harness.RestartedStates();

        Assert.Equal(1.0, restarted.Get("level").Reading!.Value);
    }

    [Fact]
    public async Task A_persisted_value_that_no_longer_fits_the_sensor_gives_way_to_the_initial_value()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("mode", "enum", initial: "Normal", persist: true, options: Modes));
        await SetAsync(harness, "mode", "Eco");

        // The options were edited: Eco is gone.
        await harness.SaveAsync(State("mode", "enum", initial: "Normal", persist: true, options: ["Normal", "Boost"]));
        using StateSensorStore restarted = harness.RestartedStates();

        Assert.Equal("Normal", restarted.Get("mode").Reading!.Value);
    }

    [Fact]
    public async Task A_damaged_file_means_that_the_sensors_start_from_their_initial_values()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("level", initial: 4, persist: true));
        await File.WriteAllTextAsync(harness.StatePath, "{ this is not json");

        using StateSensorStore restarted = harness.RestartedStates();

        Assert.Equal(4.0, restarted.Get("level").Reading!.Value);
    }

    [Fact]
    public async Task A_value_that_cannot_be_saved_is_still_set_and_the_error_says_so()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("level", persist: true));
        using var states = new StateSensorStore(harness.Store, harness.Clock, harness.StatePath, new FailingFiles());

        StateSetResult result = await states.SetTextAsync("level", "5", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StateSensorErrors.PersistFailed, result.ErrorCode);
        Assert.Contains("lost when the agent restarts", result.Message, StringComparison.Ordinal);
        Assert.Equal(5.0, states.Get("level").Reading!.Value);
    }

    [Fact]
    public async Task A_deleted_sensor_is_dropped_from_the_file_with_the_next_write()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        await harness.SaveAsync(State("gone", persist: true), State("kept", persist: true));
        await SetAsync(harness, "gone", "1");
        await SetAsync(harness, "kept", "2");

        await harness.Store.Local.DeleteAsync<SensorDocument>("gone", null, CancellationToken.None);
        await SetAsync(harness, "kept", "3");

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(harness.StatePath));
        Assert.Equal(["kept"], document.RootElement.GetProperty("values").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task Values_of_several_sensors_can_be_set_at_the_same_time_without_losing_one()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();
        SensorDocument[] sensors = [.. Enumerable.Range(0, 8).Select(index => State("s" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), persist: true))];
        await harness.SaveAsync(sensors);

        await Task.WhenAll(sensors.Select((sensor, index) =>
            harness.States.SetTextAsync(sensor.Id, index.ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None)));

        using StateSensorStore restarted = harness.RestartedStates();
        for (int index = 0; index < sensors.Length; index++)
        {
            Assert.Equal((double)index, restarted.Get(sensors[index].Id).Reading!.Value);
        }
    }

    [Fact]
    public void Validation_accepts_a_state_sensor_and_refuses_an_initial_value_of_the_wrong_kind()
    {
        Assert.True(QoffeeAutomat.Core.Validation.DocumentValidator.ValidateSensor(State("level", initial: 1, persist: true)).IsValid);

        SensorDocument wrong = State("level") with { Source = new StateSensorSource(Json("\"warm\"")) };
        Assert.False(QoffeeAutomat.Core.Validation.DocumentValidator.ValidateSensor(wrong).IsValid);
    }

    /// <summary>Files that cannot be written, like a full disk.</summary>
    private sealed class FailingFiles : IFileOperations
    {
        public Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> content, bool ownerOnly, CancellationToken cancellationToken) =>
            throw new IOException("The disk is full.");

        public void Move(string source, string destination) => throw new IOException("The disk is full.");

        public void Delete(string path)
        {
        }
    }
}
