using Bunit;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.CommandEditor;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.CommandEditor;

/// <summary>The sections for the conditions (T705) and the secrets (T706) of the command editor against a fake agent.</summary>
[Trait("Category", "CommandEditor")]
public sealed class CommandEditorSectionsTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeAgentApi _api = new FakeAgentApi().SignedIn();
    private readonly FakeBrowserInterop _browser = new();

    public CommandEditorSectionsTests()
    {
        UiTestSetup.Register(this, _api, _browser);
        _api.Secrets = [new SecretSummary("api_key"), new SecretSummary("other")];
        _api.Commands =
        [
            Samples.Command("backup", "Back up", action: new PowerShellAction(PowerShellSources.Inline, Code: "Write-Output 1")) with
            {
                Secrets = ["api_key", "gone"],
                Conditions = new CommandConditions(SessionRequirements.Unlocked),
            },
        ];
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact]
    public void The_stored_secrets_are_offered_and_a_missing_one_is_flagged()
    {
        IRenderedComponent<CommandEditorPage> cut = RenderEditor("backup");

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=secret-choice-other]")), Patience);
        Assert.True(cut.Find("[data-testid=secret-input-api_key]").HasAttribute("checked"));
        Assert.False(cut.Find("[data-testid=secret-input-other]").HasAttribute("checked"));
        Assert.Empty(cut.FindAll("[data-testid=secret-missing-api_key]"));
        Assert.NotNull(cut.Find("[data-testid=secret-missing-gone]"));
        Assert.Equal("false", cut.Find("[data-testid=secret-choice-gone]").GetAttribute("data-stored"));
    }

    [Fact]
    public void Choosing_secrets_and_conditions_is_saved_with_the_command()
    {
        IRenderedComponent<CommandEditorPage> cut = RenderEditor("backup");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=secret-choice-other]")), Patience);

        cut.Find("[data-testid=secret-input-other]").Change(true);
        cut.Find("[data-testid=field-condition-session]").Change(SessionRequirements.Locked);
        cut.Find("[data-testid=field-window-enabled]").Change(true);
        cut.Find("[data-testid=window-day-input-mon]").Change(true);
        cut.Find("[data-testid=window-day-input-fri]").Change(true);
        cut.Find("[data-testid=editor-save]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(_api.Replaced), Patience);
        CommandDocument saved = _api.Replaced[^1];
        Assert.Equal(["api_key", "gone", "other"], saved.Secrets!);
        Assert.Equal(SessionRequirements.Locked, saved.Conditions?.Session);
        TimeWindow window = saved.Conditions!.TimeWindow!;
        Assert.Equal(CommandDraft.DefaultWindowFrom, window.From);
        Assert.Equal(CommandDraft.DefaultWindowTo, window.To);
        Assert.Equal(["mon", "fri"], window.Days!);
    }

    [Fact]
    public void An_action_without_environment_cannot_choose_secrets()
    {
        IRenderedComponent<CommandEditorPage> cut = RenderEditor("backup");

        cut.Find("[data-testid=field-action-type]").Change(ActionTypes.Builtin);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=secrets-not-available]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=secret-choices]"));
    }

    [Fact]
    public void A_window_that_the_agent_refuses_is_shown_at_its_field()
    {
        _api.ReplaceFailure = new ApiException(
            422,
            Samples.Problem(
                422,
                "validation_failed",
                "The document is not valid.",
                new ApiValidationError("/conditions/timeWindow/from", "invalid", "The time must look like 22:30 (HH:mm, 24-hour clock).")));
        IRenderedComponent<CommandEditorPage> cut = RenderEditor("backup");
        cut.Find("[data-testid=field-window-enabled]").Change(true);

        cut.Find("[data-testid=editor-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='draft-error:/conditions/timeWindow/from']")), Patience);
    }

    private IRenderedComponent<CommandEditorPage> RenderEditor(string id)
    {
        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        IRenderedComponent<CommandEditorPage> cut = Render<CommandEditorPage>(parameters => parameters.Add(page => page.Id, id));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=section-general], [data-testid=editor-not-found], [data-testid=error-message]")), Patience);
        return cut;
    }
}
