using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Contracts.Tests;

/// <summary>Config document models against the fixtures in protocol/fixtures/config and the schemas in protocol/schema/config.</summary>
public class ConfigDocumentTests
{
    public static TheoryData<string> ValidConfigFixtures => ToData(RepositoryPaths.FixtureFiles("config"));

    public static TheoryData<string> InvalidConfigFixtures => ToData(RepositoryPaths.FixtureFiles("config/invalid"));

    [Fact]
    public void Every_document_kind_has_a_valid_fixture()
    {
        HashSet<string> present = RepositoryPaths.FixtureFiles("config")
            .Select(RepositoryPaths.FixtureType)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("command", present);
        Assert.Contains("sensor", present);
        Assert.Contains("metrics", present);
        Assert.Contains("settings", present);
    }

    [Theory]
    [MemberData(nameof(ValidConfigFixtures))]
    public void Valid_fixture_matches_its_schema(string fixture)
    {
        JsonElement instance = RepositoryPaths.ReadFixtureElement(fixture);

        Assert.Empty(RepositoryPaths.Schemas.Validate(instance, SchemaOf(fixture)));
    }

    [Theory]
    [MemberData(nameof(InvalidConfigFixtures))]
    public void Invalid_fixture_is_rejected_by_its_schema(string fixture)
    {
        JsonElement instance = RepositoryPaths.ReadFixtureElement(fixture);

        Assert.NotEmpty(RepositoryPaths.Schemas.Validate(instance, SchemaOf(fixture)));
    }

    [Theory]
    [MemberData(nameof(ValidConfigFixtures))]
    public void Valid_fixture_round_trips_without_losing_a_member(string fixture)
    {
        JsonElement original = RepositoryPaths.ReadFixtureElement(fixture);
        string firstPass = Reserialize(fixture, RepositoryPaths.ReadFixture(fixture));
        string secondPass = Reserialize(fixture, firstPass);

        using JsonDocument serialized = JsonDocument.Parse(firstPass);
        Assert.Null(JsonCompare.Difference(original, serialized.RootElement, ignoreNulls: true, subset: true));
        Assert.Empty(RepositoryPaths.Schemas.Validate(serialized.RootElement, SchemaOf(fixture)));
        Assert.Equal(firstPass, secondPass);
    }

    [Fact]
    public void The_command_example_of_the_plan_is_read_completely()
    {
        CommandDocument command = Read("config/command.example.json", ConfigJsonContext.Default.CommandDocument);

        Assert.Equal(1, command.SchemaVersion);
        Assert.Equal("set_display_profile", command.Id);
        Assert.Equal("Set display profile", command.Name);
        Assert.True(command.Enabled);
        Assert.True(command.ExposeToHa);
        Assert.Equal(RunAsValues.User, command.RunAs);
        Assert.Equal(60, command.TimeoutSeconds);
        Assert.Equal(ConcurrencyModes.Queue, command.Concurrency);
        Assert.True(command.CaptureOutput);

        PowerShellAction action = Assert.IsType<PowerShellAction>(command.Action);
        Assert.Equal(PowerShellSources.File, action.Source);
        Assert.Equal("set_display_profile.ps1", action.Script);
        Assert.Equal(PowerShellEngines.Auto, action.Engine);

        Assert.NotNull(command.Parameters);
        Assert.Equal(2, command.Parameters.Count);
        Assert.Equal(ParameterTypes.Choice, command.Parameters[0].Type);
        Assert.True(command.Parameters[0].Required);
        Assert.NotNull(command.Parameters[0].Options);
        Assert.Equal(3, command.Parameters[0].Options!.Count);
        Assert.Equal(JsonValueKind.False, command.Parameters[1].Default!.Value.ValueKind);

        Assert.NotNull(command.HaControl);
        Assert.Equal(HaControlTypes.Select, command.HaControl.Type);
        Assert.Equal("Profile", command.HaControl.Parameter);
        Assert.Null(command.HaControl.StateMetric);
    }

    [Fact]
    public void Action_variants_are_selected_by_type()
    {
        CommandDocument process = Read("config/command.process.json", ConfigJsonContext.Default.CommandDocument);
        CommandDocument builtin = Read("config/command.builtin.json", ConfigJsonContext.Default.CommandDocument);
        CommandDocument sequence = Read("config/command.sequence.json", ConfigJsonContext.Default.CommandDocument);
        CommandDocument inline = Read("config/command.inline.json", ConfigJsonContext.Default.CommandDocument);

        ProcessAction processAction = Assert.IsType<ProcessAction>(process.Action);
        string[] expectedArgs = ["-new-tab", "{{Url}}"];
        Assert.False(processAction.WaitForExit);
        Assert.Equal(expectedArgs, processAction.Args);

        Assert.Equal("power.shutdown", Assert.IsType<BuiltinAction>(builtin.Action).Name);
        Assert.Equal(RunAsValues.System, builtin.RunAs);

        SequenceAction sequenceAction = Assert.IsType<SequenceAction>(sequence.Action);
        Assert.Equal(3, sequenceAction.Steps.Count);
        Assert.Equal("set_display_profile", sequenceAction.Steps[0].Command);
        Assert.Equal(2.0, sequenceAction.Steps[1].DelaySeconds);
        Assert.Equal(SequenceErrorModes.Stop, sequenceAction.OnError);
        Assert.Equal(3, sequence.Revision);
        Assert.Equal(DocumentOrigins.Local, sequence.Origin);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 9, 30, 0, TimeSpan.Zero), sequence.UpdatedAt);

        PowerShellAction inlineAction = Assert.IsType<PowerShellAction>(inline.Action);
        Assert.Equal(PowerShellSources.Inline, inlineAction.Source);
        Assert.Equal(PowerShellEngines.Pwsh, inlineAction.Engine);
    }

    [Fact]
    public void Sensor_sources_are_selected_by_type()
    {
        SensorDocument script = Read("config/sensor.powershell.json", ConfigJsonContext.Default.SensorDocument);
        SensorDocument process = Read("config/sensor.process_running.json", ConfigJsonContext.Default.SensorDocument);
        SensorDocument state = Read("config/sensor.state.json", ConfigJsonContext.Default.SensorDocument);

        Assert.IsType<PowerShellSensorSource>(script.Source);
        Assert.Equal("steam", Assert.IsType<ProcessRunningSensorSource>(process.Source).Process);
        StateSensorSource stateSource = Assert.IsType<StateSensorSource>(state.Source);
        Assert.True(stateSource.Persist);
        Assert.Equal("Normal-Mode", stateSource.InitialValue!.Value.GetString());
    }

    [Fact]
    public void Metric_overrides_and_settings_are_read()
    {
        MetricOverridesDocument metrics = Read("config/metrics.json", ConfigJsonContext.Default.MetricOverridesDocument);
        AgentSettingsDocument settings = Read("config/settings.json", ConfigJsonContext.Default.AgentSettingsDocument);

        Assert.Equal(10.0, metrics.Overrides["cpu.usage"].IntervalSeconds);
        Assert.False(metrics.Overrides["window.title"].ExposeToHa);
        Assert.Equal("Gaming-PC", settings.Name);
        Assert.Equal(AgentSettingsDefaults.LinkPort, settings.EffectiveLinkPort);
        Assert.Equal("information", settings.Logging!.Level);
        Assert.Equal(365, settings.Retention!.AuditLogDays);
        Assert.True(settings.TrayIcon);
        Assert.True(settings.EffectiveTrayIcon);
    }

    [Fact]
    public void The_tray_icon_is_shown_unless_the_settings_hide_it()
    {
        var absent = new AgentSettingsDocument(DocumentSchema.CurrentVersion, "Gaming-PC");
        var hidden = absent with { TrayIcon = false };

        Assert.True(absent.EffectiveTrayIcon);
        Assert.False(hidden.EffectiveTrayIcon);
        using JsonDocument written = JsonDocument.Parse(JsonSerializer.Serialize(hidden, ConfigJsonContext.Default.AgentSettingsDocument));
        Assert.False(written.RootElement.GetProperty("trayIcon").GetBoolean());
    }

    [Fact]
    public void The_cim_class_member_is_named_class_in_json()
    {
        var source = new CimQuerySensorSource("root\\cimv2", "Win32_Battery", "EstimatedChargeRemaining");

        string json = JsonSerializer.Serialize<SensorSource>(source, ConfigJsonContext.Default.SensorSource);
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal("Win32_Battery", document.RootElement.GetProperty("class").GetString());
        Assert.Equal("cim_query", document.RootElement.GetProperty("type").GetString());
        SensorSource back = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.SensorSource)!;
        Assert.Equal("Win32_Battery", Assert.IsType<CimQuerySensorSource>(back).ClassName);
    }

    [Fact]
    public void An_unknown_action_type_becomes_an_UnknownAction_that_round_trips()
    {
        const string json = """
            {"schemaVersion":1,"id":"x","name":"X","enabled":true,"exposeToHa":false,"runAs":"system",
             "action":{"type":"telepathy","target":"moon"}}
            """;

        CommandDocument command = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.CommandDocument)!;

        UnknownAction unknown = Assert.IsType<UnknownAction>(command.Action);
        Assert.Equal("telepathy", unknown.TypeName);
        string written = JsonSerializer.Serialize(command, ConfigJsonContext.Default.CommandDocument);
        Assert.Contains("\"target\": \"moon\"", written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_action_type_becomes_an_UnknownAction_with_an_empty_name()
    {
        const string json = """{"schemaVersion":1,"id":"x","action":{}}""";

        CommandDocument command = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.CommandDocument)!;

        Assert.Equal(string.Empty, Assert.IsType<UnknownAction>(command.Action).TypeName);
    }

    [Fact]
    public void Missing_members_do_not_throw_so_that_the_validator_can_report_them()
    {
        CommandDocument command = JsonSerializer.Deserialize("""{"schemaVersion":1}""", ConfigJsonContext.Default.CommandDocument)!;

        Assert.Null(command.Id);
        Assert.Null(command.Action);
        Assert.False(command.Enabled);
        Assert.Equal(60, command.TimeoutSeconds);
        Assert.Equal(ConcurrencyModes.Exclusive, command.Concurrency);
    }

    [Fact]
    public void A_value_of_the_wrong_kind_is_rejected()
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize(
            """{"schemaVersion":1,"enabled":"yes"}""",
            ConfigJsonContext.Default.CommandDocument));
    }

    [Fact]
    public void Output_uses_camel_case_names_and_writes_the_action_type()
    {
        var command = new CommandDocument(
            DocumentSchema.CurrentVersion,
            "shutdown",
            "Shut down",
            true,
            false,
            RunAsValues.System,
            new BuiltinAction("power.shutdown"));

        string json = JsonSerializer.Serialize(command, ConfigJsonContext.Default.CommandDocument);
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.TryGetProperty("exposeToHa", out _));
        Assert.True(document.RootElement.TryGetProperty("timeoutSeconds", out _));
        Assert.Equal("builtin", document.RootElement.GetProperty("action").GetProperty("type").GetString());
        Assert.False(document.RootElement.TryGetProperty("description", out _));
    }

    private static T Read<T>(string fixture, JsonTypeInfo<T> typeInfo)
    {
        return JsonSerializer.Deserialize(RepositoryPaths.ReadFixture(fixture), typeInfo)!;
    }

    private static string SchemaOf(string fixture)
    {
        return RepositoryPaths.SchemaId("config/" + RepositoryPaths.FixtureType(fixture) + ".schema.json");
    }

    private static string Reserialize(string fixture, string json)
    {
        return RepositoryPaths.FixtureType(fixture) switch
        {
            "command" => JsonSerializer.Serialize(
                JsonSerializer.Deserialize(json, ConfigJsonContext.Default.CommandDocument)!,
                ConfigJsonContext.Default.CommandDocument),
            "sensor" => JsonSerializer.Serialize(
                JsonSerializer.Deserialize(json, ConfigJsonContext.Default.SensorDocument)!,
                ConfigJsonContext.Default.SensorDocument),
            "metrics" => JsonSerializer.Serialize(
                JsonSerializer.Deserialize(json, ConfigJsonContext.Default.MetricOverridesDocument)!,
                ConfigJsonContext.Default.MetricOverridesDocument),
            "settings" => JsonSerializer.Serialize(
                JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AgentSettingsDocument)!,
                ConfigJsonContext.Default.AgentSettingsDocument),
            _ => throw new InvalidOperationException($"Unknown config fixture type in {fixture}."),
        };
    }

    private static TheoryData<string> ToData(IEnumerable<string> items)
    {
        var data = new TheoryData<string>();
        foreach (string item in items)
        {
            data.Add(item);
        }

        return data;
    }
}
