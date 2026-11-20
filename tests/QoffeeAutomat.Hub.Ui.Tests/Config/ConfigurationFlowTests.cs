using Bunit;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Config.Rollout;
using QoffeeAutomat.Hub.Ui.Pages.Library;
using QoffeeAutomat.Hub.Ui.Pages.Profiles;
using QoffeeAutomat.Hub.Ui.Services;
using QoffeeAutomat.Hub.Ui.Tests.Support;

namespace QoffeeAutomat.Hub.Ui.Tests.Config;

/// <summary>
/// The acceptance scenario of T916 (PLAN.md S20): a user creates a command in the library, puts it in a profile, assigns the profile to a tag, publishes
/// it after the confirmation, and sees it applied on the tagged agents, all with the pages of the hub UI and without any other tool. The pages run
/// against one fake hub that keeps what they save.
/// </summary>
[Trait("Category", "Config")]
public sealed class ConfigurationFlowTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeHubApi _hub = new();
    private readonly FakeBrowserInterop _browser = new();

    public ConfigurationFlowTests()
    {
        HubUiTestSetup.Register(this, _hub, _browser);
        _hub.Agents = new AgentList(
        [
            Samples.Agent("pc-1", "PC 1", "enrolled", "1.2.3", "lab"),
            Samples.Agent("pc-2", "PC 2", "enrolled", "1.2.3", "lab"),
            Samples.Agent("pc-3", "PC 3", "enrolled", "1.2.3", "office"),
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

    [Fact]
    public async Task A_command_from_the_library_reaches_the_tagged_agents_through_a_profile_an_assignment_and_a_confirmed_publish()
    {
        await SignInAsync(HubRoleNames.Admin);

        // 1. The command is created in the library with the editor of the component library.
        IRenderedComponent<LibraryCommandEditorPage> commandEditor = Render<LibraryCommandEditorPage>();
        commandEditor.WaitForAssertion(() => Assert.NotEmpty(commandEditor.FindAll("[data-testid=field-name] input")), Patience);
        commandEditor.Find("[data-testid=field-name] input").Input("Lock screen");
        commandEditor.Find("[data-testid=code-editor] textarea").Input("rundll32.exe user32.dll,LockWorkStation");
        commandEditor.Find("[data-testid=editor-save]").Click();
        commandEditor.WaitForAssertion(() => Assert.NotEmpty(commandEditor.FindAll("[data-testid=editor-saved]")), Patience);
        Assert.Contains(_hub.LibraryDocuments, document => document.Kind == DocumentKinds.Command && document.Id == "lock_screen");

        // 2. A profile is created.
        IRenderedComponent<ProfilesPage> profiles = Render<ProfilesPage>();
        profiles.WaitForAssertion(() => Assert.NotEmpty(profiles.FindAll("[data-testid=profile-create-form]")), Patience);
        profiles.Find("[data-testid=profile-new-id] input").Input("lab_pcs");
        profiles.Find("[data-testid=profile-new-name] input").Input("Lab PCs");
        profiles.Find("[data-testid=profile-create]").Click();
        profiles.WaitForAssertion(() => Assert.Single(_hub.ProfileCreations), Patience);

        // 3. The command is put in the profile and the draft is saved.
        IRenderedComponent<ProfileEditorPage> editor = Render<ProfileEditorPage>(parameters => parameters.Add(page => page.Id, "lab_pcs"));
        editor.WaitForAssertion(() => Assert.NotEmpty(editor.FindAll("[data-testid=profile-title]")), Patience);
        editor.Find("[data-testid=profile-add-document-select]").Change("lock_screen");
        editor.Find("[data-testid=profile-add]").Click();
        editor.Find("[data-testid=profile-save]").Click();
        editor.WaitForAssertion(() => Assert.Single(_hub.ProfileUpdates), Patience);
        ProfileItemRef item = Assert.Single(_hub.ProfileUpdates[0].Request.Items!);
        Assert.Equal((DocumentKinds.Command, "lock_screen"), (item.Kind, item.Id));

        // 4. The profile is assigned to the tag. Nothing is delivered yet: the profile is not published.
        IRenderedComponent<AssignmentsPage> assignments = Render<AssignmentsPage>();
        assignments.WaitForAssertion(() => Assert.NotEmpty(assignments.FindAll("[data-testid=assignment-form]")), Patience);
        assignments.Find("[data-testid=assignment-profile]").Change("lab_pcs");
        assignments.Find("[data-testid=assignment-tag]").Change("lab");
        assignments.Find("[data-testid=assignment-create]").Click();
        assignments.WaitForAssertion(() => Assert.Single(_hub.AssignmentRequests), Patience);
        Assert.Equal(("lab_pcs", "tag:lab"), (_hub.AssignmentRequests[0].ProfileId, _hub.AssignmentRequests[0].Target));
        assignments.WaitForAssertion(() => Assert.NotEmpty(assignments.FindAll("[data-testid^=assignment-unpublished-]")), Patience);

        // 5. The publish dialog names the two tagged agents and the added command; the third agent is not affected. Confirming publishes.
        var added = new DocumentChange(DocumentKinds.Command, "lock_screen", "added", null, "hash-1");
        _hub.Preview = new PublishPreview("lab_pcs", 1, true, false, false, [], false, [Samples.Change("pc-1", "PC 1", 0, 1, added), Samples.Change("pc-2", "PC 2", 0, 1, added)]);
        editor.Find("[data-testid=profile-publish]").Click();
        editor.WaitForAssertion(() => Assert.NotEmpty(editor.FindAll("[data-testid=publish-confirmation]")), Patience);
        Assert.Equal(2, editor.FindAll("[data-testid^=publish-agent-]").Count);
        Assert.Empty(editor.FindAll("[data-testid=publish-agent-pc-3]"));
        Assert.Empty(_hub.Published);
        editor.Find("[data-testid=publish-confirm]").Click();
        editor.WaitForAssertion(() => Assert.Single(_hub.Published), Patience);
        editor.WaitForAssertion(() => Assert.Contains("Revision 1 in effect", editor.Find("[data-testid=profile-active]").TextContent, StringComparison.Ordinal), Patience);

        // 6. The agents report that they applied the generation, and the roll-out page shows it.
        _hub.ProfileRollouts["lab_pcs"] = new ProfileRolloutInfo(
            "lab_pcs",
            1,
            new RolloutCounts(2, 2, 0, 0, 0, 0),
            [
                new ProfileRolloutAgent("pc-1", "PC 1", true, RolloutStates.UpToDate, 1, 1, [new DocumentRollout(DocumentKinds.Command, "lock_screen", "applied", [])], []),
                new ProfileRolloutAgent("pc-2", "PC 2", true, RolloutStates.UpToDate, 1, 1, [new DocumentRollout(DocumentKinds.Command, "lock_screen", "applied", [])], []),
            ]);
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo("http://localhost/rollouts?profile=lab_pcs");
        IRenderedComponent<RolloutPage> rollout = Render<RolloutPage>();
        rollout.WaitForAssertion(() => Assert.Equal(2, rollout.FindAll("[data-testid^=rollout-agent-]").Count), Patience);
        Assert.All(rollout.FindAll("[data-testid^=rollout-agent-]"), row => Assert.Equal("up_to_date", row.GetAttribute("data-state")));
        Assert.Contains("lock_screen", rollout.Find("[data-testid=rollout-agent-pc-1]").TextContent, StringComparison.Ordinal);
        Assert.Contains("2 up to date", rollout.Find("[data-count=up_to_date]").TextContent, StringComparison.Ordinal);
    }
}
