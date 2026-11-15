using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Tests.Validation;

[Trait("Category", "Validation")]
public sealed class SensorDocumentTests
{
    [Theory]
    [InlineData("sensor.powershell.json")]
    [InlineData("sensor.process_running.json")]
    [InlineData("sensor.state.json")]
    public void The_sensor_fixtures_are_valid(string fileName)
    {
        string json = File.ReadAllText(TestData.ConfigFixture(fileName));
        SensorDocument? sensor = System.Text.Json.JsonSerializer.Deserialize(json, ConfigJsonContext.Default.SensorDocument);

        TestDocuments.AssertValid(DocumentValidator.ValidateSensor(Assert.IsType<SensorDocument>(sensor)));
    }

    [Fact]
    public void A_minimal_sensor_is_valid()
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateSensor(TestData.Sensor()));
    }

    [Fact]
    public void A_null_sensor_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => DocumentValidator.ValidateSensor(null!));
    }

    [Fact]
    public void The_id_and_name_follow_the_command_rules()
    {
        ValidationResult result = DocumentValidator.ValidateSensor(TestData.Sensor() with { Id = "Bad-Id", Name = string.Empty });

        TestDocuments.AssertError(result, "/id", ValidationCodes.Invalid);
        TestDocuments.AssertError(result, "/name", ValidationCodes.Required);
    }

    [Fact]
    public void The_header_is_checked()
    {
        ValidationResult result = DocumentValidator.ValidateSensor(TestData.Sensor() with { SchemaVersion = 99, Revision = -2, Origin = "x" });

        TestDocuments.AssertError(result, "/schemaVersion", ValidationCodes.Unsupported);
        TestDocuments.AssertError(result, "/revision", ValidationCodes.Invalid);
        TestDocuments.AssertError(result, "/origin", ValidationCodes.Invalid);
    }

    // ---- kind and value type ----

    [Theory]
    [InlineData("sensor", "number")]
    [InlineData("sensor", "string")]
    [InlineData("sensor", "bool")]
    [InlineData("sensor", "timestamp")]
    [InlineData("binary_sensor", "bool")]
    public void Valid_kind_and_value_type_combinations_are_accepted(string kind, string valueType)
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateSensor(TestData.Sensor() with { Kind = kind, ValueType = valueType }));
    }

    [Fact]
    public void An_unknown_kind_is_rejected()
    {
        TestDocuments.AssertError(DocumentValidator.ValidateSensor(TestData.Sensor() with { Kind = "switch" }), "/kind", ValidationCodes.Invalid);
    }

    [Fact]
    public void An_unknown_value_type_is_rejected()
    {
        TestDocuments.AssertError(DocumentValidator.ValidateSensor(TestData.Sensor() with { ValueType = "float" }), "/valueType", ValidationCodes.Invalid);
    }

    [Theory]
    [InlineData("number")]
    [InlineData("string")]
    [InlineData("timestamp")]
    public void A_binary_sensor_must_be_bool(string valueType)
    {
        ValidationResult result = DocumentValidator.ValidateSensor(TestData.Sensor() with { Kind = "binary_sensor", ValueType = valueType });

        TestDocuments.AssertError(result, "/valueType", ValidationCodes.Invalid);
    }

    // ---- options ----

    [Fact]
    public void An_enum_sensor_needs_options()
    {
        ValidationResult result = DocumentValidator.ValidateSensor(TestData.Sensor() with { ValueType = "enum" });

        TestDocuments.AssertError(result, "/options", ValidationCodes.Required);
    }

    [Fact]
    public void An_enum_sensor_with_options_is_valid()
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateSensor(TestData.Sensor() with { ValueType = "enum", Options = ["idle", "busy"] }));
    }

    [Fact]
    public void Enum_sensor_options_must_be_non_empty_and_unique()
    {
        ValidationResult empty = DocumentValidator.ValidateSensor(TestData.Sensor() with { ValueType = "enum", Options = [] });
        ValidationResult duplicate = DocumentValidator.ValidateSensor(TestData.Sensor() with { ValueType = "enum", Options = ["a", "a", string.Empty] });

        TestDocuments.AssertError(empty, "/options", ValidationCodes.Required);
        TestDocuments.AssertError(duplicate, "/options/1", ValidationCodes.Duplicate);
        TestDocuments.AssertError(duplicate, "/options/2", ValidationCodes.Invalid);
    }

    [Fact]
    public void Options_on_a_non_enum_sensor_are_not_applicable()
    {
        ValidationResult result = DocumentValidator.ValidateSensor(TestData.Sensor() with { Options = ["a"] });

        TestDocuments.AssertError(result, "/options", ValidationCodes.NotApplicable);
    }

    // ---- numbers ----

    [Fact]
    public void Precision_interval_and_deadband_must_not_be_negative()
    {
        ValidationResult result = DocumentValidator.ValidateSensor(
            TestData.Sensor() with { Precision = -1, IntervalSeconds = -1, Deadband = -0.5 });

        TestDocuments.AssertError(result, "/precision", ValidationCodes.Invalid);
        TestDocuments.AssertError(result, "/intervalSeconds", ValidationCodes.Invalid);
        TestDocuments.AssertError(result, "/deadband", ValidationCodes.Invalid);
    }

    [Fact]
    public void Zero_and_positive_numbers_are_accepted()
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateSensor(
            TestData.Sensor() with { Precision = 0, IntervalSeconds = 0, Deadband = 0.25, DeviceClass = "temperature", Unit = "°C" }));
    }

    [Fact]
    public void A_non_finite_interval_is_rejected()
    {
        ValidationResult result = DocumentValidator.ValidateSensor(TestData.Sensor() with { IntervalSeconds = double.NaN });

        TestDocuments.AssertError(result, "/intervalSeconds", ValidationCodes.Invalid);
    }

    // ---- sources ----

    [Fact]
    public void A_missing_source_is_required()
    {
        TestDocuments.AssertError(DocumentValidator.ValidateSensor(TestData.Sensor() with { Source = null! }), "/source", ValidationCodes.Required);
    }

    [Fact]
    public void An_unknown_source_type_is_unsupported()
    {
        ValidationResult result = DocumentValidator.ValidateSensor(TestData.Sensor() with { Source = new UnknownSensorSource("wmi", TestDocuments.Json("{}")) });

        TestDocuments.AssertError(result, "/source/type", ValidationCodes.Unsupported);
    }

    [Fact]
    public void A_powershell_source_follows_the_action_rules()
    {
        ValidationResult noCode = DocumentValidator.ValidateSensor(TestData.Sensor() with { Source = new PowerShellSensorSource(PowerShellSources.Inline) });
        ValidationResult traversal = DocumentValidator.ValidateSensor(TestData.Sensor() with { Source = new PowerShellSensorSource(PowerShellSources.File, Script: "../x.ps1") });
        ValidationResult engine = DocumentValidator.ValidateSensor(TestData.Sensor() with { Source = new PowerShellSensorSource(PowerShellSources.Inline, Code: "1", Engine: "cmd") });

        TestDocuments.AssertError(noCode, "/source/code", ValidationCodes.Required);
        TestDocuments.AssertError(traversal, "/source/script", ValidationCodes.Invalid);
        TestDocuments.AssertError(engine, "/source/engine", ValidationCodes.Invalid);
    }

    [Fact]
    public void A_process_source_needs_a_process_name()
    {
        ValidationResult result = DocumentValidator.ValidateSensor(TestData.Sensor() with { Source = new ProcessRunningSensorSource(" ") });

        TestDocuments.AssertError(result, "/source/process", ValidationCodes.Required);
    }

    [Fact]
    public void A_perf_counter_source_needs_category_and_counter()
    {
        ValidationResult valid = DocumentValidator.ValidateSensor(
            TestData.Sensor() with { Source = new PerfCounterSensorSource("Processor", "% Processor Time", "_Total") });
        ValidationResult invalid = DocumentValidator.ValidateSensor(
            TestData.Sensor() with { Source = new PerfCounterSensorSource(string.Empty, string.Empty) });

        TestDocuments.AssertValid(valid);
        TestDocuments.AssertError(invalid, "/source/category", ValidationCodes.Required);
        TestDocuments.AssertError(invalid, "/source/counter", ValidationCodes.Required);
    }

    [Fact]
    public void A_cim_source_needs_namespace_class_and_property()
    {
        ValidationResult valid = DocumentValidator.ValidateSensor(
            TestData.Sensor() with { Source = new CimQuerySensorSource("root/cimv2", "Win32_Battery", "EstimatedChargeRemaining") });
        ValidationResult invalid = DocumentValidator.ValidateSensor(
            TestData.Sensor() with { Source = new CimQuerySensorSource(string.Empty, string.Empty, string.Empty) });

        TestDocuments.AssertValid(valid);
        TestDocuments.AssertError(invalid, "/source/namespace", ValidationCodes.Required);
        TestDocuments.AssertError(invalid, "/source/class", ValidationCodes.Required);
        TestDocuments.AssertError(invalid, "/source/property", ValidationCodes.Required);
    }

    [Fact]
    public void A_service_source_needs_a_service_name()
    {
        ValidationResult result = DocumentValidator.ValidateSensor(TestData.Sensor() with { Source = new ServiceStateSensorSource(string.Empty) });

        TestDocuments.AssertError(result, "/source/service", ValidationCodes.Required);
    }

    [Theory]
    [InlineData("number", "5", true)]
    [InlineData("number", "\"5\"", false)]
    [InlineData("bool", "true", true)]
    [InlineData("bool", "1", false)]
    [InlineData("string", "\"x\"", true)]
    [InlineData("string", "5", false)]
    [InlineData("timestamp", "\"2026-01-01T00:00:00Z\"", true)]
    public void The_initial_value_of_a_state_source_must_fit_the_value_type(string valueType, string json, bool valid)
    {
        SensorDocument sensor = TestData.Sensor() with
        {
            ValueType = valueType,
            Source = new StateSensorSource(TestDocuments.Json(json)),
        };

        ValidationResult result = DocumentValidator.ValidateSensor(sensor);

        Assert.Equal(valid, result.IsValid);
        if (!valid)
        {
            TestDocuments.AssertError(result, "/source/initialValue", ValidationCodes.TypeMismatch);
        }
    }

    [Fact]
    public void The_initial_value_of_an_enum_state_source_must_be_an_option()
    {
        SensorDocument sensor = TestData.Sensor() with { ValueType = "enum", Options = ["a", "b"] };

        TestDocuments.AssertValid(DocumentValidator.ValidateSensor(sensor with { Source = new StateSensorSource(TestDocuments.Text("a")) }));
        TestDocuments.AssertError(
            DocumentValidator.ValidateSensor(sensor with { Source = new StateSensorSource(TestDocuments.Text("c")) }),
            "/source/initialValue",
            ValidationCodes.TypeMismatch);
    }

    [Fact]
    public void A_state_source_without_initial_value_is_valid()
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateSensor(TestData.Sensor() with { Source = new StateSensorSource(null, true) }));
    }
}

[Trait("Category", "Validation")]
public sealed class MetricOverridesTests
{
    private static MetricOverridesDocument Document(params (string Key, MetricOverride? Value)[] overrides) => new(
        DocumentSchema.CurrentVersion,
        overrides.ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal));

    [Fact]
    public void The_metrics_fixture_is_valid()
    {
        string json = File.ReadAllText(TestData.ConfigFixture("metrics.json"));
        MetricOverridesDocument? document = System.Text.Json.JsonSerializer.Deserialize(json, ConfigJsonContext.Default.MetricOverridesDocument);

        TestDocuments.AssertValid(DocumentValidator.ValidateMetricOverrides(Assert.IsType<MetricOverridesDocument>(document)));
    }

    [Fact]
    public void An_empty_document_is_valid()
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateMetricOverrides(Document()));
    }

    [Fact]
    public void A_null_document_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => DocumentValidator.ValidateMetricOverrides(null!));
    }

    [Fact]
    public void Overrides_for_known_looking_metric_ids_are_valid()
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateMetricOverrides(Document(
            ("cpu.load", new MetricOverride(Enabled: true, ExposeToHa: false, IntervalSeconds: 5, Deadband: 1)),
            ("disk.c-drive.free", new MetricOverride(Enabled: false)),
            ("power", new MetricOverride()))));
    }

    [Fact]
    public void Missing_overrides_are_required()
    {
        ValidationResult result = DocumentValidator.ValidateMetricOverrides(new MetricOverridesDocument(DocumentSchema.CurrentVersion, null!));

        TestDocuments.AssertError(result, "/overrides", ValidationCodes.Required);
    }

    [Theory]
    [InlineData("CPU.Load")]
    [InlineData("cpu load")]
    [InlineData("1cpu")]
    [InlineData("cpu..load")]
    [InlineData("")]
    public void An_invalid_metric_id_key_is_rejected(string key)
    {
        ValidationResult result = DocumentValidator.ValidateMetricOverrides(Document((key, new MetricOverride())));

        TestDocuments.AssertError(result, "/overrides/" + key, ValidationCodes.Invalid);
    }

    [Fact]
    public void Keys_are_escaped_in_the_json_pointer()
    {
        ValidationResult result = DocumentValidator.ValidateMetricOverrides(Document(("a/b", new MetricOverride())));

        TestDocuments.AssertError(result, "/overrides/a~1b", ValidationCodes.Invalid);
    }

    [Fact]
    public void Interval_and_deadband_must_not_be_negative()
    {
        ValidationResult result = DocumentValidator.ValidateMetricOverrides(
            Document(("cpu.load", new MetricOverride(IntervalSeconds: -1, Deadband: -1))));

        TestDocuments.AssertError(result, "/overrides/cpu.load/intervalSeconds", ValidationCodes.Invalid);
        TestDocuments.AssertError(result, "/overrides/cpu.load/deadband", ValidationCodes.Invalid);
    }

    [Fact]
    public void A_null_override_is_rejected()
    {
        ValidationResult result = DocumentValidator.ValidateMetricOverrides(Document(("cpu.load", null)));

        TestDocuments.AssertError(result, "/overrides/cpu.load", ValidationCodes.Required);
    }

    [Fact]
    public void The_header_is_checked()
    {
        ValidationResult result = DocumentValidator.ValidateMetricOverrides(Document() with { SchemaVersion = 0 });

        TestDocuments.AssertError(result, "/schemaVersion", ValidationCodes.Invalid);
    }
}

[Trait("Category", "Validation")]
public sealed class AgentSettingsTests
{
    private static AgentSettingsDocument Settings() => new(DocumentSchema.CurrentVersion, "Office PC");

    [Fact]
    public void The_settings_fixture_is_valid()
    {
        string json = File.ReadAllText(TestData.ConfigFixture("settings.json"));
        AgentSettingsDocument? settings = System.Text.Json.JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AgentSettingsDocument);

        TestDocuments.AssertValid(DocumentValidator.ValidateSettings(Assert.IsType<AgentSettingsDocument>(settings)));
    }

    [Fact]
    public void Minimal_settings_use_the_default_ports()
    {
        AgentSettingsDocument settings = Settings();

        TestDocuments.AssertValid(DocumentValidator.ValidateSettings(settings));
        Assert.Equal(AgentSettingsDefaults.UiPort, settings.EffectiveUiPort);
        Assert.Equal(AgentSettingsDefaults.LinkPort, settings.EffectiveLinkPort);
    }

    [Fact]
    public void A_null_document_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => DocumentValidator.ValidateSettings(null!));
    }

    [Fact]
    public void The_name_is_required_and_limited()
    {
        TestDocuments.AssertError(DocumentValidator.ValidateSettings(Settings() with { Name = " " }), "/name", ValidationCodes.Required);
        TestDocuments.AssertError(DocumentValidator.ValidateSettings(Settings() with { Name = new string('x', 101) }), "/name", ValidationCodes.Invalid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Ports_must_be_between_1_and_65535(int port)
    {
        ValidationResult ui = DocumentValidator.ValidateSettings(Settings() with { UiPort = port });
        ValidationResult link = DocumentValidator.ValidateSettings(Settings() with { LinkPort = port });

        TestDocuments.AssertError(ui, "/uiPort", ValidationCodes.Invalid);
        TestDocuments.AssertError(link, "/linkPort", ValidationCodes.Invalid);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public void The_port_limits_are_accepted(int port)
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateSettings(Settings() with { UiPort = port, LinkPort = port == 1 ? 2 : 65534 }));
    }

    [Fact]
    public void The_two_ports_must_differ()
    {
        ValidationResult explicitPorts = DocumentValidator.ValidateSettings(Settings() with { UiPort = 9000, LinkPort = 9000 });
        ValidationResult defaultMeetsExplicit = DocumentValidator.ValidateSettings(Settings() with { LinkPort = AgentSettingsDefaults.UiPort });

        TestDocuments.AssertError(explicitPorts, "/linkPort", ValidationCodes.Conflict);
        TestDocuments.AssertError(defaultMeetsExplicit, "/linkPort", ValidationCodes.Conflict);
    }

    [Theory]
    [InlineData("trace", true)]
    [InlineData("debug", true)]
    [InlineData("information", true)]
    [InlineData("warning", true)]
    [InlineData("error", true)]
    [InlineData("critical", true)]
    [InlineData("verbose", false)]
    [InlineData("Information", false)]
    public void The_log_level_must_be_known(string level, bool valid)
    {
        ValidationResult result = DocumentValidator.ValidateSettings(Settings() with { Logging = new LoggingSettings(level) });

        Assert.Equal(valid, result.IsValid);
        if (!valid)
        {
            TestDocuments.AssertError(result, "/logging/level", ValidationCodes.Invalid);
        }
    }

    [Fact]
    public void Retention_must_be_at_least_one_day()
    {
        ValidationResult bad = DocumentValidator.ValidateSettings(Settings() with { Retention = new RetentionSettings(0, -3) });
        ValidationResult good = DocumentValidator.ValidateSettings(Settings() with { Retention = new RetentionSettings(1, 365) });

        TestDocuments.AssertError(bad, "/retention/runHistoryDays", ValidationCodes.Invalid);
        TestDocuments.AssertError(bad, "/retention/auditLogDays", ValidationCodes.Invalid);
        TestDocuments.AssertValid(good);
    }

    [Fact]
    public void The_header_is_checked()
    {
        ValidationResult result = DocumentValidator.ValidateSettings(Settings() with { SchemaVersion = 7, Origin = "x" });

        TestDocuments.AssertError(result, "/schemaVersion", ValidationCodes.Unsupported);
        TestDocuments.AssertError(result, "/origin", ValidationCodes.Invalid);
    }
}
