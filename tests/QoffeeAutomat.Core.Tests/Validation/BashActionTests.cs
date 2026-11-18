using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Tests.Validation;

/// <summary>The <c>bash</c> action type in the document validator and in the serialization of command documents (PLAN.md S06, S17, T803).</summary>
[Trait("Category", "Bash")]
public sealed class BashActionTests
{
    private static ValidationResult Validate(CommandAction action) =>
        DocumentValidator.ValidateCommand(TestDocuments.Command() with { Action = action });

    [Fact]
    public void An_inline_bash_action_with_a_script_is_valid()
    {
        TestDocuments.AssertValid(Validate(new BashAction(BashSources.Inline, Code: "echo \"$1\"")));
    }

    [Fact]
    public void A_bash_action_with_a_script_file_is_valid()
    {
        TestDocuments.AssertValid(Validate(new BashAction(BashSources.File, Script: "backup.sh")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void An_inline_bash_action_needs_a_script(string? code)
    {
        TestDocuments.AssertError(Validate(new BashAction(BashSources.Inline, Code: code)), "/action/code", ValidationCodes.Required);
    }

    [Fact]
    public void A_bash_file_action_needs_a_script_name()
    {
        TestDocuments.AssertError(Validate(new BashAction(BashSources.File)), "/action/script", ValidationCodes.Required);
    }

    [Theory]
    [InlineData("../outside.sh")]
    [InlineData("sub/inner.sh")]
    [InlineData("a b.sh")]
    [InlineData("..")]
    public void A_bash_file_action_needs_a_plain_file_name(string script)
    {
        TestDocuments.AssertError(Validate(new BashAction(BashSources.File, Script: script)), "/action/script", ValidationCodes.Invalid);
    }

    [Fact]
    public void A_bash_action_needs_a_source()
    {
        TestDocuments.AssertError(Validate(new BashAction(string.Empty, Code: "echo")), "/action/source", ValidationCodes.Required);
    }

    [Fact]
    public void An_unknown_bash_source_is_invalid()
    {
        TestDocuments.AssertError(Validate(new BashAction("remote", Code: "echo")), "/action/source", ValidationCodes.Invalid);
    }

    [Fact]
    public void A_bash_action_can_use_secrets()
    {
        CommandDocument document = TestDocuments.Command() with
        {
            Action = new BashAction(BashSources.Inline, Code: "echo \"$QA_SECRET_TOKEN\""),
            Secrets = ["token"],
        };

        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(document));
    }

    [Fact]
    public void A_builtin_action_still_cannot_use_secrets()
    {
        CommandDocument document = TestDocuments.Command() with { Secrets = ["token"] };

        TestDocuments.AssertError(DocumentValidator.ValidateCommand(document), "/secrets", ValidationCodes.NotApplicable);
    }

    [Fact]
    public void The_message_for_an_unknown_action_type_lists_bash()
    {
        ValidationResult result = Validate(new UnknownAction("perl", TestDocuments.Json("{\"type\":\"perl\"}")));

        ValidationError error = Assert.Single(result.Errors, candidate => candidate.Path == "/action/type");
        Assert.Equal(ValidationCodes.Unsupported, error.Code);
        Assert.Contains("bash", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bash_command_is_written_and_read_back_with_the_type_first()
    {
        CommandDocument document = TestDocuments.Command() with { Action = new BashAction(BashSources.Inline, Code: "echo hi") };

        string json = JsonSerializer.Serialize(document, ConfigJsonContext.Default.CommandDocument);
        CommandDocument back = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.CommandDocument)!;

        BashAction action = Assert.IsType<BashAction>(back.Action);
        Assert.Equal(BashSources.Inline, action.Source);
        Assert.Equal("echo hi", action.Code);
        Assert.Null(action.Script);
        Assert.Contains("\"type\": \"bash\"", json, StringComparison.Ordinal);
        Assert.True(json.IndexOf("\"type\": \"bash\"", StringComparison.Ordinal) < json.IndexOf("\"source\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A_bash_file_command_reads_the_script_name()
    {
        const string json = """
            { "schemaVersion": 2, "id": "rotate", "name": "Rotate", "enabled": true, "exposeToHa": false, "runAs": "system",
              "action": { "type": "bash", "source": "file", "script": "rotate.sh" } }
            """;

        CommandDocument document = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.CommandDocument)!;

        BashAction action = Assert.IsType<BashAction>(document.Action);
        Assert.Equal(BashSources.File, action.Source);
        Assert.Equal("rotate.sh", action.Script);
        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(document));
    }
}
