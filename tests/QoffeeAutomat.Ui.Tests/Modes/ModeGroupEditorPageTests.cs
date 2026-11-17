using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Modes;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Modes;

/// <summary>
/// The mode group editor (T717) against a fake agent: a group is created with modes whose enter and exit steps use the step
/// editor of the sequence action, the editor's own problems and the agent's are shown at their fields, and a stored group is
/// saved against its revision.
/// </summary>
[Trait("Category", "Modes")]
public sealed class ModeGroupEditorPageTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeAgentApi _api = new FakeAgentApi().SignedIn();
    private readonly FakeBrowserInterop _browser = new();

    public ModeGroupEditorPageTests()
    {
        UiTestSetup.Register(this, _api, _browser);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact]
    public void A_new_group_with_two_modes_and_their_steps_is_created()
    {
        IRenderedComponent<ModeGroupEditorPage> cut = RenderEditor();

        cut.Find("[data-testid=mode-field-name] input").Input("PC modes");
        Assert.Equal("pc_modes", cut.Find("[data-testid=mode-field-id] input").GetAttribute("value"));

        cut.Find("[data-testid=mode-add]").Click();
        cut.Find("[data-testid=mode-0-name] input").Input("Normal");
        Assert.Equal("normal", cut.Find("[data-testid=mode-0-id] input").GetAttribute("value"));
        cut.Find("[data-testid=mode-add]").Click();
        cut.Find("[data-testid=mode-1-name] input").Input("Study");

        cut.Find("[data-testid=mode-0-enter-add-step-command]").Click();
        cut.Find("select[data-testid=mode-0-enter-step-command-0]").Change("lock_screen");
        cut.Find("[data-testid=mode-1-exit-add-step-delay]").Click();
        cut.Find("[data-testid=mode-1-exit-step-delay-0] input").Input("3");

        cut.Find("select[data-testid=mode-field-default]").Change("1");
        cut.Find("select[data-testid=mode-field-onboot]").Change(ModeBootBehaviors.ApplyDefault);
        cut.Find("select[data-testid=mode-field-onerror]").Change(SequenceErrorModes.Continue);

        cut.Find("[data-testid=mode-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=mode-saved]")), Patience);
        ModeGroupDocument created = Assert.Single(_api.CreatedModeGroups);
        Assert.Equal("pc_modes", created.Id);
        Assert.Equal("PC modes", created.Name);
        Assert.Equal("study", created.DefaultMode);
        Assert.Equal(ModeBootBehaviors.ApplyDefault, created.OnBoot);
        Assert.Equal(SequenceErrorModes.Continue, created.OnError);
        Assert.Equal(["normal", "study"], created.Modes.Select(mode => mode.Id).ToArray());
        Assert.Equal("lock_screen", Assert.Single(created.Modes[0].Enter!).Command);
        Assert.Empty(created.Modes[0].Exit!);
        Assert.Equal(3.0, Assert.Single(created.Modes[1].Exit!).DelaySeconds);
        Assert.Empty(_api.ReplacedModeGroups);
        Assert.EndsWith("/modes/pc_modes", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
        Assert.True(cut.Find("[data-testid=mode-field-id] input").HasAttribute("disabled"));
    }

    [Fact]
    public void The_steps_of_a_mode_can_run_the_commands_that_the_agent_has()
    {
        IRenderedComponent<ModeGroupEditorPage> cut = RenderEditor();
        cut.Find("[data-testid=mode-add]").Click();
        cut.Find("[data-testid=mode-0-enter-add-step-command]").Click();

        string[] offered = [.. cut.FindAll("select[data-testid=mode-0-enter-step-command-0] option").Select(option => option.GetAttribute("value") ?? string.Empty)];

        Assert.Equal(["", "backup", "lock_screen", "night_mode", "open_notes"], offered);
    }

    [Fact]
    public void The_default_mode_is_chosen_from_the_modes_and_starts_with_the_first_one()
    {
        IRenderedComponent<ModeGroupEditorPage> cut = RenderEditor();
        cut.Find("[data-testid=mode-add]").Click();
        cut.Find("[data-testid=mode-add]").Click();

        string[] values = [.. cut.FindAll("select[data-testid=mode-field-default] option").Select(option => option.GetAttribute("value") ?? string.Empty)];

        Assert.Equal(["", "0", "1"], values);
        Assert.Equal("0", Selected(cut, "mode-field-default"));
    }

    [Fact]
    public void A_group_without_a_mode_is_marked_and_nothing_is_sent()
    {
        IRenderedComponent<ModeGroupEditorPage> cut = RenderEditor();
        cut.Find("[data-testid=mode-field-name] input").Input("PC modes");

        cut.Find("[data-testid=mode-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='draft-error:/modes']")), Patience);
        Assert.Equal(ModeGroupDraft.NoModesCode, cut.Find("[data-testid='draft-error:/modes']").GetAttribute("data-code"));
        Assert.NotNull(cut.Find("[data-testid=mode-validation-summary]"));
        Assert.Empty(_api.CreatedModeGroups);
    }

    [Fact]
    public void Two_modes_with_the_same_id_are_marked_at_the_second_one_and_nothing_is_sent()
    {
        IRenderedComponent<ModeGroupEditorPage> cut = RenderEditor();
        cut.Find("[data-testid=mode-field-name] input").Input("PC modes");
        cut.Find("[data-testid=mode-add]").Click();
        cut.Find("[data-testid=mode-add]").Click();
        cut.Find("[data-testid=mode-0-id] input").Input("same");
        cut.Find("[data-testid=mode-1-id] input").Input("same");

        cut.Find("[data-testid=mode-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='draft-error:/modes/1/id']")), Patience);
        Assert.Equal(ModeGroupDraft.DuplicateModeCode, cut.Find("[data-testid='draft-error:/modes/1/id']").GetAttribute("data-code"));
        Assert.Empty(_api.CreatedModeGroups);
    }

    [Fact]
    public void Variables_that_are_no_json_object_are_marked_at_the_step_and_nothing_is_sent()
    {
        IRenderedComponent<ModeGroupEditorPage> cut = RenderEditor();
        cut.Find("[data-testid=mode-field-name] input").Input("PC modes");
        cut.Find("[data-testid=mode-add]").Click();
        cut.Find("[data-testid=mode-0-name] input").Input("Normal");
        cut.Find("[data-testid=mode-0-exit-add-step-command]").Click();
        cut.Find("select[data-testid=mode-0-exit-step-command-0]").Change("lock_screen");
        cut.Find("[data-testid=mode-0-exit-step-vars-0] input").Input("[1]");

        cut.Find("[data-testid=mode-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='draft-error:/modes/0/exit/0/vars']")), Patience);
        Assert.Empty(_api.CreatedModeGroups);
    }

    [Fact]
    public void A_stored_group_is_loaded_into_the_form_and_saved_against_its_revision()
    {
        _api.ModeGroups = [StoredGroup()];

        IRenderedComponent<ModeGroupEditorPage> cut = RenderEditor("pc");

        Assert.Equal("PC modes", cut.Find("[data-testid=mode-field-name] input").GetAttribute("value"));
        Assert.True(cut.Find("[data-testid=mode-field-id] input").HasAttribute("disabled"));
        Assert.Equal("study", cut.Find("[data-testid=mode-1-id] input").GetAttribute("value"));
        Assert.Equal("1", Selected(cut, "mode-field-default"));
        Assert.Equal(ModeBootBehaviors.Keep, Selected(cut, "mode-field-onboot"));
        Assert.Equal("lock_screen", Selected(cut, "mode-0-enter-step-command-0"));

        cut.Find("[data-testid=mode-field-name] input").Input("PC modes 2");
        cut.Find("[data-testid=mode-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=mode-saved]")), Patience);
        ModeGroupDocument replaced = Assert.Single(_api.ReplacedModeGroups);
        Assert.Equal("pc", replaced.Id);
        Assert.Equal("PC modes 2", replaced.Name);
        Assert.Equal(1, replaced.Revision);
        Assert.Equal("study", replaced.DefaultMode);
        Assert.Equal("lock_screen", Assert.Single(replaced.Modes[0].Enter!).Command);
        Assert.Empty(_api.CreatedModeGroups);
        Assert.Equal(2, _api.ModeGroups.Single().Revision);
    }

    [Fact]
    public void Renaming_a_stored_mode_keeps_its_id()
    {
        _api.ModeGroups = [StoredGroup()];
        IRenderedComponent<ModeGroupEditorPage> cut = RenderEditor("pc");

        cut.Find("[data-testid=mode-1-name] input").Input("Learning");

        Assert.Equal("study", cut.Find("[data-testid=mode-1-id] input").GetAttribute("value"));
    }

    [Fact]
    public void Removing_and_moving_modes_changes_the_form()
    {
        _api.ModeGroups = [StoredGroup()];
        IRenderedComponent<ModeGroupEditorPage> cut = RenderEditor("pc");
        Assert.Equal(2, cut.FindAll("[data-testid^=mode-card-]").Count);

        cut.Find("[data-testid=mode-down-0]").Click();

        Assert.Equal("study", cut.Find("[data-testid=mode-0-id] input").GetAttribute("value"));
        Assert.Equal("normal", cut.Find("[data-testid=mode-1-id] input").GetAttribute("value"));

        cut.Find("[data-testid=mode-remove-0]").Click();

        Assert.Single(cut.FindAll("[data-testid^=mode-card-]"));
        Assert.Equal("normal", cut.Find("[data-testid=mode-0-id] input").GetAttribute("value"));
    }

    [Fact]
    public void The_problems_that_the_agent_reports_on_save_are_shown_at_their_fields()
    {
        _api.ModeGroups = [StoredGroup()];
        _api.ModeReplaceFailure = new ApiException(
            422,
            Samples.Problem(
                422,
                "validation_failed",
                "The mode group is not valid.",
                new ApiValidationError("/modes/0/enter/0/command", "unknown_reference", "There is no command with this id.")));
        IRenderedComponent<ModeGroupEditorPage> cut = RenderEditor("pc");

        cut.Find("[data-testid=mode-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='draft-error:/modes/0/enter/0/command']")), Patience);
        Assert.Contains("1", cut.Find("[data-testid=mode-validation-summary]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=mode-saved]"));
    }

    [Fact]
    public void An_id_that_exists_is_reported_at_the_id_field()
    {
        _api.ModeGroups = [Samples.ModeGroup("pc_modes", "Other")];
        IRenderedComponent<ModeGroupEditorPage> cut = RenderEditor();
        cut.Find("[data-testid=mode-field-name] input").Input("PC modes");
        cut.Find("[data-testid=mode-add]").Click();
        cut.Find("[data-testid=mode-0-name] input").Input("Normal");

        cut.Find("[data-testid=mode-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='draft-error:/id']")), Patience);
        Assert.Equal("group_exists", cut.Find("[data-testid='draft-error:/id']").GetAttribute("data-code"));
        Assert.Empty(cut.FindAll("[data-testid=mode-saved]"));
    }

    [Fact]
    public void A_group_that_was_changed_meanwhile_is_not_overwritten()
    {
        _api.ModeGroups = [StoredGroup()];
        IRenderedComponent<ModeGroupEditorPage> cut = RenderEditor("pc");
        _api.ModeGroups[0] = _api.ModeGroups[0] with { Revision = 5 };

        cut.Find("[data-testid=mode-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=error-message]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=mode-saved]"));
        Assert.Equal(5, _api.ModeGroups.Single().Revision);
    }

    [Fact]
    public void An_unknown_group_says_so()
    {
        IRenderedComponent<ModeGroupEditorPage> cut = RenderEditor("nope");

        Assert.NotNull(cut.Find("[data-testid=mode-not-found]"));
        Assert.True(cut.Find("[data-testid=mode-save]").HasAttribute("disabled"));
    }

    [Fact]
    public void A_group_that_cannot_be_read_shows_the_error_and_no_form()
    {
        _api.ModeGroupsFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Something broke."));

        IRenderedComponent<ModeGroupEditorPage> cut = RenderEditor("pc");

        Assert.NotNull(cut.Find("[data-testid=error-message]"));
        Assert.Empty(cut.FindAll("[data-testid=mode-section-general]"));
        Assert.True(cut.Find("[data-testid=mode-save]").HasAttribute("disabled"));
    }

    private static ModeGroupDocument StoredGroup()
    {
        return Samples.ModeGroup("pc", "PC modes", true, "normal", "study") with
        {
            DefaultMode = "study",
            OnBoot = ModeBootBehaviors.Keep,
            Modes =
            [
                new ModeDefinition("normal", "Normal", [new SequenceStep(Command: "lock_screen")], []),
                new ModeDefinition("study", "Study"),
            ],
        };
    }

    /// <summary>The value of a native select: Blazor writes it as an attribute of the select or marks the option.</summary>
    private static string? Selected(IRenderedComponent<ModeGroupEditorPage> cut, string testId)
    {
        AngleSharp.Dom.IElement select = cut.Find($"select[data-testid={testId}]");
        return select.GetAttribute("value") ?? select.QuerySelector("option[selected]")?.GetAttribute("value");
    }

    private IRenderedComponent<ModeGroupEditorPage> RenderEditor(string? id = null)
    {
        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        IRenderedComponent<ModeGroupEditorPage> cut = id is null
            ? Render<ModeGroupEditorPage>()
            : Render<ModeGroupEditorPage>(parameters => parameters.Add(page => page.Id, id));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=mode-section-general], [data-testid=mode-not-found], [data-testid=error-message]")), Patience);
        return cut;
    }
}
