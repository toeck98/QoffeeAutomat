using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Contracts.Tests;

/// <summary>
/// The shared definitions of <c>power.keep_awake</c>, <c>power.cancel</c> and the optional countdown variables (PLAN.md S11, T715).
/// The Windows agent and the SessionHost cannot reference each other, so the definitions live in Contracts and are tested here, on every
/// system.
/// </summary>
[Trait("Category", "PowerDelay")]
public class PowerActionSpecTests
{
    private static IEnumerable<ParameterDefinition> All =>
        PowerActionSpec.KeepAwakeParameters.Concat(PowerActionSpec.DelayParameters).Concat(PowerActionSpec.CancelParameters);

    [Fact]
    public void Every_parameter_name_is_a_valid_variable_name()
    {
        foreach (ParameterDefinition parameter in All)
        {
            Assert.Matches("^[A-Za-z][A-Za-z0-9_]{0,31}$", parameter.Name);
        }
    }

    [Fact]
    public void The_names_follow_the_plan()
    {
        Assert.Equal("power.keep_awake", PowerActionSpec.KeepAwake);
        Assert.Equal("power.cancel", PowerActionSpec.Cancel);
        Assert.Equal("power.keep_awake", PowerActionSpec.KeepAwakeMetric);
        Assert.Equal(["On", "Display", "Minutes"], PowerActionSpec.KeepAwakeParameters.Select(parameter => parameter.Name));
        Assert.Equal(["DelaySeconds", "Message"], PowerActionSpec.DelayParameters.Select(parameter => parameter.Name));
        Assert.Empty(PowerActionSpec.CancelParameters);
    }

    [Fact]
    public void The_countdown_is_an_internal_action_with_its_own_variables_and_is_not_a_public_parameter_list()
    {
        Assert.Equal("power.countdown", PowerActionSpec.Countdown);
        Assert.DoesNotContain(All, parameter => parameter.Name is "Title" or "Seconds");
        Assert.NotEqual(PowerActionSpec.KeepAwake, PowerActionSpec.Countdown);
        Assert.NotEqual(PowerActionSpec.Cancel, PowerActionSpec.Countdown);
    }

    [Fact]
    public void The_delay_is_a_whole_number_of_seconds_from_0_to_3600_that_defaults_to_0_and_the_message_is_limited()
    {
        ParameterDefinition delay = PowerActionSpec.DelayParameters[0];
        ParameterDefinition message = PowerActionSpec.DelayParameters[1];

        Assert.Equal(ParameterTypes.WholeNumber, delay.Type);
        Assert.Equal(0d, delay.Min);
        Assert.Equal(3600d, delay.Max);
        Assert.Equal(0, delay.Default!.Value.GetInt32());
        Assert.False(delay.Required);
        Assert.Equal(ParameterTypes.Text, message.Type);
        Assert.Equal(256, message.MaxLength);
        Assert.False(message.Required);
    }

    [Fact]
    public void Keep_awake_has_On_defaulting_to_true_Display_to_false_and_minutes_from_0_to_a_week()
    {
        ParameterDefinition on = PowerActionSpec.KeepAwakeParameters[0];
        ParameterDefinition display = PowerActionSpec.KeepAwakeParameters[1];
        ParameterDefinition minutes = PowerActionSpec.KeepAwakeParameters[2];

        Assert.Equal(ParameterTypes.Flag, on.Type);
        Assert.True(on.Default!.Value.GetBoolean());
        Assert.Equal(ParameterTypes.Flag, display.Type);
        Assert.False(display.Default!.Value.GetBoolean());
        Assert.Equal(ParameterTypes.WholeNumber, minutes.Type);
        Assert.Equal(0d, minutes.Min);
        Assert.Equal(10080d, minutes.Max);
        Assert.Equal(0, minutes.Default!.Value.GetInt32());
    }

    [Fact]
    public void Every_default_lies_inside_the_range_of_its_parameter()
    {
        foreach (ParameterDefinition parameter in All.Where(candidate => candidate.Type == ParameterTypes.WholeNumber))
        {
            double value = parameter.Default!.Value.GetDouble();

            Assert.InRange(value, parameter.Min!.Value, parameter.Max!.Value);
        }
    }

    [Fact]
    public void Titles_and_descriptions_are_given()
    {
        Assert.False(string.IsNullOrWhiteSpace(PowerActionSpec.KeepAwakeTitle));
        Assert.False(string.IsNullOrWhiteSpace(PowerActionSpec.KeepAwakeDescription));
        Assert.False(string.IsNullOrWhiteSpace(PowerActionSpec.CancelTitle));
        Assert.False(string.IsNullOrWhiteSpace(PowerActionSpec.CancelDescription));
        Assert.All(All, parameter => Assert.False(string.IsNullOrWhiteSpace(parameter.Description), parameter.Name));
    }
}
