using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Fleet.Actions;
using QoffeeAutomat.Hub.Ui.Layout;
using QoffeeAutomat.Hub.Ui.Pages.Actions;
using QoffeeAutomat.Hub.Ui.Services;
using QoffeeAutomat.Hub.Ui.Tests.Support;

namespace QoffeeAutomat.Hub.Ui.Tests.Actions;

/// <summary>
/// The fleet-action page (PLAN.md S20 "Fleet actions", T917): who may start a bulk run and who may update PCs, what the page sends, how it shows the outcome of every PC, and
/// that an update that stopped says where and which PCs were updated. The hub refuses what a role may not do as well; the fake hub does so too, so a test can show that
/// the page does not even ask.
/// </summary>
[Trait("Category", "FleetActions")]
public sealed class FleetActionsPageTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeHubApi _hub = new();
    private readonly FakeBrowserInterop _browser = new();

    public FleetActionsPageTests()
    {
        HubUiTestSetup.Register(this, _hub, _browser);
        _hub.Agents = new AgentList(
        [
            Samples.Agent("pc-1", "PC 1", "enrolled", "1.0.0", "lab"),
            Samples.Agent("pc-2", "PC 2", "enrolled", "2.0.0", "lab", "office"),
            Samples.Agent("pc-3", "PC 3", "enrolled", "1.0.0", "home"),
            Samples.Agent("pc-4", "PC 4", "pending", "1.0.0"),
        ]);
        _hub.FleetVersions = new FleetVersionOverview(
            "2.0.0",
            [
                new AgentVersionInfo("pc-1", "PC 1", true, "1.0.0", ["lab"], "outdated", true),
                new AgentVersionInfo("pc-2", "PC 2", true, "2.0.0", ["lab", "office"], "current", true),
                new AgentVersionInfo("pc-3", "PC 3", false, "1.0.0", ["home"], "outdated", false),
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

    private async Task<IRenderedComponent<FleetActionsPage>> OpenAsync(string role)
    {
        await SignInAsync(role);
        IRenderedComponent<FleetActionsPage> cut = Render<FleetActionsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=bulk-card]")), Patience);
        return cut;
    }

    /// <summary>The input of a field: MudBlazor puts the attributes of a field on its input element, but the test must not depend on that.</summary>
    private static IElement Field(IRenderedComponent<FleetActionsPage> cut, string testId)
    {
        IElement element = cut.Find("[data-testid=" + testId + "]");
        return element.TagName is "INPUT" or "TEXTAREA" ? element : element.QuerySelector("input, textarea")!;
    }

    // ------------------------------------------------------------------
    // Roles
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_viewer_sees_the_versions_and_the_actions_but_gets_no_form_and_the_page_asks_for_nothing_to_start()
    {
        _hub.FleetActions.Add(Samples.FleetAction("bulk-1", FleetActionKinds.BulkRun, FleetActionStates.Completed, ["pc-1"], command: "lock", items: [Samples.FleetItem("pc-1", FleetActionItemStates.Success)]));

        IRenderedComponent<FleetActionsPage> cut = await OpenAsync(HubRoleNames.Viewer);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=action-bulk-1]")), Patience);
        Assert.NotEmpty(cut.FindAll("[data-testid=bulk-role-required]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=update-role-required]"));
        Assert.Empty(cut.FindAll("[data-testid=bulk-start]"));
        Assert.Empty(cut.FindAll("[data-testid=update-start]"));
        Assert.Empty(cut.FindAll("[data-testid^=update-select-]"));
        Assert.Contains("2.0.0", cut.Find("[data-testid=update-latest]").TextContent, StringComparison.Ordinal);
        Assert.Equal("outdated", cut.Find("[data-testid=update-agent-pc-1]").GetAttribute("data-state"));
        Assert.Equal("current", cut.Find("[data-testid=update-agent-pc-2]").GetAttribute("data-state"));
        Assert.Empty(cut.FindAll("[data-testid^=action-cancel-]"));
        Assert.DoesNotContain(_hub.Calls, call => call.StartsWith("Start", StringComparison.Ordinal) || call.StartsWith("refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_operator_gets_the_bulk_form_but_no_way_to_update_PCs()
    {
        IRenderedComponent<FleetActionsPage> cut = await OpenAsync(HubRoleNames.Operator);

        Assert.NotEmpty(cut.FindAll("[data-testid=bulk-start]"));
        Assert.Empty(cut.FindAll("[data-testid=bulk-role-required]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=update-role-required]"));
        Assert.Empty(cut.FindAll("[data-testid=update-start]"));
        Assert.Empty(cut.FindAll("[data-testid^=update-select-]"));
    }

    [Fact]
    public async Task An_administrator_gets_both_and_only_enrolled_PCs_are_offered_for_a_bulk_run()
    {
        IRenderedComponent<FleetActionsPage> cut = await OpenAsync(HubRoleNames.Admin);

        Assert.NotEmpty(cut.FindAll("[data-testid=bulk-start]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=update-start]"));
        string[] offered = [.. cut.FindAll("[data-testid^=bulk-agent-]").Select(input => input.GetAttribute("data-testid")!)];
        Assert.Equal(["bulk-agent-pc-1", "bulk-agent-pc-2", "bulk-agent-pc-3"], offered);
    }

    [Fact]
    public async Task The_menu_offers_the_page_to_every_role()
    {
        foreach (string role in HubRoleNames.All)
        {
            await SignInAsync(role);

            IRenderedComponent<HubNavMenu> menu = Render<HubNavMenu>();

            Assert.NotEmpty(menu.FindAll("[data-testid=nav-actions]"));
        }
    }

    // ------------------------------------------------------------------
    // Bulk runs
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_bulk_run_sends_the_command_the_variables_the_chosen_PCs_the_tags_and_the_parallelism()
    {
        IRenderedComponent<FleetActionsPage> cut = await OpenAsync(HubRoleNames.Operator);

        Field(cut, "bulk-command").Input(" set_volume ");
        Field(cut, "bulk-tags").Input("Lab, home");
        Field(cut, "bulk-vars").Input("{\"volume\": 30}");
        cut.Find("[data-testid=bulk-agent-pc-3]").Change(true);
        cut.Find("[data-testid=bulk-start]").Click();

        cut.WaitForAssertion(() => Assert.Single(_hub.BulkRunRequests), Patience);
        StartBulkRunRequest request = _hub.BulkRunRequests[0];
        Assert.Equal("set_volume", request.Command);
        Assert.Equal(["pc-3"], request.Agents!.ToArray());
        Assert.Equal(["lab", "home"], request.Tags!.ToArray());
        Assert.Equal(30, request.Vars!["volume"].GetInt32());
        Assert.Equal(5, request.Parallelism);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=actions-notice]")), Patience);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid^=action-bulk-]")), Patience);
    }

    [Fact]
    public async Task A_bulk_run_with_nothing_to_run_or_nowhere_to_run_it_or_bad_variables_is_not_sent()
    {
        IRenderedComponent<FleetActionsPage> cut = await OpenAsync(HubRoleNames.Operator);

        cut.Find("[data-testid=bulk-start]").Click();
        Assert.NotEmpty(cut.FindAll("[data-testid=bulk-error]"));
        Field(cut, "bulk-command").Input("lock");
        cut.Find("[data-testid=bulk-start]").Click();
        Assert.Contains("PC", cut.Find("[data-testid=bulk-error]").TextContent, StringComparison.Ordinal);
        cut.Find("[data-testid=bulk-agent-pc-1]").Change(true);
        Field(cut, "bulk-vars").Input("[1, 2]");
        cut.Find("[data-testid=bulk-start]").Click();
        Assert.Contains("JSON object", cut.Find("[data-testid=bulk-error]").TextContent, StringComparison.Ordinal);

        Assert.Empty(_hub.BulkRunRequests);
    }

    [Fact]
    public async Task A_refusal_of_the_hub_is_shown()
    {
        _hub.StartFailure = new QoffeeAutomat.Ui.Components.Api.ApiException(409, Samples.Problem(409, "too_many_actions", "Too many."));
        IRenderedComponent<FleetActionsPage> cut = await OpenAsync(HubRoleNames.Operator);
        Field(cut, "bulk-command").Input("lock");
        cut.Find("[data-testid=bulk-agent-pc-1]").Change(true);

        cut.Find("[data-testid=bulk-start]").Click();

        cut.WaitForAssertion(() => Assert.Contains("as many actions run as the hub allows", cut.Markup, StringComparison.OrdinalIgnoreCase), Patience);
        Assert.Empty(_hub.FleetActions);
    }

    // ------------------------------------------------------------------
    // Updates
    // ------------------------------------------------------------------

    [Fact]
    public async Task Choosing_the_outdated_PCs_leaves_out_the_current_ones_and_the_ones_that_cannot_be_updated()
    {
        IRenderedComponent<FleetActionsPage> cut = await OpenAsync(HubRoleNames.Admin);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=update-select-pc-1]")), Patience);
        Assert.True(cut.Find("[data-testid=update-select-pc-3]").HasAttribute("disabled"));

        cut.Find("[data-testid=update-select-outdated]").Click();

        Assert.True(cut.Find("[data-testid=update-select-pc-1]").HasAttribute("checked"));
        Assert.False(cut.Find("[data-testid=update-select-pc-2]").HasAttribute("checked"));
        Assert.False(cut.Find("[data-testid=update-select-pc-3]").HasAttribute("checked"));
    }

    [Fact]
    public async Task An_update_is_started_only_after_it_is_confirmed()
    {
        IRenderedComponent<FleetActionsPage> cut = await OpenAsync(HubRoleNames.Admin);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=update-select-pc-1]")), Patience);
        cut.Find("[data-testid=update-start]").Click();
        Assert.NotEmpty(cut.FindAll("[data-testid=update-error]"));
        Assert.Empty(_hub.UpdateRequests);

        cut.Find("[data-testid=update-select-pc-1]").Change(true);
        cut.Find("[data-testid=update-start]").Click();
        Assert.NotEmpty(cut.FindAll("[data-testid=update-confirm-text]"));
        Assert.Empty(_hub.UpdateRequests);
        cut.Find("[data-testid=update-confirm-cancel]").Click();
        Assert.Empty(_hub.UpdateRequests);
        cut.Find("[data-testid=update-start]").Click();
        cut.Find("[data-testid=update-confirm]").Click();

        cut.WaitForAssertion(() => Assert.Single(_hub.UpdateRequests), Patience);
        Assert.Equal(["pc-1"], _hub.UpdateRequests[0].Agents!.ToArray());
        Assert.Equal(600, _hub.UpdateRequests[0].TimeoutSeconds);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid^=action-update-]")), Patience);
    }

    [Fact]
    public async Task An_update_that_stopped_says_where_and_which_PCs_were_updated_and_the_details_show_each_PC()
    {
        _hub.FleetActions.Add(Samples.FleetAction(
            "update-1",
            FleetActionKinds.Update,
            FleetActionStates.Stopped,
            ["pc-1", "pc-2", "pc-3"],
            target: "2.0.0",
            items:
            [
                Samples.FleetItem("pc-1", FleetActionItemStates.Success, "The agent came back with version 2.0.0.", before: "1.0.0", after: "2.0.0"),
                Samples.FleetItem("pc-2", FleetActionItemStates.Failure, "The agent did not come back.", "version_not_reached", before: "1.0.0", after: "1.0.0"),
                Samples.FleetItem("pc-3", FleetActionItemStates.Skipped, "Not updated: an earlier agent did not come back.", "not_reached"),
            ],
            updated: ["pc-1"],
            stoppedAt: "pc-2",
            stopReason: "The agent did not come back with version 2.0.0."));

        IRenderedComponent<FleetActionsPage> cut = await OpenAsync(HubRoleNames.Viewer);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=action-update-1]")), Patience);
        Assert.Equal("stopped", cut.Find("[data-testid=action-update-1]").GetAttribute("data-state"));
        Assert.Contains("PC 1", cut.Find("[data-testid=action-updated-update-1]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("PC 3", cut.Find("[data-testid=action-updated-update-1]").TextContent, StringComparison.Ordinal);
        string stopped = cut.Find("[data-testid=action-stopped-update-1]").TextContent;
        Assert.Contains("PC 2", stopped, StringComparison.Ordinal);
        Assert.Contains("did not come back", stopped, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=action-items-update-1]"));

        cut.Find("[data-testid=action-details-update-1]").Click();

        Assert.Equal("success", cut.Find("[data-testid=action-item-pc-1]").GetAttribute("data-state"));
        Assert.Equal("failure", cut.Find("[data-testid=action-item-pc-2]").GetAttribute("data-state"));
        Assert.Equal("skipped", cut.Find("[data-testid=action-item-pc-3]").GetAttribute("data-state"));
        Assert.Contains("1.0.0", cut.Find("[data-testid=action-item-pc-1]").TextContent, StringComparison.Ordinal);
        Assert.Contains("2.0.0", cut.Find("[data-testid=action-item-pc-1]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_outcome_of_a_bulk_run_shows_success_failure_and_offline_per_PC_with_the_counts()
    {
        _hub.FleetActions.Add(Samples.FleetAction(
            "bulk-1",
            FleetActionKinds.BulkRun,
            FleetActionStates.Completed,
            ["pc-1", "pc-2", "pc-3"],
            command: "lock",
            items:
            [
                Samples.FleetItem("pc-1", FleetActionItemStates.Success),
                Samples.FleetItem("pc-2", FleetActionItemStates.Failure, "The command ended with exit code 3.", "nonzero_exit"),
                Samples.FleetItem("pc-3", FleetActionItemStates.Offline, "The agent is not connected.", "agent_offline"),
            ]));

        IRenderedComponent<FleetActionsPage> cut = await OpenAsync(HubRoleNames.Viewer);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=action-bulk-1]")), Patience);
        cut.Find("[data-testid=action-details-bulk-1]").Click();

        Assert.Equal("success", cut.Find("[data-testid=action-item-pc-1]").GetAttribute("data-state"));
        Assert.Equal("failure", cut.Find("[data-testid=action-item-pc-2]").GetAttribute("data-state"));
        Assert.Equal("offline", cut.Find("[data-testid=action-item-pc-3]").GetAttribute("data-state"));
        Assert.Contains("exit code 3", cut.Find("[data-testid=action-detail-pc-2]").TextContent, StringComparison.Ordinal);
        string counts = cut.Find("[data-testid=action-counts-bulk-1]").TextContent;
        Assert.Contains("1 succeeded", counts, StringComparison.Ordinal);
        Assert.Contains("1 failed", counts, StringComparison.Ordinal);
        Assert.Contains("1 offline", counts, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Cancelling
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_operator_can_cancel_a_running_bulk_run_but_is_not_offered_the_cancel_of_an_update()
    {
        _hub.FleetActions.Add(Samples.FleetAction("bulk-1", FleetActionKinds.BulkRun, FleetActionStates.Running, ["pc-1"], command: "lock"));
        _hub.FleetActions.Add(Samples.FleetAction("update-1", FleetActionKinds.Update, FleetActionStates.Running, ["pc-1"], target: "2.0.0"));

        IRenderedComponent<FleetActionsPage> cut = await OpenAsync(HubRoleNames.Operator);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=action-bulk-1]")), Patience);

        Assert.Empty(cut.FindAll("[data-testid=action-cancel-update-1]"));
        cut.Find("[data-testid=action-cancel-bulk-1]").Click();

        cut.WaitForAssertion(() => Assert.Equal(["bulk-1"], _hub.CancelledActions.ToArray()), Patience);
        cut.WaitForAssertion(() => Assert.Equal("cancelled", cut.Find("[data-testid=action-bulk-1]").GetAttribute("data-state")), Patience);
    }

    [Fact]
    public async Task An_administrator_can_cancel_a_running_update()
    {
        _hub.FleetActions.Add(Samples.FleetAction("update-1", FleetActionKinds.Update, FleetActionStates.Running, ["pc-1"], target: "2.0.0"));

        IRenderedComponent<FleetActionsPage> cut = await OpenAsync(HubRoleNames.Admin);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=action-cancel-update-1]")), Patience);
        cut.Find("[data-testid=action-cancel-update-1]").Click();

        cut.WaitForAssertion(() => Assert.Equal(["update-1"], _hub.CancelledActions.ToArray()), Patience);
    }

    // ------------------------------------------------------------------
    // The helpers of the page
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("lab", new[] { "lab" })]
    [InlineData(" Lab , home;office  lab ", new[] { "lab", "home", "office" })]
    public void Tags_are_split_trimmed_lower_cased_and_made_unique(string? text, string[] expected)
    {
        Assert.Equal(expected, ActionForms.ParseTags(text).ToArray());
    }

    [Theory]
    [InlineData(null, true, 0)]
    [InlineData("  ", true, 0)]
    [InlineData("{}", true, 0)]
    [InlineData("{\"a\": 1, \"b\": \"x\"}", true, 2)]
    [InlineData("[1]", false, 0)]
    [InlineData("\"text\"", false, 0)]
    [InlineData("{broken", false, 0)]
    public void Variables_must_be_empty_or_a_JSON_object(string? text, bool valid, int count)
    {
        bool ok = ActionForms.TryParseVars(text, out IReadOnlyDictionary<string, System.Text.Json.JsonElement>? vars);

        Assert.Equal(valid, ok);
        Assert.Equal(count, vars?.Count ?? 0);
    }

    [Fact]
    public void The_cancel_of_an_update_needs_an_administrator_and_that_of_a_bulk_run_an_operator()
    {
        Assert.Equal(HubRoleNames.Admin, ActionForms.CancelRole(Samples.FleetAction("u", FleetActionKinds.Update, FleetActionStates.Running, [])));
        Assert.Equal(HubRoleNames.Operator, ActionForms.CancelRole(Samples.FleetAction("b", FleetActionKinds.BulkRun, FleetActionStates.Running, [])));
    }
}
