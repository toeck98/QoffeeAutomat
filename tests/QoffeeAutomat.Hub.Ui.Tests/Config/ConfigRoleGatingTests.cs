using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Metrics;
using QoffeeAutomat.Hub.Ui.Layout;
using QoffeeAutomat.Hub.Ui.Pages.Library;
using QoffeeAutomat.Hub.Ui.Pages.Metrics;
using QoffeeAutomat.Hub.Ui.Pages.Profiles;
using QoffeeAutomat.Hub.Ui.Services;
using QoffeeAutomat.Hub.Ui.Tests.Support;

namespace QoffeeAutomat.Hub.Ui.Tests.Config;

/// <summary>
/// Role gating of the configuration and metric pages (PLAN.md S20 "Hub security", T916): a viewer reads everything and changes nothing, an operator also
/// publishes, rolls back and pushes, only an administrator changes the library, the drafts and the assignments. The UI does not offer what the role may
/// not do and does not even ask for it; the fake hub refuses with 403 like the real one, so a call that slips through would show up as a refusal.
/// </summary>
[Trait("Category", "Config")]
public sealed class ConfigRoleGatingTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeHubApi _hub = new();
    private readonly FakeBrowserInterop _browser = new();

    public ConfigRoleGatingTests()
    {
        HubUiTestSetup.Register(this, _hub, _browser);
        _hub.Agents = new AgentList([Samples.Agent("pc-1", "PC 1", "enrolled", "1.2.3", "lab")]);
        _hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Command, "lock_screen", "Lock screen", Samples.CommandBody("lock_screen")));
        _hub.ProfileStore["office"] = Samples.Profile("office", "Office", 0, 1, new ProfileItemRef(DocumentKinds.Command, "lock_screen", 1));
        _hub.ProfileRevisionStore["office"] = [new ProfileRevisionSummary(1, Samples.Now, "alice", null, true)];
        _hub.AssignmentStore.Add(new AssignmentInfo("a1", "office", "tag:lab", Samples.Now, "alice"));
        _hub.Rollouts = Samples.Rollouts(Samples.Config("pc-1"));
        _hub.AgentMetrics["pc-1"] = new AgentMetricList("pc-1", [new LatestMetric("pc-1", "cpu.total", "number", "%", Samples.Now, 5, null)]);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    private async Task SignInAsync(string role)
    {
        _hub.SignedIn(role);
        await Services.GetRequiredService<HubSessionState>().RefreshAsync();
    }

    private IRenderedComponent<TPage> RenderLoaded<TPage>(string loadedTestId, Action<ComponentParameterCollectionBuilder<TPage>>? parameters = null)
        where TPage : IComponent
    {
        IRenderedComponent<TPage> cut = parameters is null ? Render<TPage>() : Render(parameters);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll($"[data-testid={loadedTestId}]")), Patience);
        return cut;
    }

    [Fact]
    public async Task A_viewer_reads_every_configuration_page_and_nothing_is_offered_to_change_and_nothing_is_refused()
    {
        await SignInAsync(HubRoleNames.Viewer);

        IRenderedComponent<LibraryPage> library = RenderLoaded<LibraryPage>("library-table");
        IRenderedComponent<ProfilesPage> profiles = RenderLoaded<ProfilesPage>("profiles-table");
        IRenderedComponent<ProfileEditorPage> profile = RenderLoaded<ProfileEditorPage>("profile-general", parameters => parameters.Add(page => page.Id, "office"));
        IRenderedComponent<AssignmentsPage> assignments = RenderLoaded<AssignmentsPage>("assignments-table");
        IRenderedComponent<RolloutPage> rollout = RenderLoaded<RolloutPage>("rollout-table");
        IRenderedComponent<FleetMetricsPage> metrics = RenderLoaded<FleetMetricsPage>("metrics-controls");

        Assert.Empty(library.FindAll("[data-testid^=library-new-], [data-testid^=library-edit-], [data-testid^=library-delete-]"));
        Assert.Empty(profiles.FindAll("[data-testid=profile-create-form], [data-testid^=profile-delete-]"));
        Assert.Empty(profile.FindAll("[data-testid=profile-save], [data-testid=profile-publish], [data-testid^=profile-rollback-], [data-testid=profile-add]"));
        Assert.Empty(assignments.FindAll("[data-testid=assignment-form], [data-testid^=assignment-delete-]"));
        Assert.Empty(rollout.FindAll("[data-testid^=rollout-push-]"));
        Assert.Empty(metrics.FindAll("[data-testid=role-required]"));
        Assert.DoesNotContain(_hub.Calls, call => call.StartsWith("refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_operator_publishes_rolls_back_and_pushes_but_does_not_edit()
    {
        await SignInAsync(HubRoleNames.Operator);

        IRenderedComponent<LibraryPage> library = RenderLoaded<LibraryPage>("library-table");
        IRenderedComponent<ProfileEditorPage> profile = RenderLoaded<ProfileEditorPage>("profile-general", parameters => parameters.Add(page => page.Id, "office"));
        IRenderedComponent<AssignmentsPage> assignments = RenderLoaded<AssignmentsPage>("assignments-table");
        IRenderedComponent<RolloutPage> rollout = RenderLoaded<RolloutPage>("rollout-table");

        Assert.Empty(library.FindAll("[data-testid^=library-new-], [data-testid^=library-edit-], [data-testid^=library-delete-]"));
        Assert.NotEmpty(profile.FindAll("[data-testid=profile-publish]"));
        Assert.Empty(profile.FindAll("[data-testid=profile-save], [data-testid=profile-add]"));
        Assert.Empty(assignments.FindAll("[data-testid=assignment-form]"));
        Assert.NotEmpty(rollout.FindAll("[data-testid^=rollout-push-]"));
        Assert.DoesNotContain(_hub.Calls, call => call.StartsWith("refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_administrator_is_offered_everything()
    {
        await SignInAsync(HubRoleNames.Admin);

        IRenderedComponent<LibraryPage> library = RenderLoaded<LibraryPage>("library-table");
        IRenderedComponent<ProfilesPage> profiles = RenderLoaded<ProfilesPage>("profiles-table");
        IRenderedComponent<ProfileEditorPage> profile = RenderLoaded<ProfileEditorPage>("profile-general", parameters => parameters.Add(page => page.Id, "office"));
        IRenderedComponent<AssignmentsPage> assignments = RenderLoaded<AssignmentsPage>("assignments-table");

        Assert.NotEmpty(library.FindAll("[data-testid^=library-new-]"));
        Assert.NotEmpty(library.FindAll("[data-testid^=library-delete-]"));
        Assert.NotEmpty(profiles.FindAll("[data-testid=profile-create-form]"));
        Assert.NotEmpty(profile.FindAll("[data-testid=profile-save]"));
        Assert.NotEmpty(profile.FindAll("[data-testid=profile-publish]"));
        Assert.NotEmpty(assignments.FindAll("[data-testid=assignment-form]"));
    }

    [Theory]
    [InlineData(HubRoleNames.Viewer)]
    [InlineData(HubRoleNames.Operator)]
    public async Task The_editors_of_the_library_are_gated_to_administrators_when_the_address_is_typed(string role)
    {
        await SignInAsync(role);

        IRenderedComponent<LibraryCommandEditorPage> command = Render<LibraryCommandEditorPage>();
        IRenderedComponent<LibrarySensorEditorPage> sensor = Render<LibrarySensorEditorPage>();
        IRenderedComponent<LibraryDocumentEditorPage> document = Render<LibraryDocumentEditorPage>(parameters => parameters.Add(page => page.Kind, DocumentKinds.Script));

        Assert.Single(command.FindAll("[data-testid=role-required]"));
        Assert.Single(sensor.FindAll("[data-testid=role-required]"));
        Assert.Single(document.FindAll("[data-testid=role-required]"));
        Assert.DoesNotContain(_hub.Calls, call => call.StartsWith("refused", StringComparison.Ordinal));
        Assert.DoesNotContain(_hub.Calls, call => call.StartsWith("SaveLibraryDocumentAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_menu_links_to_the_configuration_pages_with_addresses_below_the_base_address()
    {
        await SignInAsync(HubRoleNames.Viewer);

        IRenderedComponent<HubNavMenu> cut = Render<HubNavMenu>();

        string[] hrefs = [.. cut.FindAll("a").Select(link => link.GetAttribute("href") ?? string.Empty)];
        foreach (string expected in new[] { "library", "profiles", "assignments", "rollouts", "metrics" })
        {
            Assert.Contains(expected, hrefs);
        }

        Assert.All(hrefs, href => Assert.False(href.StartsWith('/'), href + " would leave the path prefix of Home Assistant ingress"));
    }

    [Fact]
    public async Task A_role_lowered_while_a_page_is_open_is_refused_by_the_hub_and_the_page_shows_the_refusal()
    {
        await SignInAsync(HubRoleNames.Admin);
        IRenderedComponent<AssignmentsPage> cut = RenderLoaded<AssignmentsPage>("assignments-table");

        _hub.Me = Samples.Session(HubRoleNames.Viewer);
        cut.Find("[data-testid=assignment-delete-a1]").Click();
        cut.Find("[data-testid=assignment-delete-confirm-a1]").Click();

        cut.WaitForAssertion(() => Assert.Equal("Your role may not do this.", cut.Find("[data-testid=error-message]").TextContent), Patience);
        Assert.Contains("refused:admin", _hub.Calls);
        Assert.Empty(_hub.DeletedAssignments);
    }
}
