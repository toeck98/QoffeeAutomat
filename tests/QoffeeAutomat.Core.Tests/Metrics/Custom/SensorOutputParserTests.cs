using System.Globalization;
using QoffeeAutomat.Core.Metrics.Custom;

namespace QoffeeAutomat.Core.Tests.Metrics.Custom;

/// <summary>The last non-empty output line, parsed according to the value type (PLAN.md T215).</summary>
[Trait("Category", "CustomSensors")]
public sealed class SensorOutputParserTests
{
    [Theory]
    [InlineData("42", "42")]
    [InlineData("42\n", "42")]
    [InlineData("42\r\n", "42")]
    [InlineData("first\nsecond\nthird", "third")]
    [InlineData("first\r\nsecond\r\n\r\n   \r\n\r\n", "second")]
    [InlineData("progress...\n  7.5  \n\n\n", "7.5")]
    [InlineData("a\rb\rc", "c")]
    [InlineData("\n\n  only  \n", "only")]
    [InlineData("\t tab and spaces \t", "tab and spaces")]
    public void The_last_non_empty_line_is_found(string output, string expected)
    {
        Assert.Equal(expected, SensorOutputParser.LastNonEmptyLine(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("   ")]
    [InlineData("\r\n \t \r\n")]
    public void Output_without_a_non_empty_line_has_none(string output)
    {
        Assert.Null(SensorOutputParser.LastNonEmptyLine(output));
    }

    [Theory]
    [InlineData("42", 42.0)]
    [InlineData("-1.5", -1.5)]
    [InlineData("+3", 3.0)]
    [InlineData("1e3", 1000.0)]
    [InlineData("0.30000000000000004", 0.30000000000000004)]
    [InlineData("  7  ", 7.0)]
    [InlineData("log line\n12.25\r\n", 12.25)]
    public void A_number_is_read_with_the_invariant_culture(string output, double expected)
    {
        SensorParseResult result = SensorOutputParser.Parse(output, "number");

        Assert.True(result.IsValid);
        Assert.Null(result.Failure);
        Assert.Equal(expected, Assert.IsType<double>(result.Value));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1,5")]
    [InlineData("12 %")]
    [InlineData("0x10")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1.2.3")]
    public void Text_that_is_not_a_finite_number_is_rejected(string output)
    {
        SensorParseResult result = SensorOutputParser.Parse(output, "number");

        Assert.False(result.IsValid);
        Assert.Null(result.Value);
        Assert.Contains("is not a number", result.Failure, StringComparison.Ordinal);
        Assert.Contains(output, result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Numbers_do_not_depend_on_the_culture_of_the_machine()
    {
        CultureInfo german;
        try
        {
            german = new CultureInfo("de-DE");
        }
        catch (CultureNotFoundException)
        {
            // A machine in invariant globalization mode has no German culture; then there is nothing to prove.
            return;
        }

        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = german;

            Assert.Equal(1.5, SensorOutputParser.Parse("1.5", "number").Value);
            Assert.False(SensorOutputParser.Parse("1,5", "number").IsValid);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("On", true)]
    [InlineData("False", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("No", false)]
    [InlineData("off", false)]
    [InlineData("noise\r\nTrue\r\n", true)]
    public void A_bool_is_read_from_the_usual_spellings(string output, bool expected)
    {
        SensorParseResult result = SensorOutputParser.Parse(output, "bool");

        Assert.True(result.IsValid);
        Assert.Equal(expected, Assert.IsType<bool>(result.Value));
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("2")]
    [InlineData("truee")]
    [InlineData("$true")]
    public void Text_that_is_not_a_bool_is_rejected(string output)
    {
        SensorParseResult result = SensorOutputParser.Parse(output, "bool");

        Assert.False(result.IsValid);
        Assert.Contains("is not true or false", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_string_is_the_trimmed_last_line()
    {
        SensorParseResult result = SensorOutputParser.Parse("ignored\n  Playing: Song ✓  \n\n", "string");

        Assert.Equal("Playing: Song ✓", result.Value);
    }

    [Theory]
    [InlineData("2026-03-04T05:06:07Z", "2026-03-04T05:06:07+00:00")]
    [InlineData("2026-03-04T05:06:07+02:00", "2026-03-04T03:06:07+00:00")]
    [InlineData("2026-03-04 05:06:07", "2026-03-04T05:06:07+00:00")]
    public void A_timestamp_is_read_as_utc(string output, string expected)
    {
        SensorParseResult result = SensorOutputParser.Parse(output, "timestamp");

        DateTimeOffset moment = Assert.IsType<DateTimeOffset>(result.Value);
        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), moment);
        Assert.Equal(TimeSpan.Zero, moment.Offset);
    }

    [Fact]
    public void Text_that_is_not_a_timestamp_is_rejected()
    {
        SensorParseResult result = SensorOutputParser.Parse("yesterday", "timestamp");

        Assert.False(result.IsValid);
        Assert.Contains("not a date and time", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void An_enum_value_must_be_one_of_the_options()
    {
        string[] options = ["Idle", "Playing", "Paused"];

        Assert.Equal("Playing", SensorOutputParser.Parse("Playing", "enum", options).Value);
        Assert.Equal("Playing", SensorOutputParser.Parse("playing", "enum", options).Value);

        SensorParseResult unknown = SensorOutputParser.Parse("Stopped", "enum", options);
        Assert.False(unknown.IsValid);
        Assert.Contains("Stopped", unknown.Failure, StringComparison.Ordinal);
        Assert.Contains("Idle, Playing, Paused", unknown.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void An_enum_without_options_is_rejected()
    {
        Assert.False(SensorOutputParser.Parse("x", "enum").IsValid);
        Assert.False(SensorOutputParser.Parse("x", "enum", []).IsValid);
    }

    [Fact]
    public void An_unknown_value_type_is_rejected()
    {
        SensorParseResult result = SensorOutputParser.Parse("1", "complex");

        Assert.False(result.IsValid);
        Assert.Contains("'complex'", result.Failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n\r\n")]
    [InlineData("   \n\t")]
    public void Output_without_a_line_is_rejected_for_every_type(string output)
    {
        foreach (string valueType in new[] { "number", "bool", "string", "timestamp" })
        {
            SensorParseResult result = SensorOutputParser.Parse(output, valueType);

            Assert.False(result.IsValid);
            Assert.Equal("The script wrote no output.", result.Failure);
        }
    }

    [Fact]
    public void A_long_offending_line_is_shortened_in_the_message()
    {
        string line = new('x', 500);

        SensorParseResult result = SensorOutputParser.Parse(line, "number");

        Assert.False(result.IsValid);
        Assert.True(result.Failure!.Length < 200, "The message must stay short.");
        Assert.Contains("...", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Result_factories_enforce_their_contract()
    {
        Assert.Throws<ArgumentNullException>(() => SensorParseResult.Success(null!));
        Assert.Throws<ArgumentException>(() => SensorParseResult.Fail(" "));
        Assert.Throws<ArgumentNullException>(() => SensorOutputParser.Parse(null!, "number"));
        Assert.Throws<ArgumentNullException>(() => SensorOutputParser.LastNonEmptyLine(null!));
    }
}
