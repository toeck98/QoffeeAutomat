using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Tests.Validation;

[Trait("Category", "Validation")]
public sealed class CommandDocumentTests
{
    [Fact]
    public void The_example_command_of_PLAN_S06_is_valid()
    {
        CommandDocument example = TestData.ReadCommand("command.example.json");

        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(example));
    }

    [Theory]
    [InlineData("command.builtin.json")]
    [InlineData("command.inline.json")]
    [InlineData("command.process.json")]
    [InlineData("command.sequence.json")]
    public void The_command_fixtures_are_valid(string fileName)
    {
        CommandDocument command = TestData.ReadCommand(fileName);

        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(command));
    }

    [Fact]
    public void A_minimal_command_is_valid()
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(TestDocuments.Command()));
    }

    [Fact]
    public void A_null_document_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => DocumentValidator.ValidateCommand(null!));
    }

    [Fact]
    public void Several_errors_are_reported_together_in_document_order()
    {
        CommandDocument command = TestDocuments.Command() with { Id = "Bad Id", Name = " ", RunAs = "root" };

        ValidationResult result = DocumentValidator.ValidateCommand(command);

        Assert.Equal(["/id", "/name", "/runAs"], result.Errors.Select(error => error.Path));
    }

    // ---- Header ----

    [Fact]
    public void SchemaVersion_below_one_is_invalid()
    {
        ValidationResult result = DocumentValidator.ValidateCommand(TestDocuments.Command() with { SchemaVersion = 0 });

        TestDocuments.AssertError(result, "/schemaVersion", ValidationCodes.Invalid);
    }

    [Fact]
    public void A_newer_SchemaVersion_is_unsupported()
    {
        ValidationResult result = DocumentValidator.ValidateCommand(
            TestDocuments.Command() with { SchemaVersion = DocumentSchema.CurrentVersion + 1 });

        TestDocuments.AssertError(result, "/schemaVersion", ValidationCodes.Unsupported);
    }

    [Fact]
    public void A_negative_revision_is_invalid()
    {
        ValidationResult result = DocumentValidator.ValidateCommand(TestDocuments.Command() with { Revision = -1 });

        TestDocuments.AssertError(result, "/revision", ValidationCodes.Invalid);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("hub")]
    public void Known_origins_are_valid(string origin)
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(TestDocuments.Command() with { Origin = origin, Revision = 3 }));
    }

    [Fact]
    public void An_unknown_origin_is_invalid()
    {
        ValidationResult result = DocumentValidator.ValidateCommand(TestDocuments.Command() with { Origin = "cloud" });

        TestDocuments.AssertError(result, "/origin", ValidationCodes.Invalid);
    }

    // ---- Id and name ----

    [Theory]
    [InlineData("a")]
    [InlineData("set_display_profile")]
    [InlineData("a1_b2")]
    public void Valid_ids_are_accepted(string id)
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(TestDocuments.Command() with { Id = id }));
    }

    [Fact]
    public void An_id_of_63_characters_is_accepted_and_64_is_rejected()
    {
        string longest = "a" + new string('b', 62);

        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(TestDocuments.Command() with { Id = longest }));
        TestDocuments.AssertError(
            DocumentValidator.ValidateCommand(TestDocuments.Command() with { Id = longest + "c" }),
            "/id",
            ValidationCodes.Invalid);
    }

    [Theory]
    [InlineData("1abc")]
    [InlineData("_abc")]
    [InlineData("Abc")]
    [InlineData("a-b")]
    [InlineData("a b")]
    [InlineData("a.b")]
    [InlineData("a/b")]
    [InlineData("../x")]
    [InlineData("a\n")]
    public void Invalid_ids_are_rejected(string id)
    {
        ValidationResult result = DocumentValidator.ValidateCommand(TestDocuments.Command() with { Id = id });

        TestDocuments.AssertError(result, "/id", ValidationCodes.Invalid);
    }

    [Fact]
    public void An_empty_id_is_required()
    {
        ValidationResult result = DocumentValidator.ValidateCommand(TestDocuments.Command() with { Id = string.Empty });

        TestDocuments.AssertError(result, "/id", ValidationCodes.Required);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_is_required(string name)
    {
        ValidationResult result = DocumentValidator.ValidateCommand(TestDocuments.Command() with { Name = name });

        TestDocuments.AssertError(result, "/name", ValidationCodes.Required);
    }

    [Fact]
    public void A_name_of_100_characters_is_accepted_and_101_is_rejected()
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(TestDocuments.Command() with { Name = new string('n', 100) }));
        TestDocuments.AssertError(
            DocumentValidator.ValidateCommand(TestDocuments.Command() with { Name = new string('n', 101) }),
            "/name",
            ValidationCodes.Invalid);
    }

    [Fact]
    public void A_name_with_control_characters_is_rejected()
    {
        ValidationResult result = DocumentValidator.ValidateCommand(TestDocuments.Command() with { Name = "Line\nbreak" });

        TestDocuments.AssertError(result, "/name", ValidationCodes.Invalid);
    }

    [Fact]
    public void A_name_may_contain_unicode_letters_and_spaces()
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(TestDocuments.Command() with { Name = "Bildschirm-Profil für Büro" }));
    }

    [Fact]
    public void A_description_of_2001_characters_is_rejected()
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(TestDocuments.Command() with { Description = new string('d', 2000) }));
        TestDocuments.AssertError(
            DocumentValidator.ValidateCommand(TestDocuments.Command() with { Description = new string('d', 2001) }),
            "/description",
            ValidationCodes.Invalid);
    }

    [Theory]
    [InlineData("mdi:monitor-multiple", true)]
    [InlineData("hass:bell", true)]
    [InlineData("monitor", false)]
    [InlineData("mdi:", false)]
    [InlineData("MDI:Monitor", false)]
    public void Icons_must_look_like_prefix_colon_name(string icon, bool valid)
    {
        ValidationResult result = DocumentValidator.ValidateCommand(TestDocuments.Command() with { Icon = icon });

        Assert.Equal(valid, result.IsValid);
        if (!valid)
        {
            TestDocuments.AssertError(result, "/icon", ValidationCodes.Invalid);
        }
    }

    // ---- runAs, timeout, concurrency, cooldown ----

    [Theory]
    [InlineData("system")]
    [InlineData("user")]
    [InlineData("user_elevated")]
    public void The_three_runAs_values_are_accepted(string runAs)
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(TestDocuments.Command() with { RunAs = runAs }));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("System")]
    [InlineData("elevated")]
    public void Other_runAs_values_are_invalid(string runAs)
    {
        ValidationResult result = DocumentValidator.ValidateCommand(TestDocuments.Command() with { RunAs = runAs });

        TestDocuments.AssertError(result, "/runAs", ValidationCodes.Invalid);
    }

    [Fact]
    public void An_empty_runAs_is_required()
    {
        ValidationResult result = DocumentValidator.ValidateCommand(TestDocuments.Command() with { RunAs = string.Empty });

        TestDocuments.AssertError(result, "/runAs", ValidationCodes.Required);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(86400, true)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(86401, false)]
    public void TimeoutSeconds_is_limited_to_one_second_to_one_day(int timeout, bool valid)
    {
        ValidationResult result = DocumentValidator.ValidateCommand(TestDocuments.Command() with { TimeoutSeconds = timeout });

        Assert.Equal(valid, result.IsValid);
    }

    [Theory]
    [InlineData("single", true)]
    [InlineData("queue", true)]
    [InlineData("parallel", true)]
    [InlineData("restart", true)]
    [InlineData("exclusive", false)]
    [InlineData("", false)]
    public void Concurrency_must_be_a_known_mode(string mode, bool valid)
    {
        ValidationResult result = DocumentValidator.ValidateCommand(TestDocuments.Command() with { Concurrency = mode });

        Assert.Equal(valid, result.IsValid);
    }

    [Fact]
    public void A_negative_cooldown_is_invalid()
    {
        TestDocuments.AssertError(
            DocumentValidator.ValidateCommand(TestDocuments.Command() with { CooldownSeconds = -1 }),
            "/cooldownSeconds",
            ValidationCodes.Invalid);
        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(TestDocuments.Command() with { CooldownSeconds = 30 }));
    }
}
