using Bunit;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Config.Rollout;
using QoffeeAutomat.Hub.Ui.Pages.Profiles;
using QoffeeAutomat.Hub.Ui.Services;
using QoffeeAutomat.Hub.Ui.Tests.Support;

namespace QoffeeAutomat.Hub.Ui.Tests.Config;

/// <summary>
/// The assignment page and the roll-out page (PLAN.md S20, D-030, D-027, T916): which profile reaches which PCs, and how the desired configuration
/// has reached them, with the errors of the PCs and the push that tries again.
/// </summary>
[Trait("Category", "Config")]
public sealed class AssignmentsAndRolloutTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeHubApi _hub = new();
    private readonly FakeBrowserInterop _browser = new();

    public AssignmentsAndRolloutTests()
    {
        HubUiTestSetup.Register(this, _hub, _browser);
        _hub.Agents = new AgentList(
        [
            Samples.Agent("pc-1", "PC 1", "enrolled", "1.2.3", "lab", "office"),
            Samples.Agent("pc-2", "PC 2", "enrolled", "1.2.3", "lab"),
            Samples.Agent("pc-3", "PC 3", "pending", "1.2.3", "newbie"),
        ]);
        _hub.ProfileStore["office"] = Samples.Profile("office", "Office", 5, 2);
        _hub.ProfileStore["draft_only"] = Samples.Profile("draft_only", "Draft only");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    private async Task SignInAsync(string role)
    {
        _hub.SignedIn(role);
        await Services.GetRequiredService<HubSessionState>().RefreshAsync();
    }

    private void SeedAssignments()
    {
        _hub.AssignmentStore.Add(new AssignmentInfo("a3", "office", "agent:pc-1", Samples.Now, "alice"));
        _hub.AssignmentStore.Add(new AssignmentInfo("a1", "office", "all", Samples.Now, "alice"));
        _hub.AssignmentStore.Add(new AssignmentInfo("a2", "draft_only", "tag:lab", Samples.Now, "bob"));
    }

    // Assignments ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_assignments_are_listed_from_the_least_to_the_most_specific_target_with_the_profile_and_its_state()
    {
        await SignInAsync(HubRoleNames.Viewer);
        SeedAssignments();

        IRenderedComponent<AssignmentsPage> cut = Render<AssignmentsPage>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid^=assignment-row-]").Count), Patience);
        string[] targets = [.. cut.FindAll("[data-testid^=assignment-row-]").Select(row => row.GetAttribute("data-target")!)];
        Assert.Equal(["all", "tag:lab", "agent:pc-1"], targets);
        Assert.Contains("PC 1", cut.Find("[data-testid=assignment-row-a3]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Draft only", cut.Find("[data-testid=assignment-row-a2]").TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("[data-testid=assignment-unpublished-a2]"));
        Assert.Empty(cut.FindAll("[data-testid=assignment-unpublished-a1]"));
    }

    [Fact]
    public async Task A_viewer_sees_the_assignments_but_cannot_assign_or_unassign()
    {
        await SignInAsync(HubRoleNames.Viewer);
        SeedAssignments();

        IRenderedComponent<AssignmentsPage> cut = Render<AssignmentsPage>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid^=assignment-row-]").Count), Patience);
        Assert.Empty(cut.FindAll("[data-testid=assignment-form]"));
        Assert.Empty(cut.FindAll("[data-testid^=assignment-delete-]"));
    }

    [Fact]
    public async Task A_profile_is_assigned_to_a_tag_and_the_agents_that_got_a_new_generation_are_named()
    {
        await SignInAsync(HubRoleNames.Admin);
        _hub.AssignmentAgents.Add(Samples.Change("pc-1", "PC 1"));
        _hub.AssignmentAgents.Add(Samples.Change("pc-2", "PC 2"));
        IRenderedComponent<AssignmentsPage> cut = Render<AssignmentsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=assignment-form]")), Patience);
        Assert.True(cut.Find("[data-testid=assignment-create]").HasAttribute("disabled"));

        cut.Find("[data-testid=assignment-profile]").Change("office");
        Assert.True(cut.Find("[data-testid=assignment-create]").HasAttribute("disabled"));
        cut.Find("[data-testid=assignment-tag]").Change("lab");
        Assert.False(cut.Find("[data-testid=assignment-create]").HasAttribute("disabled"));
        cut.Find("[data-testid=assignment-create]").Click();

        cut.WaitForAssertion(() => Assert.Single(_hub.AssignmentRequests), Patience);
        Assert.Equal(("office", "tag:lab"), (_hub.AssignmentRequests[0].ProfileId, _hub.AssignmentRequests[0].Target));
        string notice = cut.Find("[data-testid=assignments-notice]").TextContent;
        Assert.Contains("2", notice, StringComparison.Ordinal);
        Assert.Contains("PC 1, PC 2", notice, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid^=assignment-row-]")), Patience);
    }

    [Fact]
    public async Task The_tags_that_are_offered_are_the_tags_of_the_agents()
    {
        await SignInAsync(HubRoleNames.Admin);
        IRenderedComponent<AssignmentsPage> cut = Render<AssignmentsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=assignment-tag]")), Patience);

        string[] tags = [.. cut.Find("[data-testid=assignment-tag]").QuerySelectorAll("option").Select(option => option.GetAttribute("value")!)];

        Assert.Equal([string.Empty, "lab", "newbie", "office"], tags);
    }

    [Fact]
    public async Task A_profile_is_assigned_to_all_pcs_without_a_value()
    {
        await SignInAsync(HubRoleNames.Admin);
        IRenderedComponent<AssignmentsPage> cut = Render<AssignmentsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=assignment-form]")), Patience);

        cut.Find("[data-testid=assignment-profile]").Change("office");
        cut.Find("[data-testid=assignment-kind]").Change("all");
        Assert.Empty(cut.FindAll("[data-testid=assignment-tag]"));
        Assert.Empty(cut.FindAll("[data-testid=assignment-agent]"));
        cut.Find("[data-testid=assignment-create]").Click();

        cut.WaitForAssertion(() => Assert.Single(_hub.AssignmentRequests), Patience);
        Assert.Equal("all", _hub.AssignmentRequests[0].Target);
        cut.WaitForAssertion(() => Assert.Contains("No PC gets a new configuration", cut.Find("[data-testid=assignments-notice]").TextContent, StringComparison.Ordinal), Patience);
    }

    [Fact]
    public async Task A_profile_is_assigned_to_one_enrolled_pc_and_a_pending_one_is_not_offered()
    {
        await SignInAsync(HubRoleNames.Admin);
        IRenderedComponent<AssignmentsPage> cut = Render<AssignmentsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=assignment-form]")), Patience);

        cut.Find("[data-testid=assignment-profile]").Change("office");
        cut.Find("[data-testid=assignment-kind]").Change("agent");
        string[] agents = [.. cut.Find("[data-testid=assignment-agent]").QuerySelectorAll("option").Select(option => option.GetAttribute("value")!)];
        Assert.Equal([string.Empty, "pc-1", "pc-2"], agents);
        cut.Find("[data-testid=assignment-agent]").Change("pc-2");
        cut.Find("[data-testid=assignment-create]").Click();

        cut.WaitForAssertion(() => Assert.Single(_hub.AssignmentRequests), Patience);
        Assert.Equal("agent:pc-2", _hub.AssignmentRequests[0].Target);
    }

    [Fact]
    public async Task An_assignment_is_removed_after_a_confirmation()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedAssignments();
        IRenderedComponent<AssignmentsPage> cut = Render<AssignmentsPage>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid^=assignment-row-]").Count), Patience);

        cut.Find("[data-testid=assignment-delete-a2]").Click();
        Assert.Empty(_hub.DeletedAssignments);
        cut.Find("[data-testid=assignment-delete-confirm-a2]").Click();

        cut.WaitForAssertion(() => Assert.Equal("a2", Assert.Single(_hub.DeletedAssignments)), Patience);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid^=assignment-row-]").Count), Patience);
    }

    // Roll-out ---------------------------------------------------------------------------------------------------------------------------

    private static AgentConfigStatusInfo Failed()
    {
        return new AgentConfigStatusInfo(
            "pc-2",
            "PC 2",
            true,
            RolloutStates.Failed,
            4,
            4,
            Samples.Now,
            null,
            3,
            Samples.Now,
            4,
            "failed",
            Samples.Now,
            ["command/lock_screen: The action type powershell is not supported."],
            [new DocumentRollout(DocumentKinds.Command, "lock_screen", "invalid", ["The action type powershell is not supported."]), new DocumentRollout(DocumentKinds.Script, "backup.ps1", "applied", [])]);
    }

    private void SeedRollouts()
    {
        AgentConfigStatusInfo undelivered = Samples.Config("pc-3", RolloutStates.Pending, 2, 1) with { Online = false, DeliveryError = "The agent did not answer in time.", PushedGeneration = 2 };
        _hub.Rollouts = new RolloutOverview(
            new RolloutCounts(3, 1, 1, 1, 0, 1),
            [Samples.Config("pc-1", RolloutStates.UpToDate, 4, 4), Failed(), undelivered]);
        _hub.Desired["pc-2"] = new AgentDesiredInfo(
            "pc-2",
            4,
            false,
            new PolicyInfo("locked", "allow"),
            ["office"],
            [new DesiredItemInfo(DocumentKinds.Command, "lock_screen", "h1", "office", 2)],
            [new ConfigWarningInfo("missing_script", DocumentKinds.Command, "lock_screen", "No profile provides the script backup.ps1.", "office", null)],
            Samples.Now);
    }

    [Fact]
    public async Task Every_pc_is_shown_with_its_state_the_generations_and_the_counts()
    {
        await SignInAsync(HubRoleNames.Viewer);
        SeedRollouts();

        IRenderedComponent<RolloutPage> cut = Render<RolloutPage>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid^=rollout-agent-]").Count), Patience);
        Assert.Equal("up_to_date", cut.Find("[data-testid=rollout-agent-pc-1]").GetAttribute("data-state"));
        Assert.Equal("failed", cut.Find("[data-testid=rollout-agent-pc-2]").GetAttribute("data-state"));
        Assert.Equal("pending", cut.Find("[data-testid=rollout-agent-pc-3]").GetAttribute("data-state"));
        Assert.Contains("3 / 4", cut.Find("[data-testid=rollout-agent-pc-2]").TextContent, StringComparison.Ordinal);
        Assert.Contains("1 failed", cut.Find("[data-count=failed]").TextContent, StringComparison.Ordinal);
        Assert.Contains("1 offline", cut.Find("[data-count=offline]").TextContent, StringComparison.Ordinal);
        Assert.Contains("3 PCs", cut.Find("[data-count=total]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_errors_that_a_pc_reported_and_the_error_of_a_delivery_are_shown_per_pc()
    {
        await SignInAsync(HubRoleNames.Viewer);
        SeedRollouts();

        IRenderedComponent<RolloutPage> cut = Render<RolloutPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=rollout-errors-pc-2]")), Patience);
        Assert.Contains("powershell is not supported", cut.Find("[data-testid=rollout-errors-pc-2]").TextContent, StringComparison.Ordinal);
        Assert.Contains("did not answer in time", cut.Find("[data-testid=rollout-delivery-error-pc-3]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.Find("[data-testid=rollout-errors-pc-1]").TextContent.Trim());
    }

    [Fact]
    public async Task The_details_of_a_pc_show_its_documents_and_the_desired_state_with_the_warnings()
    {
        await SignInAsync(HubRoleNames.Viewer);
        SeedRollouts();
        IRenderedComponent<RolloutPage> cut = Render<RolloutPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=rollout-details-pc-2]")), Patience);

        cut.Find("[data-testid=rollout-details-pc-2]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=rollout-desired-profiles]")), Patience);
        string detail = cut.Find("[data-testid=rollout-detail-pc-2]").TextContent;
        Assert.Contains("command/lock_screen", detail, StringComparison.Ordinal);
        Assert.Contains("invalid", detail, StringComparison.Ordinal);
        Assert.Contains("applied", detail, StringComparison.Ordinal);
        Assert.Contains("office", cut.Find("[data-testid=rollout-desired-profiles]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Locked", detail, StringComparison.Ordinal);
        Assert.Contains("backup.ps1", detail, StringComparison.Ordinal);

        cut.Find("[data-testid=rollout-details-pc-2]").Click();
        Assert.Empty(cut.FindAll("[data-testid=rollout-detail-pc-2]"));
    }

    [Theory]
    [InlineData(HubRoleNames.Viewer, false)]
    [InlineData(HubRoleNames.Operator, true)]
    [InlineData(HubRoleNames.Admin, true)]
    public async Task The_push_is_offered_to_operators_and_administrators(string role, bool offered)
    {
        await SignInAsync(role);
        SeedRollouts();

        IRenderedComponent<RolloutPage> cut = Render<RolloutPage>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid^=rollout-agent-]").Count), Patience);
        Assert.Equal(offered, cut.FindAll("[data-testid^=rollout-push-]").Count == 3);
    }

    [Fact]
    public async Task A_push_asks_the_hub_to_send_the_desired_state_again_and_reads_the_status_again()
    {
        await SignInAsync(HubRoleNames.Operator);
        SeedRollouts();
        IRenderedComponent<RolloutPage> cut = Render<RolloutPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=rollout-push-pc-2]")), Patience);
        int readsBefore = _hub.Calls.Count(call => call == nameof(IHubApi.GetRolloutsAsync));

        cut.Find("[data-testid=rollout-push-pc-2]").Click();

        cut.WaitForAssertion(() => Assert.Contains("PushConfigAsync:pc-2", _hub.Calls), Patience);
        cut.WaitForAssertion(() => Assert.Contains("sent to pc-2 again", cut.Find("[data-testid=rollout-notice]").TextContent, StringComparison.Ordinal), Patience);
        Assert.True(_hub.Calls.Count(call => call == nameof(IHubApi.GetRolloutsAsync)) > readsBefore);
    }

    [Fact]
    public async Task A_viewer_that_pushes_anyway_is_refused_by_the_hub_and_the_page_shows_it()
    {
        await SignInAsync(HubRoleNames.Operator);
        SeedRollouts();
        IRenderedComponent<RolloutPage> cut = Render<RolloutPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=rollout-push-pc-2]")), Patience);

        // The role was lowered while the page was open.
        _hub.Me = Samples.Session(HubRoleNames.Viewer);
        cut.Find("[data-testid=rollout-push-pc-2]").Click();

        cut.WaitForAssertion(() => Assert.Equal("Your role may not do this.", cut.Find("[data-testid=error-message]").TextContent), Patience);
        Assert.Contains("refused:operator", _hub.Calls);
    }

    [Fact]
    public async Task The_roll_out_of_a_profile_shows_the_pcs_it_reaches_with_the_state_of_its_documents()
    {
        await SignInAsync(HubRoleNames.Viewer);
        SeedRollouts();
        _hub.ProfileRollouts["office"] = new ProfileRolloutInfo(
            "office",
            2,
            new RolloutCounts(2, 1, 0, 1, 0, 0),
            [
                new ProfileRolloutAgent("pc-1", "PC 1", true, RolloutStates.UpToDate, 4, 4, [new DocumentRollout(DocumentKinds.Command, "lock_screen", "applied", [])], []),
                new ProfileRolloutAgent("pc-2", "PC 2", true, RolloutStates.Failed, 4, 3, [new DocumentRollout(DocumentKinds.Command, "lock_screen", "invalid", ["bad"])], ["command/lock_screen: bad"]),
            ]);

        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo("http://localhost/rollouts?profile=office");
        IRenderedComponent<RolloutPage> cut = Render<RolloutPage>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid^=rollout-agent-]").Count), Patience);
        Assert.Equal("Office, revision 2", cut.Find("[data-testid=rollout-profile-title]").TextContent);
        Assert.NotEmpty(cut.FindAll("[data-testid=rollout-profile-table]"));
        Assert.Contains("command/lock_screen: bad", cut.Find("[data-testid=rollout-errors-pc-2]").TextContent, StringComparison.Ordinal);
        Assert.Contains("invalid", cut.Find("[data-testid=rollout-agent-pc-2]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(_hub.Calls, call => call == nameof(IHubApi.GetRolloutsAsync));
    }

    [Fact]
    public async Task Choosing_a_profile_in_the_scope_switches_between_the_views()
    {
        await SignInAsync(HubRoleNames.Viewer);
        SeedRollouts();
        _hub.ProfileRollouts["office"] = new ProfileRolloutInfo("office", 2, new RolloutCounts(0, 0, 0, 0, 0, 0), []);
        IRenderedComponent<RolloutPage> cut = Render<RolloutPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=rollout-table]")), Patience);

        cut.Find("[data-testid=rollout-scope]").Change("office");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=rollout-profile-title]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=rollout-table]"));

        cut.Find("[data-testid=rollout-scope]").Change(string.Empty);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=rollout-table]")), Patience);
    }

    [Fact]
    public async Task A_profile_that_is_not_published_has_no_roll_out_and_the_page_says_so_instead_of_failing()
    {
        await SignInAsync(HubRoleNames.Viewer);
        SeedRollouts();

        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo("http://localhost/rollouts?profile=draft_only");
        IRenderedComponent<RolloutPage> cut = Render<RolloutPage>();

        cut.WaitForAssertion(() => Assert.Contains("not published", cut.Find("[data-testid=rollout-notice]").TextContent, StringComparison.Ordinal), Patience);
        Assert.Empty(cut.FindAll("[data-testid=error-display]"));
    }

    [Fact]
    public async Task Without_any_pc_the_page_says_so()
    {
        await SignInAsync(HubRoleNames.Viewer);
        _hub.Rollouts = new RolloutOverview(new RolloutCounts(0, 0, 0, 0, 0, 0), []);

        IRenderedComponent<RolloutPage> cut = Render<RolloutPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=rollout-none]")), Patience);
    }

    [Fact]
    public async Task An_error_of_the_hub_is_shown_with_a_retry()
    {
        await SignInAsync(HubRoleNames.Viewer);
        _hub.Rollouts = null;

        IRenderedComponent<RolloutPage> cut = Render<RolloutPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=error-display]")), Patience);
    }
}
