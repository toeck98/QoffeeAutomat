using QoffeeAutomat.Platform.Windows.Sessions;

namespace QoffeeAutomat.Platform.Windows.Tests.Sessions;

/// <summary>
/// The environment block of an elevated process (PLAN.md S11, T311): the block of the interactive user plus the variables of
/// the run (<c>QA_*</c>). Pure text handling, so it is tested on every system.
/// </summary>
[Trait("Category", "Elevated")]
public sealed class ElevatedEnvironmentTests
{
    [Fact]
    public void A_block_is_read_into_variables_that_are_looked_up_ignoring_case()
    {
        Dictionary<string, string> variables = ElevatedEnvironment.Parse("Path=C:\\Windows\0TEMP=C:\\Temp\0\0");

        Assert.Equal(2, variables.Count);
        Assert.Equal("C:\\Windows", variables["PATH"]);
        Assert.Equal("C:\\Temp", variables["temp"]);
    }

    [Fact]
    public void The_hidden_entries_that_start_with_an_equals_sign_are_kept()
    {
        Dictionary<string, string> variables = ElevatedEnvironment.Parse("=C:=C:\\Users\\alice\0A=1\0\0");

        Assert.Equal("C:\\Users\\alice", variables["=C:"]);
        Assert.Equal("1", variables["A"]);
    }

    [Fact]
    public void A_value_may_contain_equals_signs()
    {
        Dictionary<string, string> variables = ElevatedEnvironment.Parse("OPTIONS=a=b=c\0\0");

        Assert.Equal("a=b=c", variables["OPTIONS"]);
    }

    [Fact]
    public void Reading_stops_at_the_terminating_empty_entry()
    {
        Dictionary<string, string> variables = ElevatedEnvironment.Parse("A=1\0\0B=2\0\0");

        Assert.Equal("A", Assert.Single(variables.Keys));
    }

    [Fact]
    public void An_entry_without_a_name_or_without_an_equals_sign_is_skipped()
    {
        Dictionary<string, string> variables = ElevatedEnvironment.Parse("NOVALUE\0=\0A=1\0\0");

        Assert.Equal("A", Assert.Single(variables.Keys));
    }

    [Fact]
    public void An_empty_block_has_no_variables()
    {
        Assert.Empty(ElevatedEnvironment.Parse(string.Empty));
        Assert.Empty(ElevatedEnvironment.Parse("\0\0"));
    }

    [Fact]
    public void A_block_without_the_final_terminator_is_still_read()
    {
        Dictionary<string, string> variables = ElevatedEnvironment.Parse("A=1\0B=2");

        Assert.Equal("1", variables["A"]);
        Assert.Equal("2", variables["B"]);
    }

    [Fact]
    public void The_variables_of_the_run_are_added_to_those_of_the_user_and_replace_variables_of_the_same_name_ignoring_case()
    {
        var user = new Dictionary<string, string> { ["Path"] = "C:\\Win", ["TEMP"] = "C:\\T" };
        var additions = new Dictionary<string, string> { ["QA_RUN_ID"] = "run1", ["temp"] = "D:\\X" };

        string block = ElevatedEnvironment.Build(user, additions);

        Assert.Equal("Path=C:\\Win\0QA_RUN_ID=run1\0TEMP=D:\\X\0\0", block);
    }

    [Fact]
    public void The_entries_are_sorted_by_name_ignoring_case_with_the_hidden_ones_first()
    {
        var user = new Dictionary<string, string> { ["zeta"] = "1", ["Alpha"] = "2", ["=C:"] = "C:\\" };

        string block = ElevatedEnvironment.Build(user, new Dictionary<string, string>());

        Assert.Equal("=C:=C:\\\0Alpha=2\0zeta=1\0\0", block);
    }

    [Fact]
    public void An_empty_environment_is_two_nul_characters()
    {
        string block = ElevatedEnvironment.Build(new Dictionary<string, string>(), new Dictionary<string, string>());

        Assert.Equal("\0\0", block);
    }

    [Fact]
    public void A_built_block_reads_back_to_the_same_variables()
    {
        var user = new Dictionary<string, string> { ["USERPROFILE"] = "C:\\Users\\alice", ["OPTIONS"] = "a=b" };
        var additions = new Dictionary<string, string> { ["QA_VAR_LEVEL"] = "42" };

        Dictionary<string, string> roundTrip = ElevatedEnvironment.Parse(ElevatedEnvironment.Build(user, additions));

        Assert.Equal(3, roundTrip.Count);
        Assert.Equal("C:\\Users\\alice", roundTrip["USERPROFILE"]);
        Assert.Equal("a=b", roundTrip["OPTIONS"]);
        Assert.Equal("42", roundTrip["QA_VAR_LEVEL"]);
    }

    [Fact]
    public void Building_does_not_change_the_dictionaries_that_were_passed_in()
    {
        var user = new Dictionary<string, string> { ["A"] = "1" };
        var additions = new Dictionary<string, string> { ["A"] = "2", ["B"] = "3" };

        ElevatedEnvironment.Build(user, additions);

        Assert.Equal("1", Assert.Single(user).Value);
        Assert.Equal(2, additions.Count);
    }

    [Theory]
    [InlineData("", "value")]
    [InlineData("A=B", "value")]
    public void A_variable_with_an_empty_name_or_an_equals_sign_in_the_name_is_refused(string name, string value)
    {
        var additions = new Dictionary<string, string> { [name] = value };

        Assert.Throws<ArgumentException>(() => ElevatedEnvironment.Build(new Dictionary<string, string>(), additions));
    }

    [Fact]
    public void A_nul_character_in_a_name_or_a_value_is_refused_because_it_would_end_the_entry()
    {
        var nulInName = new Dictionary<string, string> { ["A" + '\0' + "B"] = "value" };
        var nulInValue = new Dictionary<string, string> { ["NAME"] = "va" + '\0' + "lue" };

        Assert.Throws<ArgumentException>(() => ElevatedEnvironment.Build(new Dictionary<string, string>(), nulInName));
        Assert.Throws<ArgumentException>(() => ElevatedEnvironment.Build(new Dictionary<string, string>(), nulInValue));
    }

    [Fact]
    public void A_value_with_shell_syntax_is_copied_unchanged()
    {
        const string Value = "a & b | c > d \"quoted\" %PATH% $(whoami)";
        var additions = new Dictionary<string, string> { ["QA_VAR_TEXT"] = Value };

        Dictionary<string, string> roundTrip = ElevatedEnvironment.Parse(ElevatedEnvironment.Build(new Dictionary<string, string>(), additions));

        Assert.Equal(Value, roundTrip["QA_VAR_TEXT"]);
    }
}
