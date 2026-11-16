using System.Globalization;
using System.Text;
using QoffeeAutomat.Core.Execution.PowerShell;
using static QoffeeAutomat.Core.Tests.Execution.PowerShell.PowerShellTestSupport;

namespace QoffeeAutomat.Core.Tests.Execution.PowerShell;

/// <summary>The command line, the wrapper text and the parameter file of PLAN.md D-012.</summary>
[Trait("Category", "PowerShell")]
public sealed class PowerShellWrapperTests
{
    /// <summary>Quotes, semicolons, dollar signs, backticks and newlines, the characters of PLAN.md T208's acceptance test.</summary>
    internal const string Hostile = "it's \"quoted\"; $env:PATH `whoami` $(Get-Date) & | > < \n second line \\ é✓";

    [Fact]
    public void The_engine_is_started_on_the_wrapper_file_with_the_documented_switches()
    {
        string wrapper = Path.Combine("some dir", "wrapper.ps1");

        IReadOnlyList<string> arguments = PowerShellWrapper.BuildArguments(wrapper);

        Assert.Equal(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", wrapper], arguments);
    }

    [Fact]
    public void The_wrapper_reads_the_parameter_file_and_splats_it_into_the_target()
    {
        string script = PowerShellWrapper.Script;

        Assert.Contains("$env:QA_PARAMS_FILE", script, StringComparison.Ordinal);
        Assert.Contains("$env:QA_TARGET_SCRIPT", script, StringComparison.Ordinal);
        Assert.Contains("& $qaTarget @qaArguments", script, StringComparison.Ordinal);
        Assert.Equal("QA_PARAMS_FILE", PowerShellWrapper.ParametersFileVariable);
        Assert.Equal("QA_TARGET_SCRIPT", PowerShellWrapper.TargetScriptVariable);
    }

    [Fact]
    public void The_wrapper_cannot_run_text_as_code_and_holds_no_data()
    {
        string script = PowerShellWrapper.Script;

        Assert.DoesNotContain("Invoke-Expression", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iex ", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Command", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[scriptblock]::Create", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Add-Type", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_wrapper_is_plain_ascii_so_that_no_engine_misreads_it()
    {
        Assert.All(PowerShellWrapper.Script, character => Assert.True(character < 0x80, $"Non-ASCII character U+{(int)character:X4}."));
    }

    [Fact]
    public void The_wrapper_uses_only_what_windows_powershell_5_1_and_powershell_7_both_have()
    {
        string script = PowerShellWrapper.Script;

        // Members and switches that exist in PowerShell 7 only.
        Assert.DoesNotContain("-AsHashtable", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-DateKind", script, StringComparison.Ordinal);
        Assert.DoesNotContain("?.", script, StringComparison.Ordinal);
        Assert.DoesNotContain("??", script, StringComparison.Ordinal);
        Assert.DoesNotContain("::new(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ForEach-Object -Parallel", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_wrapper_turns_a_terminating_error_into_exit_code_one_with_text_on_standard_error()
    {
        string script = PowerShellWrapper.Script;

        Assert.Contains("catch {", script, StringComparison.Ordinal);
        Assert.Contains("[Console]::Error.WriteLine", script, StringComparison.Ordinal);
        Assert.Contains("$qaExitCode = 1", script, StringComparison.Ordinal);
        Assert.EndsWith("exit $qaExitCode\n", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_parameter_file_carries_every_type_exactly()
    {
        byte[] file = PowerShellParameterFile.Serialize(Vars(
            ("Text", "hello"),
            ("Count", 42L),
            ("Big", 5_000_000_000L),
            ("Ratio", 1.5),
            ("Third", 1.0 / 3.0),
            ("Flag", true),
            ("Off", false),
            ("Small", 7)));

        Dictionary<string, (string Type, string Value)> read = ReadParameters(file);

        Assert.Equal(("string", Convert.ToBase64String("hello"u8.ToArray())), read["Text"]);
        Assert.Equal(("int", "42"), read["Count"]);
        Assert.Equal(("int", "5000000000"), read["Big"]);
        Assert.Equal(("int", "7"), read["Small"]);
        Assert.Equal(("float", "1.5"), read["Ratio"]);
        Assert.Equal("float", read["Third"].Type);
        Assert.Equal(1.0 / 3.0, double.Parse(read["Third"].Value, NumberStyles.Float, CultureInfo.InvariantCulture));
        Assert.Equal(("bool", "true"), read["Flag"]);
        Assert.Equal(("bool", "false"), read["Off"]);
    }

    [Fact]
    public void A_whole_valued_float_stays_a_float()
    {
        Dictionary<string, (string Type, string Value)> read = ReadParameters(PowerShellParameterFile.Serialize(Vars(("Three", 3.0))));

        Assert.Equal("float", read["Three"].Type);
        Assert.Equal(3.0, double.Parse(read["Three"].Value, NumberStyles.Float, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Text_survives_the_round_trip_whatever_it_contains()
    {
        string[] values =
        [
            Hostile,
            string.Empty,
            "2024-01-01T00:00:00Z",
            "/Date(0)/",
            "日本語 😀",
            "line1\r\nline2",
            "\0 nul",
        ];

        foreach (string value in values)
        {
            Dictionary<string, (string Type, string Value)> read = ReadParameters(PowerShellParameterFile.Serialize(Vars(("Value", value))));

            Assert.Equal("string", read["Value"].Type);
            Assert.Equal(value, Encoding.UTF8.GetString(Convert.FromBase64String(read["Value"].Value)));
        }
    }

    [Fact]
    public void The_parameter_file_never_contains_a_value_as_readable_text_and_is_ascii()
    {
        byte[] file = PowerShellParameterFile.Serialize(Vars(("Value", Hostile), ("Other", "2024-01-01T00:00:00Z")));
        string text = Encoding.ASCII.GetString(file);

        Assert.All(file, value => Assert.True(value < 0x80));
        Assert.DoesNotContain("whoami", text, StringComparison.Ordinal);
        Assert.DoesNotContain("2024-01-01", text, StringComparison.Ordinal);
        Assert.DoesNotContain("$env", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Equal_variables_give_equal_bytes_whatever_the_insertion_order()
    {
        byte[] first = PowerShellParameterFile.Serialize(Vars(("B", 1L), ("A", "x"), ("C", true)));
        byte[] second = PowerShellParameterFile.Serialize(Vars(("C", true), ("A", "x"), ("B", 1L)));

        Assert.Equal(first, second);
        Assert.Equal(["A", "B", "C"], ReadParameters(first).Keys.ToArray());
    }

    [Fact]
    public void No_variables_give_an_empty_parameter_set()
    {
        byte[] file = PowerShellParameterFile.Serialize(new Dictionary<string, object>());

        Assert.Equal("""{"version":1,"parameters":{}}""", Encoding.ASCII.GetString(file));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1bad")]
    [InlineData("has space")]
    [InlineData("a-b")]
    [InlineData("-Flag")]
    [InlineData("$x")]
    [InlineData("name\n")]
    [InlineData("ThisParameterNameIsFarTooLongToBeValid")]
    public void An_invalid_parameter_name_is_rejected_before_it_can_reach_a_splat(string name)
    {
        Assert.Throws<ArgumentException>(() => PowerShellParameterFile.Serialize(Vars((name, "x"))));
    }

    [Fact]
    public void A_value_of_an_unsupported_type_is_rejected_without_echoing_it()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => PowerShellParameterFile.Serialize(Vars(("When", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)))));

        Assert.Contains("When", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("2026", exception.Message, StringComparison.Ordinal);
    }
}
