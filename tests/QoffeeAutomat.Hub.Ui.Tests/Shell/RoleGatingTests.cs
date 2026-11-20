using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Ui.Layout;
using QoffeeAutomat.Hub.Ui.Pages.Agents;
using QoffeeAutomat.Hub.Ui.Pages.Audit;
using QoffeeAutomat.Hub.Ui.Pages.Enrollment;
using QoffeeAutomat.Hub.Ui.Pages.Pairing;
using QoffeeAutomat.Hub.Ui.Pages.Users;
using QoffeeAutomat.Hub.Ui.Services;
using QoffeeAutomat.Hub.Ui.Tests.Support;

namespace QoffeeAutomat.Hub.Ui.Tests.Shell;

/// <summary>
/// Navigation and actions that a role may not use are hidden (PLAN.md S20 "Hub security", T915), and the hub refuses them as well: the fake hub answers
/// 403 like the real one, so the tests can show both that a page does not ask and that it reports a refusal.
/// </summary>
[Trait("Category", "HubUi")]
public sealed class RoleGatingTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeHubApi _hub = new();
    private readonly FakeBrowserInterop _browser = new();

    public RoleGatingTests()
    {
        HubUiTestSetup.Register(this, _hub, _browser);
        _hub.Details["pc-1"] = Samples.Detail(Samples.Agent("pc-1", null, "enrolled", "1.2.3", "office"));
        _hub.Rollouts = Samples.Rollouts(Samples.Config("pc-1"));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    private async Task SignInAsync(string role)
    {
        _hub.SignedIn(role);
        await Services.GetRequiredService<HubSessionState>().RefreshAsync();
    }

    [Theory]
    [InlineData(HubRoleNames.Viewer, "agents,library,profiles,assignments,rollouts,metrics,actions,settings")]
    [InlineData(HubRoleNames.Operator, "agents,library,profiles,assignments,rollouts,metrics,actions,settings")]
    [InlineData(HubRoleNames.Admin, "agents,library,profiles,assignments,rollouts,metrics,actions,enrollment,access,users,tokens,audit,settings")]
    public async Task The_menu_offers_only_the_pages_that_the_role_may_use(string role, string expected)
    {
        await SignInAsync(role);

        IRenderedComponent<HubNavMenu> cut = Render<HubNavMenu>();

        string[] hrefs = [.. cut.FindAll("a").Select(link => link.GetAttribute("href") ?? string.Empty)];
        Assert.Equal(expected.Split(','), hrefs);
    }

    [Fact]
    public async Task The_menu_follows_the_session_when_the_user_signs_out()
    {
        await SignInAsync(HubRoleNames.Admin);
        IRenderedComponent<HubNavMenu> cut = Render<HubNavMenu>();
        Assert.Equal(13, cut.FindAll("a").Count);

        await Services.GetRequiredService<HubSessionState>().LogOutAsync();

        cut.WaitForAssertion(() => Assert.Equal(8, cut.FindAll("a").Count), Patience);
    }

    [Fact]
    public async Task An_administrator_page_shows_a_notice_to_a_viewer_and_the_page_makes_no_call_of_its_own()
    {
        await SignInAsync(HubRoleNames.Viewer);

        AssertGated<UsersPage>();
        AssertGated<TokensPage>();
        AssertGated<QoffeeAutomat.Hub.Ui.Pages.Audit.AuditPage>();
        AssertGated<EnrollmentPage>();
        AssertGated<AccessPage>();

        // Neither a read nor a refused attempt: a page that is hidden does not ask.
        Assert.DoesNotContain(_hub.Calls, call => call.StartsWith("refused", StringComparison.Ordinal));
        Assert.DoesNotContain(_hub.Calls, call => call is nameof(IHubApi.GetUsersAsync) or nameof(IHubApi.GetTokensAsync) or nameof(IHubApi.GetAuditAsync));
    }

    [Fact]
    public async Task An_operator_does_not_get_the_administrator_pages_either()
    {
        await SignInAsync(HubRoleNames.Operator);

        AssertGated<UsersPage>();
        AssertGated<EnrollmentPage>();
    }

    [Fact]
    public async Task An_administrator_gets_the_page_and_its_data()
    {
        await SignInAsync(HubRoleNames.Admin);
        _hub.Users = new UserList([Samples.User("u1", "bob", HubRoleNames.Operator)]);

        IRenderedComponent<UsersPage> cut = Render<UsersPage>();

        cut.WaitForAssertion(() => Assert.Contains("bob", cut.Markup, StringComparison.Ordinal), Patience);
        Assert.Empty(cut.FindAll("[data-testid=role-required]"));
    }

    [Fact]
    public async Task A_refusal_of_the_hub_is_shown_when_the_role_changed_while_the_page_was_open()
    {
        await SignInAsync(HubRoleNames.Admin);
        IRenderedComponent<UsersPage> cut = Render<UsersPage>();
        cut.WaitForAssertion(() => Assert.Contains("user-form", cut.Markup, StringComparison.Ordinal), Patience);

        // An administrator lowered the role of this user; the page has not noticed yet, the hub has.
        _hub.Me = Samples.Session(HubRoleNames.Viewer);
        cut.Find("[data-testid=user-create]").Click();

        cut.WaitForAssertion(() => Assert.Equal("Your role may not do this.", cut.Find("[data-testid=error-message]").TextContent), Patience);
        Assert.Contains("refused:admin", _hub.Calls);
    }

    [Theory]
    [InlineData(HubRoleNames.Viewer, "overview,dashboard,history,logs")]
    [InlineData(HubRoleNames.Operator, "overview,dashboard,commands,history,logs")]
    [InlineData(HubRoleNames.Admin, "overview,dashboard,commands,sensors,metrics,history,logs,secrets")]
    public async Task The_tabs_of_an_agent_are_the_ones_that_the_role_may_open(string role, string expected)
    {
        await SignInAsync(role);

        IRenderedComponent<AgentDetailPage> cut = Render<AgentDetailPage>(parameters => parameters.Add(page => page.AgentId, "pc-1"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=agent-tabs]")), Patience);
        string[] tabs = [.. cut.FindAll("[data-testid^=agent-tab-]").Select(tab => tab.GetAttribute("data-testid")!["agent-tab-".Length..])];
        Assert.Equal(expected.Split(','), tabs);
    }

    [Fact]
    public async Task A_page_that_needs_a_higher_role_shows_a_notice_instead_of_its_content_when_the_address_is_typed()
    {
        await SignInAsync(HubRoleNames.Viewer);

        IRenderedComponent<AgentCommandsPage> cut = Render<AgentCommandsPage>(parameters => parameters.Add(page => page.AgentId, "pc-1"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=role-required]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=agent-content]"));
    }

    [Theory]
    [InlineData(HubRoleNames.Viewer, false, false)]
    [InlineData(HubRoleNames.Operator, true, false)]
    [InlineData(HubRoleNames.Admin, true, true)]
    public async Task The_actions_on_an_agent_are_offered_to_the_roles_that_may_use_them(string role, bool canPush, bool canAdminister)
    {
        await SignInAsync(role);

        IRenderedComponent<AgentDetailPage> cut = Render<AgentDetailPage>(parameters => parameters.Add(page => page.AgentId, "pc-1"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=agent-info]")), Patience);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=config-generation]")), Patience);
        Assert.Equal(canPush, cut.FindAll("[data-testid=config-push]").Count == 1);
        Assert.Equal(canAdminister, cut.FindAll("[data-testid=tags-save]").Count == 1);
        Assert.Equal(canAdminister, cut.FindAll("[data-testid=rotate]").Count == 1);
        Assert.Equal(canAdminister, cut.FindAll("[data-testid=remove]").Count == 1);
    }

    [Fact]
    public async Task The_role_gate_shows_its_content_to_the_role_and_above_only()
    {
        await SignInAsync(HubRoleNames.Operator);

        IRenderedComponent<RoleGate> operatorGate = Render<RoleGate>(parameters => parameters
            .Add(gate => gate.Role, HubRoleNames.Operator)
            .AddChildContent("inside"));
        IRenderedComponent<RoleGate> adminGate = Render<RoleGate>(parameters => parameters
            .Add(gate => gate.Role, HubRoleNames.Admin)
            .AddChildContent("inside"));

        Assert.Contains("inside", operatorGate.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("inside", adminGate.Markup, StringComparison.Ordinal);
        Assert.Contains("needs the role", adminGate.Markup, StringComparison.Ordinal);
    }

    private void AssertGated<TPage>()
        where TPage : IComponent
    {
        IRenderedComponent<TPage> cut = Render<TPage>();
        Assert.Single(cut.FindAll("[data-testid=role-required]"));
    }
}
