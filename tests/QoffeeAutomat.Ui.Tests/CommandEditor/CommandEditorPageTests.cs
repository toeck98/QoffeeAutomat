using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.CommandEditor;
using QoffeeAutomat.Ui.Components.Pages.Commands;
using QoffeeAutomat.Ui.Components.Services;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.CommandEditor;

/// <summary>
/// The command editor (T404) against a fake agent: creating a PowerShell command with an enum and an int parameter, testing
/// it, binding it to a select control and saving it without leaving the editor; the checks of the agent at the fields;
/// the other action types; and the guard against leaving with unsaved changes.
/// </summary>
[Trait("Category", "CommandEditor")]
public sealed class CommandEditorPageTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeAgentApi _api = new FakeAgentApi().SignedIn();
    private readonly FakeBrowserInterop _browser = new();

    public CommandEditorPageTests()
    {
        UiTestSetup.Register(this, _api, _browser);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact]
    public void Monaco_is_replaced_by_a_text_area_where_there_is_no_browser()
    {
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _);

        Assert.Equal("text", cut.Find("[data-testid=code-editor]").GetAttribute("data-editor"));
        Assert.NotNull(cut.Find("[data-testid=code-editor] textarea"));
        Assert.True(new CommandEditorOptions().UseMonaco);
    }

    [Fact]
    public void A_powershell_command_with_an_enum_and_an_int_parameter_is_tested_bound_to_a_select_and_saved_in_the_editor()
    {
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _);

        // General data: the id follows the name.
        cut.Find("[data-testid=field-name] input").Input("Set profile");
        Assert.Equal("set_profile", cut.Find("[data-testid=field-id] input").GetAttribute("value"));
        cut.Find("[data-testid=code-editor] textarea").Input("Write-Output \"$Profile $Count\"");

        // The parameter table: an enum and an int with a range.
        cut.Find("[data-testid=add-parameter]").Click();
        cut.Find("[data-testid=param-0-name] input").Input("Profile");
        cut.Find("[data-testid=param-0-type]").Change(ParameterTypes.Choice);
        cut.Find("[data-testid=param-0-options] textarea").Input("Normal\nTV");
        cut.Find("[data-testid=param-0-required]").Change(true);
        cut.Find("[data-testid=add-parameter]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=param-1-name] input")), Patience);
        cut.Find("[data-testid=param-1-name] input").Input("Count");
        cut.Find("[data-testid=param-1-type]").Change(ParameterTypes.WholeNumber);
        cut.Find("[data-testid=param-1-min] input").Input("1");
        cut.Find("[data-testid=param-1-max] input").Input("5");

        // The button writes the param() block from the table into the code.
        cut.Find("[data-testid=insert-param-block]").Click();

        // Only the controls that these parameters allow are offered.
        string[] offered = [.. cut.Find("[data-testid=ha-type]").QuerySelectorAll("option").Select(option => option.GetAttribute("value")!)];
        Assert.Equal([HaControlTypes.None, HaControlTypes.Select, HaControlTypes.Number], offered);
        cut.Find("[data-testid=ha-type]").Change(HaControlTypes.Select);
        string[] parameters = [.. cut.Find("[data-testid=ha-parameter]").QuerySelectorAll("option").Select(option => option.GetAttribute("value")!)];
        Assert.Equal(["", "Profile"], parameters);
        cut.Find("[data-testid=ha-parameter]").Change("Profile");

        // The test panel runs the unsaved draft with the entered variables and shows the live output.
        cut.Find("[data-testid=test-var-Profile]").Change("TV");
        cut.Find("[data-testid=test-var-Count] input").Input("3");
        cut.Find("[data-testid=test-form]").Submit();
        cut.WaitForAssertion(() => Assert.Single(_api.TestRequests), Patience);
        (CommandDocument draft, IReadOnlyDictionary<string, JsonElement> variables) = _api.TestRequests[0];
        Assert.Equal("set_profile", draft.Id);
        Assert.Equal("TV", variables["Profile"].GetString());
        Assert.Equal(3, variables["Count"].GetInt32());
        Assert.Empty(_api.Created);

        _api.PushRun(new RunStreamItem(Output: new RunOutputEvent("stdout", "TV 3\n", Samples.Now)));
        _api.PushRun(new RunStreamItem(End: new RunEndEvent("test-1", true, 0, null, null, 120, false)));
        cut.WaitForAssertion(() => Assert.Equal("succeeded", cut.Find("[data-testid=test-result]").GetAttribute("data-state")), Patience);
        Assert.Contains("TV 3", cut.Find("[data-testid=test-output]").TextContent, StringComparison.Ordinal);

        // The preview shows the entity and the YAML with the values that were entered.
        Assert.Equal("select", cut.Find("[data-testid=ha-entity]").GetAttribute("data-platform"));
        Assert.Contains("Normal, TV", cut.Find("[data-testid=ha-entity-options]").TextContent, StringComparison.Ordinal);
        string yaml = cut.Find("[data-testid=ha-yaml]").TextContent;
        Assert.Contains("action: qoffeeautomat.run_command", yaml, StringComparison.Ordinal);
        Assert.Contains("command: \"set_profile\"", yaml, StringComparison.Ordinal);
        Assert.Contains("Profile: \"TV\"", yaml, StringComparison.Ordinal);
        Assert.Contains("Count: 3", yaml, StringComparison.Ordinal);
        cut.Find("[data-testid=copy-yaml]").Click();
        cut.WaitForAssertion(() => Assert.Equal(yaml, Assert.Single(_browser.Copied)), Patience);

        // Saving creates the command and the editor stays.
        Assert.NotNull(cut.Find("[data-testid=dirty-indicator]"));
        cut.Find("[data-testid=editor-save]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=editor-saved]")), Patience);

        CommandDocument saved = Assert.Single(_api.Created);
        Assert.Equal("set_profile", saved.Id);
        Assert.Equal("Set profile", saved.Name);
        var action = Assert.IsType<PowerShellAction>(saved.Action);
        Assert.Contains("param(", action.Code, StringComparison.Ordinal);
        Assert.Contains("[Parameter(Mandatory)] [ValidateSet('Normal', 'TV')] [string]$Profile", action.Code, StringComparison.Ordinal);
        Assert.Contains("[int]$Count", action.Code, StringComparison.Ordinal);
        Assert.Contains("Write-Output", action.Code, StringComparison.Ordinal);
        Assert.Equal(2, saved.Parameters!.Count);
        Assert.Equal(ParameterTypes.Choice, saved.Parameters[0].Type);
        Assert.Equal(["Normal", "TV"], saved.Parameters[0].Options);
        Assert.True(saved.Parameters[0].Required);
        Assert.Equal(ParameterTypes.WholeNumber, saved.Parameters[1].Type);
        Assert.Equal(1, saved.Parameters[1].Min);
        Assert.Equal(5, saved.Parameters[1].Max);
        Assert.Equal(HaControlTypes.Select, saved.HaControl?.Type);
        Assert.Equal("Profile", saved.HaControl?.Parameter);

        Assert.Empty(cut.FindAll("[data-testid=dirty-indicator]"));
        Assert.True(cut.Find("[data-testid=field-id] input").HasAttribute("disabled"));
        Assert.EndsWith("/commands/set_profile", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
        Assert.Contains(_api.Commands, command => command.Id == "set_profile");
    }

    [Fact]
    public void The_draft_is_validated_once_after_the_typing_has_rested_and_the_errors_are_shown_at_their_fields()
    {
        Services.GetRequiredService<CommandEditorOptions>().ValidationDelay = TimeSpan.FromMilliseconds(400);
        _api.ValidationHandler = _ => new ValidationReport(
            false,
            [
                new ApiValidationError("/timeoutSeconds", "range", "timeoutSeconds must be between 1 and 86400."),
                new ApiValidationError("/parameters/0/pattern", "invalid", "The pattern is not a valid regular expression."),
                new ApiValidationError("/action/code", "required", "Inline PowerShell needs code."),
            ]);
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _);
        cut.Find("[data-testid=add-parameter]").Click();

        cut.Find("[data-testid=field-name] input").Input("S");
        cut.Find("[data-testid=field-name] input").Input("Se");
        cut.Find("[data-testid=field-name] input").Input("Set");

        cut.WaitForAssertion(() => Assert.Equal("invalid", cut.Find("[data-testid=validation-status]").GetAttribute("data-state")), Patience);
        Assert.Equal("Set", _api.Validated.Last().Name);
        Assert.True(_api.Validated.Count <= 2, "The typing was validated " + _api.Validated.Count + " times.");
        Assert.Contains("between 1 and 86400", cut.Find("[data-testid='draft-error:/timeoutSeconds']").TextContent, StringComparison.Ordinal);
        Assert.Contains("not a valid regular expression", cut.Find("[data-testid='draft-error:/parameters/0/pattern']").TextContent, StringComparison.Ordinal);
        Assert.Contains("needs code", cut.Find("[data-testid='draft-error:/action/code']").TextContent, StringComparison.Ordinal);
        Assert.Contains("3", cut.Find("[data-testid=validation-summary]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_draft_that_the_agent_accepts_is_marked_as_valid_and_the_errors_go_away()
    {
        Services.GetRequiredService<CommandEditorOptions>().ValidationDelay = TimeSpan.FromMilliseconds(10);
        bool valid = false;
        _api.ValidationHandler = _ => valid
            ? new ValidationReport(true, [])
            : new ValidationReport(false, [new ApiValidationError("/name", "required", "A name is required.")]);
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _);

        cut.Find("[data-testid=field-description]  textarea").Input("x");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='draft-error:/name']")), Patience);

        valid = true;
        cut.Find("[data-testid=field-name] input").Input("Fine");

        cut.WaitForAssertion(() => Assert.Equal("valid", cut.Find("[data-testid=validation-status]").GetAttribute("data-state")), Patience);
        Assert.Empty(cut.FindAll("[data-testid='draft-error:/name']"));
        Assert.Empty(cut.FindAll("[data-testid=validation-summary]"));
    }

    [Fact]
    public void A_number_that_is_no_number_is_marked_at_once_and_blocks_saving()
    {
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _);
        cut.Find("[data-testid=field-name] input").Input("Draft");
        cut.Find("[data-testid=field-timeout] input").Input("soon");

        cut.Find("[data-testid=editor-save]").Click();

        Assert.Contains("not a number", cut.Find("[data-testid='draft-error:/timeoutSeconds']").TextContent, StringComparison.Ordinal);
        Assert.Empty(_api.Created);
    }

    [Fact]
    public void A_save_that_the_agent_refuses_shows_the_errors_at_the_fields_and_keeps_the_draft()
    {
        _api.CreateFailure = new ApiException(
            422,
            Samples.Problem(
                422,
                "validation_failed",
                "The document is not valid.",
                new ApiValidationError("/id", "invalid", "The id must match the pattern."),
                new ApiValidationError("/haControl/parameter", "required", "A select control needs a parameter of type enum.")));
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _);
        cut.Find("[data-testid=field-name] input").Input("Set profile");

        cut.Find("[data-testid=editor-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='draft-error:/id']")), Patience);
        Assert.Contains("must match the pattern", cut.Find("[data-testid='draft-error:/id']").TextContent, StringComparison.Ordinal);
        Assert.Contains("needs a parameter", cut.Find("[data-testid='draft-error:/haControl/parameter']").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=editor-saved]"));
        Assert.NotNull(cut.Find("[data-testid=dirty-indicator]"));
        // The draft is kept: the id that follows the name is still there. (The name field itself does not push its text back
        // into the DOM while it is typed in, MudBlazor's TextUpdateSuppression, so its value attribute is no evidence.)
        Assert.Equal("set_profile", cut.Find("[data-testid=field-id] input").GetAttribute("value"));
    }

    [Fact]
    public void An_id_that_exists_is_reported_at_the_id_field()
    {
        _api.Commands.Add(Samples.Command("set_profile"));
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _);
        cut.Find("[data-testid=field-name] input").Input("Set profile");

        cut.Find("[data-testid=editor-save]").Click();

        cut.WaitForAssertion(() => Assert.Contains("exists already", cut.Find("[data-testid='draft-error:/id']").TextContent, StringComparison.Ordinal), Patience);
    }

    [Fact]
    public void A_failure_of_the_agent_that_is_not_about_a_field_is_shown_as_an_error()
    {
        _api.CreateFailure = new ApiException(500, Samples.Problem(500, "internal", "The agent failed."));
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _);
        cut.Find("[data-testid=field-name] input").Input("Set profile");

        cut.Find("[data-testid=editor-save]").Click();

        cut.WaitForAssertion(() => Assert.Contains("The agent failed.", cut.Find("[data-testid=error-message]").TextContent, StringComparison.Ordinal), Patience);
    }

    [Fact]
    public void A_test_that_the_agent_refuses_marks_the_draft_in_the_editor_and_the_variables_in_the_panel()
    {
        _api.TestHandler = (_, _) => new ApiException(
            422,
            Samples.Problem(
                422,
                "validation_failed",
                "Not valid.",
                new ApiValidationError("/command/action/code", "required", "Inline PowerShell needs code."),
                new ApiValidationError("/vars/Count", "range", "Too big.")));
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _);
        cut.Find("[data-testid=field-name] input").Input("Set profile");
        cut.Find("[data-testid=add-parameter]").Click();
        cut.Find("[data-testid=param-0-name] input").Input("Count");
        cut.Find("[data-testid=param-0-type]").Change(ParameterTypes.WholeNumber);

        cut.Find("[data-testid=test-form]").Submit();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=test-draft-rejected]")), Patience);
        Assert.NotNull(cut.Find("[data-testid=test-rejected]"));
        Assert.Contains("range", cut.Find("[data-testid=test-field-error-Count]").TextContent, StringComparison.Ordinal);
        Assert.Contains("needs code", cut.Find("[data-testid='draft-error:/action/code']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_variable_that_breaks_its_rules_is_not_sent_to_the_test()
    {
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _);
        cut.Find("[data-testid=add-parameter]").Click();
        cut.Find("[data-testid=param-0-name] input").Input("Count");
        cut.Find("[data-testid=param-0-type]").Change(ParameterTypes.WholeNumber);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=param-0-max] input")), Patience);
        cut.Find("[data-testid=param-0-max] input").Input("5");
        cut.Find("[data-testid=test-var-Count] input").Input("9");

        cut.Find("[data-testid=test-form]").Submit();

        Assert.NotNull(cut.Find("[data-testid=test-field-error-Count]"));
        Assert.Empty(_api.TestRequests);
    }

    [Fact]
    public async Task The_button_offers_itself_only_when_no_required_parameter_lacks_a_default()
    {
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _);
        Assert.Equal([HaControlTypes.None, HaControlTypes.Button], OptionValues(cut, "ha-type"));

        cut.Find("[data-testid=add-parameter]").Click();
        cut.Find("[data-testid=param-0-name] input").Input("Flag");
        await cut.InvokeAsync(() => { });
        cut.Find("[data-testid=param-0-type]").Change(ParameterTypes.Flag);
        cut.WaitForAssertion(() => Assert.Equal([HaControlTypes.None, HaControlTypes.Button, HaControlTypes.Switch], OptionValues(cut, "ha-type")), Patience);

        cut.Find("[data-testid=param-0-required]").Change(true);
        cut.WaitForAssertion(() => Assert.Equal([HaControlTypes.None, HaControlTypes.Switch], OptionValues(cut, "ha-type")), Patience);
    }

    [Fact]
    public void The_state_metric_of_a_control_is_chosen_from_the_metrics_with_its_type()
    {
        _api.Catalog = new MetricList(
        [
            Metric("custom.mode", "enum"),
            Metric("custom.on", "bool"),
            Metric("cpu.usage", "number"),
        ]);
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _);
        cut.Find("[data-testid=add-parameter]").Click();
        cut.Find("[data-testid=param-0-name] input").Input("Mode");
        cut.Find("[data-testid=param-0-type]").Change(ParameterTypes.Choice);

        cut.Find("[data-testid=ha-type]").Change(HaControlTypes.Select);

        cut.WaitForAssertion(() => Assert.Equal(["", "custom.mode"], OptionValues(cut, "ha-state-metric")), Patience);
        cut.Find("[data-testid=ha-state-metric]").Change("custom.mode");
        Assert.Contains("custom.mode", cut.Find("[data-testid=ha-entity-state]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void All_three_run_contexts_are_explained_and_the_user_is_warned_when_elevation_is_not_possible()
    {
        _api.Status = Samples.Status() with { Session = new SessionStatus(true, "alice", false, UserIsAdministrator: false) };
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _);

        Assert.Contains("administrator", cut.Find("[data-testid=runas-help-user_elevated]").TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(cut.Find("[data-testid=runas-help-system]").TextContent);
        Assert.NotEmpty(cut.Find("[data-testid=runas-help-user]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=runas-not-admin]"));

        cut.Find("[data-testid=field-runas]").Change(RunAsValues.UserElevated);

        Assert.NotNull(cut.Find("[data-testid=runas-not-admin]"));
        Assert.Equal("true", cut.Find("[data-testid=runas-help-user_elevated]").GetAttribute("data-selected"));
    }

    [Fact]
    public void A_process_command_is_saved_with_its_arguments_one_per_line()
    {
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _);
        cut.Find("[data-testid=field-name] input").Input("Open notes");
        cut.Find("[data-testid=field-action-type]").Change(ActionTypes.Process);

        cut.Find("[data-testid=field-process-file] input").Input("notepad.exe");
        cut.Find("[data-testid=field-process-args] textarea").Input("{{File}}\n--new");
        cut.Find("[data-testid=editor-save]").Click();

        cut.WaitForAssertion(() => Assert.Single(_api.Created), Patience);
        var action = Assert.IsType<ProcessAction>(_api.Created[0].Action);
        Assert.Equal("notepad.exe", action.File);
        Assert.Equal(["{{File}}", "--new"], action.Args);
    }

    [Fact]
    public void A_builtin_command_takes_the_name_of_a_suggestion()
    {
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _);
        cut.Find("[data-testid=field-name] input").Input("Power off");
        cut.Find("[data-testid=field-action-type]").Change(ActionTypes.Builtin);

        cut.Find("[data-testid='builtin-power.shutdown']").Click();
        cut.Find("[data-testid=editor-save]").Click();

        cut.WaitForAssertion(() => Assert.Single(_api.Created), Patience);
        Assert.Equal("power.shutdown", Assert.IsType<BuiltinAction>(_api.Created[0].Action).Name);
    }

    [Fact]
    public async Task A_sequence_runs_the_commands_that_exist_with_waits_between()
    {
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _);
        cut.Find("[data-testid=field-name] input").Input("Good night");
        await cut.InvokeAsync(() => { });
        cut.Find("[data-testid=field-action-type]").Change(ActionTypes.Sequence);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=add-step-command]")), Patience);
        cut.Find("[data-testid=add-step-command]").Click();
        cut.Find("[data-testid=step-command-0]").Change("lock_screen");
        cut.Find("[data-testid=add-step-delay]").Click();
        cut.Find("[data-testid=step-delay-1] input").Input("2.5");
        cut.Find("[data-testid=add-step-command]").Click();
        cut.Find("[data-testid=step-command-2]").Change("backup");
        cut.Find("[data-testid=step-vars-2] input").Input("{\"Target\":\"D:\\\\\"}");
        cut.Find("[data-testid=step-up-2]").Click();
        cut.Find("[data-testid=field-sequence-onerror]").Change(SequenceErrorModes.Continue);
        cut.Find("[data-testid=editor-save]").Click();

        cut.WaitForAssertion(() => Assert.Single(_api.Created), Patience);
        var action = Assert.IsType<SequenceAction>(_api.Created[0].Action);
        Assert.Equal(SequenceErrorModes.Continue, action.OnError);
        Assert.Equal(3, action.Steps.Count);
        Assert.Equal("lock_screen", action.Steps[0].Command);
        Assert.Equal("backup", action.Steps[1].Command);
        Assert.Equal("D:\\", action.Steps[1].Vars!["Target"].GetString());
        Assert.Equal(2.5, action.Steps[2].DelaySeconds);
    }

    [Fact]
    public void A_script_file_can_be_chosen_from_the_scripts_of_the_agent()
    {
        _api.Scripts = new ScriptSummaryList([new ScriptSummary("set_profile.ps1", "abc", Samples.Now)]);
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _);
        cut.Find("[data-testid=field-name] input").Input("Set profile");

        cut.Find("[data-testid=field-ps-source]").Change(PowerShellSources.File);
        cut.Find("[data-testid='script-set_profile.ps1']").Click();
        cut.Find("[data-testid=editor-save]").Click();

        cut.WaitForAssertion(() => Assert.Single(_api.Created), Patience);
        var action = Assert.IsType<PowerShellAction>(_api.Created[0].Action);
        Assert.Equal(PowerShellSources.File, action.Source);
        Assert.Equal("set_profile.ps1", action.Script);
        Assert.Null(action.Code);
    }

    [Fact]
    public void An_existing_command_is_loaded_and_saved_against_its_revision()
    {
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _, "backup");

        Assert.Contains("Back up documents", cut.Find("[data-testid=editor-title]").TextContent, StringComparison.Ordinal);
        Assert.True(cut.Find("[data-testid=field-id] input").HasAttribute("disabled"));
        Assert.Equal("backup", cut.Find("[data-testid=field-id] input").GetAttribute("value"));
        Assert.Empty(cut.FindAll("[data-testid=dirty-indicator]"));

        cut.Find("[data-testid=field-description] textarea").Input("Copies everything.");
        cut.Find("[data-testid=editor-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=editor-saved]")), Patience);
        CommandDocument replaced = Assert.Single(_api.Replaced);
        Assert.Equal(1, replaced.Revision);
        Assert.Equal("Copies everything.", replaced.Description);
        Assert.Equal(2, _api.Commands.Single(command => command.Id == "backup").Revision);
        Assert.Empty(_api.Created);
        Assert.Empty(cut.FindAll("[data-testid=dirty-indicator]"));

        // A second save goes against the new revision.
        cut.Find("[data-testid=field-description] textarea").Input("Copies more.");
        cut.Find("[data-testid=editor-save]").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, _api.Replaced.Count), Patience);
        Assert.Equal(2, _api.Replaced[1].Revision);
    }

    [Fact]
    public void A_command_that_somebody_else_saved_in_the_meantime_is_not_overwritten()
    {
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _, "backup");
        int index = _api.Commands.FindIndex(command => command.Id == "backup");
        _api.Commands[index] = _api.Commands[index] with { Revision = 5 };

        cut.Find("[data-testid=field-description] textarea").Input("Mine.");
        cut.Find("[data-testid=editor-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=error-display]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=editor-saved]"));
        Assert.NotNull(cut.Find("[data-testid=dirty-indicator]"));
    }

    [Fact]
    public void An_unknown_command_is_reported_and_nothing_can_be_saved()
    {
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _, "gone");

        Assert.NotNull(cut.Find("[data-testid=editor-not-found]"));
        Assert.True(cut.Find("[data-testid=editor-save]").HasAttribute("disabled"));
    }

    [Fact]
    public void Leaving_with_unsaved_changes_asks_first_and_staying_keeps_the_editor()
    {
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out IRenderedComponent<MudDialogProvider> dialogs);
        NavigationManager navigation = Services.GetRequiredService<NavigationManager>();
        string start = navigation.Uri;
        cut.Find("[data-testid=field-name] input").Input("Draft");

        navigation.NavigateTo("/commands");
        dialogs.WaitForElement(".mud-message-box__cancel-button", Patience).Click();

        cut.WaitForAssertion(() => Assert.Empty(dialogs.FindAll(".mud-message-box__cancel-button")), Patience);
        Assert.Equal(start, navigation.Uri);
        Assert.Equal("draft", cut.Find("[data-testid=field-id] input").GetAttribute("value"));
        Assert.NotNull(cut.Find("[data-testid=dirty-indicator]"));
    }

    [Fact]
    public void Leaving_with_unsaved_changes_goes_on_when_the_user_confirms()
    {
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out IRenderedComponent<MudDialogProvider> dialogs);
        NavigationManager navigation = Services.GetRequiredService<NavigationManager>();
        cut.Find("[data-testid=field-name] input").Input("Draft");

        navigation.NavigateTo("/commands");
        dialogs.WaitForElement(".mud-message-box__yes-button", Patience).Click();

        cut.WaitForAssertion(() => Assert.EndsWith("/commands", navigation.Uri, StringComparison.Ordinal), Patience);
    }

    [Fact]
    public void Leaving_without_changes_asks_nothing()
    {
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out IRenderedComponent<MudDialogProvider> dialogs);
        NavigationManager navigation = Services.GetRequiredService<NavigationManager>();

        navigation.NavigateTo("/commands");

        cut.WaitForAssertion(() => Assert.EndsWith("/commands", navigation.Uri, StringComparison.Ordinal), Patience);
        Assert.Empty(dialogs.FindAll(".mud-message-box__yes-button"));
    }

    [Fact]
    public void A_changed_draft_is_clean_again_when_the_change_is_undone()
    {
        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _);

        cut.Find("[data-testid=field-description] textarea").Input("Something");
        Assert.NotNull(cut.Find("[data-testid=dirty-indicator]"));
        cut.Find("[data-testid=field-description] textarea").Input(string.Empty);

        Assert.Empty(cut.FindAll("[data-testid=dirty-indicator]"));
    }

    [Fact]
    public void The_list_of_commands_offers_a_new_command()
    {
        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();

        IRenderedComponent<CommandsPage> cut = Render<CommandsPage>();

        cut.WaitForAssertion(() => Assert.Equal("/commands/new", cut.Find("[data-testid=command-new]").GetAttribute("href")), Patience);
    }

    [Fact]
    public void The_editor_has_texts_in_german()
    {
        Services.GetRequiredService<Localizer>().SetLanguage("de");

        IRenderedComponent<CommandEditorPage> cut = RenderEditor(out _);

        Assert.Equal("Neuer Befehl", cut.Find("[data-testid=editor-title]").TextContent);
        Assert.Contains("Läuft als", cut.Markup, StringComparison.Ordinal);
    }

    private static string[] OptionValues(IRenderedComponent<CommandEditorPage> cut, string testId)
    {
        return [.. cut.Find("[data-testid=" + testId + "]").QuerySelectorAll("option").Select(option => option.GetAttribute("value")!)];
    }

    private static MetricInfo Metric(string id, string valueType)
    {
        return new MetricInfo(id, id, "sensor", valueType, "system", null, null, null, null, null, null, null, true, true, true, 5, null, false, false, null);
    }

    /// <summary>Renders the providers that MudBlazor needs for popovers and dialogs, then the editor, and waits until it is loaded.</summary>
    private IRenderedComponent<CommandEditorPage> RenderEditor(out IRenderedComponent<MudDialogProvider> dialogs, string? id = null)
    {
        Render<MudPopoverProvider>();
        dialogs = Render<MudDialogProvider>();
        IRenderedComponent<CommandEditorPage> cut = id is null
            ? Render<CommandEditorPage>()
            : Render<CommandEditorPage>(parameters => parameters.Add(page => page.Id, id));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=section-general], [data-testid=editor-not-found], [data-testid=error-message]")), Patience);
        return cut;
    }
}
