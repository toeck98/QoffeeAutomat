using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Tests.Validation;

[Trait("Category", "Validation")]
public sealed class VariableBinderTests
{
    private static BindResult Bind(ParameterDefinition parameter, JsonElement? value)
    {
        IReadOnlyDictionary<string, JsonElement> variables = value is { } supplied
            ? TestDocuments.Vars((parameter.Name, supplied))
            : TestDocuments.Vars();
        return VariableBinder.Bind([parameter], variables);
    }

    private static void AssertBound(BindResult result, string name, object expected)
    {
        Assert.True(result.IsValid, TestDocuments.Describe(result));
        Assert.Equal(expected, result.Values[name]);
    }

    // ---- shape ----

    [Fact]
    public void No_parameters_and_no_variables_bind_to_nothing()
    {
        BindResult result = VariableBinder.Bind(null, null);

        Assert.True(result.IsValid);
        Assert.Empty(result.Values);
    }

    [Fact]
    public void Unknown_variables_are_rejected()
    {
        BindResult result = VariableBinder.Bind(
            [TestDocuments.Parameter("Known", ParameterTypes.Text)],
            TestDocuments.Vars(("Known", TestDocuments.Text("x")), ("Unknown", TestDocuments.Text("y"))));

        Assert.False(result.IsValid);
        TestDocuments.AssertError(result, "/vars/Unknown", ValidationCodes.UnknownVariable);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void Variables_without_any_declared_parameters_are_rejected()
    {
        BindResult result = VariableBinder.Bind(null, TestDocuments.Vars(("X", TestDocuments.Text("y"))));

        TestDocuments.AssertError(result, "/vars/X", ValidationCodes.UnknownVariable);
    }

    [Fact]
    public void Variable_names_are_case_sensitive()
    {
        BindResult result = VariableBinder.Bind(
            [TestDocuments.Parameter("Profile", ParameterTypes.Text)],
            TestDocuments.Vars(("profile", TestDocuments.Text("x"))));

        TestDocuments.AssertError(result, "/vars/profile", ValidationCodes.UnknownVariable);
    }

    [Fact]
    public void The_pointer_of_an_unknown_name_is_escaped()
    {
        BindResult result = VariableBinder.Bind(null, TestDocuments.Vars(("a/b", TestDocuments.Text("y"))));

        TestDocuments.AssertError(result, "/vars/a~1b", ValidationCodes.UnknownVariable);
    }

    [Fact]
    public void A_missing_required_variable_is_rejected()
    {
        BindResult result = Bind(TestDocuments.Parameter("Must", ParameterTypes.Text) with { Required = true }, null);

        TestDocuments.AssertError(result, "/vars/Must", ValidationCodes.Required);
    }

    [Fact]
    public void A_JSON_null_counts_as_missing()
    {
        ParameterDefinition required = TestDocuments.Parameter("Must", ParameterTypes.Text) with { Required = true };
        ParameterDefinition withDefault = required with { Default = TestDocuments.Text("fallback") };

        TestDocuments.AssertError(Bind(required, TestDocuments.Json("null")), "/vars/Must", ValidationCodes.Required);
        AssertBound(Bind(withDefault, TestDocuments.Json("null")), "Must", "fallback");
    }

    [Fact]
    public void An_optional_variable_without_value_and_default_is_absent()
    {
        BindResult result = Bind(TestDocuments.Parameter("Optional", ParameterTypes.WholeNumber), null);

        Assert.True(result.IsValid);
        Assert.DoesNotContain("Optional", result.Values.Keys);
    }

    [Fact]
    public void All_errors_of_one_call_are_reported_together()
    {
        BindResult result = VariableBinder.Bind(
            [
                TestDocuments.Parameter("A", ParameterTypes.Text) with { Required = true },
                TestDocuments.Parameter("B", ParameterTypes.WholeNumber) with { Max = 5 },
            ],
            TestDocuments.Vars(("B", TestDocuments.Number(9)), ("C", TestDocuments.Text("x"))));

        Assert.Equal(3, result.Errors.Count);
        TestDocuments.AssertError(result, "/vars/A", ValidationCodes.Required);
        TestDocuments.AssertError(result, "/vars/B", ValidationCodes.Range);
        TestDocuments.AssertError(result, "/vars/C", ValidationCodes.UnknownVariable);
    }

    [Fact]
    public void The_example_command_of_PLAN_S06_binds_its_variables()
    {
        CommandDocument example = TestData.ReadCommand("command.example.json");

        BindResult result = VariableBinder.Bind(example.Parameters, TestDocuments.Vars(("Profile", TestDocuments.Text("TV-Mode"))));

        Assert.True(result.IsValid, TestDocuments.Describe(result));
        Assert.Equal("TV-Mode", result.Values["Profile"]);
        Assert.Equal(false, result.Values["UpdateAdapterIds"]);
        TestDocuments.AssertError(
            VariableBinder.Bind(example.Parameters, TestDocuments.Vars(("Profile", TestDocuments.Text("Gaming")))),
            "/vars/Profile",
            ValidationCodes.Options);
        TestDocuments.AssertError(VariableBinder.Bind(example.Parameters, TestDocuments.Vars()), "/vars/Profile", ValidationCodes.Required);
    }

    // ---- defaults ----

    [Fact]
    public void A_default_is_applied_and_converted()
    {
        AssertBound(Bind(TestDocuments.Parameter("N", ParameterTypes.WholeNumber) with { Default = TestDocuments.Number(7) }, null), "N", 7L);
        AssertBound(Bind(TestDocuments.Parameter("R", ParameterTypes.Real) with { Default = TestDocuments.Number(1.5) }, null), "R", 1.5);
        AssertBound(Bind(TestDocuments.Parameter("F", ParameterTypes.Flag) with { Default = TestDocuments.Flag(true) }, null), "F", true);
        AssertBound(Bind(TestDocuments.Parameter("S", ParameterTypes.Text) with { Default = TestDocuments.Text("x") }, null), "S", "x");
        AssertBound(Bind(TestData.Choice("E", "a", "b") with { Default = TestDocuments.Text("b") }, null), "E", "b");
    }

    [Fact]
    public void A_supplied_value_wins_over_the_default()
    {
        ParameterDefinition parameter = TestDocuments.Parameter("N", ParameterTypes.WholeNumber) with { Default = TestDocuments.Number(7) };

        AssertBound(Bind(parameter, TestDocuments.Number(8)), "N", 8L);
    }

    [Fact]
    public void A_required_parameter_with_default_needs_no_value()
    {
        ParameterDefinition parameter = TestDocuments.Parameter("N", ParameterTypes.WholeNumber) with { Required = true, Default = TestDocuments.Number(7) };

        AssertBound(Bind(parameter, null), "N", 7L);
    }

    [Fact]
    public void An_invalid_default_is_reported_instead_of_applied()
    {
        ParameterDefinition parameter = TestDocuments.Parameter("N", ParameterTypes.WholeNumber) with { Max = 5, Default = TestDocuments.Number(7) };

        TestDocuments.AssertError(Bind(parameter, null), "/vars/N", ValidationCodes.Range);
    }

    // ---- string ----

    [Fact]
    public void A_string_is_bound_as_is()
    {
        AssertBound(Bind(TestDocuments.Parameter("S", ParameterTypes.Text), TestDocuments.Text("hello world")), "S", "hello world");
    }

    [Fact]
    public void A_string_may_be_empty()
    {
        AssertBound(Bind(TestDocuments.Parameter("S", ParameterTypes.Text), TestDocuments.Text(string.Empty)), "S", string.Empty);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("[]")]
    public void A_string_parameter_rejects_other_JSON_types(string json)
    {
        BindResult result = Bind(TestDocuments.Parameter("S", ParameterTypes.Text), TestDocuments.Json(json));

        TestDocuments.AssertError(result, "/vars/S", ValidationCodes.TypeMismatch);
    }

    [Fact]
    public void A_string_over_maxLength_is_rejected()
    {
        ParameterDefinition parameter = TestDocuments.Parameter("S", ParameterTypes.Text) with { MaxLength = 5 };

        AssertBound(Bind(parameter, TestDocuments.Text("12345")), "S", "12345");
        TestDocuments.AssertError(Bind(parameter, TestDocuments.Text("123456")), "/vars/S", ValidationCodes.MaxLength);
    }

    [Fact]
    public void Without_maxLength_the_limit_is_1024()
    {
        ParameterDefinition parameter = TestDocuments.Parameter("S", ParameterTypes.Text);

        Assert.True(Bind(parameter, TestDocuments.Text(new string('x', 1024))).IsValid);
        TestDocuments.AssertError(Bind(parameter, TestDocuments.Text(new string('x', 1025))), "/vars/S", ValidationCodes.MaxLength);
    }

    [Fact]
    public void A_string_must_match_the_pattern()
    {
        ParameterDefinition parameter = TestDocuments.Parameter("S", ParameterTypes.Text) with { Pattern = "^[a-z]{2,4}$" };

        AssertBound(Bind(parameter, TestDocuments.Text("abc")), "S", "abc");
        TestDocuments.AssertError(Bind(parameter, TestDocuments.Text("ABC")), "/vars/S", ValidationCodes.Pattern);
        TestDocuments.AssertError(Bind(parameter, TestDocuments.Text("abcde")), "/vars/S", ValidationCodes.Pattern);
    }

    [Fact]
    public void A_catastrophic_pattern_ends_with_a_timeout_error()
    {
        ParameterDefinition parameter = TestDocuments.Parameter("S", ParameterTypes.Text) with { Pattern = "^(a+)+$" };
        JsonElement hostile = TestDocuments.Text(new string('a', 64) + "!");

        BindResult result = Bind(parameter, hostile);

        TestDocuments.AssertError(result, "/vars/S", ValidationCodes.PatternTimeout);
    }

    [Fact]
    public void An_invalid_pattern_in_the_definition_is_reported_not_thrown()
    {
        ParameterDefinition parameter = TestDocuments.Parameter("S", ParameterTypes.Text) with { Pattern = "([" };

        TestDocuments.AssertError(Bind(parameter, TestDocuments.Text("x")), "/vars/S", ValidationCodes.InvalidPattern);
    }

    [Fact]
    public void Error_messages_never_contain_the_value()
    {
        const string Secret = "hunter2-secret-value";
        ParameterDefinition parameter = TestDocuments.Parameter("Password", ParameterTypes.Text) with { Secret = true, Pattern = "^[0-9]+$", MaxLength = 5 };

        BindResult tooLong = Bind(parameter, TestDocuments.Text(Secret));
        BindResult wrongPattern = Bind(parameter with { MaxLength = null }, TestDocuments.Text(Secret));

        Assert.DoesNotContain(Secret, TestDocuments.Describe(tooLong), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, TestDocuments.Describe(wrongPattern), StringComparison.Ordinal);
    }

    // ---- int ----

    [Fact]
    public void An_int_is_bound_as_long()
    {
        AssertBound(Bind(TestDocuments.Parameter("N", ParameterTypes.WholeNumber), TestDocuments.Number(42)), "N", 42L);
        AssertBound(Bind(TestDocuments.Parameter("N", ParameterTypes.WholeNumber), TestDocuments.Number(-3)), "N", -3L);
    }

    [Fact]
    public void An_int_accepts_whole_numbers_written_in_other_forms()
    {
        ParameterDefinition parameter = TestDocuments.Parameter("N", ParameterTypes.WholeNumber);

        AssertBound(Bind(parameter, TestDocuments.Json("1e3")), "N", 1000L);
        AssertBound(Bind(parameter, TestDocuments.Json("5.0")), "N", 5L);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("\"abc\"")]
    [InlineData("\"1.5\"")]
    [InlineData("1e30")]
    [InlineData("{}")]
    public void An_int_rejects_non_integers(string json)
    {
        BindResult result = Bind(TestDocuments.Parameter("N", ParameterTypes.WholeNumber), TestDocuments.Json(json));

        TestDocuments.AssertError(result, "/vars/N", ValidationCodes.TypeMismatch);
    }

    [Fact]
    public void A_numeric_string_is_converted_for_an_int()
    {
        ParameterDefinition parameter = TestDocuments.Parameter("N", ParameterTypes.WholeNumber);

        AssertBound(Bind(parameter, TestDocuments.Text("42")), "N", 42L);
        AssertBound(Bind(parameter, TestDocuments.Text("-7")), "N", -7L);
    }

    [Fact]
    public void An_int_outside_min_and_max_is_rejected()
    {
        ParameterDefinition parameter = TestDocuments.Parameter("N", ParameterTypes.WholeNumber) with { Min = 0, Max = 100 };

        AssertBound(Bind(parameter, TestDocuments.Number(0)), "N", 0L);
        AssertBound(Bind(parameter, TestDocuments.Number(100)), "N", 100L);
        TestDocuments.AssertError(Bind(parameter, TestDocuments.Number(-1)), "/vars/N", ValidationCodes.Range);
        TestDocuments.AssertError(Bind(parameter, TestDocuments.Number(101)), "/vars/N", ValidationCodes.Range);
        TestDocuments.AssertError(Bind(parameter, TestDocuments.Text("500")), "/vars/N", ValidationCodes.Range);
    }

    [Fact]
    public void An_int_must_lie_on_the_step_grid_starting_at_min()
    {
        ParameterDefinition parameter = TestDocuments.Parameter("N", ParameterTypes.WholeNumber) with { Min = 3, Max = 30, Step = 5 };

        AssertBound(Bind(parameter, TestDocuments.Number(3)), "N", 3L);
        AssertBound(Bind(parameter, TestDocuments.Number(13)), "N", 13L);
        TestDocuments.AssertError(Bind(parameter, TestDocuments.Number(10)), "/vars/N", ValidationCodes.Step);
    }

    [Fact]
    public void Without_min_the_step_grid_starts_at_zero()
    {
        ParameterDefinition parameter = TestDocuments.Parameter("N", ParameterTypes.WholeNumber) with { Step = 5 };

        AssertBound(Bind(parameter, TestDocuments.Number(-10)), "N", -10L);
        TestDocuments.AssertError(Bind(parameter, TestDocuments.Number(7)), "/vars/N", ValidationCodes.Step);
    }

    // ---- float ----

    [Fact]
    public void A_float_is_bound_as_double()
    {
        AssertBound(Bind(TestDocuments.Parameter("R", ParameterTypes.Real), TestDocuments.Number(1.25)), "R", 1.25);
        AssertBound(Bind(TestDocuments.Parameter("R", ParameterTypes.Real), TestDocuments.Number(3)), "R", 3.0);
    }

    [Fact]
    public void A_numeric_string_is_converted_for_a_float_with_the_invariant_culture()
    {
        AssertBound(Bind(TestDocuments.Parameter("R", ParameterTypes.Real), TestDocuments.Text("2.5")), "R", 2.5);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("\"abc\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("[1]")]
    public void A_float_rejects_non_numbers_and_non_finite_values(string json)
    {
        BindResult result = Bind(TestDocuments.Parameter("R", ParameterTypes.Real), TestDocuments.Json(json));

        TestDocuments.AssertError(result, "/vars/R", ValidationCodes.TypeMismatch);
    }

    [Fact]
    public void A_float_outside_min_and_max_is_rejected()
    {
        ParameterDefinition parameter = TestDocuments.Parameter("R", ParameterTypes.Real) with { Min = 0.5, Max = 2.5 };

        AssertBound(Bind(parameter, TestDocuments.Number(0.5)), "R", 0.5);
        AssertBound(Bind(parameter, TestDocuments.Number(2.5)), "R", 2.5);
        TestDocuments.AssertError(Bind(parameter, TestDocuments.Number(0.4)), "/vars/R", ValidationCodes.Range);
        TestDocuments.AssertError(Bind(parameter, TestDocuments.Number(2.6)), "/vars/R", ValidationCodes.Range);
    }

    [Fact]
    public void A_float_step_tolerates_binary_rounding()
    {
        ParameterDefinition parameter = TestDocuments.Parameter("R", ParameterTypes.Real) with { Min = 0, Step = 0.1 };

        AssertBound(Bind(parameter, TestDocuments.Number(0.3)), "R", 0.3);
        AssertBound(Bind(parameter, TestDocuments.Number(0.7)), "R", 0.7);
        TestDocuments.AssertError(Bind(parameter, TestDocuments.Number(0.35)), "/vars/R", ValidationCodes.Step);
    }

    // ---- bool ----

    [Fact]
    public void A_bool_is_bound_as_bool()
    {
        ParameterDefinition parameter = TestDocuments.Parameter("F", ParameterTypes.Flag);

        AssertBound(Bind(parameter, TestDocuments.Flag(true)), "F", true);
        AssertBound(Bind(parameter, TestDocuments.Flag(false)), "F", false);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    public void The_strings_true_and_false_are_converted_for_a_bool(string text, bool expected)
    {
        AssertBound(Bind(TestDocuments.Parameter("F", ParameterTypes.Flag), TestDocuments.Text(text)), "F", expected);
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("\"1\"")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("[]")]
    public void A_bool_rejects_other_values(string json)
    {
        BindResult result = Bind(TestDocuments.Parameter("F", ParameterTypes.Flag), TestDocuments.Json(json));

        TestDocuments.AssertError(result, "/vars/F", ValidationCodes.TypeMismatch);
    }

    // ---- enum ----

    [Fact]
    public void An_enum_accepts_only_its_options()
    {
        ParameterDefinition parameter = TestData.Choice("E", "Normal", "TV", "Study");

        AssertBound(Bind(parameter, TestDocuments.Text("TV")), "E", "TV");
        TestDocuments.AssertError(Bind(parameter, TestDocuments.Text("Gaming")), "/vars/E", ValidationCodes.Options);
    }

    [Fact]
    public void Enum_options_are_matched_case_sensitively()
    {
        TestDocuments.AssertError(Bind(TestData.Choice("E", "TV"), TestDocuments.Text("tv")), "/vars/E", ValidationCodes.Options);
    }

    [Fact]
    public void An_enum_rejects_non_strings()
    {
        TestDocuments.AssertError(Bind(TestData.Choice("E", "1"), TestDocuments.Number(1)), "/vars/E", ValidationCodes.TypeMismatch);
    }

    [Fact]
    public void An_enum_with_optionsFrom_accepts_any_non_empty_string()
    {
        ParameterDefinition parameter = TestDocuments.Parameter("Device", ParameterTypes.Choice) with { OptionsFrom = "audio.output_devices" };

        AssertBound(Bind(parameter, TestDocuments.Text("Speakers")), "Device", "Speakers");
        TestDocuments.AssertError(Bind(parameter, TestDocuments.Text(string.Empty)), "/vars/Device", ValidationCodes.Options);
    }

    // ---- unsupported type ----

    [Fact]
    public void An_unknown_parameter_type_is_reported()
    {
        BindResult result = Bind(TestDocuments.Parameter("X", "decimal"), TestDocuments.Number(1));

        TestDocuments.AssertError(result, "/vars/X", ValidationCodes.Unsupported);
    }
}
