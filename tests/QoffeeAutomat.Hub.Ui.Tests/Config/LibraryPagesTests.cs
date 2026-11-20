using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Ui.Pages.Library;
using QoffeeAutomat.Hub.Ui.Services;
using QoffeeAutomat.Hub.Ui.Tests.Support;
using QoffeeAutomat.Ui.Components.Api;

namespace QoffeeAutomat.Hub.Ui.Tests.Config;

/// <summary>
/// The library pages of the hub UI (PLAN.md S20, D-033, T916): the list of library documents with role-dependent actions, the text editor of scripts and
/// JSON documents, and the editors of the component library (commands, sensors) running against the library through <c>LibraryAgentApi</c>.
/// </summary>
[Trait("Category", "Config")]
public sealed class LibraryPagesTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeHubApi _hub = new();
    private readonly FakeBrowserInterop _browser = new();

    public LibraryPagesTests()
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

    private void SeedLibrary()
    {
        _hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Sensor, "explorer", "Explorer", Samples.SensorBody("explorer"), revision: 2));
        _hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Command, "lock_screen", "Lock screen", Samples.CommandBody("lock_screen"), revision: 3));
        _hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Script, "backup.ps1", null, Samples.Text("Get-Date")));
        _hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Command, "reboot", "Reboot", Samples.CommandBody("reboot", "Reboot")));
    }

    // The list ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_list_shows_every_document_with_its_kind_and_revision_in_the_order_of_the_kinds()
    {
        await SignInAsync(HubRoleNames.Viewer);
        SeedLibrary();

        IRenderedComponent<LibraryPage> cut = Render<LibraryPage>();

        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("[data-testid^=library-row-]").Count), Patience);
        string[] order = [.. cut.FindAll("[data-testid^=library-row-]").Select(row => row.GetAttribute("data-testid")!["library-row-".Length..])];
        Assert.Equal(["command-lock_screen", "command-reboot", "sensor-explorer", "script-backup.ps1"], order);
        Assert.Contains("3", cut.Find("[data-testid=library-row-command-lock_screen]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Lock screen", cut.Find("[data-testid=library-row-command-lock_screen]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_kind_filter_asks_the_hub_for_that_kind_only()
    {
        await SignInAsync(HubRoleNames.Viewer);
        SeedLibrary();
        IRenderedComponent<LibraryPage> cut = Render<LibraryPage>();
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("[data-testid^=library-row-]").Count), Patience);

        cut.Find("[data-testid=library-kind-filter]").Change(DocumentKinds.Command);

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid^=library-row-]").Count), Patience);
        Assert.All(cut.FindAll("[data-testid^=library-row-]"), row => Assert.Equal("command", row.GetAttribute("data-kind")));
    }

    [Fact]
    public async Task An_empty_library_says_so()
    {
        await SignInAsync(HubRoleNames.Admin);

        IRenderedComponent<LibraryPage> cut = Render<LibraryPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=library-none]")), Patience);
    }

    [Theory]
    [InlineData(HubRoleNames.Viewer, false)]
    [InlineData(HubRoleNames.Operator, false)]
    [InlineData(HubRoleNames.Admin, true)]
    public async Task Only_an_administrator_is_offered_to_create_edit_and_delete_a_document(string role, bool offered)
    {
        await SignInAsync(role);
        SeedLibrary();

        IRenderedComponent<LibraryPage> cut = Render<LibraryPage>();

        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("[data-testid^=library-row-]").Count), Patience);
        Assert.Equal(offered, cut.FindAll("[data-testid^=library-new-]").Count == 6);
        Assert.Equal(offered, cut.FindAll("[data-testid^=library-edit-]").Count == 4);
        Assert.Equal(offered, cut.FindAll("[data-testid^=library-delete-]").Count == 4);
    }

    [Fact]
    public async Task Create_and_edit_lead_to_the_editor_that_fits_the_kind()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedLibrary();

        IRenderedComponent<LibraryPage> cut = Render<LibraryPage>();

        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("[data-testid^=library-row-]").Count), Patience);
        Assert.Equal("library/command/new", cut.Find("[data-testid=library-new-command]").GetAttribute("href"));
        Assert.Equal("library/sensor/new", cut.Find("[data-testid=library-new-sensor]").GetAttribute("href"));
        Assert.Equal("library/document/script/new", cut.Find("[data-testid=library-new-script]").GetAttribute("href"));
        Assert.Equal("library/document/metric_overrides/new", cut.Find("[data-testid=library-new-metric_overrides]").GetAttribute("href"));
        Assert.Equal("library/command/lock_screen", cut.Find("[data-testid=library-edit-command-lock_screen]").GetAttribute("href"));
        Assert.Equal("library/document/script/backup.ps1", cut.Find("[data-testid='library-edit-script-backup.ps1']").GetAttribute("href"));
    }

    [Fact]
    public async Task A_document_is_deleted_only_after_the_user_confirms()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedLibrary();
        IRenderedComponent<LibraryPage> cut = Render<LibraryPage>();
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("[data-testid^=library-row-]").Count), Patience);

        cut.Find("[data-testid=library-delete-command-reboot]").Click();

        Assert.Empty(_hub.DeletedDocuments);
        cut.Find("[data-testid=library-delete-confirm-command-reboot]").Click();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid^=library-row-]").Count), Patience);
        Assert.Equal((DocumentKinds.Command, "reboot"), Assert.Single(_hub.DeletedDocuments));
        Assert.Contains("reboot", cut.Find("[data-testid=library-notice]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_delete_deletes_nothing()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedLibrary();
        IRenderedComponent<LibraryPage> cut = Render<LibraryPage>();
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("[data-testid^=library-row-]").Count), Patience);

        cut.Find("[data-testid=library-delete-command-reboot]").Click();
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        Assert.Empty(cut.FindAll("[data-testid^=library-delete-confirm-]"));
        Assert.Empty(_hub.DeletedDocuments);
    }

    // The text editor --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_script_is_saved_as_a_json_string_with_its_file_name_and_the_editor_goes_on_with_it()
    {
        await SignInAsync(HubRoleNames.Admin);
        IRenderedComponent<LibraryDocumentEditorPage> cut = Render<LibraryDocumentEditorPage>(parameters => parameters.Add(page => page.Kind, DocumentKinds.Script));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=library-doc-id] input")), Patience);

        cut.Find("[data-testid=library-doc-id] input").Input("backup.ps1");
        cut.Find("[data-testid=library-doc-body] textarea").Input("Get-Date");
        cut.Find("[data-testid=library-doc-save]").Click();

        cut.WaitForAssertion(() => Assert.Single(_hub.SavedDocuments), Patience);
        (string kind, string id, System.Text.Json.JsonElement body) = _hub.SavedDocuments[0];
        Assert.Equal(DocumentKinds.Script, kind);
        Assert.Equal("backup.ps1", id);
        Assert.Equal("Get-Date", body.GetString());
        cut.WaitForAssertion(() => Assert.Contains("revision 1", cut.Find("[data-testid=library-doc-notice]").TextContent, StringComparison.Ordinal), Patience);
        Assert.EndsWith("/library/document/script/backup.ps1", Navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_document_without_an_id_or_with_a_bad_one_is_not_sent()
    {
        await SignInAsync(HubRoleNames.Admin);
        IRenderedComponent<LibraryDocumentEditorPage> cut = Render<LibraryDocumentEditorPage>(parameters => parameters.Add(page => page.Kind, DocumentKinds.Variables));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=library-doc-id] input")), Patience);

        cut.Find("[data-testid=library-doc-save]").Click();
        Assert.Contains("Enter an id.", cut.Find("[data-testid=library-doc-local-error]").TextContent, StringComparison.Ordinal);

        cut.Find("[data-testid=library-doc-id] input").Input("Bad-Id");
        cut.Find("[data-testid=library-doc-save]").Click();
        Assert.Contains("lower-case", cut.Find("[data-testid=library-doc-local-error]").TextContent, StringComparison.Ordinal);

        Assert.Empty(_hub.SavedDocuments);
    }

    [Fact]
    public async Task Text_that_is_not_a_json_object_is_reported_and_not_sent()
    {
        await SignInAsync(HubRoleNames.Admin);
        IRenderedComponent<LibraryDocumentEditorPage> cut = Render<LibraryDocumentEditorPage>(parameters => parameters.Add(page => page.Kind, DocumentKinds.Mode));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=library-doc-id] input")), Patience);
        cut.Find("[data-testid=library-doc-id] input").Input("living_room");

        cut.Find("[data-testid=library-doc-body] textarea").Input("{not json");
        cut.Find("[data-testid=library-doc-save]").Click();

        Assert.Contains("not a usable JSON", cut.Find("[data-testid=library-doc-local-error]").TextContent, StringComparison.Ordinal);
        Assert.Empty(_hub.SavedDocuments);
    }

    [Fact]
    public async Task The_places_that_the_hub_found_wrong_are_listed()
    {
        await SignInAsync(HubRoleNames.Admin);
        _hub.SaveFailure = new ApiException(
            422,
            new ApiProblem("about:blank", "Validation failed", 422, "The request has a validation error.", "validation_failed", [new ApiValidationError("/modes/0/name", "required", "The name is required.")]));
        IRenderedComponent<LibraryDocumentEditorPage> cut = Render<LibraryDocumentEditorPage>(parameters => parameters.Add(page => page.Kind, DocumentKinds.Mode));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=library-doc-id] input")), Patience);
        cut.Find("[data-testid=library-doc-id] input").Input("living_room");

        cut.Find("[data-testid=library-doc-save]").Click();

        cut.WaitForAssertion(() => Assert.Contains("/modes/0/name", cut.Find("[data-testid=error-display]").TextContent, StringComparison.Ordinal), Patience);
        Assert.Contains("The name is required.", cut.Find("[data-testid=error-display]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_existing_script_shows_its_revisions_and_an_old_revision_can_be_loaded_into_the_editor()
    {
        await SignInAsync(HubRoleNames.Admin);
        _hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Script, "backup.ps1", null, Samples.Text("Get-Date"), revision: 3));
        IRenderedComponent<LibraryDocumentEditorPage> cut = Render<LibraryDocumentEditorPage>(parameters => parameters
            .Add(page => page.Kind, DocumentKinds.Script)
            .Add(page => page.Id, "backup.ps1"));

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid^=library-doc-revision-]").Count), Patience);
        cut.Find("[data-testid=library-doc-load-1]").Click();

        cut.WaitForAssertion(() => Assert.Contains("Revision 1 is in the editor", cut.Find("[data-testid=library-doc-notice]").TextContent, StringComparison.Ordinal), Patience);
        Assert.Empty(cut.FindAll("[data-testid=library-doc-id]"));
    }

    [Fact]
    public async Task Saving_the_same_content_again_says_that_nothing_changed()
    {
        await SignInAsync(HubRoleNames.Admin);
        _hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Script, "backup.ps1", null, Samples.Text("Get-Date"), revision: 3));
        IRenderedComponent<LibraryDocumentEditorPage> cut = Render<LibraryDocumentEditorPage>(parameters => parameters
            .Add(page => page.Kind, DocumentKinds.Script)
            .Add(page => page.Id, "backup.ps1"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=library-doc-revisions]")), Patience);

        cut.Find("[data-testid=library-doc-save]").Click();

        cut.WaitForAssertion(() => Assert.Contains("Nothing changed", cut.Find("[data-testid=library-doc-notice]").TextContent, StringComparison.Ordinal), Patience);
    }

    [Fact]
    public async Task A_kind_with_its_own_editor_is_not_edited_as_text()
    {
        await SignInAsync(HubRoleNames.Admin);

        IRenderedComponent<LibraryDocumentEditorPage> cut = Render<LibraryDocumentEditorPage>(parameters => parameters.Add(page => page.Kind, DocumentKinds.Command));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=library-doc-unknown-kind]")), Patience);
        Assert.Empty(cut.FindAll("textarea"));
    }

    [Theory]
    [InlineData(HubRoleNames.Viewer)]
    [InlineData(HubRoleNames.Operator)]
    public async Task The_editor_of_a_document_is_for_administrators_and_a_lower_role_makes_no_call(string role)
    {
        await SignInAsync(role);
        _hub.LibraryDocuments.Add(Samples.Library(DocumentKinds.Script, "backup.ps1", null, Samples.Text("Get-Date")));

        IRenderedComponent<LibraryDocumentEditorPage> cut = Render<LibraryDocumentEditorPage>(parameters => parameters
            .Add(page => page.Kind, DocumentKinds.Script)
            .Add(page => page.Id, "backup.ps1"));

        Assert.Single(cut.FindAll("[data-testid=role-required]"));
        Assert.DoesNotContain(_hub.Calls, call => call.StartsWith("GetLibraryDocumentAsync", StringComparison.Ordinal));
        Assert.DoesNotContain(_hub.Calls, call => call.StartsWith("refused", StringComparison.Ordinal));
    }

    // The editors of the component library ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_command_editor_of_the_component_library_edits_a_library_command_that_is_not_read_only_although_the_hub_owns_it()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedLibrary();

        IRenderedComponent<LibraryCommandEditorPage> cut = Render<LibraryCommandEditorPage>(parameters => parameters.Add(page => page.CommandId, "lock_screen"));

        cut.WaitForAssertion(() => Assert.Contains("Lock screen", cut.Find("[data-testid=editor-title]").TextContent, StringComparison.Ordinal), Patience);
        Assert.Empty(cut.FindAll("[data-testid=editor-read-only]"));
        Assert.False(cut.Find("[data-testid=editor-save]").HasAttribute("disabled"));
        Assert.Contains(_hub.Calls, call => call == "GetLibraryDocumentAsync:command/lock_screen");
    }

    [Fact]
    public async Task A_command_that_is_changed_in_the_editor_is_saved_as_the_next_revision_in_the_library()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedLibrary();
        IRenderedComponent<LibraryCommandEditorPage> cut = Render<LibraryCommandEditorPage>(parameters => parameters.Add(page => page.CommandId, "lock_screen"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=field-name] input")), Patience);

        cut.Find("[data-testid=field-name] input").Input("Lock the screen");
        cut.Find("[data-testid=editor-save]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=editor-saved]")), Patience);
        (string kind, string id, System.Text.Json.JsonElement body) = Assert.Single(_hub.SavedDocuments);
        Assert.Equal(DocumentKinds.Command, kind);
        Assert.Equal("lock_screen", id);
        Assert.Equal("Lock the screen", body.GetProperty("name").GetString());
        Assert.Equal(4, _hub.LibraryDocuments.Single(document => document.Id == "lock_screen").Revision);
    }

    [Fact]
    public async Task A_new_command_is_created_in_the_library_and_the_editor_stays_on_it()
    {
        await SignInAsync(HubRoleNames.Admin);
        IRenderedComponent<LibraryCommandEditorPage> cut = Render<LibraryCommandEditorPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=field-name] input")), Patience);

        cut.Find("[data-testid=field-name] input").Input("Set profile");
        cut.Find("[data-testid=code-editor] textarea").Input("Write-Output 'hello'");
        cut.Find("[data-testid=editor-save]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=editor-saved]")), Patience);
        (_, string id, _) = Assert.Single(_hub.SavedDocuments);
        Assert.Equal("set_profile", id);
        Assert.Contains(_hub.LibraryDocuments, document => document.Kind == DocumentKinds.Command && document.Id == "set_profile");

        // The address becomes the one of the library editor, not the one of an agent.
        Assert.EndsWith("/library/command/set_profile", Navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_a_command_with_an_id_that_exists_is_reported_at_the_id_field_and_replaces_nothing()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedLibrary();
        IRenderedComponent<LibraryCommandEditorPage> cut = Render<LibraryCommandEditorPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=field-name] input")), Patience);

        cut.Find("[data-testid=field-name] input").Input("Reboot");
        cut.Find("[data-testid=code-editor] textarea").Input("Restart-Computer");
        cut.Find("[data-testid=editor-save]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=validation-summary]")), Patience);
        Assert.Empty(_hub.SavedDocuments);
        Assert.Equal(1, _hub.LibraryDocuments.Single(document => document.Id == "reboot").Revision);
    }

    [Fact]
    public async Task A_command_that_the_library_does_not_have_shows_not_found()
    {
        await SignInAsync(HubRoleNames.Admin);

        IRenderedComponent<LibraryCommandEditorPage> cut = Render<LibraryCommandEditorPage>(parameters => parameters.Add(page => page.CommandId, "missing"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=editor-not-found]")), Patience);
    }

    [Fact]
    public async Task The_test_panel_of_the_editor_asks_for_a_pc_and_without_one_says_so()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedLibrary();
        IRenderedComponent<LibraryCommandEditorPage> cut = Render<LibraryCommandEditorPage>(parameters => parameters.Add(page => page.CommandId, "lock_screen"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=test-form]")), Patience);

        cut.Find("[data-testid=test-form]").Submit();

        cut.WaitForAssertion(() => Assert.Contains("online PC", cut.Markup, StringComparison.Ordinal), Patience);
    }

    [Fact]
    public async Task The_frame_offers_the_pcs_that_are_online_to_test_on_and_remembers_the_choice()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedLibrary();
        _hub.Inventory = new QoffeeAutomat.Hub.Gateway.InventoryList(
        [
            Samples.Inventory(Samples.Agent("pc-1", "PC 1"), online: true),
            Samples.Inventory(Samples.Agent("pc-2", "PC 2"), online: false),
            Samples.Inventory(Samples.Agent("pc-3", "PC 3", "pending"), online: true),
        ]);

        IRenderedComponent<LibraryCommandEditorPage> cut = Render<LibraryCommandEditorPage>(parameters => parameters.Add(page => page.CommandId, "lock_screen"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=library-target-select]")), Patience);
        string[] offered = [.. cut.Find("[data-testid=library-target-select]").QuerySelectorAll("option").Select(option => option.GetAttribute("value")!)];
        Assert.Equal([string.Empty, "pc-1"], offered);

        cut.Find("[data-testid=library-target-select]").Change("pc-1");

        Assert.Equal("pc-1", Services.GetRequiredService<AgentSelection>().TestTargetId);
    }

    [Fact]
    public async Task Without_a_pc_online_the_frame_says_that_a_draft_cannot_be_tested_but_can_be_saved()
    {
        await SignInAsync(HubRoleNames.Admin);

        IRenderedComponent<LibraryCommandEditorPage> cut = Render<LibraryCommandEditorPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=library-target-none]")), Patience);
        Assert.False(cut.Find("[data-testid=editor-save]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task The_editors_are_for_administrators_and_a_lower_role_gets_the_notice_and_no_editor()
    {
        await SignInAsync(HubRoleNames.Operator);
        SeedLibrary();

        IRenderedComponent<LibraryCommandEditorPage> command = Render<LibraryCommandEditorPage>(parameters => parameters.Add(page => page.CommandId, "lock_screen"));
        IRenderedComponent<LibrarySensorEditorPage> sensor = Render<LibrarySensorEditorPage>(parameters => parameters.Add(page => page.SensorId, "explorer"));

        Assert.Single(command.FindAll("[data-testid=role-required]"));
        Assert.Single(sensor.FindAll("[data-testid=role-required]"));
        Assert.Empty(command.FindAll("[data-testid=editor-title]"));
        Assert.DoesNotContain(_hub.Calls, call => call.StartsWith("GetLibraryDocumentAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_sensor_editor_of_the_component_library_edits_a_library_sensor()
    {
        await SignInAsync(HubRoleNames.Admin);
        SeedLibrary();

        IRenderedComponent<LibrarySensorEditorPage> cut = Render<LibrarySensorEditorPage>(parameters => parameters.Add(page => page.SensorId, "explorer"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=sensor-save]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=sensor-read-only]"));
        Assert.False(cut.Find("[data-testid=sensor-save]").HasAttribute("disabled"));
        Assert.Contains(_hub.Calls, call => call == "GetLibraryAsync");
    }
}
