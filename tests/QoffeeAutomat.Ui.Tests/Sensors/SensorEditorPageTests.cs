using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Sensors;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Sensors;

/// <summary>
/// The sensor editor (T405) against a fake agent: a script sensor is created, tested and saved; only the source types and the
/// Home Assistant attributes that are valid are offered; the agent's problems are shown at their fields.
/// </summary>
[Trait("Category", "Sensors")]
public sealed class SensorEditorPageTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeAgentApi _api = new FakeAgentApi().SignedIn();
    private readonly FakeBrowserInterop _browser = new();

    public SensorEditorPageTests()
    {
        UiTestSetup.Register(this, _api, _browser);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact]
    public void A_new_script_sensor_can_be_created_tested_and_saved()
    {
        IRenderedComponent<SensorEditorPage> cut = RenderEditor();

        cut.Find("[data-testid=sensor-field-name] input").Input("Disk temp");
        Assert.Equal("disk_temp", cut.Find("[data-testid=sensor-field-id] input").GetAttribute("value"));
        cut.Find("[data-testid=sensor-field-code] textarea").Input("Get-Content C:\\temp.txt");
        cut.Find("[data-testid=sensor-field-unit]").Change("°C");
        cut.Find("[data-testid=sensor-field-device-class]").Change("temperature");
        cut.Find("[data-testid=sensor-field-state-class]").Change("measurement");

        cut.Find("[data-testid=sensor-test]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=sensor-test-success]")), Patience);
        Assert.Contains("42", cut.Find("[data-testid=sensor-test-value]").TextContent, StringComparison.Ordinal);
        Assert.Contains("°C", cut.Find("[data-testid=sensor-test-value]").TextContent, StringComparison.Ordinal);
        SensorDocument tested = Assert.Single(_api.TestedSensors);
        Assert.Equal("disk_temp", tested.Id);
        Assert.Equal("Get-Content C:\\temp.txt", Assert.IsType<PowerShellSensorSource>(tested.Source).Code);
        Assert.Empty(_api.CreatedSensors);

        cut.Find("[data-testid=sensor-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=sensor-saved]")), Patience);
        SensorDocument created = Assert.Single(_api.CreatedSensors);
        Assert.Equal("disk_temp", created.Id);
        Assert.Equal("Disk temp", created.Name);
        Assert.Equal("sensor", created.Kind);
        Assert.Equal("number", created.ValueType);
        Assert.Equal("°C", created.Unit);
        Assert.Equal("temperature", created.DeviceClass);
        Assert.Equal("measurement", created.StateClass);
        Assert.True(created.Enabled);
        Assert.True(created.ExposeToHa);
        Assert.True(cut.Find("[data-testid=sensor-field-id] input").HasAttribute("disabled"));
        Assert.EndsWith("/sensors/disk_temp", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
        Assert.Contains(_api.Sensors, sensor => sensor.Id == "disk_temp");
    }

    [Fact]
    public void A_test_shows_the_value_that_was_parsed_with_its_duration()
    {
        _api.SensorTestHandler = _ => new SensorTestResult(true, Samples.Text("on"), null, 87);
        IRenderedComponent<SensorEditorPage> cut = RenderEditor();
        cut.Find("[data-testid=sensor-field-value-type]").Change("string");

        cut.Find("[data-testid=sensor-test]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=sensor-test-success]")), Patience);
        Assert.Equal("on", cut.Find("[data-testid=sensor-test-value]").TextContent.Trim());
        Assert.Contains("87", cut.Find("[data-testid=sensor-test-success]").TextContent, StringComparison.Ordinal);
        Assert.Equal("succeeded", cut.Find("[data-testid=sensor-test-panel]").GetAttribute("data-state"));
    }

    [Fact]
    public void A_test_that_fails_shows_the_error_of_the_sensor()
    {
        _api.SensorTestHandler = _ => new SensorTestResult(false, null, "The output 'abc' is not a number.", 5);
        IRenderedComponent<SensorEditorPage> cut = RenderEditor();

        cut.Find("[data-testid=sensor-test]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=sensor-test-failure]")), Patience);
        Assert.Contains("is not a number", cut.Find("[data-testid=sensor-test-error]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=sensor-test-success]"));
        Assert.Equal("failed", cut.Find("[data-testid=sensor-test-panel]").GetAttribute("data-state"));
    }

    [Fact]
    public void A_draft_without_id_and_name_can_still_be_tested()
    {
        IRenderedComponent<SensorEditorPage> cut = RenderEditor();

        cut.Find("[data-testid=sensor-test]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=sensor-test-success]")), Patience);
        Assert.Equal("test_sensor", Assert.Single(_api.TestedSensors).Id);
    }

    [Fact]
    public void A_test_that_the_agent_refuses_marks_the_fields()
    {
        _api.SensorTestFailure = new ApiException(
            422,
            Samples.Problem(422, "validation_failed", "The sensor is not valid.", new ApiValidationError("/source/code", "required", "A value is required.")));
        IRenderedComponent<SensorEditorPage> cut = RenderEditor();

        cut.Find("[data-testid=sensor-test]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='draft-error:/source/code']")), Patience);
        Assert.NotNull(cut.Find("[data-testid=sensor-validation-summary]"));
        Assert.Empty(cut.FindAll("[data-testid=sensor-test-success]"));
    }

    [Fact]
    public void A_test_that_cannot_reach_the_source_shows_the_error_in_the_test_panel()
    {
        _api.SensorTestFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Something broke."));
        IRenderedComponent<SensorEditorPage> cut = RenderEditor();

        cut.Find("[data-testid=sensor-test]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=sensor-test-panel] [data-testid=error-message]")), Patience);
        Assert.Equal("error", cut.Find("[data-testid=sensor-test-panel]").GetAttribute("data-state"));
    }

    [Fact]
    public void Windows_offers_every_source_type()
    {
        IRenderedComponent<SensorEditorPage> cut = RenderEditor();

        Assert.Equal(
            ["powershell", "process_running", "state", "perf_counter", "cim_query", "service_state"],
            SourceTypes(cut));
    }

    [Theory]
    [InlineData("linux")]
    [InlineData("simulated")]
    public void Types_that_the_platform_cannot_sample_are_not_offered(string platform)
    {
        _api.Status = Samples.Status(platform: platform);

        IRenderedComponent<SensorEditorPage> cut = RenderEditor();

        Assert.Equal(["powershell", "process_running", "state"], SourceTypes(cut));
    }

    [Fact]
    public void Choosing_a_source_type_shows_the_fields_of_that_type()
    {
        IRenderedComponent<SensorEditorPage> cut = RenderEditor();
        Assert.NotNull(cut.Find("[data-testid=sensor-field-code]"));

        cut.Find("[data-testid=sensor-field-source-type]").Change("perf_counter");
        Assert.NotNull(cut.Find("[data-testid=sensor-field-category]"));
        Assert.NotNull(cut.Find("[data-testid=sensor-field-counter]"));
        Assert.Empty(cut.FindAll("[data-testid=sensor-field-code]"));

        cut.Find("[data-testid=sensor-field-source-type]").Change("process_running");
        Assert.NotNull(cut.Find("[data-testid=sensor-field-process]"));

        cut.Find("[data-testid=sensor-field-source-type]").Change("cim_query");
        Assert.NotNull(cut.Find("[data-testid=sensor-field-cim-class]"));

        cut.Find("[data-testid=sensor-field-source-type]").Change("service_state");
        Assert.NotNull(cut.Find("[data-testid=sensor-field-service]"));
    }

    [Fact]
    public void A_process_sensor_is_saved_as_a_binary_sensor()
    {
        IRenderedComponent<SensorEditorPage> cut = RenderEditor();
        cut.Find("[data-testid=sensor-field-name] input").Input("Browser running");
        cut.Find("[data-testid=sensor-field-source-type]").Change("process_running");
        cut.Find("[data-testid=sensor-field-process] input").Input("firefox");
        cut.Find("[data-testid=sensor-field-value-type]").Change("bool");
        cut.Find("[data-testid=sensor-field-device-class]").Change("running");

        cut.Find("[data-testid=sensor-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=sensor-saved]")), Patience);
        SensorDocument created = Assert.Single(_api.CreatedSensors);
        Assert.Equal("binary_sensor", created.Kind);
        Assert.Equal("bool", created.ValueType);
        Assert.Equal("running", created.DeviceClass);
        Assert.Equal("firefox", Assert.IsType<ProcessRunningSensorSource>(created.Source).Process);
    }

    [Fact]
    public void The_home_assistant_attributes_are_chosen_from_valid_values_that_fit_the_value_type()
    {
        IRenderedComponent<SensorEditorPage> cut = RenderEditor();

        // A number: units, numeric device classes and state classes.
        Assert.Contains("°C", Options(cut, "sensor-field-unit"));
        Assert.Contains("kWh", Options(cut, "sensor-field-unit"));
        Assert.Contains("temperature", Options(cut, "sensor-field-device-class"));
        Assert.DoesNotContain("connectivity", Options(cut, "sensor-field-device-class"));
        Assert.Equal(["", "measurement", "total", "total_increasing"], Options(cut, "sensor-field-state-class"));
        Assert.NotNull(cut.Find("[data-testid=sensor-field-precision]"));

        // Yes or no: binary device classes, and no unit or state class.
        cut.Find("[data-testid=sensor-field-value-type]").Change("bool");
        Assert.Contains("connectivity", Options(cut, "sensor-field-device-class"));
        Assert.DoesNotContain("temperature", Options(cut, "sensor-field-device-class"));
        Assert.Empty(cut.FindAll("[data-testid=sensor-field-unit]"));
        Assert.Empty(cut.FindAll("[data-testid=sensor-field-state-class]"));
        Assert.Empty(cut.FindAll("[data-testid=sensor-field-precision]"));

        // Text: nothing to choose but the category.
        cut.Find("[data-testid=sensor-field-value-type]").Change("string");
        Assert.Empty(cut.FindAll("[data-testid=sensor-field-unit]"));
        Assert.Empty(cut.FindAll("[data-testid=sensor-field-device-class]"));
        Assert.Empty(cut.FindAll("[data-testid=sensor-field-state-class]"));
        Assert.NotNull(cut.Find("[data-testid=sensor-field-entity-category]"));

        // One of several options: the list of options.
        cut.Find("[data-testid=sensor-field-value-type]").Change("enum");
        Assert.NotNull(cut.Find("[data-testid=sensor-field-options]"));
    }

    [Fact]
    public void Changing_the_value_type_clears_a_class_that_no_longer_fits()
    {
        IRenderedComponent<SensorEditorPage> cut = RenderEditor();
        cut.Find("[data-testid=sensor-field-name] input").Input("Thing");
        cut.Find("[data-testid=sensor-field-unit]").Change("°C");
        cut.Find("[data-testid=sensor-field-device-class]").Change("temperature");

        cut.Find("[data-testid=sensor-field-value-type]").Change("bool");
        cut.Find("[data-testid=sensor-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=sensor-saved]")), Patience);
        SensorDocument created = Assert.Single(_api.CreatedSensors);
        Assert.Null(created.DeviceClass);
        Assert.Null(created.Unit);
        Assert.Null(created.StateClass);
    }

    [Fact]
    public void An_enum_sensor_is_saved_with_its_options()
    {
        IRenderedComponent<SensorEditorPage> cut = RenderEditor();
        cut.Find("[data-testid=sensor-field-name] input").Input("Mode");
        cut.Find("[data-testid=sensor-field-value-type]").Change("enum");
        cut.Find("[data-testid=sensor-field-options] textarea").Input("day\nnight");

        cut.Find("[data-testid=sensor-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=sensor-saved]")), Patience);
        Assert.Equal(["day", "night"], Assert.Single(_api.CreatedSensors).Options);
    }

    [Fact]
    public void The_problems_that_the_agent_reports_on_save_are_shown_at_their_fields()
    {
        _api.SensorCreateFailure = new ApiException(
            422,
            Samples.Problem(
                422,
                "validation_failed",
                "The sensor is not valid.",
                new ApiValidationError("/name", "required", "A value is required."),
                new ApiValidationError("/source/code", "required", "A value is required.")));
        IRenderedComponent<SensorEditorPage> cut = RenderEditor();

        cut.Find("[data-testid=sensor-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='draft-error:/name']")), Patience);
        Assert.NotNull(cut.Find("[data-testid='draft-error:/source/code']"));
        Assert.Contains("2", cut.Find("[data-testid=sensor-validation-summary]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=sensor-saved]"));
    }

    [Fact]
    public void An_id_that_exists_is_reported_at_the_id_field()
    {
        _api.Sensors = [Samples.Sensor("disk_temp")];
        IRenderedComponent<SensorEditorPage> cut = RenderEditor();
        cut.Find("[data-testid=sensor-field-name] input").Input("Disk temp");

        cut.Find("[data-testid=sensor-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='draft-error:/id']")), Patience);
        Assert.Equal("already_exists", cut.Find("[data-testid='draft-error:/id']").GetAttribute("data-code"));
    }

    [Fact]
    public void A_number_that_is_not_a_number_is_marked_and_nothing_is_sent()
    {
        IRenderedComponent<SensorEditorPage> cut = RenderEditor();
        cut.Find("[data-testid=sensor-field-name] input").Input("Thing");
        cut.Find("[data-testid=sensor-field-interval] input").Input("soon");

        cut.Find("[data-testid=sensor-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='draft-error:/intervalSeconds']")), Patience);
        Assert.Empty(_api.CreatedSensors);
    }

    [Fact]
    public void A_stored_sensor_is_loaded_into_the_form_and_saved_against_its_revision()
    {
        _api.Sensors = [Samples.Sensor("disk_temp", "Disk temperature", unit: "°C", deviceClass: "temperature", stateClass: "measurement")];

        IRenderedComponent<SensorEditorPage> cut = RenderEditor("disk_temp");

        Assert.Equal("Disk temperature", cut.Find("[data-testid=sensor-field-name] input").GetAttribute("value"));
        Assert.True(cut.Find("[data-testid=sensor-field-id] input").HasAttribute("disabled"));
        Assert.Equal("°C", Selected(cut, "sensor-field-unit"));
        Assert.Equal("temperature", Selected(cut, "sensor-field-device-class"));

        cut.Find("[data-testid=sensor-field-name] input").Input("Disk temperature C");
        cut.Find("[data-testid=sensor-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=sensor-saved]")), Patience);
        SensorDocument replaced = Assert.Single(_api.ReplacedSensors);
        Assert.Equal("disk_temp", replaced.Id);
        Assert.Equal("Disk temperature C", replaced.Name);
        Assert.Equal(1, replaced.Revision);
        Assert.Empty(_api.CreatedSensors);
        Assert.Equal(2, _api.Sensors.Single().Revision);
    }

    [Fact]
    public void A_sensor_that_was_changed_meanwhile_is_not_overwritten()
    {
        _api.Sensors = [Samples.Sensor("disk_temp")];
        IRenderedComponent<SensorEditorPage> cut = RenderEditor("disk_temp");
        _api.Sensors[0] = _api.Sensors[0] with { Revision = 5 };

        cut.Find("[data-testid=sensor-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=error-message]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=sensor-saved]"));
        Assert.Equal(5, _api.Sensors.Single().Revision);
    }

    [Fact]
    public void An_unknown_sensor_says_so()
    {
        IRenderedComponent<SensorEditorPage> cut = RenderEditor("nope");

        Assert.NotNull(cut.Find("[data-testid=sensor-not-found]"));
        Assert.True(cut.Find("[data-testid=sensor-save]").HasAttribute("disabled"));
    }

    [Fact]
    public void A_new_state_sensor_has_an_initial_value_and_a_persist_switch_but_no_interval_and_no_value_to_set_yet()
    {
        IRenderedComponent<SensorEditorPage> cut = RenderEditor();

        cut.Find("[data-testid=sensor-field-source-type]").Change("state");

        Assert.NotNull(cut.Find("[data-testid=sensor-field-initial-value]"));
        Assert.NotNull(cut.Find("[data-testid=sensor-field-persist]"));
        Assert.Empty(cut.FindAll("[data-testid=sensor-field-interval]"));
        Assert.Empty(cut.FindAll("[data-testid=sensor-field-code]"));
        Assert.Empty(cut.FindAll("[data-testid=sensor-state-panel]"));
    }

    [Fact]
    public void A_state_sensor_is_saved_with_its_initial_value_and_without_an_interval()
    {
        IRenderedComponent<SensorEditorPage> cut = RenderEditor();
        cut.Find("[data-testid=sensor-field-name] input").Input("Mode");
        cut.Find("[data-testid=sensor-field-source-type]").Change("state");
        cut.Find("[data-testid=sensor-field-value-type]").Change("string");
        cut.Find("[data-testid=sensor-field-initial-value] input").Input("Normal");

        cut.Find("[data-testid=sensor-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=sensor-saved]")), Patience);
        SensorDocument created = Assert.Single(_api.CreatedSensors);
        StateSensorSource source = Assert.IsType<StateSensorSource>(created.Source);
        Assert.Equal("Normal", source.InitialValue!.Value.GetString());
        Assert.False(source.Persist);
        Assert.Null(created.IntervalSeconds);

        // Once the sensor exists its value can be set by hand.
        Assert.NotNull(cut.Find("[data-testid=sensor-state-panel]"));
    }

    [Fact]
    public void The_value_of_a_stored_state_sensor_is_shown_and_can_be_set_by_hand()
    {
        _api.Sensors = [Samples.Sensor("mode", "Mode", valueType: "string", source: new StateSensorSource(Samples.Text("Normal")))];
        _api.SensorValues["mode"] = "Normal";
        IRenderedComponent<SensorEditorPage> cut = RenderEditor("mode");

        Assert.Equal("Normal", cut.Find("[data-testid=sensor-state-current]").TextContent.Trim());
        Assert.True(cut.Find("[data-testid=sensor-state-set]").HasAttribute("disabled"));

        cut.Find("[data-testid=sensor-state-input] input").Input("Eco");
        cut.Find("[data-testid=sensor-state-set]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=sensor-state-notice]")), Patience);
        Assert.Equal(("mode", "Eco"), Assert.Single(_api.SetSensorValues));
        Assert.Equal("Eco", cut.Find("[data-testid=sensor-state-current]").TextContent.Trim());
        Assert.Empty(_api.ReplacedSensors);
    }

    [Fact]
    public void A_value_that_the_agent_refuses_is_shown_with_its_reason_and_the_old_value_stays()
    {
        _api.Sensors = [Samples.Sensor("level", "Level", source: new StateSensorSource(Samples.Number(1)))];
        _api.SensorValues["level"] = "1";
        _api.SensorValueSetFailure = new ApiException(
            422,
            Samples.Problem(422, "validation_failed", "The request has a validation error.", new ApiValidationError("/value", "invalid", "Sensor 'level': 'warm' is not a number.")));
        IRenderedComponent<SensorEditorPage> cut = RenderEditor("level");

        cut.Find("[data-testid=sensor-state-input] input").Input("warm");
        cut.Find("[data-testid=sensor-state-set]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=error-message]")), Patience);
        Assert.Contains("is not a number", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=sensor-state-notice]"));
        Assert.Equal("1", cut.Find("[data-testid=sensor-state-current]").TextContent.Trim());
    }

    [Fact]
    public void An_enum_state_sensor_offers_its_options_to_choose_from()
    {
        SensorDocument sensor = Samples.Sensor("mode", "Mode", valueType: "enum", source: new StateSensorSource()) with { Options = ["Normal", "Eco"] };
        _api.Sensors = [sensor];
        IRenderedComponent<SensorEditorPage> cut = RenderEditor("mode");

        Assert.Equal(["", "Normal", "Eco"], Options(cut, "sensor-state-input"));
        Assert.Equal("n/a", cut.Find("[data-testid=sensor-state-current]").TextContent.Trim());

        cut.Find("select[data-testid=sensor-state-input]").Change("Eco");
        cut.Find("[data-testid=sensor-state-set]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=sensor-state-notice]")), Patience);
        Assert.Equal(("mode", "Eco"), Assert.Single(_api.SetSensorValues));
    }

    [Fact]
    public void Saving_a_stored_state_sensor_keeps_its_persist_flag()
    {
        _api.Sensors = [Samples.Sensor("mode", "Mode", valueType: "string", source: new StateSensorSource(Samples.Text("Normal"), Persist: true))];
        IRenderedComponent<SensorEditorPage> cut = RenderEditor("mode");

        cut.Find("[data-testid=sensor-field-name] input").Input("Mode switch");
        cut.Find("[data-testid=sensor-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=sensor-saved]")), Patience);
        StateSensorSource source = Assert.IsType<StateSensorSource>(Assert.Single(_api.ReplacedSensors).Source);
        Assert.True(source.Persist);
        Assert.Equal("Normal", source.InitialValue!.Value.GetString());
    }

    [Fact]
    public void A_script_sensor_has_no_value_to_set()
    {
        _api.Sensors = [Samples.Sensor("disk_temp")];

        IRenderedComponent<SensorEditorPage> cut = RenderEditor("disk_temp");

        Assert.Empty(cut.FindAll("[data-testid=sensor-state-panel]"));
    }

    /// <summary>The value of a native select: Blazor writes it as an attribute of the select or marks the option.</summary>
    private static string? Selected(IRenderedComponent<SensorEditorPage> cut, string testId)
    {
        AngleSharp.Dom.IElement select = cut.Find($"select[data-testid={testId}]");
        return select.GetAttribute("value") ?? select.QuerySelector("option[selected]")?.GetAttribute("value");
    }

    private static string[] SourceTypes(IRenderedComponent<SensorEditorPage> cut)
    {
        return Options(cut, "sensor-field-source-type");
    }

    private static string[] Options(IRenderedComponent<SensorEditorPage> cut, string testId)
    {
        return [.. cut.FindAll($"select[data-testid={testId}] option").Select(option => option.GetAttribute("value") ?? string.Empty)];
    }

    private IRenderedComponent<SensorEditorPage> RenderEditor(string? id = null)
    {
        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        IRenderedComponent<SensorEditorPage> cut = id is null
            ? Render<SensorEditorPage>()
            : Render<SensorEditorPage>(parameters => parameters.Add(page => page.Id, id));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=sensor-section-general], [data-testid=sensor-not-found], [data-testid=error-message]")), Patience);
        return cut;
    }
}
