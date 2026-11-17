using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Tests.Validation;

/// <summary>
/// The parameter field <c>optionsFrom</c> (PLAN.md S06, T713): an enum variable that takes its options from an enum metric. It is a
/// backward compatible addition: a document without it validates exactly as before, and the new rules only apply where the field
/// is set.
/// </summary>
[Trait("Category", "OptionsFrom")]
public sealed class OptionsFromValidationTests
{
    private static ParameterDefinition Device(string? optionsFrom = "audio.output_device") =>
        new("Device", ParameterTypes.Choice, Required: true, OptionsFrom: optionsFrom);

    private static CommandDocument SelectCommand(ParameterDefinition parameter) =>
        TestDocuments.WithParameters(parameter) with
        {
            RunAs = RunAsValues.User,
            Action = new BuiltinAction("audio.set_default_output"),
            HaControl = new HaControl(HaControlTypes.Select, parameter.Name, "audio.output_device"),
        };

    private static CommandDocument ReadInvalid(string fileName)
    {
        string json = File.ReadAllText(Path.Combine(TestData.ConfigFixtureDirectory, "invalid", fileName));
        return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.CommandDocument)!;
    }

    // ------------------------------------------------------------------
    // Valid documents
    // ------------------------------------------------------------------

    [Fact]
    public void An_enum_variable_with_optionsFrom_and_no_options_is_valid()
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(SelectCommand(Device())));
    }

    [Theory]
    [InlineData("audio.output_device")]
    [InlineData("audio.input_device")]
    [InlineData("mode.pc_mode")]
    [InlineData("custom.my_sensor")]
    [InlineData("cpu")]
    public void Any_metric_id_is_accepted_as_a_source(string metricId)
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(SelectCommand(Device(metricId))));
    }

    [Fact]
    public void The_fixture_with_optionsFrom_is_valid_and_keeps_the_field()
    {
        CommandDocument command = TestData.ReadCommand("command.options_from.json");

        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(command));
        ParameterDefinition device = Assert.Single(command.Parameters!);
        Assert.Equal("audio.output_device", device.OptionsFrom);
        Assert.Null(device.Options);
        Assert.Equal(HaControlTypes.Select, command.HaControl!.Type);
        Assert.Equal("audio.output_device", command.HaControl.StateMetric);
    }

    // ------------------------------------------------------------------
    // The new rules
    // ------------------------------------------------------------------

    [Fact]
    public void OptionsFrom_replaces_options_so_both_together_are_a_conflict()
    {
        ParameterDefinition both = Device() with { Options = ["Speakers"] };

        ValidationResult result = DocumentValidator.ValidateCommand(SelectCommand(both));

        TestDocuments.AssertError(result, "/parameters/0/optionsFrom", ValidationCodes.Conflict);
    }

    [Theory]
    [InlineData(ParameterTypes.Text)]
    [InlineData(ParameterTypes.WholeNumber)]
    [InlineData(ParameterTypes.Real)]
    [InlineData(ParameterTypes.Flag)]
    public void OptionsFrom_is_only_for_enum_variables(string type)
    {
        ParameterDefinition parameter = new("Device", type, OptionsFrom: "audio.output_device");

        ValidationResult result = DocumentValidator.ValidateCommand(TestDocuments.WithParameters(parameter));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Path == "/parameters/0/optionsFrom");
    }

    [Theory]
    [InlineData("Not A Metric")]
    [InlineData("Audio.Output")]
    [InlineData("1audio")]
    [InlineData("audio..device")]
    [InlineData("audio.")]
    public void OptionsFrom_must_be_a_metric_id(string metricId)
    {
        ValidationResult result = DocumentValidator.ValidateCommand(SelectCommand(Device(metricId)));

        TestDocuments.AssertError(result, "/parameters/0/optionsFrom", ValidationCodes.Invalid);
    }

    [Fact]
    public void An_enum_variable_needs_options_or_optionsFrom()
    {
        ParameterDefinition neither = new("Device", ParameterTypes.Choice);

        ValidationResult result = DocumentValidator.ValidateCommand(TestDocuments.WithParameters(neither));

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("command.options_from_not_enum.json")]
    [InlineData("command.options_from_and_options.json")]
    public void The_invalid_fixtures_are_rejected(string fileName)
    {
        Assert.False(DocumentValidator.ValidateCommand(ReadInvalid(fileName)).IsValid);
    }

    // ------------------------------------------------------------------
    // Backward compatibility
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("command.example.json")]
    [InlineData("command.builtin.json")]
    [InlineData("command.inline.json")]
    [InlineData("command.process.json")]
    [InlineData("command.sequence.json")]
    [InlineData("command.conditions.json")]
    public void Documents_without_optionsFrom_validate_unchanged(string fileName)
    {
        CommandDocument command = TestData.ReadCommand(fileName);

        Assert.All(command.Parameters ?? [], parameter => Assert.Null(parameter.OptionsFrom));
        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(command));
    }

    [Fact]
    public void An_enum_variable_with_fixed_options_is_still_valid_and_still_needs_a_non_empty_list()
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(SelectCommand(TestData.Choice("Device", "Speakers", "Headset"))));

        ValidationResult empty = DocumentValidator.ValidateCommand(SelectCommand(TestData.Choice("Device")));

        Assert.False(empty.IsValid);
    }

    [Fact]
    public void A_document_without_optionsFrom_does_not_write_the_member()
    {
        CommandDocument command = SelectCommand(TestData.Choice("Device", "Speakers", "Headset"));

        string json = JsonSerializer.Serialize(command, ConfigJsonContext.Default.CommandDocument);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement parameter = document.RootElement.GetProperty("parameters")[0];
        Assert.False(parameter.TryGetProperty("optionsFrom", out _));
        Assert.Equal(["Speakers", "Headset"], parameter.GetProperty("options").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void OptionsFrom_is_written_in_camel_case_and_survives_a_round_trip()
    {
        CommandDocument command = SelectCommand(Device());

        string json = JsonSerializer.Serialize(command, ConfigJsonContext.Default.CommandDocument);
        CommandDocument read = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.CommandDocument)!;

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement parameter = document.RootElement.GetProperty("parameters")[0];
        Assert.Equal("audio.output_device", parameter.GetProperty("optionsFrom").GetString());
        Assert.False(parameter.TryGetProperty("options", out _));
        Assert.Equal("audio.output_device", Assert.Single(read.Parameters!).OptionsFrom);
        Assert.Null(read.Parameters![0].Options);
    }

    // ------------------------------------------------------------------
    // Binding values
    // ------------------------------------------------------------------

    [Fact]
    public void A_value_for_a_variable_with_optionsFrom_is_any_non_empty_string_because_the_options_change()
    {
        // The agent cannot know the options at the time the command is saved; the action that runs checks the name itself
        // (the audio actions refuse an unknown or ambiguous device with invalid_vars).
        ParameterDefinition device = Device();

        BindResult known = VariableBinder.Bind([device], TestDocuments.Vars(("Device", TestDocuments.Text("Speakers (Realtek Audio)"))));
        BindResult empty = VariableBinder.Bind([device], TestDocuments.Vars(("Device", TestDocuments.Text(string.Empty))));
        BindResult missing = VariableBinder.Bind([device], null);

        Assert.True(known.IsValid);
        Assert.Equal("Speakers (Realtek Audio)", known.Values["Device"]);
        TestDocuments.AssertError(empty, "/vars/Device", ValidationCodes.Options);
        Assert.False(missing.IsValid);
    }

    [Fact]
    public void A_value_that_is_not_a_string_is_still_refused()
    {
        BindResult result = VariableBinder.Bind([Device()], TestDocuments.Vars(("Device", TestDocuments.Number(3))));

        TestDocuments.AssertError(result, "/vars/Device", ValidationCodes.TypeMismatch);
    }
}
