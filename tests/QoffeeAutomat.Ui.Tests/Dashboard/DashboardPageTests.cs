using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Dashboard;
using QoffeeAutomat.Ui.Components.Services;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Dashboard;

/// <summary>
/// The dashboard (T402) against a fake agent: what it shows, that values arrive without a reload, and, above all, that
/// the live metric stream exists only while the page is visible (PLAN.md D-007).
/// </summary>
[Trait("Category", "Dashboard")]
public sealed class DashboardPageTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeAgentApi _api = new FakeAgentApi().SignedIn();
    private readonly FakeBrowserInterop _browser = new();

    public DashboardPageTests()
    {
        UiTestSetup.Register(this, _api, _browser);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact]
    public void The_dashboard_shows_the_agent_status()
    {
        IRenderedComponent<DashboardPage> cut = Render<DashboardPage>();

        cut.WaitForAssertion(() => Assert.Equal("1.2.3", cut.Find("[data-testid=agent-version]").TextContent), Patience);
        Assert.Equal("3d 4h 12m", cut.Find("[data-testid=agent-uptime]").TextContent);
        Assert.Equal("windows (Windows 11, x64)", cut.Find("[data-testid=agent-platform]").TextContent);
        Assert.Equal("STUDY-PC", cut.Find("[data-testid=agent-host]").TextContent);
        Assert.Equal("Ready, alice, administrator", cut.Find("[data-testid=agent-session]").TextContent);
        Assert.Equal("false", cut.Find("[data-testid=agent-paused]").GetAttribute("data-paused"));
        Assert.Contains("Running", cut.Find("[data-testid=agent-paused]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_paused_flag_and_the_state_of_the_session_host_are_shown()
    {
        _api.Status = Samples.Status(paused: true, sessionReady: false, configProblems: 2);

        IRenderedComponent<DashboardPage> cut = Render<DashboardPage>();

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=agent-paused]").GetAttribute("data-paused")), Patience);
        Assert.Contains("Paused", cut.Find("[data-testid=agent-paused]").TextContent, StringComparison.Ordinal);
        Assert.StartsWith("Not running", cut.Find("[data-testid=agent-session]").TextContent, StringComparison.Ordinal);
        Assert.Contains("2 configuration file(s)", cut.Find("[data-testid=agent-config-problems]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_dashboard_shows_the_paired_clients_whether_they_are_connected_and_when_they_were_last_seen()
    {
        IRenderedComponent<DashboardPage> cut = Render<DashboardPage>();

        cut.WaitForAssertion(() => Assert.Equal("2", cut.Find("[data-testid=link-paired]").TextContent), Patience);
        Assert.Equal("1", cut.Find("[data-testid=link-connections]").TextContent);
        IElement connected = cut.Find("[data-testid=link-client-c1]");
        IElement idle = cut.Find("[data-testid=link-client-c2]");
        Assert.Equal("true", connected.GetAttribute("data-connected"));
        Assert.Contains("Home Assistant", connected.TextContent, StringComparison.Ordinal);
        Assert.Equal("false", idle.GetAttribute("data-connected"));
        Assert.Contains("never", idle.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_dashboard_says_so_when_no_client_is_paired()
    {
        _api.Clients = new LinkClientList([]);

        IRenderedComponent<DashboardPage> cut = Render<DashboardPage>();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=link-none]")), Patience);
    }

    [Fact]
    public void The_dashboard_shows_the_recent_runs_and_events()
    {
        IRenderedComponent<DashboardPage> cut = Render<DashboardPage>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=recent-runs] tbody tr").Count), Patience);
        IElement failed = cut.Find("[data-testid=run-r1]");
        Assert.Contains("backup", failed.TextContent, StringComparison.Ordinal);
        Assert.Contains("exit_nonzero", failed.TextContent, StringComparison.Ordinal);
        Assert.Equal("succeeded", cut.Find("[data-testid=run-r2] [data-result]").GetAttribute("data-result"));
        Assert.Equal("85 ms", cut.Find("[data-testid=run-r2] td:nth-child(4)").TextContent);
        Assert.Equal("4.2 s", cut.Find("[data-testid=run-r1] td:nth-child(4)").TextContent);

        Assert.Equal(2, cut.FindAll("[data-testid=recent-events] tbody tr").Count);
        Assert.Contains("hello", cut.Find("[data-testid=event-1]").TextContent, StringComparison.Ordinal);
        Assert.Contains("session.locked", cut.Find("[data-testid=event-2]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tiles_show_the_current_value_and_a_chart_of_the_recorded_values()
    {
        IRenderedComponent<DashboardPage> cut = Render<DashboardPage>();

        cut.WaitForAssertion(() => Assert.Equal("12.5 %", Value(cut, "cpu.usage")), Patience);
        Assert.Equal("40.0 %", Value(cut, "memory.used_percent"));
        Assert.NotNull(cut.Find("[data-testid='tile-cpu.usage'] polyline"));

        // Only enabled, numeric, non-diagnostic metrics get a tile.
        Assert.Empty(cut.FindAll("[data-testid='tile-gpu.usage']"));
        Assert.Empty(cut.FindAll("[data-testid='tile-system.uptime']"));
    }

    [Fact]
    public void Live_values_update_the_tiles_without_reloading_the_page()
    {
        IRenderedComponent<DashboardPage> cut = Render<DashboardPage>();
        cut.WaitForAssertion(() => Assert.Equal(1, _api.StreamsActive), Patience);
        string chartBefore = cut.Find("[data-testid='tile-cpu.usage'] polyline").GetAttribute("points")!;
        int statusCallsBefore = _api.StatusCalls;
        int catalogCallsBefore = _api.MetricCalls;

        _api.Push(Samples.Update(Samples.Now.AddSeconds(10), ("cpu.usage", 73.26)));

        cut.WaitForAssertion(() => Assert.Equal("73.3 %", Value(cut, "cpu.usage")), Patience);
        Assert.Equal("live", cut.Find("[data-testid=live-state]").GetAttribute("data-state"));
        Assert.NotEqual(chartBefore, cut.Find("[data-testid='tile-cpu.usage'] polyline").GetAttribute("points"));

        // A second update changes the value again; nothing was read again from the agent.
        _api.Push(Samples.Update(Samples.Now.AddSeconds(12), ("cpu.usage", 5)));
        cut.WaitForAssertion(() => Assert.Equal("5.0 %", Value(cut, "cpu.usage")), Patience);
        Assert.Equal(statusCallsBefore, _api.StatusCalls);
        Assert.Equal(catalogCallsBefore, _api.MetricCalls);
    }

    [Fact]
    public void An_unavailable_metric_is_shown_as_unavailable_and_not_as_a_stale_value()
    {
        IRenderedComponent<DashboardPage> cut = Render<DashboardPage>();
        cut.WaitForAssertion(() => Assert.Equal(1, _api.StreamsActive), Patience);

        _api.Push(new MetricStreamUpdate(
            Samples.Now.AddSeconds(5),
            Snapshot: false,
            new Dictionary<string, JsonElement> { ["cpu.usage"] = Samples.Null() }));

        cut.WaitForAssertion(() => Assert.Equal("n/a", Value(cut, "cpu.usage")), Patience);
    }

    [Fact]
    public void The_stream_asks_for_the_metrics_of_the_tiles_only()
    {
        IRenderedComponent<DashboardPage> cut = Render<DashboardPage>();

        cut.WaitForAssertion(() => Assert.Equal(1, _api.StreamsActive), Patience);
        string[] expected = ["cpu.usage", "memory.used_percent"];
        Assert.Equal(expected, _api.LastStreamIds);
    }

    [Fact]
    public async Task Leaving_the_page_closes_the_stream()
    {
        IRenderedComponent<DashboardHost> host = Render<DashboardHost>();
        host.WaitForAssertion(() => Assert.Equal(1, _api.StreamsActive), Patience);
        Assert.Equal(1, _browser.ActiveSubscriptions);

        await host.InvokeAsync(() => host.Instance.Leave());

        // The page is disposed asynchronously and nothing is rendered afterwards, so poll instead of waiting for a render.
        await EventuallyAsync(() => _api.StreamsActive == 0 && _api.StreamsClosed == 1 && _browser.ActiveSubscriptions == 0);
    }

    [Fact]
    public async Task Disposing_the_page_closes_the_stream_and_stops_listening_to_the_browser()
    {
        IRenderedComponent<DashboardPage> cut = Render<DashboardPage>();
        cut.WaitForAssertion(() => Assert.Equal(1, _api.StreamsActive), Patience);

        await DisposeComponentsAsync();

        await EventuallyAsync(() => _api.StreamsActive == 0 && _browser.ActiveSubscriptions == 0);
        Assert.Equal(1, _api.StreamsClosed);
    }

    [Fact]
    public async Task The_stream_is_closed_while_the_page_is_hidden_and_opened_again_when_it_is_shown()
    {
        IRenderedComponent<DashboardPage> cut = Render<DashboardPage>();
        cut.WaitForAssertion(() => Assert.Equal(1, _api.StreamsActive), Patience);

        await _browser.SetVisibleAsync(false);
        cut.WaitForAssertion(() => Assert.Equal(0, _api.StreamsActive), Patience);
        Assert.Equal("paused", cut.Find("[data-testid=live-state]").GetAttribute("data-state"));

        await _browser.SetVisibleAsync(true);
        cut.WaitForAssertion(() => Assert.Equal(1, _api.StreamsActive), Patience);
        Assert.Equal(2, _api.StreamAttempts);
        Assert.Equal(1, _api.StreamsClosed);
    }

    [Fact]
    public async Task A_page_that_is_hidden_from_the_start_never_opens_the_stream()
    {
        _browser.Visible = false;

        IRenderedComponent<DashboardPage> cut = Render<DashboardPage>();

        cut.WaitForAssertion(() => Assert.Equal("1.2.3", cut.Find("[data-testid=agent-version]").TextContent), Patience);
        Assert.Equal(0, _api.StreamAttempts);
        Assert.Equal("paused", cut.Find("[data-testid=live-state]").GetAttribute("data-state"));

        await _browser.SetVisibleAsync(true);
        cut.WaitForAssertion(() => Assert.Equal(1, _api.StreamsActive), Patience);
    }

    [Fact]
    public async Task The_stream_is_opened_again_after_it_failed_or_ended()
    {
        _api.FailNextStreams = 1;

        Render<DashboardPage>();

        // Opening the stream does not render the page, so poll instead of waiting for a render.
        await EventuallyAsync(() => _api.StreamsActive == 1);
        Assert.Equal(2, _api.StreamAttempts);

        _api.EndStreams();
        await EventuallyAsync(() => _api.StreamAttempts == 3 && _api.StreamsActive == 1);
    }

    [Fact]
    public void The_page_without_numeric_metrics_shows_a_note_and_opens_no_stream()
    {
        _api.Catalog = new MetricList([Samples.Metric("system.uptime", "Uptime", valueType: "string")]);

        IRenderedComponent<DashboardPage> cut = Render<DashboardPage>();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=metrics-none]")), Patience);
        Assert.Equal(0, _api.StreamAttempts);
    }

    [Fact]
    public void The_status_runs_and_events_are_read_again_periodically_while_the_page_is_visible()
    {
        Services.GetRequiredService<DashboardOptions>().RefreshInterval = TimeSpan.FromMilliseconds(30);
        IRenderedComponent<DashboardPage> cut = Render<DashboardPage>();
        cut.WaitForAssertion(() => Assert.Equal("1.2.3", cut.Find("[data-testid=agent-version]").TextContent), Patience);

        _api.Status = Samples.Status(version: "2.0.0");

        cut.WaitForAssertion(() => Assert.Equal("2.0.0", cut.Find("[data-testid=agent-version]").TextContent), Patience);
    }

    [Fact]
    public async Task The_periodic_reading_stops_while_the_page_is_hidden()
    {
        Services.GetRequiredService<DashboardOptions>().RefreshInterval = TimeSpan.FromMilliseconds(20);
        IRenderedComponent<DashboardPage> cut = Render<DashboardPage>();
        cut.WaitForAssertion(() => Assert.True(_api.StatusCalls >= 2), Patience);

        await _browser.SetVisibleAsync(false);
        int calls = _api.StatusCalls;
        await Task.Delay(150);

        Assert.Equal(calls, _api.StatusCalls);
    }

    [Fact]
    public void A_failed_call_is_shown_as_an_error_and_the_rest_of_the_page_still_works()
    {
        _api.StatusFailure = new ApiException(0, Samples.Problem(0, ApiException.NetworkErrorCode, "The agent cannot be reached."));

        IRenderedComponent<DashboardPage> cut = Render<DashboardPage>();

        cut.WaitForAssertion(() => Assert.Equal("The agent cannot be reached.", cut.Find("[data-testid=error-message]").TextContent), Patience);
        Assert.Equal("12.5 %", Value(cut, "cpu.usage"));
    }

    [Fact]
    public async Task An_expired_session_signs_the_user_out_and_no_stream_is_opened()
    {
        SessionState session = Services.GetRequiredService<SessionState>();
        await session.RefreshAsync();
        Assert.Equal(SignInStatus.SignedIn, session.Status);
        _api.StatusFailure = new ApiException(401, Samples.Problem(401, "unauthorized", "Sign in to use the management API."));

        IRenderedComponent<DashboardPage> cut = Render<DashboardPage>();

        cut.WaitForAssertion(() => Assert.Equal(SignInStatus.SignedOut, session.Status), Patience);
        Assert.Equal(0, _api.StreamAttempts);
    }

    [Fact]
    public void Switching_the_language_translates_the_dashboard()
    {
        if (Globalization.IsInvariant)
        {
            // Without ICU there are no cultures; the texts stay English and there is nothing to switch.
            return;
        }

        IRenderedComponent<DashboardPage> cut = Render<DashboardPage>();
        cut.WaitForAssertion(() => Assert.Equal("1.2.3", cut.Find("[data-testid=agent-version]").TextContent), Patience);
        Assert.Contains("Recent runs", cut.Markup, StringComparison.Ordinal);

        Services.GetRequiredService<Localizer>().SetLanguage("de");

        cut.WaitForAssertion(() => Assert.Contains("Letzte Ausführungen", cut.Markup, StringComparison.Ordinal), Patience);
    }

    private static string Value<T>(IRenderedComponent<T> cut, string metricId)
        where T : Microsoft.AspNetCore.Components.IComponent
    {
        return cut.Find($"[data-testid='tile-{metricId}'] [data-testid=tile-value]").TextContent;
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition(), "The condition did not become true in five seconds.");
    }
}
