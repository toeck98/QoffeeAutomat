using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Tests.Validation;

[Trait("Category", "Validation")]
public sealed class ActionTests
{
    private static ValidationResult Validate(CommandAction action, params ParameterDefinition[] parameters) =>
        DocumentValidator.ValidateCommand(TestDocuments.Command() with { Action = action, Parameters = parameters });

    // ---- builtin ----

    [Theory]
    [InlineData("power.shutdown")]
    [InlineData("audio.set_volume")]
    [InlineData("notify.toast")]
    public void Builtin_names_are_accepted(string name)
    {
        TestDocuments.AssertValid(Validate(new BuiltinAction(name)));
    }

    [Theory]
    [InlineData("shutdown")]
    [InlineData("Power.Shutdown")]
    [InlineData("power.")]
    [InlineData("power shutdown")]
    public void Malformed_builtin_names_are_rejected(string name)
    {
        TestDocuments.AssertError(Validate(new BuiltinAction(name)), "/action/name", ValidationCodes.Invalid);
    }

    [Fact]
    public void An_empty_builtin_name_is_required()
    {
        TestDocuments.AssertError(Validate(new BuiltinAction(string.Empty)), "/action/name", ValidationCodes.Required);
    }

    [Fact]
    public void A_missing_action_is_required()
    {
        ValidationResult result = DocumentValidator.ValidateCommand(TestDocuments.Command() with { Action = null! });

        TestDocuments.AssertError(result, "/action", ValidationCodes.Required);
    }

    [Fact]
    public void An_unknown_action_type_is_unsupported()
    {
        ValidationResult result = Validate(new UnknownAction("ssh", TestDocuments.Json("{}")));

        TestDocuments.AssertError(result, "/action/type", ValidationCodes.Unsupported);
    }

    // ---- powershell ----

    [Fact]
    public void Inline_powershell_with_code_is_valid()
    {
        TestDocuments.AssertValid(Validate(new PowerShellAction(PowerShellSources.Inline, Code: "Get-Date")));
    }

    [Fact]
    public void Inline_powershell_without_code_is_rejected()
    {
        TestDocuments.AssertError(Validate(new PowerShellAction(PowerShellSources.Inline, Code: "  ")), "/action/code", ValidationCodes.Required);
    }

    [Theory]
    [InlineData("set_display_profile.ps1")]
    [InlineData("a-b_c.1.ps1")]
    public void A_script_file_name_is_accepted(string script)
    {
        TestDocuments.AssertValid(Validate(new PowerShellAction(PowerShellSources.File, Script: script)));
    }

    [Fact]
    public void A_file_source_without_script_is_rejected()
    {
        TestDocuments.AssertError(Validate(new PowerShellAction(PowerShellSources.File)), "/action/script", ValidationCodes.Required);
    }

    [Theory]
    [InlineData("../evil.ps1")]
    [InlineData("a/b.ps1")]
    [InlineData("a\\b.ps1")]
    [InlineData("C:\\x.ps1")]
    [InlineData("..")]
    [InlineData("a..b.ps1")]
    [InlineData(".hidden.ps1")]
    public void A_script_name_with_a_path_is_rejected(string script)
    {
        TestDocuments.AssertError(Validate(new PowerShellAction(PowerShellSources.File, Script: script)), "/action/script", ValidationCodes.Invalid);
    }

    [Fact]
    public void A_missing_or_unknown_source_is_rejected()
    {
        TestDocuments.AssertError(Validate(new PowerShellAction(string.Empty)), "/action/source", ValidationCodes.Required);
        TestDocuments.AssertError(Validate(new PowerShellAction("url")), "/action/source", ValidationCodes.Invalid);
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("pwsh", true)]
    [InlineData("powershell", true)]
    [InlineData("cmd", false)]
    [InlineData("", false)]
    public void The_engine_must_be_known(string engine, bool valid)
    {
        ValidationResult result = Validate(new PowerShellAction(PowerShellSources.Inline, Code: "1", Engine: engine));

        Assert.Equal(valid, result.IsValid);
    }

    // ---- process ----

    [Fact]
    public void A_process_with_a_file_is_valid()
    {
        TestDocuments.AssertValid(Validate(new ProcessAction("notepad.exe")));
    }

    [Fact]
    public void A_process_without_file_is_rejected()
    {
        TestDocuments.AssertError(Validate(new ProcessAction(" ")), "/action/file", ValidationCodes.Required);
    }

    [Fact]
    public void Arguments_may_use_declared_variables_and_context_variables()
    {
        TestDocuments.AssertValid(Validate(
            new ProcessAction("tool", ["--level", "{{Level}}", "--run", "{{qa.run_id}}", "{{qa.source}}", "{{qa.command_id}}", "{{qa.agent_name}}"]),
            TestDocuments.Parameter("Level", ParameterTypes.WholeNumber)));
    }

    [Fact]
    public void An_undeclared_placeholder_is_rejected_with_the_index_of_the_argument()
    {
        ValidationResult result = Validate(new ProcessAction("tool", ["--ok", "{{Missing}}"]));

        TestDocuments.AssertError(result, "/action/args/1", ValidationCodes.UnknownVariable);
    }

    [Fact]
    public void Placeholder_names_are_case_sensitive()
    {
        ValidationResult result = Validate(
            new ProcessAction("tool", ["{{level}}"]),
            TestDocuments.Parameter("Level", ParameterTypes.WholeNumber));

        TestDocuments.AssertError(result, "/action/args/0", ValidationCodes.UnknownVariable);
    }

    [Theory]
    [InlineData("{{}}")]
    [InlineData("{{ Level }}")]
    [InlineData("{{qa.unknown}}")]
    [InlineData("{{1x}}")]
    public void A_malformed_placeholder_is_rejected(string argument)
    {
        ValidationResult result = Validate(
            new ProcessAction("tool", [argument]),
            TestDocuments.Parameter("Level", ParameterTypes.WholeNumber));

        TestDocuments.AssertError(result, "/action/args/0", ValidationCodes.Invalid);
    }

    [Fact]
    public void A_null_argument_is_rejected()
    {
        ValidationResult result = Validate(new ProcessAction("tool", [null!]));

        TestDocuments.AssertError(result, "/action/args/0", ValidationCodes.Invalid);
    }

    // ---- sequence ----

    private static SequenceAction Calls(params string[] commands) =>
        new(commands.Select(command => new SequenceStep(Command: command)).ToArray());

    private static CommandDocument SequenceCommand(string id, params string[] calls) =>
        TestDocuments.Command() with { Id = id, Action = Calls(calls) };

    [Fact]
    public void A_sequence_of_commands_and_delays_is_valid()
    {
        TestDocuments.AssertValid(Validate(new SequenceAction(
        [
            new SequenceStep(Command: "lights_off"),
            new SequenceStep(DelaySeconds: 2.5),
            new SequenceStep(Command: "set_volume", Vars: TestDocuments.Vars(("Level", TestDocuments.Number(30)))),
        ])));
    }

    [Fact]
    public void A_sequence_without_steps_is_rejected()
    {
        TestDocuments.AssertError(Validate(new SequenceAction([])), "/action/steps", ValidationCodes.Required);
    }

    [Fact]
    public void A_step_needs_exactly_one_of_command_and_delay()
    {
        ValidationResult both = Validate(new SequenceAction([new SequenceStep(Command: "x", DelaySeconds: 1)]));
        ValidationResult neither = Validate(new SequenceAction([new SequenceStep()]));

        TestDocuments.AssertError(both, "/action/steps/0", ValidationCodes.Conflict);
        TestDocuments.AssertError(neither, "/action/steps/0", ValidationCodes.Required);
    }

    [Fact]
    public void A_step_command_must_be_a_command_id()
    {
        TestDocuments.AssertError(Validate(Calls("Not An Id")), "/action/steps/0/command", ValidationCodes.Invalid);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void A_delay_must_not_be_negative(double delay)
    {
        ValidationResult result = Validate(new SequenceAction([new SequenceStep(DelaySeconds: delay)]));

        TestDocuments.AssertError(result, "/action/steps/0/delaySeconds", ValidationCodes.Invalid);
    }

    [Fact]
    public void Vars_on_a_delay_step_are_not_applicable()
    {
        ValidationResult result = Validate(new SequenceAction(
            [new SequenceStep(DelaySeconds: 1, Vars: TestDocuments.Vars(("A", TestDocuments.Number(1))))]));

        TestDocuments.AssertError(result, "/action/steps/0/vars", ValidationCodes.NotApplicable);
    }

    [Fact]
    public void Step_variables_must_be_scalars()
    {
        ValidationResult result = Validate(new SequenceAction(
        [
            new SequenceStep(Command: "x", Vars: TestDocuments.Vars(("Nested", TestDocuments.Json("[1]")), ("Gone", TestDocuments.Json("null")))),
        ]));

        TestDocuments.AssertError(result, "/action/steps/0/vars/Nested", ValidationCodes.TypeMismatch);
        TestDocuments.AssertError(result, "/action/steps/0/vars/Gone", ValidationCodes.TypeMismatch);
    }

    [Theory]
    [InlineData("stop", true)]
    [InlineData("continue", true)]
    [InlineData("ignore", false)]
    [InlineData("", false)]
    public void OnError_must_be_stop_or_continue(string onError, bool valid)
    {
        ValidationResult result = Validate(new SequenceAction([new SequenceStep(Command: "x")], onError));

        Assert.Equal(valid, result.IsValid);
    }

    // ---- sequence cycles ----

    [Fact]
    public void A_sequence_calling_itself_is_a_cycle()
    {
        CommandDocument self = SequenceCommand("loop", "loop");

        ValidationResult result = DocumentValidator.ValidateCommand(self, _ => null);

        TestDocuments.AssertError(result, "/action/steps/0/command", ValidationCodes.Cycle);
    }

    [Fact]
    public void A_cycle_is_found_without_a_lookup_only_for_direct_self_calls()
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(SequenceCommand("a", "b")));
        TestDocuments.AssertError(DocumentValidator.ValidateCommand(SequenceCommand("a", "a")), "/action/steps/0/command", ValidationCodes.Cycle);
    }

    [Fact]
    public void Two_sequences_calling_each_other_are_a_cycle()
    {
        CommandDocument b = SequenceCommand("b", "a");
        CommandDocument a = SequenceCommand("a", "b");

        ValidationResult result = DocumentValidator.ValidateCommand(a, id => id == "b" ? b : null);

        ValidationError error = Assert.Single(result.Errors);
        Assert.Equal(ValidationCodes.Cycle, error.Code);
        Assert.Contains("a -> b -> a", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cycle_through_three_sequences_is_found_and_reports_the_step()
    {
        var store = new Dictionary<string, CommandDocument>(StringComparer.Ordinal)
        {
            ["b"] = SequenceCommand("b", "c"),
            ["c"] = SequenceCommand("c", "a"),
        };
        CommandDocument a = TestDocuments.Command() with
        {
            Id = "a",
            Action = new SequenceAction([new SequenceStep(DelaySeconds: 1), new SequenceStep(Command: "b")]),
        };

        ValidationResult result = DocumentValidator.ValidateCommand(a, id => store.GetValueOrDefault(id));

        TestDocuments.AssertError(result, "/action/steps/1/command", ValidationCodes.Cycle);
        Assert.Contains("a -> b -> c -> a", result.Errors.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cycle_that_does_not_include_the_document_is_not_reported_and_terminates()
    {
        var store = new Dictionary<string, CommandDocument>(StringComparer.Ordinal)
        {
            ["b"] = SequenceCommand("b", "c"),
            ["c"] = SequenceCommand("c", "b"),
        };

        ValidationResult result = DocumentValidator.ValidateCommand(SequenceCommand("a", "b"), id => store.GetValueOrDefault(id));

        TestDocuments.AssertValid(result);
    }

    [Fact]
    public void A_diamond_of_sequences_is_not_a_cycle()
    {
        var store = new Dictionary<string, CommandDocument>(StringComparer.Ordinal)
        {
            ["b"] = SequenceCommand("b", "d"),
            ["c"] = SequenceCommand("c", "d"),
            ["d"] = TestDocuments.Command() with { Id = "d" },
        };

        ValidationResult result = DocumentValidator.ValidateCommand(SequenceCommand("a", "b", "c"), id => store.GetValueOrDefault(id));

        TestDocuments.AssertValid(result);
    }

    [Fact]
    public void Calling_unknown_commands_is_not_a_cycle()
    {
        ValidationResult result = DocumentValidator.ValidateCommand(SequenceCommand("a", "ghost"), _ => null);

        TestDocuments.AssertValid(result);
    }

    [Fact]
    public void Only_the_offending_steps_are_reported()
    {
        CommandDocument b = SequenceCommand("b", "a");
        CommandDocument a = SequenceCommand("a", "x", "b", "y");

        ValidationResult result = DocumentValidator.ValidateCommand(a, id => id == "b" ? b : null);

        Assert.Equal("/action/steps/1/command", Assert.Single(result.Errors).Path);
    }

    [Fact]
    public void The_lookup_never_sees_the_document_being_validated()
    {
        var asked = new List<string>();
        CommandDocument a = SequenceCommand("a", "b");

        _ = DocumentValidator.ValidateCommand(a, id =>
        {
            asked.Add(id);
            return null;
        });

        Assert.DoesNotContain("a", asked);
        Assert.Contains("b", asked);
    }

    [Fact]
    public void Step_vars_use_JsonElement_values()
    {
        JsonElement value = TestDocuments.Text("x");

        TestDocuments.AssertValid(Validate(new SequenceAction([new SequenceStep(Command: "x", Vars: TestDocuments.Vars(("A", value)))])));
    }
}
