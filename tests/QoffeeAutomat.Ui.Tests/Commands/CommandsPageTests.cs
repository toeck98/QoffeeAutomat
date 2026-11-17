using Bunit;
using MudBlazor;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Commands;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Commands;

/// <summary>
/// The command list (T403) against a fake agent: what it shows, that the switches save at once and spring back when the
/// agent refuses, that deleting asks first, and that the run dialog opens and runs.
/// </summary>
[Trait("Category", "Commands")]
public sealed class CommandsPageTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeAgentApi _api = new FakeAgentApi().SignedIn();
    private readonly FakeBrowserInterop _browser = new();

    public CommandsPageTests()
    {
        UiTestSetup.Register(this, _api, _browser);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact]
    public void The_list_shows_every_command_with_its_state_action_and_run_context()
    {
        IRenderedComponent<CommandsPage> cut = RenderPage(out _);

        Assert.Equal(4, cut.FindAll("[data-testid=command-table] tbody tr").Count);
        Assert.Equal("false", cut.Find("[data-testid=command-backup]").GetAttribute("data-enabled"));
        Assert.Equal("true", cut.Find("[data-testid=command-lock_screen]").GetAttribute("data-enabled"));
        Assert.Equal("powershell", cut.Find("[data-testid=action-backup]").GetAttribute("data-value"));
        Assert.Equal("sequence", cut.Find("[data-testid=action-night_mode]").GetAttribute("data-value"));
        Assert.Equal("system", cut.Find("[data-testid=runas-backup]").GetAttribute("data-value"));
        Assert.Contains("Back up documents", cut.Find("[data-testid=command-backup]").TextContent, StringComparison.Ordinal);
        Assert.Contains("4", cut.Find("[data-testid=command-count]").TextContent, StringComparison.Ordinal);
        Assert.Equal(1, _api.CommandsCalls);
    }

    [Fact]
    public void The_last_run_of_every_command_is_shown()
    {
        IRenderedComponent<CommandsPage> cut = RenderPage(out _);

        Assert.Equal("succeeded", cut.Find("[data-testid=lastrun-lock_screen]").GetAttribute("data-state"));
        Assert.Equal("failed", cut.Find("[data-testid=lastrun-backup]").GetAttribute("data-state"));
        Assert.Equal("never", cut.Find("[data-testid=lastrun-open_notes]").GetAttribute("data-state"));
    }

    [Fact]
    public void An_agent_without_commands_says_so()
    {
        _api.Commands = [];

        IRenderedComponent<CommandsPage> cut = RenderPage(out _);

        Assert.NotNull(cut.Find("[data-testid=commands-none]"));
        Assert.Empty(cut.FindAll("[data-testid=command-table]"));
    }

    [Fact]
    public void A_list_that_cannot_be_read_shows_the_error_instead_of_a_table()
    {
        _api.CommandsFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Something broke."));

        IRenderedComponent<CommandsPage> cut = RenderPage(out _);

        Assert.NotNull(cut.Find("[data-testid=error-message]"));
        Assert.Empty(cut.FindAll("[data-testid=command-table]"));
        Assert.Empty(cut.FindAll("[data-testid=commands-none]"));
    }

    [Fact]
    public void The_search_narrows_the_list()
    {
        IRenderedComponent<CommandsPage> cut = RenderPage(out _);

        cut.Find("[data-testid=filter-search] input").Input("lock");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=command-table] tbody tr")), Patience);
        Assert.NotNull(cut.Find("[data-testid=command-lock_screen]"));
    }

    [Fact]
    public void Switching_a_command_on_saves_at_once_and_keeps_the_new_state()
    {
        IRenderedComponent<CommandsPage> cut = RenderPage(out _);

        cut.Find("[data-testid=enabled-backup] input").Change(true);

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=command-backup]").GetAttribute("data-enabled")), Patience);
        (string id, QoffeeAutomat.Contracts.Api.CommandPatch patch) = Assert.Single(_api.Patches);
        Assert.Equal("backup", id);
        Assert.True(patch.Enabled);
        Assert.Null(patch.ExposeToHa);
        Assert.Equal("true", cut.Find("[data-testid=enabled-backup]").GetAttribute("data-checked"));
        Assert.Empty(cut.FindAll("[data-testid=action-failed]"));
    }

    [Fact]
    public void Switching_the_exposure_changes_only_expose_to_ha()
    {
        IRenderedComponent<CommandsPage> cut = RenderPage(out _);

        cut.Find("[data-testid=expose-night_mode] input").Change(true);

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=expose-night_mode]").GetAttribute("data-checked")), Patience);
        (string id, QoffeeAutomat.Contracts.Api.CommandPatch patch) = Assert.Single(_api.Patches);
        Assert.Equal("night_mode", id);
        Assert.Null(patch.Enabled);
        Assert.True(patch.ExposeToHa);
    }

    [Fact]
    public void A_switch_springs_back_and_the_error_is_shown_when_the_agent_refuses_the_change()
    {
        _api.PatchFailure = new ApiException(422, Samples.Problem(422, "validation_failed", "Not valid."));
        IRenderedComponent<CommandsPage> cut = RenderPage(out _);

        cut.Find("[data-testid=enabled-backup] input").Change(true);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=action-failed]")), Patience);
        Assert.Equal("false", cut.Find("[data-testid=enabled-backup]").GetAttribute("data-checked"));
        Assert.Equal("false", cut.Find("[data-testid=command-backup]").GetAttribute("data-enabled"));
    }

    [Fact]
    public void A_disabled_command_cannot_be_run()
    {
        IRenderedComponent<CommandsPage> cut = RenderPage(out _);

        Assert.True(cut.Find("[data-testid=run-backup]").HasAttribute("disabled"));
        Assert.False(cut.Find("[data-testid=run-lock_screen]").HasAttribute("disabled"));
    }

    [Fact]
    public void Deleting_asks_first_and_nothing_is_deleted_when_the_user_cancels()
    {
        IRenderedComponent<CommandsPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);

        cut.Find("[data-testid=delete-lock_screen]").Click();
        dialogs.WaitForElement(".mud-message-box__cancel-button", Patience).Click();

        cut.WaitForAssertion(() => Assert.Empty(dialogs.FindAll(".mud-message-box__cancel-button")), Patience);
        Assert.Empty(_api.Deleted);
        Assert.NotNull(cut.Find("[data-testid=command-lock_screen]"));
    }

    [Fact]
    public void Deleting_after_the_confirmation_removes_the_command_and_says_so()
    {
        IRenderedComponent<CommandsPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);

        cut.Find("[data-testid=delete-lock_screen]").Click();
        dialogs.WaitForElement(".mud-message-box__yes-button", Patience).Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=command-lock_screen]")), Patience);
        Assert.Equal(["lock_screen"], _api.Deleted);
        Assert.Equal(3, cut.FindAll("[data-testid=command-table] tbody tr").Count);
        Assert.Contains("Lock screen", cut.Find("[data-testid=notice]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_delete_keeps_the_command_and_shows_the_error()
    {
        _api.DeleteFailure = new ApiException(409, Samples.Problem(409, "in_use", "The command is used by a sequence."));
        IRenderedComponent<CommandsPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);

        cut.Find("[data-testid=delete-lock_screen]").Click();
        dialogs.WaitForElement(".mud-message-box__yes-button", Patience).Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=action-failed]")), Patience);
        Assert.NotNull(cut.Find("[data-testid=command-lock_screen]"));
        Assert.Empty(cut.FindAll("[data-testid=notice]"));
    }

    [Fact]
    public void Duplicating_creates_a_copy_with_a_free_id()
    {
        IRenderedComponent<CommandsPage> cut = RenderPage(out _);

        cut.Find("[data-testid=duplicate-lock_screen]").Click();

        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[data-testid=command-table] tbody tr").Count), Patience);
        QoffeeAutomat.Contracts.Config.CommandDocument created = Assert.Single(_api.Created);
        Assert.NotEqual("lock_screen", created.Id);
        Assert.Contains("lock_screen", created.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void Running_opens_the_form_of_the_command_and_a_run_can_be_started_in_it()
    {
        IRenderedComponent<CommandsPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);

        cut.Find("[data-testid=run-lock_screen]").Click();
        dialogs.WaitForElement("[data-testid=run-form]", Patience);
        Assert.NotNull(dialogs.Find("[data-testid=run-no-parameters]"));

        dialogs.Find("[data-testid=run-form]").Submit();

        dialogs.WaitForAssertion(() => Assert.Equal("running", dialogs.Find("[data-testid=run-form]").GetAttribute("data-phase")), Patience);
        (string commandId, _) = Assert.Single(_api.RunRequests);
        Assert.Equal("lock_screen", commandId);
        _api.PushRun(new RunStreamItem(Output: new QoffeeAutomat.Contracts.Api.RunOutputEvent("stdout", "locked", Samples.Now)));
        _api.PushRun(new RunStreamItem(End: new QoffeeAutomat.Contracts.Api.RunEndEvent("run-1", true, 0, null, null, 12, false)));
        dialogs.WaitForAssertion(() => Assert.Equal("succeeded", dialogs.Find("[data-testid=run-result]").GetAttribute("data-state")), Patience);
        Assert.Contains("locked", dialogs.Find("[data-testid=run-output]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Closing_the_run_dialog_reads_the_last_runs_again()
    {
        IRenderedComponent<CommandsPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);
        cut.Find("[data-testid=run-lock_screen]").Click();
        dialogs.WaitForElement("[data-testid=run-form]", Patience);
        _api.Runs = new QoffeeAutomat.Contracts.Api.RunList([], 8, 0, false);

        dialogs.Find("[data-testid=run-close]").Click();

        cut.WaitForAssertion(() => Assert.Equal("never", cut.Find("[data-testid=lastrun-lock_screen]").GetAttribute("data-state")), Patience);
    }

    /// <summary>Renders the providers that MudBlazor needs for popovers and dialogs, then the page, and waits until the list is there.</summary>
    private IRenderedComponent<CommandsPage> RenderPage(out IRenderedComponent<MudDialogProvider> dialogs)
    {
        Render<MudPopoverProvider>();
        dialogs = Render<MudDialogProvider>();
        IRenderedComponent<CommandsPage> cut = Render<CommandsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=command-count], [data-testid=commands-none], [data-testid=error-message]")), Patience);
        return cut;
    }
}
