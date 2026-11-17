using Bunit;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Metrics;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Sensors;

/// <summary>
/// The metric catalog (T405) against a fake agent: the groups, the current values, the privacy hint, and that the switches,
/// the interval and the deadband save at once and spring back when the agent refuses.
/// </summary>
[Trait("Category", "Sensors")]
public sealed class MetricsPageTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeAgentApi _api = new FakeAgentApi().SignedIn();
    private readonly FakeBrowserInterop _browser = new();

    public MetricsPageTests()
    {
        UiTestSetup.Register(this, _api, _browser);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact]
    public void The_catalog_is_grouped_by_provider()
    {
        _api.Catalog = new MetricList([.. _api.Catalog.Metrics, Samples.CustomMetric("disk_temp", "Disk temperature")]);

        IRenderedComponent<MetricsPage> cut = RenderPage();

        Assert.Equal(
            ["group-cpu", "group-custom", "group-gpu", "group-memory", "group-system"],
            cut.FindAll("[data-testid^=group-]").Select(element => element.GetAttribute("data-testid")!).Order(StringComparer.Ordinal));
        Assert.NotNull(cut.Find("[data-testid=group-cpu] [data-testid='metric-cpu.usage']"));
        Assert.NotNull(cut.Find("[data-testid=group-custom] [data-testid='metric-custom.disk_temp']"));
        Assert.Contains("Processor", cut.Find("[data-testid=group-cpu]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_value_of_the_catalog_is_shown_and_the_live_stream_replaces_it()
    {
        IRenderedComponent<MetricsPage> cut = RenderPage();
        Assert.Contains("12.5", cut.Find("[data-testid='value-cpu.usage']").TextContent, StringComparison.Ordinal);
        Assert.Equal("false", cut.Find("[data-testid='value-system.uptime']").GetAttribute("data-has-value"));

        cut.WaitForAssertion(() => Assert.Equal(1, _api.StreamsActive), Patience);
        _api.Push(Samples.Update(Samples.Now, ("cpu.usage", 50)));

        cut.WaitForAssertion(() => Assert.Contains("50.0", cut.Find("[data-testid='value-cpu.usage']").TextContent, StringComparison.Ordinal), Patience);
    }

    [Fact]
    public void Only_the_metrics_that_are_switched_on_are_streamed()
    {
        IRenderedComponent<MetricsPage> cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Equal(["cpu.usage", "memory.used_percent", "system.uptime"], _api.LastStreamIds), Patience);
    }

    [Fact]
    public async Task Leaving_the_page_closes_the_stream()
    {
        IRenderedComponent<MetricsPage> cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Equal(1, _api.StreamsActive), Patience);

        await DisposeComponentsAsync();

        for (int attempt = 0; attempt < 100 && _api.StreamsActive != 0; attempt++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(0, _api.StreamsActive);
        Assert.Equal(1, _api.StreamsClosed);
    }

    [Fact]
    public void A_metric_that_shows_what_the_user_does_has_a_privacy_hint()
    {
        _api.Catalog = new MetricList([.. _api.Catalog.Metrics, Samples.Metric("window.title", "Active window", enabled: false)]);

        IRenderedComponent<MetricsPage> cut = RenderPage();

        Assert.NotNull(cut.Find("[data-testid='privacy-window.title']"));
        Assert.Empty(cut.FindAll("[data-testid='privacy-cpu.usage']"));
    }

    [Fact]
    public void Switching_a_metric_on_saves_at_once_with_the_whole_override_and_streams_it()
    {
        IRenderedComponent<MetricsPage> cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Equal(1, _api.StreamsActive), Patience);

        cut.Find("[data-testid='enabled-gpu.usage'] input").Change(true);

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid='metric-gpu.usage']").GetAttribute("data-enabled")), Patience);
        (string id, MetricOverride change) = Assert.Single(_api.Overrides);
        Assert.Equal("gpu.usage", id);
        Assert.True(change.Enabled);
        Assert.True(change.ExposeToHa);
        Assert.Equal(2, change.IntervalSeconds);
        Assert.Equal("true", cut.Find("[data-testid='enabled-gpu.usage']").GetAttribute("data-checked"));
        Assert.Empty(cut.FindAll("[data-testid=action-failed]"));
        cut.WaitForAssertion(() => Assert.Contains("gpu.usage", _api.LastStreamIds), Patience);
    }

    [Fact]
    public void Switching_the_exposure_changes_only_that_member_and_keeps_the_others()
    {
        IRenderedComponent<MetricsPage> cut = RenderPage();

        cut.Find("[data-testid='expose-cpu.usage'] input").Change(false);

        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find("[data-testid='expose-cpu.usage']").GetAttribute("data-checked")), Patience);
        (_, MetricOverride change) = Assert.Single(_api.Overrides);
        Assert.False(change.ExposeToHa);
        Assert.True(change.Enabled);
        Assert.Equal(2, change.IntervalSeconds);
    }

    [Fact]
    public void A_switch_shows_the_new_state_while_the_agent_answers_and_springs_back_when_it_refuses()
    {
        _api.OverrideFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Something broke."));
        _api.OverrideGate = new TaskCompletionSource();
        IRenderedComponent<MetricsPage> cut = RenderPage();

        cut.Find("[data-testid='enabled-cpu.usage'] input").Change(false);

        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find("[data-testid='enabled-cpu.usage']").GetAttribute("data-checked")), Patience);
        _api.OverrideGate.SetResult();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=action-failed]")), Patience);
        Assert.Equal("true", cut.Find("[data-testid='enabled-cpu.usage']").GetAttribute("data-checked"));
        Assert.Equal("true", cut.Find("[data-testid='metric-cpu.usage']").GetAttribute("data-enabled"));
    }

    [Fact]
    public void A_new_interval_is_saved_when_the_field_is_left()
    {
        IRenderedComponent<MetricsPage> cut = RenderPage();

        cut.Find("[data-testid='interval-cpu.usage']").Change("5");

        cut.WaitForAssertion(() => Assert.Single(_api.Overrides), Patience);
        (string id, MetricOverride change) = _api.Overrides[0];
        Assert.Equal("cpu.usage", id);
        Assert.Equal(5, change.IntervalSeconds);
        Assert.True(change.Enabled);
        Assert.True(change.ExposeToHa);
        Assert.Null(change.Deadband);
        cut.WaitForAssertion(() => Assert.Equal("5", cut.Find("[data-testid='interval-cpu.usage']").GetAttribute("value")), Patience);
    }

    [Fact]
    public void A_new_deadband_is_saved_with_the_rest_of_the_override()
    {
        IRenderedComponent<MetricsPage> cut = RenderPage();

        cut.Find("[data-testid='deadband-cpu.usage']").Change("0.5");

        cut.WaitForAssertion(() => Assert.Single(_api.Overrides), Patience);
        (_, MetricOverride change) = _api.Overrides[0];
        Assert.Equal(0.5, change.Deadband);
        Assert.Equal(2, change.IntervalSeconds);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-3")]
    public void A_number_that_is_not_valid_is_not_sent_and_the_field_says_so(string text)
    {
        IRenderedComponent<MetricsPage> cut = RenderPage();

        cut.Find("[data-testid='interval-cpu.usage']").Change(text);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='interval-error-cpu.usage']")), Patience);
        Assert.Empty(_api.Overrides);
        Assert.Equal("2", cut.Find("[data-testid='interval-cpu.usage']").GetAttribute("value"));
    }

    [Fact]
    public void A_refused_interval_goes_back_to_the_saved_one()
    {
        _api.OverrideFailure = new ApiException(422, Samples.Problem(422, "validation_failed", "Not valid."));
        IRenderedComponent<MetricsPage> cut = RenderPage();

        cut.Find("[data-testid='interval-cpu.usage']").Change("9");

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=action-failed]")), Patience);
        Assert.Equal("2", cut.Find("[data-testid='interval-cpu.usage']").GetAttribute("value"));
    }

    [Fact]
    public void The_metrics_of_custom_sensors_are_changed_on_the_sensors_page()
    {
        _api.Catalog = new MetricList([.. _api.Catalog.Metrics, Samples.CustomMetric("disk_temp", "Disk temperature")]);

        IRenderedComponent<MetricsPage> cut = RenderPage();

        Assert.NotNull(cut.Find("[data-testid='custom-custom.disk_temp']"));
        Assert.True(cut.Find("[data-testid='enabled-custom.disk_temp'] input").HasAttribute("disabled"));
        Assert.True(cut.Find("[data-testid='expose-custom.disk_temp'] input").HasAttribute("disabled"));
        Assert.True(cut.Find("[data-testid='interval-custom.disk_temp']").HasAttribute("disabled"));
    }

    [Fact]
    public void An_agent_without_metrics_says_so_and_opens_no_stream()
    {
        _api.Catalog = new MetricList([]);

        IRenderedComponent<MetricsPage> cut = RenderPage();

        Assert.NotNull(cut.Find("[data-testid=metrics-none]"));
        Assert.Equal(0, _api.StreamAttempts);
    }

    private IRenderedComponent<MetricsPage> RenderPage()
    {
        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        IRenderedComponent<MetricsPage> cut = Render<MetricsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid^=group-], [data-testid=metrics-none], [data-testid=error-message]")), Patience);
        return cut;
    }
}
