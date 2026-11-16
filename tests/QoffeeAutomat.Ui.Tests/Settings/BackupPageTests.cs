using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Backup;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Settings;

/// <summary>
/// The backup page (T407) against a fake agent: export and diagnostics hand a file to the browser, an import shows a preview
/// of what will change and applies nothing when any document is invalid, and the revision history restores a revision after
/// a confirmation.
/// </summary>
[Trait("Category", "Settings")]
public sealed class BackupPageTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeAgentApi _api = new FakeAgentApi().SignedIn();
    private readonly FakeBrowserInterop _browser = new();
    private IRenderedComponent<MudDialogProvider> _dialogs = null!;

    public BackupPageTests()
    {
        UiTestSetup.Register(this, _api, _browser);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact]
    public void Export_hands_the_archive_to_the_browser_under_the_name_of_the_agent()
    {
        IRenderedComponent<BackupPage> cut = RenderPage();

        cut.Find("[data-testid=export-start]").Click();

        cut.WaitForAssertion(() => Assert.Single(_browser.Downloads), Patience);
        Assert.Equal("qoffeeautomat-config-20260101-120000.zip", _browser.Downloads[0].FileName);
        Assert.Equal("application/zip", _browser.Downloads[0].ContentType);
        Assert.Equal(new byte[] { 1, 2, 3 }, _browser.Downloads[0].Content);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=notice]")), Patience);
    }

    [Fact]
    public void A_failed_export_shows_the_error_and_downloads_nothing()
    {
        _api.ExportFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Something broke."));
        IRenderedComponent<BackupPage> cut = RenderPage();

        cut.Find("[data-testid=export-start]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=action-failed]")), Patience);
        Assert.Empty(_browser.Downloads);
    }

    [Fact]
    public void Diagnostics_are_handed_to_the_browser()
    {
        IRenderedComponent<BackupPage> cut = RenderPage();

        cut.Find("[data-testid=diagnostics-download]").Click();

        cut.WaitForAssertion(() => Assert.Single(_browser.Downloads), Patience);
        Assert.Equal("qoffeeautomat-diagnostics-20260101-120000.zip", _browser.Downloads[0].FileName);
        Assert.Equal(1, _api.DiagnosticsDownloads);
    }

    [Fact]
    public void Choosing_an_archive_shows_a_preview_of_what_will_change()
    {
        IRenderedComponent<BackupPage> cut = RenderPage();

        Upload(cut, [9, 9]);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=import-preview]")), Patience);
        Assert.Equal("true", cut.Find("[data-testid=import-preview]").GetAttribute("data-valid"));
        Assert.Equal("changed", cut.Find("[data-testid=import-item-command-lock_screen]").GetAttribute("data-change"));
        Assert.Equal("new", cut.Find("[data-testid=import-item-command-new_one]").GetAttribute("data-change"));
        Assert.Equal("unchanged", cut.Find("[data-testid=import-item-sensor-temp]").GetAttribute("data-change"));
        Assert.Contains("1 new, 1 changed, 1 unchanged", cut.Find("[data-testid=import-summary]").TextContent, StringComparison.Ordinal);
        Assert.False(cut.Find("[data-testid=import-apply]").HasAttribute("disabled"));
        (byte[] archive, bool includeSettings) = Assert.Single(_api.Previews);
        Assert.Equal(new byte[] { 9, 9 }, archive);
        Assert.False(includeSettings);
        Assert.Empty(_api.Imports);
    }

    [Fact]
    public void Applying_a_valid_import_sends_the_archive_and_reports_what_was_stored()
    {
        _api.ImportResult = new ConfigImportResult(2, 1, 3, false, false);
        IRenderedComponent<BackupPage> cut = RenderPage();
        Upload(cut, [1, 2, 3, 4]);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=import-apply]")), Patience);

        cut.Find("[data-testid=import-apply]").Click();

        cut.WaitForAssertion(() => Assert.Single(_api.Imports), Patience);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, _api.Imports[0].Archive);
        cut.WaitForAssertion(() => Assert.Contains("2 commands, 1 sensors and 3 scripts", cut.Find("[data-testid=notice]").TextContent, StringComparison.Ordinal), Patience);
        Assert.Empty(cut.FindAll("[data-testid=import-preview]"));
    }

    [Fact]
    public void An_invalid_document_shows_the_errors_and_offers_no_way_to_apply_anything()
    {
        _api.Preview = Samples.InvalidImportPreview();
        IRenderedComponent<BackupPage> cut = RenderPage();

        Upload(cut, [7]);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=import-invalid]")), Patience);
        Assert.Equal("false", cut.Find("[data-testid=import-preview]").GetAttribute("data-valid"));
        Assert.Contains("/config/commands/broken.json/steps/0", cut.Find("[data-testid=import-errors]").TextContent, StringComparison.Ordinal);
        Assert.Contains("The step needs an action.", cut.Find("[data-testid=import-errors]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=import-nothing-applied]"));
        Assert.Empty(cut.FindAll("[data-testid=import-apply]"));
        Assert.Empty(_api.Imports);
    }

    [Fact]
    public void An_import_that_the_agent_rejects_applies_nothing_and_shows_its_errors()
    {
        _api.ImportFailure = new ApiException(
            422,
            Samples.Problem(422, "validation_failed", "Invalid.", new ApiValidationError("/config/sensors/bad.json/source", "required", "The sensor needs a source.")));
        IRenderedComponent<BackupPage> cut = RenderPage();
        Upload(cut, [1]);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=import-apply]")), Patience);

        cut.Find("[data-testid=import-apply]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=import-invalid]")), Patience);
        Assert.Contains("/config/sensors/bad.json/source", cut.Find("[data-testid=import-errors]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=import-apply]"));
        Assert.Empty(cut.FindAll("[data-testid=notice]"));
    }

    [Fact]
    public void A_preview_that_is_all_unchanged_cannot_be_applied()
    {
        _api.Preview = new ConfigImportPreview(true, [new ImportPreviewItem("command", "lock_screen", "unchanged")], []);
        IRenderedComponent<BackupPage> cut = RenderPage();

        Upload(cut, [1]);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=import-apply]")), Patience);
        Assert.True(cut.Find("[data-testid=import-apply]").HasAttribute("disabled"));
    }

    [Fact]
    public void The_settings_switch_asks_for_a_new_preview_with_the_settings()
    {
        IRenderedComponent<BackupPage> cut = RenderPage();
        Upload(cut, [1]);
        cut.WaitForAssertion(() => Assert.Single(_api.Previews), Patience);

        cut.Find("[data-testid=import-settings] input").Change(true);

        cut.WaitForAssertion(() => Assert.Equal(2, _api.Previews.Count), Patience);
        Assert.True(_api.Previews[1].IncludeSettings);
    }

    [Fact]
    public void A_preview_that_fails_shows_the_error_and_no_preview()
    {
        _api.PreviewFailure = new ApiException(413, Samples.Problem(413, "payload_too_large", "The archive is too large."));
        IRenderedComponent<BackupPage> cut = RenderPage();

        Upload(cut, [1]);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=error-display]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=import-preview]"));
    }

    [Fact]
    public void The_revisions_of_a_command_are_listed_with_the_current_one_marked()
    {
        IRenderedComponent<BackupPage> cut = RenderPage();

        cut.Find("[data-testid=revision-document]").Change("lock_screen");

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=revision-table]")), Patience);
        Assert.Equal(3, cut.FindAll("[data-testid=revision-table] tbody tr").Count);
        Assert.Equal("true", cut.Find("[data-testid=revision-3]").GetAttribute("data-current"));
        Assert.Equal("false", cut.Find("[data-testid=revision-1]").GetAttribute("data-current"));
        Assert.True(cut.Find("[data-testid=restore-3]").HasAttribute("disabled"));
        Assert.False(cut.Find("[data-testid=restore-1]").HasAttribute("disabled"));
    }

    [Fact]
    public void Restoring_a_revision_asks_first_and_restores_it_against_the_current_revision()
    {
        IRenderedComponent<BackupPage> cut = RenderPage();
        cut.Find("[data-testid=revision-document]").Change("lock_screen");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=restore-1]")), Patience);

        cut.Find("[data-testid=restore-1]").Click();
        IElement confirm = _dialogs.WaitForElement(".mud-message-box__yes-button", Patience);
        Assert.Empty(_api.Restores);
        confirm.Click();

        cut.WaitForAssertion(() => Assert.Single(_api.Restores), Patience);
        Assert.Equal((RevisionKinds.Command, "lock_screen", 1, (int?)3), _api.Restores[0]);
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=revision-4]").GetAttribute("data-current")), Patience);
        Assert.NotNull(cut.Find("[data-testid=notice]"));
    }

    [Fact]
    public void Cancelling_the_confirmation_restores_nothing()
    {
        IRenderedComponent<BackupPage> cut = RenderPage();
        cut.Find("[data-testid=revision-document]").Change("lock_screen");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=restore-2]")), Patience);

        cut.Find("[data-testid=restore-2]").Click();
        _dialogs.WaitForElement(".mud-message-box__cancel-button", Patience).Click();

        cut.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".mud-message-box__cancel-button")), Patience);
        Assert.Empty(_api.Restores);
    }

    [Fact]
    public void A_stale_restore_shows_the_error()
    {
        _api.RestoreFailure = new ApiException(412, Samples.Problem(412, "revision_mismatch", "The document was changed meanwhile."));
        IRenderedComponent<BackupPage> cut = RenderPage();
        cut.Find("[data-testid=revision-document]").Change("lock_screen");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=restore-1]")), Patience);

        cut.Find("[data-testid=restore-1]").Click();
        _dialogs.WaitForElement(".mud-message-box__yes-button", Patience).Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=action-failed]")), Patience);
        Assert.Contains("changed meanwhile", cut.Find("[data-testid=error-message]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_settings_have_a_revision_history_without_choosing_a_document()
    {
        IRenderedComponent<BackupPage> cut = RenderPage();

        cut.Find("[data-testid=revision-kind]").Change(RevisionKinds.Settings);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=revision-table]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=revision-document]"));
        Assert.Equal("true", cut.Find("[data-testid=revision-3]").GetAttribute("data-current"));
    }

    [Fact]
    public void A_document_without_earlier_revisions_says_so()
    {
        _api.Revisions[(RevisionKinds.Command, "backup")] = new RevisionList(RevisionKinds.Command, "backup", null, []);
        IRenderedComponent<BackupPage> cut = RenderPage();

        cut.Find("[data-testid=revision-document]").Change("backup");

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=revisions-none]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=revision-table]"));
    }

    private static void Upload(IRenderedComponent<BackupPage> cut, byte[] content)
    {
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(content, "export.zip"));
    }

    private IRenderedComponent<BackupPage> RenderPage()
    {
        Render<MudPopoverProvider>();
        _dialogs = Render<MudDialogProvider>();
        IRenderedComponent<BackupPage> cut = Render<BackupPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=revision-document] option[value=lock_screen]")), Patience);
        return cut;
    }
}
