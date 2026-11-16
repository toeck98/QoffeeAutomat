using AngleSharp.Dom;
using Bunit;
using MudBlazor;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Sensors;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Sensors;

/// <summary>The custom sensor list (T405) against a fake agent: the sensors with their live values, the platform, and deleting after a confirmation.</summary>
[Trait("Category", "Sensors")]
public sealed class SensorsPageTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeAgentApi _api = new FakeAgentApi().SignedIn();
    private readonly FakeBrowserInterop _browser = new();

    public SensorsPageTests()
    {
        UiTestSetup.Register(this, _api, _browser);
        _api.Sensors =
        [
            Samples.Sensor("browser", "Browser running", new ProcessRunningSensorSource("firefox"), valueType: "bool", kind: "binary_sensor"),
            Samples.Sensor("disk_temp", "Disk temperature", unit: "°C", deviceClass: "temperature"),
        ];
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact]
    public void The_list_shows_every_sensor_with_its_source_type()
    {
        IRenderedComponent<SensorsPage> cut = RenderPage(out _);

        Assert.Equal(2, cut.FindAll("[data-testid=sensor-table] tbody tr").Count);
        Assert.Equal("process_running", cut.Find("[data-testid=type-browser]").GetAttribute("data-value"));
        Assert.Equal("powershell", cut.Find("[data-testid=type-disk_temp]").GetAttribute("data-value"));
        Assert.Contains("Disk temperature", cut.Find("[data-testid=sensor-disk_temp]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=sensor-new]"));
    }

    [Fact]
    public void An_agent_without_sensors_says_so()
    {
        _api.Sensors = [];

        IRenderedComponent<SensorsPage> cut = RenderPage(out _);

        Assert.NotNull(cut.Find("[data-testid=sensors-none]"));
        Assert.Empty(cut.FindAll("[data-testid=sensor-table]"));
        Assert.Equal(0, _api.StreamAttempts);
    }

    [Fact]
    public void A_list_that_cannot_be_read_shows_the_error_instead_of_a_table()
    {
        _api.SensorsFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Something broke."));

        IRenderedComponent<SensorsPage> cut = RenderPage(out _);

        Assert.NotNull(cut.Find("[data-testid=error-message]"));
        Assert.Empty(cut.FindAll("[data-testid=sensor-table]"));
        Assert.Empty(cut.FindAll("[data-testid=sensors-none]"));
    }

    [Fact]
    public void The_live_value_of_a_sensor_appears_when_the_agent_reports_it()
    {
        IRenderedComponent<SensorsPage> cut = RenderPage(out _);
        cut.WaitForAssertion(() => Assert.Equal(1, _api.StreamsActive), Patience);
        Assert.Equal("false", cut.Find("[data-testid=value-disk_temp]").GetAttribute("data-has-value"));
        Assert.Equal(["custom.browser", "custom.disk_temp"], _api.LastStreamIds);

        _api.Push(Samples.Update(Samples.Now, ("custom.disk_temp", 21.5)));

        cut.WaitForAssertion(() => Assert.Contains("21.5 °C", cut.Find("[data-testid=value-disk_temp]").TextContent, StringComparison.Ordinal), Patience);
        Assert.Equal("true", cut.Find("[data-testid=value-disk_temp]").GetAttribute("data-has-value"));
    }

    [Fact]
    public void A_disabled_sensor_is_not_streamed()
    {
        _api.Sensors = [Samples.Sensor("disk_temp", enabled: false)];

        IRenderedComponent<SensorsPage> cut = RenderPage(out _);

        Assert.Equal(0, _api.StreamAttempts);
        Assert.Contains("disabled", cut.Find("[data-testid=value-disk_temp]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leaving_the_page_closes_the_stream()
    {
        IRenderedComponent<SensorsPage> cut = RenderPage(out _);
        cut.WaitForAssertion(() => Assert.Equal(1, _api.StreamsActive), Patience);

        await DisposeComponentsAsync();

        for (int attempt = 0; attempt < 100 && _api.StreamsActive != 0; attempt++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(0, _api.StreamsActive);
    }

    [Fact]
    public void A_windows_sensor_can_be_edited_on_windows()
    {
        _api.Sensors = [Samples.Sensor("cpu_load", source: new PerfCounterSensorSource("Processor", "% Processor Time", "_Total"))];

        IRenderedComponent<SensorsPage> cut = RenderPage(out _);

        Assert.False(IsDisabled(cut.Find("[data-testid=edit-cpu_load]")));
        Assert.Empty(cut.FindAll("[data-testid=unsupported-cpu_load]"));
    }

    [Fact]
    public void A_windows_sensor_is_marked_and_cannot_be_edited_on_another_platform()
    {
        _api.Status = Samples.Status(platform: "linux");
        _api.Sensors = [Samples.Sensor("cpu_load", source: new PerfCounterSensorSource("Processor", "% Processor Time", "_Total"))];

        IRenderedComponent<SensorsPage> cut = RenderPage(out _);

        Assert.NotNull(cut.Find("[data-testid=unsupported-cpu_load]"));
        Assert.True(IsDisabled(cut.Find("[data-testid=edit-cpu_load]")));
        Assert.False(IsDisabled(cut.Find("[data-testid=delete-cpu_load]")));
    }

    [Fact]
    public void Deleting_asks_first_and_nothing_is_deleted_when_the_user_cancels()
    {
        IRenderedComponent<SensorsPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);

        cut.Find("[data-testid=delete-disk_temp]").Click();
        dialogs.WaitForElement(".mud-message-box__cancel-button", Patience).Click();

        cut.WaitForAssertion(() => Assert.Empty(dialogs.FindAll(".mud-message-box__cancel-button")), Patience);
        Assert.Empty(_api.DeletedSensors);
        Assert.NotNull(cut.Find("[data-testid=sensor-disk_temp]"));
    }

    [Fact]
    public void Deleting_after_the_confirmation_removes_the_sensor_and_says_so()
    {
        IRenderedComponent<SensorsPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);

        cut.Find("[data-testid=delete-disk_temp]").Click();
        dialogs.WaitForElement(".mud-message-box__yes-button", Patience).Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=sensor-disk_temp]")), Patience);
        Assert.Equal(["disk_temp"], _api.DeletedSensors);
        Assert.Contains("Disk temperature", cut.Find("[data-testid=notice]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_delete_keeps_the_sensor_and_shows_the_error()
    {
        _api.SensorDeleteFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Something broke."));
        IRenderedComponent<SensorsPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);

        cut.Find("[data-testid=delete-disk_temp]").Click();
        dialogs.WaitForElement(".mud-message-box__yes-button", Patience).Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=action-failed]")), Patience);
        Assert.NotNull(cut.Find("[data-testid=sensor-disk_temp]"));
        Assert.Empty(cut.FindAll("[data-testid=notice]"));
    }

    private static bool IsDisabled(IElement element)
    {
        return element.HasAttribute("disabled")
            || element.ClassList.Contains("mud-disabled")
            || string.Equals(element.GetAttribute("aria-disabled"), "true", StringComparison.Ordinal);
    }

    private IRenderedComponent<SensorsPage> RenderPage(out IRenderedComponent<MudDialogProvider> dialogs)
    {
        Render<MudPopoverProvider>();
        dialogs = Render<MudDialogProvider>();
        IRenderedComponent<SensorsPage> cut = Render<SensorsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=sensor-table], [data-testid=sensors-none], [data-testid=error-message]")), Patience);
        return cut;
    }
}
