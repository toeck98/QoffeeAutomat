using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Tests.Validation;

/// <summary>One test group per row of the haControl table of PLAN.md S06.</summary>
[Trait("Category", "Validation")]
public sealed class HaControlTests
{
    private static ValidationResult Validate(HaControl control, params ParameterDefinition[] parameters) =>
        DocumentValidator.ValidateCommand(TestData.WithControl(control, parameters));

    private static ParameterDefinition ParameterOf(string type) => type == ParameterTypes.Choice
        ? TestData.Choice("P", "a", "b")
        : TestDocuments.Parameter("P", type);

    // ---- none ----

    [Fact]
    public void None_needs_no_parameter()
    {
        TestDocuments.AssertValid(Validate(new HaControl(HaControlTypes.None)));
    }

    [Fact]
    public void None_rejects_a_parameter()
    {
        ValidationResult result = Validate(new HaControl(HaControlTypes.None, "P"), TestDocuments.Parameter("P", ParameterTypes.Flag));

        TestDocuments.AssertError(result, "/haControl/parameter", ValidationCodes.NotApplicable);
    }

    [Fact]
    public void A_missing_haControl_is_valid()
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(TestDocuments.Command() with { HaControl = null }));
    }

    // ---- button ----

    [Fact]
    public void Button_without_parameters_is_valid()
    {
        TestDocuments.AssertValid(Validate(new HaControl(HaControlTypes.Button)));
    }

    [Fact]
    public void Button_accepts_optional_parameters_and_required_ones_with_a_default()
    {
        TestDocuments.AssertValid(Validate(
            new HaControl(HaControlTypes.Button),
            TestDocuments.Parameter("Optional", ParameterTypes.Text),
            TestDocuments.Parameter("WithDefault", ParameterTypes.WholeNumber) with { Required = true, Default = TestDocuments.Number(3) }));
    }

    [Fact]
    public void Button_rejects_a_required_parameter_without_default()
    {
        ValidationResult result = Validate(
            new HaControl(HaControlTypes.Button),
            TestDocuments.Parameter("Must", ParameterTypes.Text) with { Required = true });

        TestDocuments.AssertError(result, "/haControl/type", ValidationCodes.Invalid);
    }

    [Fact]
    public void Button_rejects_a_required_parameter_whose_default_is_null()
    {
        ValidationResult result = Validate(
            new HaControl(HaControlTypes.Button),
            TestDocuments.Parameter("Must", ParameterTypes.Text) with { Required = true, Default = TestDocuments.Json("null") });

        TestDocuments.AssertError(result, "/haControl/type", ValidationCodes.Invalid);
    }

    [Fact]
    public void Button_rejects_a_parameter_reference()
    {
        ValidationResult result = Validate(new HaControl(HaControlTypes.Button, "P"), TestDocuments.Parameter("P", ParameterTypes.Text));

        TestDocuments.AssertError(result, "/haControl/parameter", ValidationCodes.NotApplicable);
    }

    // ---- switch, select, number, text: parameter required and typed ----

    [Theory]
    [InlineData(HaControlTypes.Switch, ParameterTypes.Flag)]
    [InlineData(HaControlTypes.Select, ParameterTypes.Choice)]
    [InlineData(HaControlTypes.Number, ParameterTypes.WholeNumber)]
    [InlineData(HaControlTypes.Number, ParameterTypes.Real)]
    [InlineData(HaControlTypes.Text, ParameterTypes.Text)]
    public void A_control_accepts_a_parameter_of_its_type(string control, string parameterType)
    {
        TestDocuments.AssertValid(Validate(new HaControl(control, "P"), ParameterOf(parameterType)));
    }

    [Theory]
    [InlineData(HaControlTypes.Switch)]
    [InlineData(HaControlTypes.Select)]
    [InlineData(HaControlTypes.Number)]
    [InlineData(HaControlTypes.Text)]
    public void A_control_without_a_parameter_is_rejected(string control)
    {
        ValidationResult result = Validate(new HaControl(control));

        TestDocuments.AssertError(result, "/haControl/parameter", ValidationCodes.Required);
    }

    [Theory]
    [InlineData(HaControlTypes.Switch)]
    [InlineData(HaControlTypes.Select)]
    [InlineData(HaControlTypes.Number)]
    [InlineData(HaControlTypes.Text)]
    public void A_control_naming_an_undeclared_parameter_is_rejected(string control)
    {
        ValidationResult result = Validate(new HaControl(control, "Missing"), TestDocuments.Parameter("Other", ParameterTypes.Text));

        TestDocuments.AssertError(result, "/haControl/parameter", ValidationCodes.UnknownReference);
    }

    [Fact]
    public void A_control_without_any_parameters_declared_is_rejected()
    {
        ValidationResult result = DocumentValidator.ValidateCommand(
            TestDocuments.Command() with { HaControl = new HaControl(HaControlTypes.Switch, "P"), Parameters = null });

        TestDocuments.AssertError(result, "/haControl/parameter", ValidationCodes.UnknownReference);
    }

    [Fact]
    public void The_parameter_reference_is_case_sensitive()
    {
        ValidationResult result = Validate(new HaControl(HaControlTypes.Switch, "p"), TestDocuments.Parameter("P", ParameterTypes.Flag));

        TestDocuments.AssertError(result, "/haControl/parameter", ValidationCodes.UnknownReference);
    }

    [Theory]
    [InlineData(HaControlTypes.Switch, ParameterTypes.Text)]
    [InlineData(HaControlTypes.Switch, ParameterTypes.WholeNumber)]
    [InlineData(HaControlTypes.Switch, ParameterTypes.Real)]
    [InlineData(HaControlTypes.Switch, ParameterTypes.Choice)]
    [InlineData(HaControlTypes.Select, ParameterTypes.Text)]
    [InlineData(HaControlTypes.Select, ParameterTypes.Flag)]
    [InlineData(HaControlTypes.Select, ParameterTypes.WholeNumber)]
    [InlineData(HaControlTypes.Select, ParameterTypes.Real)]
    [InlineData(HaControlTypes.Number, ParameterTypes.Text)]
    [InlineData(HaControlTypes.Number, ParameterTypes.Flag)]
    [InlineData(HaControlTypes.Number, ParameterTypes.Choice)]
    [InlineData(HaControlTypes.Text, ParameterTypes.Flag)]
    [InlineData(HaControlTypes.Text, ParameterTypes.WholeNumber)]
    [InlineData(HaControlTypes.Text, ParameterTypes.Real)]
    [InlineData(HaControlTypes.Text, ParameterTypes.Choice)]
    public void A_control_rejects_a_parameter_of_the_wrong_type(string control, string parameterType)
    {
        ValidationResult result = Validate(new HaControl(control, "P"), ParameterOf(parameterType));

        TestDocuments.AssertError(result, "/haControl/parameter", ValidationCodes.TypeMismatch);
    }

    // ---- type and stateMetric ----

    [Theory]
    [InlineData("toggle")]
    [InlineData("Switch")]
    [InlineData("")]
    public void An_unknown_control_type_is_rejected(string type)
    {
        ValidationResult result = Validate(new HaControl(type));

        TestDocuments.AssertError(result, "/haControl/type", ValidationCodes.Invalid);
    }

    [Fact]
    public void A_stateMetric_must_be_a_metric_id()
    {
        TestDocuments.AssertValid(Validate(
            new HaControl(HaControlTypes.Switch, "P", "custom.lamp_state"),
            TestDocuments.Parameter("P", ParameterTypes.Flag)));
        TestDocuments.AssertError(
            Validate(new HaControl(HaControlTypes.Switch, "P", "Not A Metric"), TestDocuments.Parameter("P", ParameterTypes.Flag)),
            "/haControl/stateMetric",
            ValidationCodes.Invalid);
    }

    [Fact]
    public void A_null_stateMetric_means_an_optimistic_entity()
    {
        TestDocuments.AssertValid(Validate(
            new HaControl(HaControlTypes.Select, "P", null),
            TestData.Choice("P", "a")));
    }
}
