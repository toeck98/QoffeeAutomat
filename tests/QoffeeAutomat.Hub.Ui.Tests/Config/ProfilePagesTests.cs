using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Ui.Pages.Profiles;
using QoffeeAutomat.Hub.Ui.Services;
using QoffeeAutomat.Hub.Ui.Tests.Support;

namespace QoffeeAutomat.Hub.Ui.Tests.Config;

/// <summary>
/// The profile pages (PLAN.md S20, D-030, T916): the list, and the editor with the draft (name, policy, documents pinned at revisions, variables),
/// the publish with a confirmation that shows the affected agents and their differences, and the roll-back to an earlier revision.
/// </summary>
[Trait("Category", "Config")]
public sealed class ProfilePagesTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeHubApi _hub = new();
    private readonly FakeBrowserInterop _browser = new();

    public ProfilePagesTests()
    {
        HubUiTestSetup.Register(this, _hub, _browser);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    private BunitNavigationManager Navigation => Services.GetRequiredService<BunitNavigationManager>();

    private async Task SignInAsync(string role)
    {
        _hub.SignedIn(role);
        await Services.GetRequiredService<HubSessionState>().RefreshAsync();
    }

    private void SeedProfile(int? active = null)
    {
        _hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Command, "lock_screen", "Lock screen", Samples.CommandBody("lock_screen"), revision: 3));
        _hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Command, "reboot", "Reboot", Samples.CommandBody("reboot", "Reboot")));
        _hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Script, "backup.ps1", null, Samples.Text("Get-Date")));
        _hub.ProfileStore["office"] = Samples.Profile("office", "Office", 5, active, new ProfileItemRef(DocumentKinds.Command, "lock_screen", 2));
        if (active is { } revision)
        {
            _hub.ProfileRevisionStore["office"] = [.. Enumerable.Range(1, revision).Reverse().Select(number => new ProfileRevisionSummary(number, Samples.Now.AddDays(-number), "alice", number == 1 ? "first" : null, number == revision))];
        }
    }

    private IRenderedComponent<ProfileEditorPage> RenderEditor()
    {
        IRenderedComponent<ProfileEditorPage> cut = Render<ProfileEditorPage>(parameters => parameters.Add(page => page.Id, "office"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=profile-title]")), Patience);
        return cut;
    }

    // The list ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_list_shows_name_priority_policy_documents_and_the_revision_in_effect()
    {
        await SignInAsync(HubRoleNames.Viewer);
        SeedProfile(active: 2);
        _hub.ProfileStore["lab"] = Samples.Profile("lab", "Lab");

        IRenderedComponent<ProfilesPage> cut = Render<ProfilesPage>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid^=profile-row-]").Count), Patience);
        Assert.Contains("Office", cut.Find("[data-testid=profile-row-office]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Revision 2 in effect", cut.Find("[data-testid=profile-active-office]").TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("[data-testid=profile-unpublished-lab]"));
        Assert.Equal("profiles/office", cut.Find("[data-testid=profile-open-office]").GetAttribute("href"));
    }

    [Fact]
    public async Task A_published_profile_with_a_newer_draft_is_marked()
    {
        await SignInAsync(HubRoleNames.Viewer);
        SeedProfile(active: 1);
        _hub.ProfileStore["office"] = _hub.ProfileStore["office"] with { HasUnpublishedChanges = true };

        IRenderedComponent<ProfilesPage> cut = Render<ProfilesPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=profile-changes-office]")), Patience);
    }

    [Fact]
    public async Task An_administrator_creates_a_profile_with_an_id_and_a_name_and_lands_in_its_editor()
    {
        await SignInAsync(HubRoleNames.Admin);
        IRenderedComponent<ProfilesPage> cut = Render<ProfilesPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=profile-create-form]")), Patience);

        cut.Find("[data-testid=profile-new-id] input").Input("lab_pcs");
        cut.Find("[data-testid=profile-new-name] input").Input("  Lab PCs ");
        cut.Find("[data-testid=profile-create]").Click();

        cut.WaitForAssertion(() => Assert.Single(_hub.ProfileCreations), Patience);
        Assert.Equal("lab_pcs", _hub.ProfileCreations[0].Id);
        Assert.Equal("Lab PCs", _hub.ProfileCreations[0].Name);
        cut.WaitForAssertion(() => Assert.EndsWith("/profiles/lab_pcs", Navigation.Uri, StringComparison.Ordinal), Patience);
    }

    [Fact]
    public async Task A_profile_without_a_name_or_with_a_bad_id_is_not_sent()
    {
        await SignInAsync(HubRoleNames.Admin);
        IRenderedComponent<ProfilesPage> cut = Render<ProfilesPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=profile-create-form]")), Patience);

        cut.Find("[data-testid=profile-new-id] input").Input("Bad Id");
        cut.Find("[data-testid=profile-new-name] input").Input("Lab");
        cut.Find("[data-testid=profile-create]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=error-display]")), Patience);
        Assert.Empty(_hub.ProfileCreations);
    }

    [Fact]
    public async Task A_profile_is_deleted_after_a_confirmation_that_warns_about_the_assignments()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedProfile();
        IRenderedComponent<ProfilesPage> cut = Render<ProfilesPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=profile-delete-office]")), Patience);

        cut.Find("[data-testid=profile-delete-office]").Click();
        Assert.NotEmpty(cut.FindAll("[data-testid=profile-delete-warning]"));
        Assert.Empty(_hub.DeletedProfiles);
        cut.Find("[data-testid=profile-delete-confirm-office]").Click();

        cut.WaitForAssertion(() => Assert.Equal("office", Assert.Single(_hub.DeletedProfiles)), Patience);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=profile-row-office]")), Patience);
    }

    [Theory]
    [InlineData(HubRoleNames.Viewer)]
    [InlineData(HubRoleNames.Operator)]
    public async Task Below_administrator_a_profile_cannot_be_created_or_deleted(string role)
    {
        await SignInAsync(role);
        SeedProfile();

        IRenderedComponent<ProfilesPage> cut = Render<ProfilesPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=profile-row-office]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=profile-create-form]"));
        Assert.Empty(cut.FindAll("[data-testid=profile-delete-office]"));
    }

    // The editor: the draft --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_editor_shows_the_draft_with_its_documents_pinned_and_a_newer_revision_of_the_library_flagged()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedProfile(active: 1);

        IRenderedComponent<ProfileEditorPage> cut = RenderEditor();

        Assert.Equal("Office", cut.Find("[data-testid=profile-title]").TextContent);
        Assert.Contains("Revision 1 in effect", cut.Find("[data-testid=profile-active]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Revision 2", cut.Find("[data-testid=profile-item-command-lock_screen]").TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("[data-testid=profile-item-newer-command-lock_screen]"));
        Assert.Contains("Lock screen", cut.Find("[data-testid=profile-item-command-lock_screen]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_changed_name_marks_the_draft_unsaved_and_blocks_publishing_until_it_is_saved()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedProfile(active: 1);
        IRenderedComponent<ProfileEditorPage> cut = RenderEditor();
        Assert.Empty(cut.FindAll("[data-testid=profile-dirty]"));
        Assert.True(cut.Find("[data-testid=profile-save]").HasAttribute("disabled"));

        cut.Find("[data-testid=profile-name] input").Input("Office PCs");

        Assert.NotEmpty(cut.FindAll("[data-testid=profile-dirty]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=profile-save-first]"));
        Assert.True(cut.Find("[data-testid=profile-publish]").HasAttribute("disabled"));
        Assert.False(cut.Find("[data-testid=profile-save]").HasAttribute("disabled"));

        cut.Find("[data-testid=profile-save]").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=profile-dirty]")), Patience);
        (string id, ProfileDraftRequest request) = Assert.Single(_hub.ProfileUpdates);
        Assert.Equal("office", id);
        Assert.Equal("Office PCs", request.Name);
        Assert.Equal(5, request.Priority);
        Assert.Contains("No PC is affected", cut.Find("[data-testid=profile-notice]").TextContent, StringComparison.Ordinal);
        Assert.False(cut.Find("[data-testid=profile-publish]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task The_policy_and_the_os_filter_are_chosen_with_selects_and_sent_as_the_draft()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedProfile();
        IRenderedComponent<ProfileEditorPage> cut = RenderEditor();

        cut.Find("[data-testid=profile-local-config]").Change("locked");
        cut.Find("[data-testid=profile-direct-link]").Change("deny");
        cut.Find("[data-testid=profile-os]").Change("windows");
        cut.Find("[data-testid=profile-save]").Click();

        cut.WaitForAssertion(() => Assert.Single(_hub.ProfileUpdates), Patience);
        ProfileDraftRequest request = _hub.ProfileUpdates[0].Request;
        Assert.Equal("locked", request.Policy!.LocalConfig);
        Assert.Equal("deny", request.Policy.DirectLink);
        Assert.Equal("windows", request.OsFilter);
    }

    [Fact]
    public async Task A_policy_can_be_left_to_the_other_profiles()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedProfile();
        IRenderedComponent<ProfileEditorPage> cut = RenderEditor();

        string[] offered = [.. cut.Find("[data-testid=profile-local-config]").QuerySelectorAll("option").Select(option => option.GetAttribute("value")!)];

        Assert.Equal(["inherit", "allow", "locked"], offered);
        Assert.Contains("inherit", cut.Find("[data-testid=profile-local-config] option[selected]").GetAttribute("value"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_document_of_the_library_is_added_removed_and_pinned_on_save()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedProfile();
        IRenderedComponent<ProfileEditorPage> cut = RenderEditor();

        // The documents that are in the profile already are not offered again.
        string[] offered = [.. cut.Find("[data-testid=profile-add-document-select]").QuerySelectorAll("option").Select(option => option.GetAttribute("value")!)];
        Assert.Equal([string.Empty, "reboot"], offered);

        cut.Find("[data-testid=profile-add-document-select]").Change("reboot");
        cut.Find("[data-testid=profile-add]").Click();
        Assert.NotEmpty(cut.FindAll("[data-testid=profile-item-command-reboot]"));

        cut.Find("[data-testid=profile-add-kind]").Change(DocumentKinds.Script);
        cut.Find("[data-testid=profile-add-document-select]").Change("backup.ps1");
        cut.Find("[data-testid=profile-add]").Click();
        cut.Find("[data-testid=profile-item-remove-command-lock_screen]").Click();
        cut.Find("[data-testid=profile-save]").Click();

        cut.WaitForAssertion(() => Assert.Single(_hub.ProfileUpdates), Patience);
        ProfileItemRef[] items = [.. _hub.ProfileUpdates[0].Request.Items!];
        Assert.Equal(["command:reboot", "script:backup.ps1"], items.Select(item => item.Kind + ":" + item.Id).ToArray());

        // New documents are sent without a revision; the hub pins the current one, which the editor then shows.
        Assert.All(items, item => Assert.Null(item.Revision));
        cut.WaitForAssertion(() => Assert.Contains("Revision 1", cut.Find("[data-testid=profile-item-command-reboot]").TextContent, StringComparison.Ordinal), Patience);
    }

    [Fact]
    public async Task A_pinned_document_can_be_moved_to_the_newest_revision()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedProfile();
        IRenderedComponent<ProfileEditorPage> cut = RenderEditor();

        cut.Find("[data-testid=profile-item-newest-command-lock_screen]").Click();

        Assert.NotEmpty(cut.FindAll("[data-testid=profile-dirty]"));
        cut.Find("[data-testid=profile-save]").Click();
        cut.WaitForAssertion(() => Assert.Single(_hub.ProfileUpdates), Patience);
        Assert.Null(_hub.ProfileUpdates[0].Request.Items![0].Revision);
        cut.WaitForAssertion(() => Assert.Contains("Revision 3", cut.Find("[data-testid=profile-item-command-lock_screen]").TextContent, StringComparison.Ordinal), Patience);
    }

    [Fact]
    public async Task Variables_are_added_edited_and_removed()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedProfile();
        IRenderedComponent<ProfileEditorPage> cut = RenderEditor();

        cut.Find("[data-testid=profile-variable-add]").Click();
        IReadOnlyList<AngleSharp.Dom.IElement> inputs = cut.FindAll("[data-testid=profile-variable-row] input");
        inputs[0].Input("SITE");
        inputs[1].Input("berlin");
        cut.Find("[data-testid=profile-variable-add]").Click();
        cut.FindAll("[data-testid=profile-variable-remove]")[1].Click();
        cut.Find("[data-testid=profile-save]").Click();

        cut.WaitForAssertion(() => Assert.Single(_hub.ProfileUpdates), Patience);
        KeyValuePair<string, string> variable = Assert.Single(_hub.ProfileUpdates[0].Request.Variables!);
        Assert.Equal("SITE", variable.Key);
        Assert.Equal("berlin", variable.Value);
    }

    [Fact]
    public async Task A_draft_with_a_bad_variable_name_is_not_sent_and_the_problem_is_listed()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedProfile();
        IRenderedComponent<ProfileEditorPage> cut = RenderEditor();

        cut.Find("[data-testid=profile-variable-add]").Click();
        cut.FindAll("[data-testid=profile-variable-row] input")[0].Input("1bad");
        cut.Find("[data-testid=profile-save]").Click();

        Assert.Contains("starts with a letter", cut.Find("[data-testid=profile-problems]").TextContent, StringComparison.Ordinal);
        Assert.Empty(_hub.ProfileUpdates);
    }

    [Fact]
    public async Task A_profile_that_does_not_exist_shows_the_error_of_the_hub()
    {
        await SignInAsync(HubRoleNames.Admin);

        IRenderedComponent<ProfileEditorPage> cut = Render<ProfileEditorPage>(parameters => parameters.Add(page => page.Id, "missing"));

        cut.WaitForAssertion(() => Assert.Equal("There is no such profile.", cut.Find("[data-testid=error-message]").TextContent), Patience);
        Assert.Empty(cut.FindAll("[data-testid=profile-title]"));
    }

    // The editor: roles ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(HubRoleNames.Viewer, false, false, false)]
    [InlineData(HubRoleNames.Operator, false, true, true)]
    [InlineData(HubRoleNames.Admin, true, true, true)]
    public async Task The_actions_are_offered_to_the_roles_that_may_use_them(string role, bool canEdit, bool canPublish, bool canRollback)
    {
        await SignInAsync(role);
        SeedProfile(active: 2);

        IRenderedComponent<ProfileEditorPage> cut = RenderEditor();

        Assert.Equal(canEdit, cut.FindAll("[data-testid=profile-save]").Count == 1);
        Assert.Equal(canEdit, cut.FindAll("[data-testid=profile-add]").Count == 1);
        Assert.Equal(canEdit, cut.FindAll("[data-testid=profile-variable-add]").Count == 1);
        Assert.Equal(canEdit, cut.FindAll("[data-testid=profile-item-remove-command-lock_screen]").Count == 1);
        Assert.Equal(canPublish, cut.FindAll("[data-testid=profile-publish]").Count == 1);
        Assert.Equal(canRollback, cut.FindAll("[data-testid=profile-rollback-1]").Count == 1);
        Assert.Empty(cut.FindAll("[data-testid=profile-rollback-2]"));
        Assert.Equal(!canEdit, cut.Find("[data-testid=profile-name] input").HasAttribute("disabled"));
    }

    // Publish ----------------------------------------------------------------------------------------------------------------------------

    private static PublishPreview PreviewWith(bool systemScripts = false, params AgentChange[] agents)
    {
        return new PublishPreview("office", 1, true, false, systemScripts, systemScripts ? ["lock_screen"] : [], systemScripts, agents);
    }

    [Fact]
    public async Task The_publish_dialog_shows_the_affected_agents_and_the_differences_and_nothing_is_published_before_the_confirmation()
    {
        await SignInAsync(HubRoleNames.Operator);
        SeedProfile();
        AgentChange change = new(
            "pc-1",
            "PC 1",
            1,
            2,
            [new DocumentChange(DocumentKinds.Command, "lock_screen", "added", null, "h1"), new DocumentChange(DocumentKinds.Script, "backup.ps1", "changed", "h2", "h3"), new DocumentChange(DocumentKinds.Sensor, "old", "removed", "h4", null)],
            [new PolicyDifference("localConfig", "allow", "locked")],
            [new ConfigWarningInfo("overridden", DocumentKinds.Command, "lock_screen", "lock_screen of lab is replaced by office.", "office", "lab")]);
        _hub.Preview = PreviewWith(false, change, Samples.Change("pc-2", "PC 2", 4, 5, new DocumentChange(DocumentKinds.Command, "lock_screen", "added", null, "h1")));
        IRenderedComponent<ProfileEditorPage> cut = RenderEditor();

        cut.Find("[data-testid=profile-publish]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=publish-confirmation]")), Patience);
        Assert.Equal("Publish Office", cut.Find("[data-testid=publish-title]").TextContent);
        Assert.Contains("creates revision 1", cut.Find("[data-testid=publish-summary]").TextContent, StringComparison.Ordinal);
        Assert.Contains("PCs that get a new configuration: 2", cut.Find("[data-testid=publish-summary]").TextContent, StringComparison.Ordinal);
        Assert.Equal(2, cut.FindAll("[data-testid^=publish-agent-]").Count);
        string first = cut.Find("[data-testid=publish-agent-pc-1]").TextContent;
        Assert.Contains("PC 1", first, StringComparison.Ordinal);
        Assert.Contains("Generation 1 to 2", first, StringComparison.Ordinal);
        string[] changes = [.. cut.FindAll("[data-testid=publish-documents-pc-1] li").Select(item => item.GetAttribute("data-change")!)];
        Assert.Equal(["added", "changed", "removed"], changes);
        Assert.Contains("Policy localConfig: allow to locked", cut.Find("[data-testid=publish-policy-pc-1]").TextContent, StringComparison.Ordinal);
        Assert.Contains("replaced by office", cut.Find("[data-testid=publish-warning-pc-1]").TextContent, StringComparison.Ordinal);

        // Only the preview was asked for.
        Assert.Equal(("office", (int?)null), Assert.Single(_hub.Previews));
        Assert.Empty(_hub.Published);
    }

    [Fact]
    public async Task Confirming_publishes_the_draft_with_the_note_and_the_page_shows_the_revision_in_effect()
    {
        await SignInAsync(HubRoleNames.Operator);
        SeedProfile();
        _hub.Preview = PreviewWith(false, Samples.Change("pc-1", "PC 1"));
        IRenderedComponent<ProfileEditorPage> cut = RenderEditor();
        cut.Find("[data-testid=profile-publish]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=publish-confirmation]")), Patience);

        cut.Find("[data-testid=publish-note] input").Input("first roll-out");
        cut.Find("[data-testid=publish-confirm]").Click();

        cut.WaitForAssertion(() => Assert.Equal(("office", (string?)"first roll-out"), Assert.Single(_hub.Published)), Patience);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=publish-confirmation]")), Patience);
        Assert.Contains("Published revision 1", cut.Find("[data-testid=profile-notice]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Revision 1 in effect", cut.Find("[data-testid=profile-active]").TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("[data-testid=profile-revision-1]"));
    }

    [Fact]
    public async Task Cancelling_the_dialog_publishes_nothing()
    {
        await SignInAsync(HubRoleNames.Operator);
        SeedProfile();
        _hub.Preview = PreviewWith(false, Samples.Change("pc-1", "PC 1"));
        IRenderedComponent<ProfileEditorPage> cut = RenderEditor();
        cut.Find("[data-testid=profile-publish]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=publish-confirmation]")), Patience);

        cut.Find("[data-testid=publish-cancel]").Click();

        Assert.Empty(cut.FindAll("[data-testid=publish-confirmation]"));
        Assert.Empty(_hub.Published);
    }

    [Fact]
    public async Task A_publish_that_changes_nothing_says_so_in_the_dialog()
    {
        await SignInAsync(HubRoleNames.Operator);
        SeedProfile(active: 1);
        _hub.Preview = new PublishPreview("office", 1, false, true, false, [], false, []);
        IRenderedComponent<ProfileEditorPage> cut = RenderEditor();

        cut.Find("[data-testid=profile-publish]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=publish-unchanged]")), Patience);
        Assert.NotEmpty(cut.FindAll("[data-testid=publish-no-agents]"));
    }

    [Fact]
    public async Task Content_with_system_scripts_lists_them_and_an_operator_cannot_confirm_it()
    {
        await SignInAsync(HubRoleNames.Operator);
        SeedProfile();
        _hub.Preview = PreviewWith(true, Samples.Change("pc-1", "PC 1"));
        IRenderedComponent<ProfileEditorPage> cut = RenderEditor();

        cut.Find("[data-testid=profile-publish]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=publish-confirmation]")), Patience);
        Assert.Contains("lock_screen", cut.Find("[data-testid=publish-system-scripts]").TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("[data-testid=publish-admin-required]"));
        Assert.True(cut.Find("[data-testid=publish-confirm]").HasAttribute("disabled"));
        Assert.Empty(_hub.Published);
    }

    [Fact]
    public async Task An_administrator_may_confirm_content_with_system_scripts()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedProfile();
        _hub.Preview = PreviewWith(true, Samples.Change("pc-1", "PC 1"));
        IRenderedComponent<ProfileEditorPage> cut = RenderEditor();
        cut.Find("[data-testid=profile-publish]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=publish-confirmation]")), Patience);

        Assert.NotEmpty(cut.FindAll("[data-testid=publish-system-scripts]"));
        Assert.Empty(cut.FindAll("[data-testid=publish-admin-required]"));
        Assert.False(cut.Find("[data-testid=publish-confirm]").HasAttribute("disabled"));
        cut.Find("[data-testid=publish-confirm]").Click();

        cut.WaitForAssertion(() => Assert.Single(_hub.Published), Patience);
    }

    [Fact]
    public async Task A_refusal_of_the_hub_for_a_publish_is_shown_and_the_dialog_stays_open()
    {
        await SignInAsync(HubRoleNames.Operator);
        SeedProfile();

        // The preview did not know about system scripts, but the hub does when it publishes.
        _hub.Preview = new PublishPreview("office", 1, true, false, false, [], false, [Samples.Change("pc-1", "PC 1")]);
        IRenderedComponent<ProfileEditorPage> cut = RenderEditor();
        cut.Find("[data-testid=profile-publish]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=publish-confirmation]")), Patience);
        _hub.Preview = new PublishPreview("office", 1, true, false, false, [], true, [Samples.Change("pc-1", "PC 1")]);

        cut.Find("[data-testid=publish-confirm]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=error-display]")), Patience);
        Assert.NotEmpty(cut.FindAll("[data-testid=publish-confirmation]"));
        Assert.Empty(_hub.Published);
    }

    // Roll-back --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_roll_back_shows_what_it_would_do_and_republishes_the_earlier_revision_after_the_confirmation()
    {
        await SignInAsync(HubRoleNames.Operator);
        SeedProfile(active: 2);
        _hub.Preview = new PublishPreview("office", 1, false, false, false, [], false, [Samples.Change("pc-1", "PC 1", 5, 6, new DocumentChange(DocumentKinds.Command, "lock_screen", "changed", "h3", "h2"))]);
        IRenderedComponent<ProfileEditorPage> cut = RenderEditor();

        cut.Find("[data-testid=profile-rollback-1]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=publish-confirmation]")), Patience);
        Assert.Equal("Roll Office back to revision 1", cut.Find("[data-testid=publish-title]").TextContent);
        Assert.Contains("makes revision 1 the revision in effect", cut.Find("[data-testid=publish-summary]").TextContent, StringComparison.Ordinal);
        Assert.Equal(("office", (int?)1), Assert.Single(_hub.Previews));
        Assert.Empty(_hub.RolledBack);

        cut.Find("[data-testid=publish-confirm]").Click();

        cut.WaitForAssertion(() => Assert.Equal(("office", 1, (string?)null), Assert.Single(_hub.RolledBack)), Patience);
        cut.WaitForAssertion(() => Assert.Contains("Rolled back to revision 1", cut.Find("[data-testid=profile-notice]").TextContent, StringComparison.Ordinal), Patience);
        Assert.Empty(_hub.Published);
    }

    [Fact]
    public async Task The_revisions_are_listed_newest_first_with_the_one_in_effect_marked()
    {
        await SignInAsync(HubRoleNames.Viewer);
        SeedProfile(active: 3);

        IRenderedComponent<ProfileEditorPage> cut = RenderEditor();

        string[] revisions = [.. cut.FindAll("[data-testid^=profile-revision-]").Select(row => row.GetAttribute("data-testid")!["profile-revision-".Length..])];
        Assert.Equal(["3", "2", "1"], revisions);
        Assert.Equal("true", cut.Find("[data-testid=profile-revision-3]").GetAttribute("data-active"));
        Assert.Equal("false", cut.Find("[data-testid=profile-revision-2]").GetAttribute("data-active"));
        Assert.Contains("first", cut.Find("[data-testid=profile-revision-1]").TextContent, StringComparison.Ordinal);
        Assert.Equal("rollouts?profile=office", cut.Find("[data-testid=profile-rollout-link]").GetAttribute("href"));
    }

    [Fact]
    public async Task A_profile_that_was_never_published_says_so()
    {
        await SignInAsync(HubRoleNames.Viewer);
        SeedProfile();

        IRenderedComponent<ProfileEditorPage> cut = RenderEditor();

        Assert.NotEmpty(cut.FindAll("[data-testid=profile-not-published]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=profile-revisions-none]"));
    }
}
