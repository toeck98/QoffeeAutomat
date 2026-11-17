using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Pages.Metrics;
using QoffeeAutomat.Ui.Components.Pages.Sensors;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Sensors;

/// <summary>The rules behind the sensor editor and the metric list that need no browser: which types a platform offers, which attributes fit a value type, and what a draft turns into.</summary>
[Trait("Category", "Sensors")]
public sealed class SensorLogicTests
{
    [Fact]
    public void Windows_offers_every_source_type_that_can_be_authored()
    {
        IReadOnlyList<string> types = SensorAttributeRules.OfferedSourceTypes("windows");

        Assert.Equal(
            [
                SensorSourceTypes.PowerShell,
                SensorSourceTypes.ProcessRunning,
                SensorSourceTypes.State,
                SensorSourceTypes.PerfCounter,
                SensorSourceTypes.CimQuery,
                SensorSourceTypes.ServiceState,
            ],
            types);
    }

    [Theory]
    [InlineData("linux")]
    [InlineData("simulated")]
    [InlineData(null)]
    [InlineData("")]
    public void Other_platforms_do_not_offer_the_windows_only_types(string? platform)
    {
        IReadOnlyList<string> types = SensorAttributeRules.OfferedSourceTypes(platform);

        Assert.Equal([SensorSourceTypes.PowerShell, SensorSourceTypes.ProcessRunning, SensorSourceTypes.State], types);
        Assert.False(SensorAttributeRules.IsOffered(platform, SensorSourceTypes.PerfCounter));
        Assert.False(SensorAttributeRules.IsOffered(platform, SensorSourceTypes.CimQuery));
        Assert.False(SensorAttributeRules.IsOffered(platform, SensorSourceTypes.ServiceState));
        Assert.True(SensorAttributeRules.IsOffered(platform, SensorSourceTypes.PowerShell));
    }

    [Fact]
    public void The_platform_is_compared_without_regard_to_case()
    {
        Assert.True(SensorAttributeRules.IsOffered("Windows", SensorSourceTypes.CimQuery));
    }

    [Fact]
    public void A_sensor_whose_value_is_set_from_outside_is_offered_on_every_platform()
    {
        Assert.True(SensorAttributeRules.IsOffered("windows", SensorSourceTypes.State));
        Assert.True(SensorAttributeRules.IsOffered("linux", SensorSourceTypes.State));
        Assert.True(SensorAttributeRules.IsOffered(null, SensorSourceTypes.State));
    }

    [Theory]
    [InlineData("number", true, true, true, false, "temperature")]
    [InlineData("string", false, false, false, false, null)]
    [InlineData("bool", false, false, false, false, "connectivity")]
    [InlineData("timestamp", false, false, false, false, null)]
    [InlineData("enum", false, false, false, true, null)]
    public void The_attributes_that_fit_a_value_type_are_the_ones_that_are_offered(string valueType, bool unit, bool stateClass, bool precision, bool options, string? deviceClass)
    {
        Assert.Equal(unit, SensorAttributeRules.AllowsUnit(valueType));
        Assert.Equal(stateClass, SensorAttributeRules.AllowsStateClass(valueType));
        Assert.Equal(precision, SensorAttributeRules.AllowsPrecision(valueType));
        Assert.Equal(options, SensorAttributeRules.UsesOptions(valueType));
        IReadOnlyList<string> classes = SensorAttributeRules.DeviceClassesFor(valueType);
        if (deviceClass is null)
        {
            Assert.Empty(classes);
        }
        else
        {
            Assert.Contains(deviceClass, classes);
        }
    }

    [Fact]
    public void Only_a_bool_value_makes_a_binary_sensor()
    {
        Assert.Equal("binary_sensor", SensorAttributeRules.KindFor("bool"));
        Assert.Equal("sensor", SensorAttributeRules.KindFor("number"));
        Assert.Equal("sensor", SensorAttributeRules.KindFor("enum"));
    }

    [Fact]
    public void A_new_draft_builds_a_powershell_sensor_with_the_defaults()
    {
        var draft = new SensorDraft { Id = "disk_temp", Name = "Disk temperature", Code = "42" };

        SensorBuild build = draft.Build();

        Assert.Empty(build.Problems);
        Assert.True(draft.IsNew);
        Assert.Equal("sensor", build.Document.Kind);
        Assert.Equal("number", build.Document.ValueType);
        PowerShellSensorSource source = Assert.IsType<PowerShellSensorSource>(build.Document.Source);
        Assert.Equal(PowerShellSources.Inline, source.Source);
        Assert.Equal("42", source.Code);
        Assert.Null(build.Document.Unit);
        Assert.Null(build.Document.IntervalSeconds);
        Assert.Null(build.Document.Revision);
    }

    [Fact]
    public void Attributes_that_do_not_fit_the_value_type_are_dropped()
    {
        var draft = new SensorDraft
        {
            Id = "uptime_text",
            Name = "Uptime text",
            ValueType = "string",
            Unit = "°C",
            DeviceClass = "temperature",
            StateClass = "measurement",
            PrecisionText = "2",
            DeadbandText = "1",
            OptionsText = "a\nb",
        };

        SensorDocument document = draft.Build().Document;

        Assert.Null(document.Unit);
        Assert.Null(document.DeviceClass);
        Assert.Null(document.StateClass);
        Assert.Null(document.Precision);
        Assert.Null(document.Deadband);
        Assert.Null(document.Options);
    }

    [Fact]
    public void A_number_sensor_keeps_its_unit_classes_precision_and_timing()
    {
        var draft = new SensorDraft
        {
            Id = "disk_temp",
            Name = "Disk temperature",
            Unit = "°C",
            DeviceClass = "temperature",
            StateClass = "measurement",
            PrecisionText = "1",
            IntervalText = "30",
            DeadbandText = "0.5",
            EntityCategory = "diagnostic",
        };

        SensorDocument document = draft.Build().Document;

        Assert.Equal("°C", document.Unit);
        Assert.Equal("temperature", document.DeviceClass);
        Assert.Equal("measurement", document.StateClass);
        Assert.Equal(1, document.Precision);
        Assert.Equal(30, document.IntervalSeconds);
        Assert.Equal(0.5, document.Deadband);
        Assert.Equal("diagnostic", document.EntityCategory);
    }

    [Fact]
    public void An_enum_sensor_gets_its_options_from_the_lines()
    {
        var draft = new SensorDraft { Id = "mode", Name = "Mode", ValueType = "enum", OptionsText = " day \n\nnight\r\n" };

        SensorDocument document = draft.Build().Document;

        Assert.Equal(["day", "night"], document.Options);
    }

    [Fact]
    public void A_bool_value_builds_a_binary_sensor()
    {
        var draft = new SensorDraft { Id = "browser", Name = "Browser", ValueType = "bool", SourceType = SensorSourceTypes.ProcessRunning, Process = " firefox " };

        SensorDocument document = draft.Build().Document;

        Assert.Equal("binary_sensor", document.Kind);
        Assert.Equal("firefox", Assert.IsType<ProcessRunningSensorSource>(document.Source).Process);
    }

    [Fact]
    public void Every_source_type_builds_its_own_source()
    {
        var draft = new SensorDraft
        {
            Id = "s",
            Name = "S",
            SourceType = SensorSourceTypes.PerfCounter,
            Category = "Processor",
            Counter = "% Processor Time",
            Instance = "_Total",
        };
        PerfCounterSensorSource counter = Assert.IsType<PerfCounterSensorSource>(draft.Build().Document.Source);
        Assert.Equal(("Processor", "% Processor Time", "_Total"), (counter.Category, counter.Counter, counter.Instance));

        draft.SourceType = SensorSourceTypes.CimQuery;
        draft.CimClass = "Win32_Battery";
        draft.CimProperty = "EstimatedChargeRemaining";
        CimQuerySensorSource cim = Assert.IsType<CimQuerySensorSource>(draft.Build().Document.Source);
        Assert.Equal(("Win32_Battery", "EstimatedChargeRemaining", null), (cim.ClassName, cim.Property, cim.Filter));

        draft.SourceType = SensorSourceTypes.ServiceState;
        draft.Service = "Spooler";
        Assert.Equal("Spooler", Assert.IsType<ServiceStateSensorSource>(draft.Build().Document.Source).Service);

        draft.SourceType = SensorSourceTypes.PowerShell;
        draft.ScriptSource = PowerShellSources.File;
        draft.Script = "temp.ps1";
        PowerShellSensorSource script = Assert.IsType<PowerShellSensorSource>(draft.Build().Document.Source);
        Assert.Equal(("file", "temp.ps1", null), (script.Source, script.Script, script.Code));
    }

    [Fact]
    public void Numbers_that_are_no_numbers_are_reported_at_their_paths_and_nothing_is_guessed()
    {
        var draft = new SensorDraft { Id = "s", Name = "S", PrecisionText = "1.5", IntervalText = "soon", DeadbandText = "-1" };

        SensorBuild build = draft.Build();

        Assert.Equal(["/deadband", "/intervalSeconds", "/precision"], build.Problems.Select(problem => problem.Path).Order(StringComparer.Ordinal));
        Assert.All(build.Problems, problem => Assert.Equal("not_a_number", problem.Code));
    }

    [Fact]
    public void A_draft_from_a_stored_sensor_keeps_the_revision_and_what_the_form_does_not_show()
    {
        SensorDocument stored = Samples.Sensor("disk_temp", unit: "°C", deviceClass: "temperature", stateClass: "measurement") with
        {
            Revision = 7,
            EnabledDefault = false,
            Precision = 2,
            IntervalSeconds = 15,
        };

        SensorDraft draft = SensorDraft.From(stored);
        SensorDocument rebuilt = draft.Build().Document;

        Assert.False(draft.IsNew);
        Assert.Equal(7, rebuilt.Revision);
        Assert.False(rebuilt.EnabledDefault);
        Assert.Equal(stored.Origin, rebuilt.Origin);
        Assert.Equal(2, rebuilt.Precision);
        Assert.Equal(15, rebuilt.IntervalSeconds);
        Assert.Equal(stored.Source, rebuilt.Source);
    }

    [Fact]
    public void A_source_that_the_editor_cannot_author_is_kept_as_stored()
    {
        var unknown = new UnknownSensorSource("future_type", Samples.Null());
        SensorDocument stored = Samples.Sensor("external", source: unknown);

        SensorDocument rebuilt = SensorDraft.From(stored).Build().Document;

        Assert.Equal(unknown, rebuilt.Source);
    }

    [Fact]
    public void A_state_sensor_keeps_its_persist_flag_and_initial_value_through_the_draft()
    {
        SensorDocument stored = Samples.Sensor("mode", valueType: "enum", source: new StateSensorSource(Samples.Text("Normal"), Persist: true)) with
        {
            Options = ["Normal", "Eco"],
        };

        SensorDraft draft = SensorDraft.From(stored);
        Assert.Equal("Normal", draft.InitialValueText);
        Assert.True(draft.Persist);

        StateSensorSource rebuilt = Assert.IsType<StateSensorSource>(draft.Build().Document.Source);
        Assert.True(rebuilt.Persist);
        Assert.Equal("Normal", rebuilt.InitialValue!.Value.GetString());
    }

    [Theory]
    [InlineData("number", "21.5", JsonValueKind.Number)]
    [InlineData("bool", "true", JsonValueKind.True)]
    [InlineData("bool", "No", JsonValueKind.False)]
    [InlineData("string", "idle", JsonValueKind.String)]
    [InlineData("timestamp", "2026-01-01T00:00:00Z", JsonValueKind.String)]
    public void The_initial_value_of_a_state_sensor_is_typed_by_the_value_type(string valueType, string text, JsonValueKind kind)
    {
        var draft = new SensorDraft { Id = "s", Name = "S", ValueType = valueType, SourceType = SensorSourceTypes.State, InitialValueText = text };

        SensorBuild build = draft.Build();

        Assert.Empty(build.Problems);
        StateSensorSource source = Assert.IsType<StateSensorSource>(build.Document.Source);
        Assert.Equal(kind, source.InitialValue!.Value.ValueKind);
    }

    [Theory]
    [InlineData("number", "warm", "/source/initialValue")]
    [InlineData("bool", "maybe", "/source/initialValue")]
    public void An_initial_value_that_does_not_fit_the_value_type_is_reported_at_its_path(string valueType, string text, string path)
    {
        var draft = new SensorDraft { Id = "s", Name = "S", ValueType = valueType, SourceType = SensorSourceTypes.State, InitialValueText = text };

        SensorBuild build = draft.Build();

        Assert.Equal(path, Assert.Single(build.Problems).Path);
    }

    [Fact]
    public void A_state_sensor_without_an_initial_value_has_none_and_is_never_sampled_on_a_timer()
    {
        var draft = new SensorDraft { Id = "s", Name = "S", SourceType = SensorSourceTypes.State, IntervalText = "30", Persist = true };

        SensorDocument document = draft.Build().Document;

        StateSensorSource source = Assert.IsType<StateSensorSource>(document.Source);
        Assert.Null(source.InitialValue);
        Assert.True(source.Persist);
        Assert.Null(document.IntervalSeconds);
    }

    [Fact]
    public void The_persist_flag_and_the_initial_value_do_not_leak_into_other_source_types()
    {
        var draft = new SensorDraft { Id = "s", Name = "S", SourceType = SensorSourceTypes.State, InitialValueText = "5", Persist = true };
        draft.SourceType = SensorSourceTypes.ProcessRunning;
        draft.Process = "notepad.exe";

        Assert.IsType<ProcessRunningSensorSource>(draft.Build().Document.Source);
    }

    [Fact]
    public void Metrics_are_grouped_by_provider_with_the_custom_sensors_last()
    {
        var metrics = new[]
        {
            Samples.Metric("memory.used", "Memory used"),
            Samples.CustomMetric("disk_temp", "Disk temperature"),
            Samples.Metric("cpu.usage", "CPU usage"),
            Samples.Metric("cpu.temperature", "CPU temperature"),
            Samples.Metric("standalone", "Standalone"),
        };

        IReadOnlyList<MetricGroup> groups = MetricCatalogView.Group(metrics);

        Assert.Equal(["cpu", "memory", "other", "custom"], groups.Select(group => group.Key));
        Assert.Equal(["cpu.temperature", "cpu.usage"], groups[0].Metrics.Select(metric => metric.Id));
    }

    [Fact]
    public void The_metrics_that_reveal_what_the_user_does_are_sensitive()
    {
        Assert.True(MetricCatalogView.IsSensitive(Samples.Metric("window.title", "Active window")));
        Assert.True(MetricCatalogView.IsSensitive(Samples.Metric("window.process", "Active process")));
        Assert.True(MetricCatalogView.IsSensitive(Samples.Metric("session.user", "User")));
        Assert.True(MetricCatalogView.IsSensitive(Samples.Metric("audio.app", "App") with { Context = "user", EnabledDefault = false }));
        Assert.False(MetricCatalogView.IsSensitive(Samples.Metric("cpu.usage", "CPU usage")));
        Assert.False(MetricCatalogView.IsSensitive(Samples.Metric("session.locked", "Locked") with { Context = "user", EnabledDefault = true }));
    }

    [Theory]
    [InlineData("5", true, 5)]
    [InlineData("0", true, 0)]
    [InlineData(" 2.5 ", true, 2.5)]
    [InlineData("-1", false, 0)]
    [InlineData("abc", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(null, false, 0)]
    [InlineData("NaN", false, 0)]
    [InlineData("Infinity", false, 0)]
    public void A_number_that_is_typed_is_read_with_the_invariant_culture(string? text, bool valid, double expected)
    {
        bool result = MetricCatalogView.TryParseNonNegative(text, out double value);

        Assert.Equal(valid, result);
        if (valid)
        {
            Assert.Equal(expected, value);
        }
    }
}
