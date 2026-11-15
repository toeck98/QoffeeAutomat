using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Tests.Validation;

/// <summary>One test group per row of the parameter table of PLAN.md S06.</summary>
[Trait("Category", "Validation")]
public sealed class ParameterDefinitionTests
{
    private static ValidationResult Validate(params ParameterDefinition[] parameters) =>
        DocumentValidator.ValidateCommand(TestDocuments.WithParameters(parameters));

    private static void AssertParameterError(ParameterDefinition parameter, string member, string code) =>
        TestDocuments.AssertError(Validate(parameter), "/parameters/0/" + member, code);

    // ---- name ----

    [Theory]
    [InlineData("Profile")]
    [InlineData("a")]
    [InlineData("Update_Adapter_Ids2")]
    public void Valid_names_are_accepted(string name)
    {
        TestDocuments.AssertValid(Validate(TestDocuments.Parameter(name, ParameterTypes.Text)));
    }

    [Fact]
    public void A_name_of_32_characters_is_accepted_and_33_is_rejected()
    {
        TestDocuments.AssertValid(Validate(TestDocuments.Parameter("A" + new string('b', 31), ParameterTypes.Text)));
        AssertParameterError(TestDocuments.Parameter("A" + new string('b', 32), ParameterTypes.Text), "name", ValidationCodes.Invalid);
    }

    [Theory]
    [InlineData("1abc")]
    [InlineData("_abc")]
    [InlineData("a-b")]
    [InlineData("a b")]
    [InlineData("qa.run_id")]
    [InlineData("{{x}}")]
    public void Invalid_names_are_rejected(string name)
    {
        AssertParameterError(TestDocuments.Parameter(name, ParameterTypes.Text), "name", ValidationCodes.Invalid);
    }

    [Fact]
    public void An_empty_name_is_required()
    {
        AssertParameterError(TestDocuments.Parameter(string.Empty, ParameterTypes.Text), "name", ValidationCodes.Required);
    }

    [Fact]
    public void Names_must_be_unique_ignoring_case()
    {
        ValidationResult result = Validate(
            TestDocuments.Parameter("Volume", ParameterTypes.WholeNumber),
            TestDocuments.Parameter("volume", ParameterTypes.WholeNumber));

        TestDocuments.AssertError(result, "/parameters/1/name", ValidationCodes.Duplicate);
    }

    [Fact]
    public void An_unknown_type_is_invalid_and_reports_nothing_else_for_the_parameter()
    {
        ValidationResult result = Validate(TestDocuments.Parameter("X", "integer") with { Min = 1 });

        Assert.Single(result.Errors);
        TestDocuments.AssertError(result, "/parameters/0/type", ValidationCodes.Invalid);
    }

    [Fact]
    public void An_empty_parameter_list_is_valid()
    {
        TestDocuments.AssertValid(Validate());
    }

    [Fact]
    public void Required_secret_unit_and_description_are_accepted_on_every_type()
    {
        TestDocuments.AssertValid(Validate(
            TestDocuments.Parameter("A", ParameterTypes.Text) with { Required = true, Secret = true, Unit = "x", Description = "d" },
            TestDocuments.Parameter("B", ParameterTypes.WholeNumber) with { Required = true, Unit = "%", Description = "d" },
            TestDocuments.Parameter("C", ParameterTypes.Real) with { Secret = true },
            TestDocuments.Parameter("D", ParameterTypes.Flag) with { Required = true },
            TestData.Choice("E", "x", "y") with { Description = "d" }));
    }

    [Fact]
    public void Errors_carry_the_index_of_their_parameter()
    {
        ValidationResult result = Validate(
            TestDocuments.Parameter("Ok", ParameterTypes.Text),
            TestDocuments.Parameter("Bad", ParameterTypes.WholeNumber) with { Min = 9, Max = 1 });

        Assert.Equal("/parameters/1/min", Assert.Single(result.Errors).Path);
    }

    // ---- string ----

    [Fact]
    public void A_string_parameter_accepts_pattern_maxLength_and_default()
    {
        TestDocuments.AssertValid(Validate(
            TestDocuments.Parameter("Text", ParameterTypes.Text) with
            {
                Pattern = "^[a-z]+$",
                MaxLength = 10,
                Default = TestDocuments.Text("abc"),
            }));
    }

    [Theory]
    [InlineData("min")]
    [InlineData("max")]
    [InlineData("step")]
    [InlineData("options")]
    [InlineData("optionsFrom")]
    public void A_string_parameter_rejects_members_of_other_types(string member)
    {
        ParameterDefinition parameter = member switch
        {
            "min" => TestDocuments.Parameter("X", ParameterTypes.Text) with { Min = 1 },
            "max" => TestDocuments.Parameter("X", ParameterTypes.Text) with { Max = 1 },
            "step" => TestDocuments.Parameter("X", ParameterTypes.Text) with { Step = 1 },
            "options" => TestDocuments.Parameter("X", ParameterTypes.Text) with { Options = ["a"] },
            _ => TestDocuments.Parameter("X", ParameterTypes.Text) with { OptionsFrom = "audio.devices" },
        };

        AssertParameterError(parameter, member, ValidationCodes.NotApplicable);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_string_maxLength_below_one_is_invalid(int maxLength)
    {
        AssertParameterError(TestDocuments.Parameter("X", ParameterTypes.Text) with { MaxLength = maxLength }, "maxLength", ValidationCodes.Invalid);
    }

    [Fact]
    public void A_string_pattern_must_be_a_valid_regular_expression()
    {
        AssertParameterError(TestDocuments.Parameter("X", ParameterTypes.Text) with { Pattern = "([unclosed" }, "pattern", ValidationCodes.InvalidPattern);
    }

    [Fact]
    public void A_string_default_must_be_a_string()
    {
        AssertParameterError(TestDocuments.Parameter("X", ParameterTypes.Text) with { Default = TestDocuments.Number(1) }, "default", ValidationCodes.TypeMismatch);
    }

    [Fact]
    public void A_string_default_must_respect_maxLength()
    {
        AssertParameterError(
            TestDocuments.Parameter("X", ParameterTypes.Text) with { MaxLength = 3, Default = TestDocuments.Text("abcd") },
            "default",
            ValidationCodes.MaxLength);
    }

    [Fact]
    public void A_string_default_must_match_the_pattern()
    {
        AssertParameterError(
            TestDocuments.Parameter("X", ParameterTypes.Text) with { Pattern = "^[0-9]+$", Default = TestDocuments.Text("abc") },
            "default",
            ValidationCodes.Pattern);
    }

    [Fact]
    public void A_null_default_is_treated_as_absent()
    {
        TestDocuments.AssertValid(Validate(TestDocuments.Parameter("X", ParameterTypes.Text) with { Default = TestDocuments.Json("null") }));
    }

    [Fact]
    public void A_structured_default_is_a_type_mismatch()
    {
        AssertParameterError(TestDocuments.Parameter("X", ParameterTypes.Text) with { Default = TestDocuments.Json("[1]") }, "default", ValidationCodes.TypeMismatch);
    }

    // ---- int ----

    [Fact]
    public void An_int_parameter_accepts_min_max_step_and_default()
    {
        TestDocuments.AssertValid(Validate(
            TestDocuments.Parameter("Volume", ParameterTypes.WholeNumber) with
            {
                Min = 0,
                Max = 100,
                Step = 5,
                Default = TestDocuments.Number(50),
                Unit = "%",
            }));
    }

    [Theory]
    [InlineData("pattern")]
    [InlineData("maxLength")]
    [InlineData("options")]
    [InlineData("optionsFrom")]
    public void An_int_parameter_rejects_members_of_other_types(string member)
    {
        ParameterDefinition parameter = member switch
        {
            "pattern" => TestDocuments.Parameter("X", ParameterTypes.WholeNumber) with { Pattern = "a" },
            "maxLength" => TestDocuments.Parameter("X", ParameterTypes.WholeNumber) with { MaxLength = 3 },
            "options" => TestDocuments.Parameter("X", ParameterTypes.WholeNumber) with { Options = ["a"] },
            _ => TestDocuments.Parameter("X", ParameterTypes.WholeNumber) with { OptionsFrom = "audio.devices" },
        };

        AssertParameterError(parameter, member, ValidationCodes.NotApplicable);
    }

    [Theory]
    [InlineData("min")]
    [InlineData("max")]
    public void An_int_bound_must_be_a_whole_number(string member)
    {
        ParameterDefinition parameter = member == "min"
            ? TestDocuments.Parameter("X", ParameterTypes.WholeNumber) with { Min = 1.5 }
            : TestDocuments.Parameter("X", ParameterTypes.WholeNumber) with { Max = 1.5 };

        AssertParameterError(parameter, member, ValidationCodes.Invalid);
    }

    [Fact]
    public void An_int_step_must_be_a_whole_number()
    {
        AssertParameterError(TestDocuments.Parameter("X", ParameterTypes.WholeNumber) with { Step = 0.5 }, "step", ValidationCodes.Invalid);
    }

    [Fact]
    public void Min_must_not_exceed_max()
    {
        AssertParameterError(TestDocuments.Parameter("X", ParameterTypes.WholeNumber) with { Min = 10, Max = 5 }, "min", ValidationCodes.Invalid);
        TestDocuments.AssertValid(Validate(TestDocuments.Parameter("Y", ParameterTypes.WholeNumber) with { Min = 5, Max = 5 }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_step_must_be_greater_than_zero(double step)
    {
        AssertParameterError(TestDocuments.Parameter("X", ParameterTypes.Real) with { Step = step }, "step", ValidationCodes.Invalid);
    }

    [Fact]
    public void An_int_default_must_be_a_whole_number()
    {
        AssertParameterError(TestDocuments.Parameter("X", ParameterTypes.WholeNumber) with { Default = TestDocuments.Number(1.5) }, "default", ValidationCodes.TypeMismatch);
    }

    [Fact]
    public void An_int_default_must_not_be_a_string()
    {
        AssertParameterError(TestDocuments.Parameter("X", ParameterTypes.WholeNumber) with { Default = TestDocuments.Text("5") }, "default", ValidationCodes.TypeMismatch);
    }

    [Fact]
    public void An_int_default_must_lie_within_min_and_max()
    {
        ParameterDefinition below = TestDocuments.Parameter("X", ParameterTypes.WholeNumber) with { Min = 10, Default = TestDocuments.Number(5) };
        ParameterDefinition above = TestDocuments.Parameter("X", ParameterTypes.WholeNumber) with { Max = 10, Default = TestDocuments.Number(11) };

        AssertParameterError(below, "default", ValidationCodes.Range);
        AssertParameterError(above, "default", ValidationCodes.Range);
    }

    [Fact]
    public void An_int_default_must_lie_on_the_step_grid()
    {
        AssertParameterError(
            TestDocuments.Parameter("X", ParameterTypes.WholeNumber) with { Min = 0, Step = 5, Default = TestDocuments.Number(7) },
            "default",
            ValidationCodes.Step);
    }

    // ---- float ----

    [Fact]
    public void A_float_parameter_accepts_fractional_bounds_step_and_default()
    {
        TestDocuments.AssertValid(Validate(
            TestDocuments.Parameter("Gain", ParameterTypes.Real) with
            {
                Min = 0.5,
                Max = 2.5,
                Step = 0.25,
                Default = TestDocuments.Number(1.25),
            }));
    }

    [Theory]
    [InlineData("pattern")]
    [InlineData("maxLength")]
    [InlineData("options")]
    public void A_float_parameter_rejects_members_of_other_types(string member)
    {
        ParameterDefinition parameter = member switch
        {
            "pattern" => TestDocuments.Parameter("X", ParameterTypes.Real) with { Pattern = "a" },
            "maxLength" => TestDocuments.Parameter("X", ParameterTypes.Real) with { MaxLength = 3 },
            _ => TestDocuments.Parameter("X", ParameterTypes.Real) with { Options = ["a"] },
        };

        AssertParameterError(parameter, member, ValidationCodes.NotApplicable);
    }

    [Fact]
    public void A_float_default_on_a_decimal_step_grid_tolerates_rounding()
    {
        TestDocuments.AssertValid(Validate(
            TestDocuments.Parameter("X", ParameterTypes.Real) with { Min = 0, Step = 0.1, Default = TestDocuments.Number(0.3) }));
    }

    [Fact]
    public void A_float_default_off_the_step_grid_is_rejected()
    {
        AssertParameterError(
            TestDocuments.Parameter("X", ParameterTypes.Real) with { Min = 0, Step = 0.5, Default = TestDocuments.Number(0.75) },
            "default",
            ValidationCodes.Step);
    }

    [Fact]
    public void A_float_default_outside_the_range_is_rejected()
    {
        AssertParameterError(
            TestDocuments.Parameter("X", ParameterTypes.Real) with { Max = 1, Default = TestDocuments.Number(1.5) },
            "default",
            ValidationCodes.Range);
    }

    [Fact]
    public void A_float_default_must_be_a_number()
    {
        AssertParameterError(TestDocuments.Parameter("X", ParameterTypes.Real) with { Default = TestDocuments.Flag(true) }, "default", ValidationCodes.TypeMismatch);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Non_finite_bounds_are_invalid(double bound)
    {
        AssertParameterError(TestDocuments.Parameter("X", ParameterTypes.Real) with { Min = bound }, "min", ValidationCodes.Invalid);
    }

    // ---- bool ----

    [Fact]
    public void A_bool_parameter_accepts_required_and_default()
    {
        TestDocuments.AssertValid(Validate(
            TestDocuments.Parameter("Flag", ParameterTypes.Flag) with { Default = TestDocuments.Flag(false) },
            TestDocuments.Parameter("Other", ParameterTypes.Flag) with { Required = true }));
    }

    [Theory]
    [InlineData("min")]
    [InlineData("max")]
    [InlineData("step")]
    [InlineData("pattern")]
    [InlineData("maxLength")]
    [InlineData("options")]
    [InlineData("optionsFrom")]
    public void A_bool_parameter_rejects_every_constraint(string member)
    {
        ParameterDefinition parameter = member switch
        {
            "min" => TestDocuments.Parameter("X", ParameterTypes.Flag) with { Min = 0 },
            "max" => TestDocuments.Parameter("X", ParameterTypes.Flag) with { Max = 1 },
            "step" => TestDocuments.Parameter("X", ParameterTypes.Flag) with { Step = 1 },
            "pattern" => TestDocuments.Parameter("X", ParameterTypes.Flag) with { Pattern = "a" },
            "maxLength" => TestDocuments.Parameter("X", ParameterTypes.Flag) with { MaxLength = 1 },
            "options" => TestDocuments.Parameter("X", ParameterTypes.Flag) with { Options = ["a"] },
            _ => TestDocuments.Parameter("X", ParameterTypes.Flag) with { OptionsFrom = "audio.devices" },
        };

        AssertParameterError(parameter, member, ValidationCodes.NotApplicable);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    public void A_bool_default_must_be_a_JSON_boolean(string json)
    {
        AssertParameterError(TestDocuments.Parameter("X", ParameterTypes.Flag) with { Default = TestDocuments.Json(json) }, "default", ValidationCodes.TypeMismatch);
    }

    // ---- enum ----

    [Fact]
    public void An_enum_parameter_accepts_options_and_a_default_among_them()
    {
        TestDocuments.AssertValid(Validate(TestData.Choice("Profile", "Normal", "TV", "Study") with { Default = TestDocuments.Text("TV") }));
    }

    [Fact]
    public void An_enum_without_options_or_optionsFrom_is_rejected()
    {
        AssertParameterError(TestDocuments.Parameter("X", ParameterTypes.Choice), "options", ValidationCodes.Required);
    }

    [Fact]
    public void An_enum_with_an_empty_options_list_is_rejected()
    {
        AssertParameterError(TestData.Choice("X"), "options", ValidationCodes.Required);
    }

    [Fact]
    public void Enum_options_must_be_unique()
    {
        ValidationResult result = Validate(TestData.Choice("X", "a", "b", "a"));

        TestDocuments.AssertError(result, "/parameters/0/options/2", ValidationCodes.Duplicate);
    }

    [Fact]
    public void Enum_options_must_not_be_empty_strings()
    {
        ValidationResult result = Validate(TestData.Choice("X", "a", string.Empty));

        TestDocuments.AssertError(result, "/parameters/0/options/1", ValidationCodes.Invalid);
    }

    [Fact]
    public void Enum_options_are_compared_case_sensitively()
    {
        TestDocuments.AssertValid(Validate(TestData.Choice("X", "tv", "TV")));
    }

    [Fact]
    public void An_enum_with_optionsFrom_and_no_options_is_valid()
    {
        TestDocuments.AssertValid(Validate(TestDocuments.Parameter("Device", ParameterTypes.Choice) with { OptionsFrom = "audio.output_devices" }));
    }

    [Fact]
    public void An_enum_must_not_set_options_and_optionsFrom_together()
    {
        AssertParameterError(TestData.Choice("X", "a") with { OptionsFrom = "audio.devices" }, "optionsFrom", ValidationCodes.Conflict);
    }

    [Fact]
    public void OptionsFrom_must_be_a_metric_id()
    {
        AssertParameterError(TestDocuments.Parameter("X", ParameterTypes.Choice) with { OptionsFrom = "Not A Metric" }, "optionsFrom", ValidationCodes.Invalid);
    }

    [Theory]
    [InlineData("min")]
    [InlineData("max")]
    [InlineData("step")]
    [InlineData("pattern")]
    [InlineData("maxLength")]
    public void An_enum_parameter_rejects_members_of_other_types(string member)
    {
        ParameterDefinition parameter = member switch
        {
            "min" => TestData.Choice("X", "a") with { Min = 0 },
            "max" => TestData.Choice("X", "a") with { Max = 1 },
            "step" => TestData.Choice("X", "a") with { Step = 1 },
            "pattern" => TestData.Choice("X", "a") with { Pattern = "a" },
            _ => TestData.Choice("X", "a") with { MaxLength = 1 },
        };

        AssertParameterError(parameter, member, ValidationCodes.NotApplicable);
    }

    [Fact]
    public void An_enum_default_must_be_one_of_the_options()
    {
        AssertParameterError(TestData.Choice("X", "a", "b") with { Default = TestDocuments.Text("c") }, "default", ValidationCodes.Options);
    }

    [Fact]
    public void An_enum_default_must_be_a_string()
    {
        AssertParameterError(TestData.Choice("X", "a") with { Default = TestDocuments.Number(1) }, "default", ValidationCodes.TypeMismatch);
    }
}
