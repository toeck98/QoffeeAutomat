using Bunit;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Metrics;
using QoffeeAutomat.Hub.Ui.Pages.Metrics;
using QoffeeAutomat.Hub.Ui.Services;
using QoffeeAutomat.Hub.Ui.Tests.Support;

namespace QoffeeAutomat.Hub.Ui.Tests.Config;

/// <summary>
/// The fleet metrics page (PLAN.md S20, D-032, T910, T916): one metric for several agents over a range that the user selects, drawn in one chart with a
/// shared axis and listed in a table, from the query API of the hub.
/// </summary>
[Trait("Category", "Config")]
public sealed class FleetMetricsPageTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeHubApi _hub = new();
    private readonly FakeBrowserInterop _browser = new();

    public FleetMetricsPageTests()
    {
        HubUiTestSetup.Register(this, _hub, _browser);
        _hub.Agents = new AgentList(
        [
            Samples.Agent("pc-1", "PC 1"),
            Samples.Agent("pc-2", "PC 2"),
            Samples.Agent("pc-3", "PC 3", "pending"),
        ]);
        _hub.AgentMetrics["pc-1"] = new AgentMetricList(
            "pc-1",
            [
                new LatestMetric("pc-1", "cpu.total", "number", "%", Samples.Now, 12, null),
                new LatestMetric("pc-1", "session.locked", "bool", null, Samples.Now, 1, null),
                new LatestMetric("pc-1", "system.hostname", "string", null, Samples.Now, null, "pc-1"),
            ]);
        _hub.AgentMetrics["pc-2"] = new AgentMetricList(
            "pc-2",
            [
                new LatestMetric("pc-2", "cpu.total", "number", "%", Samples.Now, 55, null),
                new LatestMetric("pc-2", "memory.used", "number", "MB", Samples.Now, 2048, null),
            ]);
        _hub.MetricAnswer = query => new MetricQueryResult(
            "raw",
            query.From ?? Samples.Now.AddHours(-1),
            query.To ?? Samples.Now,
            [
                .. query.Agents.Select(agent => agent == "pc-1"
                    ? Samples.Series("pc-1", "cpu.total", "%", (300, 10), (200, 30), (100, 20))
                    : Samples.Series(agent, "cpu.total", "%", (300, 50), (200, 70), (100, 90))),
            ]);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    private async Task SignInAsync(string role)
    {
        _hub.SignedIn(role);
        await Services.GetRequiredService<HubSessionState>().RefreshAsync();
    }

    private IRenderedComponent<FleetMetricsPage> RenderPage()
    {
        IRenderedComponent<FleetMetricsPage> cut = Render<FleetMetricsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=metrics-controls]")), Patience);
        return cut;
    }

    private static void Select(IRenderedComponent<FleetMetricsPage> cut, string agentId, bool selected = true)
    {
        cut.Find($"[data-testid=metrics-agent-{agentId}]").Change(selected);
    }

    [Fact]
    public async Task Only_enrolled_agents_are_offered_and_nothing_is_queried_before_a_metric_is_chosen()
    {
        await SignInAsync(HubRoleNames.Viewer);

        IRenderedComponent<FleetMetricsPage> cut = RenderPage();

        Assert.Equal(2, cut.FindAll("[data-testid^=metrics-agent-]").Count);
        Assert.NotEmpty(cut.FindAll("[data-testid=metrics-select-agents]"));
        Assert.Empty(_hub.MetricQueries);
    }

    [Fact]
    public async Task The_metrics_that_are_offered_are_the_numeric_ones_of_the_selected_agents_without_duplicates()
    {
        await SignInAsync(HubRoleNames.Viewer);
        IRenderedComponent<FleetMetricsPage> cut = RenderPage();

        Select(cut, "pc-1");
        Select(cut, "pc-2");

        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=metrics-metric]").HasAttribute("disabled")), Patience);
        string[] offered = [.. cut.Find("[data-testid=metrics-metric]").QuerySelectorAll("option").Select(option => option.GetAttribute("value")!)];
        Assert.Equal([string.Empty, "cpu.total", "memory.used", "session.locked"], offered);
    }

    [Fact]
    public async Task One_metric_of_several_agents_is_drawn_as_one_line_each_in_one_chart_over_the_default_range()
    {
        await SignInAsync(HubRoleNames.Viewer);
        IRenderedComponent<FleetMetricsPage> cut = RenderPage();
        Select(cut, "pc-1");
        Select(cut, "pc-2");
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=metrics-metric]").HasAttribute("disabled")), Patience);

        cut.Find("[data-testid=metrics-metric]").Change("cpu.total");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=metric-chart]")), Patience);
        Assert.Equal(2, cut.FindAll("[data-testid=metric-chart] polyline").Count);
        Assert.Equal(2, cut.FindAll("[data-testid=metric-chart-legend] li").Count);
        MetricQueryParameters query = _hub.MetricQueries[^1];
        Assert.Equal(["pc-1", "pc-2"], query.Agents.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["cpu.total"], query.Metrics.ToArray());
        Assert.Equal(Samples.Now, query.To);
        Assert.Equal(Samples.Now.AddHours(-6), query.From);
        Assert.Null(query.Resolution);

        // The table next to the chart compares the agents by number.
        Assert.Contains("20", cut.Find("[data-testid=metrics-latest-pc-1]").TextContent, StringComparison.Ordinal);
        Assert.Contains("90", cut.Find("[data-testid=metrics-latest-pc-2]").TextContent, StringComparison.Ordinal);
        Assert.Contains("%", cut.Find("[data-testid=metrics-chart-title]").TextContent, StringComparison.Ordinal);
        Assert.Contains("cpu.total", cut.Find("[data-testid=metrics-chart-title]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_selectable_range_changes_the_time_range_of_the_query()
    {
        await SignInAsync(HubRoleNames.Viewer);
        IRenderedComponent<FleetMetricsPage> cut = RenderPage();
        Select(cut, "pc-1");
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=metrics-metric]").HasAttribute("disabled")), Patience);
        cut.Find("[data-testid=metrics-metric]").Change("cpu.total");
        cut.WaitForAssertion(() => Assert.NotEmpty(_hub.MetricQueries), Patience);

        foreach ((string key, TimeSpan length) in MetricRanges.All)
        {
            int before = _hub.MetricQueries.Count;

            cut.Find("[data-testid=metrics-range]").Change(key);

            cut.WaitForAssertion(() => Assert.Equal(before + 1, _hub.MetricQueries.Count), Patience);
            MetricQueryParameters query = _hub.MetricQueries[^1];
            Assert.Equal(length, query.To - query.From);
        }
    }

    [Fact]
    public async Task A_forced_resolution_is_sent_and_automatic_is_not()
    {
        await SignInAsync(HubRoleNames.Viewer);
        IRenderedComponent<FleetMetricsPage> cut = RenderPage();
        Select(cut, "pc-1");
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=metrics-metric]").HasAttribute("disabled")), Patience);
        cut.Find("[data-testid=metrics-metric]").Change("cpu.total");
        cut.WaitForAssertion(() => Assert.NotEmpty(_hub.MetricQueries), Patience);

        cut.Find("[data-testid=metrics-resolution]").Change("5m");
        cut.WaitForAssertion(() => Assert.Equal("5m", _hub.MetricQueries[^1].Resolution), Patience);
        cut.Find("[data-testid=metrics-resolution]").Change("auto");

        cut.WaitForAssertion(() => Assert.Null(_hub.MetricQueries[^1].Resolution), Patience);
    }

    [Fact]
    public async Task The_resolution_that_the_hub_chose_is_named_next_to_the_chart()
    {
        await SignInAsync(HubRoleNames.Viewer);
        _hub.MetricAnswer = query => new MetricQueryResult("1h", query.From!.Value, query.To!.Value, [Samples.Series("pc-1", "cpu.total", "%", (300, 10), (100, 20)) with { Resolution = "1h" }]);
        IRenderedComponent<FleetMetricsPage> cut = RenderPage();
        Select(cut, "pc-1");
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=metrics-metric]").HasAttribute("disabled")), Patience);

        cut.Find("[data-testid=metrics-metric]").Change("cpu.total");

        cut.WaitForAssertion(() => Assert.Contains("1 hour buckets", cut.Find("[data-testid=metrics-resolution-used]").TextContent, StringComparison.Ordinal), Patience);
        Assert.Equal("1h", cut.Find("[data-testid=metrics-result]").GetAttribute("data-resolution"));
    }

    [Fact]
    public async Task Deselecting_an_agent_queries_again_without_it_and_deselecting_all_clears_the_chart()
    {
        await SignInAsync(HubRoleNames.Viewer);
        IRenderedComponent<FleetMetricsPage> cut = RenderPage();
        Select(cut, "pc-1");
        Select(cut, "pc-2");
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=metrics-metric]").HasAttribute("disabled")), Patience);
        cut.Find("[data-testid=metrics-metric]").Change("cpu.total");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=metric-chart] polyline").Count), Patience);

        Select(cut, "pc-2", false);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=metric-chart] polyline")), Patience);
        Assert.Equal(["pc-1"], _hub.MetricQueries[^1].Agents.ToArray());

        Select(cut, "pc-1", false);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=metrics-result]")), Patience);
        Assert.NotEmpty(cut.FindAll("[data-testid=metrics-select-agents]"));
    }

    [Fact]
    public async Task A_metric_that_the_remaining_agents_do_not_have_is_dropped_from_the_choice()
    {
        await SignInAsync(HubRoleNames.Viewer);
        IRenderedComponent<FleetMetricsPage> cut = RenderPage();
        Select(cut, "pc-2");
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=metrics-metric]").HasAttribute("disabled")), Patience);
        cut.Find("[data-testid=metrics-metric]").Change("memory.used");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=metrics-result]")), Patience);

        Select(cut, "pc-1");
        Select(cut, "pc-2", false);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=metrics-result]")), Patience);
        Assert.NotEmpty(cut.FindAll("[data-testid=metrics-select-metric]"));
    }

    [Fact]
    public async Task A_range_without_values_says_so_instead_of_drawing_an_empty_chart()
    {
        await SignInAsync(HubRoleNames.Viewer);
        _hub.MetricAnswer = query => new MetricQueryResult("raw", query.From!.Value, query.To!.Value, []);
        IRenderedComponent<FleetMetricsPage> cut = RenderPage();
        Select(cut, "pc-1");
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=metrics-metric]").HasAttribute("disabled")), Patience);

        cut.Find("[data-testid=metrics-metric]").Change("cpu.total");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=metrics-no-data]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=metric-chart]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=metrics-row-pc-1]"));
    }

    [Fact]
    public async Task A_series_that_the_hub_cut_is_marked_in_the_table()
    {
        await SignInAsync(HubRoleNames.Viewer);
        _hub.MetricAnswer = query => new MetricQueryResult("raw", query.From!.Value, query.To!.Value, [Samples.Series("pc-1", "cpu.total", "%", (300, 10), (100, 20)) with { Truncated = true }]);
        IRenderedComponent<FleetMetricsPage> cut = RenderPage();
        Select(cut, "pc-1");
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=metrics-metric]").HasAttribute("disabled")), Patience);

        cut.Find("[data-testid=metrics-metric]").Change("cpu.total");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=metrics-truncated-pc-1]")), Patience);
    }

    [Fact]
    public async Task An_error_of_the_query_is_shown_and_does_not_leave_a_chart_of_the_previous_question()
    {
        await SignInAsync(HubRoleNames.Viewer);
        IRenderedComponent<FleetMetricsPage> cut = RenderPage();
        Select(cut, "pc-1");
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=metrics-metric]").HasAttribute("disabled")), Patience);
        _hub.MetricAnswer = null;

        cut.Find("[data-testid=metrics-metric]").Change("cpu.total");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=error-display]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=metrics-result]"));
    }

    [Fact]
    public async Task A_link_from_the_page_of_a_pc_selects_the_pc_and_the_metric_at_once()
    {
        await SignInAsync(HubRoleNames.Viewer);

        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo("http://localhost/metrics?agent=pc-2&metric=memory.used");
        IRenderedComponent<FleetMetricsPage> cut = Render<FleetMetricsPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=metrics-result]")), Patience);
        MetricQueryParameters query = Assert.Single(_hub.MetricQueries);
        Assert.Equal(["pc-2"], query.Agents.ToArray());
        Assert.Equal(["memory.used"], query.Metrics.ToArray());
    }

    [Theory]
    [InlineData(HubRoleNames.Viewer)]
    [InlineData(HubRoleNames.Operator)]
    [InlineData(HubRoleNames.Admin)]
    public async Task Every_role_may_look_at_the_metrics(string role)
    {
        await SignInAsync(role);

        IRenderedComponent<FleetMetricsPage> cut = RenderPage();

        Assert.Empty(cut.FindAll("[data-testid=role-required]"));
        Assert.Empty(cut.FindAll("[data-testid=error-display]"));
    }

    [Fact]
    public async Task Without_agents_the_page_says_so()
    {
        await SignInAsync(HubRoleNames.Viewer);
        _hub.Agents = new AgentList([]);

        IRenderedComponent<FleetMetricsPage> cut = Render<FleetMetricsPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=metrics-no-agents]")), Patience);
    }

    [Fact]
    public async Task The_chart_has_an_accessible_name_and_a_legend_that_names_every_line()
    {
        await SignInAsync(HubRoleNames.Viewer);
        IRenderedComponent<FleetMetricsPage> cut = RenderPage();
        Select(cut, "pc-1");
        Select(cut, "pc-2");
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=metrics-metric]").HasAttribute("disabled")), Patience);

        cut.Find("[data-testid=metrics-metric]").Change("cpu.total");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=metric-chart]")), Patience);
        Assert.Contains("cpu.total", cut.Find("[data-testid=metric-chart] svg").GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Equal("img", cut.Find("[data-testid=metric-chart] svg").GetAttribute("role"));
        string legend = cut.Find("[data-testid=metric-chart-legend]").TextContent;
        Assert.Contains("PC 1", legend, StringComparison.Ordinal);
        Assert.Contains("PC 2", legend, StringComparison.Ordinal);
        Assert.Contains("90", cut.Find("[data-testid=metric-chart-max]").TextContent, StringComparison.Ordinal);
        Assert.Contains("10", cut.Find("[data-testid=metric-chart-min]").TextContent, StringComparison.Ordinal);
    }
}
