using Bunit;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.History;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Settings;

/// <summary>
/// The history page and the log viewer (T407) against a fake agent: runs with filters and paging, the detail of a run with its
/// variables, output, exit code and source, the events with a type filter, and the log viewer with its level filter and follow mode.
/// </summary>
[Trait("Category", "Settings")]
public sealed class HistoryPageTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeAgentApi _api = new FakeAgentApi().SignedIn();
    private readonly FakeBrowserInterop _browser = new();

    public HistoryPageTests()
    {
        UiTestSetup.Register(this, _api, _browser);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact]
    public void The_runs_are_listed_newest_first_with_their_result()
    {
        IRenderedComponent<HistoryPage> cut = RenderHistory();

        Assert.Equal(2, cut.FindAll("[data-testid=run-table] tbody tr").Count);
        Assert.Equal("succeeded", cut.Find("[data-testid=run-r2]").GetAttribute("data-result"));
        Assert.Equal("failed", cut.Find("[data-testid=run-r1]").GetAttribute("data-result"));
        Assert.Equal(new RunQuery(25, 0, null, null, false), _api.RunQueries[0]);
    }

    [Fact]
    public void The_filters_are_sent_to_the_agent()
    {
        IRenderedComponent<HistoryPage> cut = RenderHistory();

        cut.Find("[data-testid=run-filter-command] input").Input("backup");
        cut.Find("[data-testid=run-filter-source]").Change("ui");
        cut.Find("[data-testid=run-filter-failed] input").Change(true);
        cut.Find("[data-testid=run-filter-apply]").Click();

        cut.WaitForAssertion(() => Assert.Equal(new RunQuery(25, 0, "backup", "ui", true), _api.RunQueries[^1]), Patience);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=run-table] tbody tr")), Patience);
        Assert.NotNull(cut.Find("[data-testid=run-r1]"));
    }

    [Fact]
    public void Filters_without_a_match_say_so()
    {
        IRenderedComponent<HistoryPage> cut = RenderHistory();

        cut.Find("[data-testid=run-filter-command] input").Input("nothing");
        cut.Find("[data-testid=run-filter-apply]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=runs-none]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=run-table]"));
    }

    [Fact]
    public void The_detail_of_a_run_shows_variables_output_exit_code_and_source()
    {
        IRenderedComponent<HistoryPage> cut = RenderHistory();

        cut.Find("[data-testid=run-open-r1]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=run-detail]")), Patience);
        Assert.Equal("r1", cut.Find("[data-testid=run-detail]").GetAttribute("data-run"));
        Assert.Equal("ui", cut.Find("[data-testid=detail-source]").GetAttribute("data-value"));
        Assert.Equal("1", cut.Find("[data-testid=detail-exitcode]").TextContent.Trim());
        Assert.Equal("exit_nonzero", cut.Find("[data-testid=detail-errorcode]").TextContent.Trim());
        Assert.Equal("copying files", cut.Find("[data-testid=detail-stdout]").TextContent);
        Assert.Equal("disk full", cut.Find("[data-testid=detail-stderr]").TextContent);
        Assert.Equal("5", cut.Find("[data-variable=minutes] code").TextContent);
        Assert.Equal("***", cut.Find("[data-variable=password] code").TextContent);
        Assert.Equal("true", cut.Find("[data-testid=run-r1]").GetAttribute("data-selected"));
    }

    [Fact]
    public void The_detail_shows_who_asked_when_a_client_did()
    {
        IRenderedComponent<HistoryPage> cut = RenderHistory();

        cut.Find("[data-testid=run-open-r2]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=run-detail]")), Patience);
        Assert.Equal("c1", cut.Find("[data-testid=detail-client]").TextContent.Trim());
        Assert.Equal("ha-user", cut.Find("[data-testid=detail-user]").TextContent.Trim());
        Assert.Equal("0", cut.Find("[data-testid=detail-exitcode]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=detail-stderr]"));
    }

    [Fact]
    public void A_run_that_is_gone_shows_the_error_and_no_detail()
    {
        IRenderedComponent<HistoryPage> cut = RenderHistory();
        _api.RunDetails.Remove("r1");

        cut.Find("[data-testid=run-open-r1]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=error-display]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=run-detail]"));
    }

    [Fact]
    public void The_detail_can_be_closed()
    {
        IRenderedComponent<HistoryPage> cut = RenderHistory();
        cut.Find("[data-testid=run-open-r1]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=run-detail]")), Patience);

        cut.Find("[data-testid=run-detail-close]").Click();

        Assert.Empty(cut.FindAll("[data-testid=run-detail]"));
    }

    [Fact]
    public void Runs_are_paged_when_the_agent_says_there_are_more()
    {
        _api.Runs = new RunList(
            [.. Enumerable.Range(1, 30).Select(number => new RunSummary("p" + number, "cmd", "ui", "finished", Samples.Now.AddMinutes(-number), 10, 0, null, true))],
            25,
            0,
            true);
        IRenderedComponent<HistoryPage> cut = RenderHistory();

        Assert.Equal(25, cut.FindAll("[data-testid=run-table] tbody tr").Count);
        Assert.True(cut.Find("[data-testid=runs-previous]").HasAttribute("disabled"));

        cut.Find("[data-testid=runs-next]").Click();

        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[data-testid=run-table] tbody tr").Count), Patience);
        Assert.Equal(25, _api.RunQueries[^1].Offset);
        Assert.False(cut.Find("[data-testid=runs-previous]").HasAttribute("disabled"));
    }

    [Fact]
    public void Runs_that_cannot_be_read_show_the_error()
    {
        _api.RunsFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Something broke."));

        IRenderedComponent<HistoryPage> cut = RenderHistory();

        Assert.Contains("Something broke.", cut.Find("[data-testid=error-message]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=run-table]"));
        Assert.NotNull(cut.Find("[data-testid=event-table]"));
    }

    [Fact]
    public void The_events_are_listed_and_filtered_by_type()
    {
        IRenderedComponent<HistoryPage> cut = RenderHistory();
        Assert.Equal(2, cut.FindAll("[data-testid=event-table] tbody tr").Count);

        cut.Find("[data-testid=event-filter-type] input").Input("session.locked");
        cut.Find("[data-testid=event-filter-apply]").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=event-table] tbody tr")), Patience);
        Assert.Equal("session.locked", cut.Find("[data-testid=event-2]").GetAttribute("data-type"));
        Assert.Equal("session.locked", _api.EventQueries[^1].Type);
    }

    [Fact]
    public void The_log_viewer_shows_the_newest_entries_first_with_their_level()
    {
        IRenderedComponent<LogsPage> cut = RenderLogs();

        // The default level is information, so the debug line is not there.
        string[] levels = [.. cut.FindAll("[data-testid=log-line]").Select(line => line.GetAttribute("data-level")!)];
        Assert.Equal(["error", "warning", "information"], levels);
        Assert.Equal("information", _api.LogQueries[0].Level);
        Assert.Contains("System.InvalidOperationException: boom", cut.Find("[data-testid=log-exception]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_level_filter_asks_the_agent_for_that_level()
    {
        IRenderedComponent<LogsPage> cut = RenderLogs();

        cut.Find("[data-testid=log-level]").Change("warning");

        cut.WaitForAssertion(() => Assert.Equal("warning", _api.LogQueries[^1].Level), Patience);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=log-line]").Count), Patience);

        cut.Find("[data-testid=log-level]").Change("debug");

        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("[data-testid=log-line]").Count), Patience);
    }

    [Fact]
    public void The_search_text_narrows_the_entries_in_the_browser()
    {
        IRenderedComponent<LogsPage> cut = RenderLogs();

        cut.Find("[data-testid=log-search] input").Input("client lost");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=log-line]")), Patience);
        Assert.Equal("warning", cut.Find("[data-testid=log-line]").GetAttribute("data-level"));
    }

    [Fact]
    public void Follow_mode_reads_new_entries_until_it_is_switched_off()
    {
        IRenderedComponent<LogsPage> cut = RenderLogs();
        int before = _api.LogQueries.Count;
        Assert.Equal("false", cut.Find("[data-testid=log-follow]").GetAttribute("data-checked"));

        cut.Find("[data-testid=log-follow] input").Change(true);
        _api.LogEntries.Add(new LogEntry(Samples.Now, "Error", "Run", "a new failure", null));

        cut.WaitForAssertion(() => Assert.Contains("a new failure", cut.Find("[data-testid=log-output]").TextContent, StringComparison.Ordinal), Patience);
        Assert.Equal("true", cut.Find("[data-testid=log-follow]").GetAttribute("data-checked"));

        cut.Find("[data-testid=log-follow] input").Change(false);
        int atSwitchOff = _api.LogQueries.Count;
        Assert.True(atSwitchOff > before);
        Thread.Sleep(150);
        Assert.Equal(atSwitchOff, _api.LogQueries.Count);
    }

    [Fact]
    public async Task Leaving_the_log_viewer_stops_following()
    {
        IRenderedComponent<LogsPage> cut = RenderLogs();
        cut.Find("[data-testid=log-follow] input").Change(true);
        cut.WaitForAssertion(() => Assert.True(_api.LogQueries.Count > 1), Patience);

        await cut.Instance.DisposeAsync();
        int atLeaving = _api.LogQueries.Count;
        await Task.Delay(150);

        Assert.Equal(atLeaving, _api.LogQueries.Count);
    }

    [Fact]
    public void A_log_that_cannot_be_read_shows_the_error()
    {
        _api.LogsFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Something broke."));

        IRenderedComponent<LogsPage> cut = RenderLogs();

        Assert.Contains("Something broke.", cut.Find("[data-testid=error-message]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=log-output]"));
    }

    private IRenderedComponent<HistoryPage> RenderHistory()
    {
        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        IRenderedComponent<HistoryPage> cut = Render<HistoryPage>();
        cut.WaitForAssertion(
            () =>
            {
                Assert.NotEmpty(cut.FindAll("[data-testid=run-table], [data-testid=runs-none], [data-testid=error-message]"));
                Assert.NotEmpty(cut.FindAll("[data-testid=event-table], [data-testid=events-none]"));
            },
            Patience);
        return cut;
    }

    private IRenderedComponent<LogsPage> RenderLogs()
    {
        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        IRenderedComponent<LogsPage> cut = Render<LogsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=log-output], [data-testid=logs-none], [data-testid=error-message]")), Patience);
        return cut;
    }
}
