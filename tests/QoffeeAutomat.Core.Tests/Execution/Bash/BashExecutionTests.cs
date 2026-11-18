using System.Globalization;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Execution.Bash;
using QoffeeAutomat.Core.Platform;
using static QoffeeAutomat.Core.Tests.Execution.Bash.BashTestSupport;

namespace QoffeeAutomat.Core.Tests.Execution.Bash;

/// <summary>
/// The injection test suite of PLAN.md T208, for bash (T803): a real shell, hostile strings, and the proof that a value is data and never
/// code. The tests that need bash are reported as skipped where it is not installed (and on Windows).
/// </summary>
[Trait("Category", "Bash")]
public sealed class BashExecutionTests
{
    /// <summary>Values that a shell would treat specially if they were ever part of the script text or of a command line that a shell reads.</summary>
    public static TheoryData<string> HostileValues { get; } = new()
    {
        string.Empty,
        " ",
        "-n",
        "-e",
        "--help",
        "--",
        "-- -x",
        "a b  c",
        "  leading and trailing  ",
        "*",
        "~",
        "$HOME",
        "${IFS}",
        "$(id)",
        "`id`",
        "'",
        "\"",
        "\\",
        "\\n",
        ";",
        "&&",
        "||",
        "|",
        ">",
        "<",
        "2>&1",
        "!!",
        "=",
        "a=b",
        "\n",
        "line1\nline2\n",
        "tab\there",
        "'; echo injected; '",
        "\"; echo injected; \"",
        "$(echo injected)",
        "`echo injected`",
        "日本語 \U0001F600",
        "héllo ✓",
        Hostile,
        new string('x', 1024),
    };

    /// <summary>A real runner and a real process launcher over a temporary scripts and run directory.</summary>
    private sealed class RealRun : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly RoutingProcessLauncher _launcher = new(new ProcessRunner());

        public RealRun()
        {
            Directory.CreateDirectory(ScriptsDirectory);
            Runner = new BashRunner(new BashLocator(), new BashOptions { ScriptsDirectory = ScriptsDirectory, TempRoot = TempRoot });
        }

        public string ScriptsDirectory => _temp.Combine("scripts");

        public string TempRoot => _temp.Combine("run");

        public string Combine(string name) => _temp.Combine(name);

        public BashRunner Runner { get; }

        public string[] LeftoverRunDirectories => Directory.Exists(TempRoot) ? Directory.GetDirectories(TempRoot) : [];

        public void WriteScript(string name, string content)
        {
            File.WriteAllText(Path.Combine(ScriptsDirectory, name), content);
        }

        public Task<ExecutionResult> RunInlineAsync(string code, TimeSpan? timeout = null, params (string Name, object Value)[] variables)
        {
            return RunAsync(new BashAction(BashSources.Inline, Code: code), Declare(variables), timeout, variables, null);
        }

        public Task<ExecutionResult> RunFileAsync(string script, TimeSpan? timeout = null, params (string Name, object Value)[] variables)
        {
            return RunAsync(new BashAction(BashSources.File, Script: script), Declare(variables), timeout, variables, null);
        }

        public Task<ExecutionResult> RunDeclaredAsync(string code, ParameterDefinition[] parameters, params (string Name, object Value)[] variables)
        {
            return RunAsync(new BashAction(BashSources.Inline, Code: code), parameters, null, variables, null);
        }

        public Task<ExecutionResult> RunWithEnvironmentAsync(string code, Dictionary<string, string> environment)
        {
            return RunAsync(new BashAction(BashSources.Inline, Code: code), [], null, [], environment);
        }

        public void Dispose()
        {
            _temp.Dispose();
        }

        private static ParameterDefinition[] Declare((string Name, object Value)[] variables)
        {
            return
            [
                .. variables.Select(variable => variable.Value switch
                {
                    long => Whole(variable.Name),
                    double => Real(variable.Name),
                    bool => Flag(variable.Name),
                    _ => Text(variable.Name),
                }),
            ];
        }

        private async Task<ExecutionResult> RunAsync(
            BashAction action,
            ParameterDefinition[] parameters,
            TimeSpan? timeout,
            (string Name, object Value)[] variables,
            Dictionary<string, string>? environment)
        {
            CommandDocument command = Command(action, parameters: parameters);
            ActionContext context = Context(command, _launcher, Vars(variables), timeout ?? TimeSpan.FromSeconds(50), environment);
            var handler = new BashActionHandler(Runner);
            return await handler.ExecuteAsync(context, CancellationToken.None).AsTask().WaitAsync(Patience);
        }
    }

    // ------------------------------------------------------------------
    // Strings arrive unchanged and are never executed
    // ------------------------------------------------------------------

    [BashFact]
    public async Task A_hostile_string_reaches_a_script_file_unchanged_and_is_not_executed()
    {
        using var run = new RealRun();
        string canary = run.Combine("canary.txt");
        string value = Hostile + $"; touch '{canary}' ; $(touch '{canary}') ; `touch '{canary}'`";
        run.WriteScript("echo.sh", "printf '%s' \"$1\"\n");

        ExecutionResult result = await run.RunFileAsync("echo.sh", variables: [("Value", value)]);

        Assert.Null(result.ErrorCode);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(value, result.StandardOutput);
        Assert.False(File.Exists(canary), "The value was executed as code.");
        Assert.Empty(run.LeftoverRunDirectories);
    }

    [BashFact]
    public async Task A_hostile_string_reaches_inline_code_unchanged_as_an_argument_and_as_an_environment_variable()
    {
        using var run = new RealRun();
        string canary = run.Combine("canary.txt");
        string value = Hostile + $"; touch '{canary}' ; $(touch '{canary}') ; `touch '{canary}'`";

        ExecutionResult argument = await run.RunInlineAsync("printf '%s' \"$1\"", variables: [("Value", value)]);
        ExecutionResult environment = await run.RunInlineAsync("printf '%s' \"$QA_VAR_VALUE\"", variables: [("Value", value)]);

        Assert.Equal(value, argument.StandardOutput);
        Assert.Equal(value, environment.StandardOutput);
        Assert.False(File.Exists(canary), "The value was executed as code.");
    }

    [BashTheory]
    [MemberData(nameof(HostileValues))]
    public async Task Hostile_strings_arrive_unchanged_through_the_argument_and_the_environment(string value)
    {
        using var run = new RealRun();

        ExecutionResult argument = await run.RunInlineAsync("printf '%s' \"$1\"", variables: [("Value", value)]);
        ExecutionResult environment = await run.RunInlineAsync("printf '%s' \"$QA_VAR_VALUE\"", variables: [("Value", value)]);

        Assert.Null(argument.ErrorCode);
        Assert.Equal(value, argument.StandardOutput);
        Assert.Equal(value, environment.StandardOutput);
        if (value.Length > 0)
        {
            Assert.DoesNotContain("injected", argument.StandardOutput.Replace(value, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }

    [BashFact]
    public async Task A_value_is_never_word_split_or_expanded_even_without_quotes_around_the_use_in_a_safe_script()
    {
        using var run = new RealRun();

        // The script counts its arguments: one value is one argument, however many spaces and globs it holds.
        ExecutionResult result = await run.RunInlineAsync("printf '%s' \"$#\"", variables: [("Value", "a b  *  c ; d")]);

        Assert.Equal("1", result.StandardOutput);
    }

    [BashFact]
    public async Task Strings_that_look_like_other_types_stay_strings()
    {
        using var run = new RealRun();
        string[] values = ["2024-01-01T00:00:00Z", "/Date(0)/", "2024-02-30", "12:30", "1e3", "true", "null", "0x10", "007"];

        foreach (string value in values)
        {
            ExecutionResult result = await run.RunInlineAsync("printf '%s' \"$1\"", variables: [("Value", value)]);

            Assert.Equal(value, result.StandardOutput);
        }
    }

    // ------------------------------------------------------------------
    // Types and positions
    // ------------------------------------------------------------------

    [BashFact]
    public async Task Int_float_and_bool_arrive_in_the_documented_text_form()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync(
            "printf '%s|%s|%s|%s|%s|%s' \"$1\" \"$2\" \"$3\" \"$4\" \"$5\" \"$QA_VAR_D\"",
            variables: [("I", 42L), ("Big", 5_000_000_000L), ("D", 1.5), ("T", true), ("F", false)]);

        Assert.Equal("42|5000000000|1.5|true|false|1.5", result.StandardOutput);
    }

    [BashFact]
    public async Task Numbers_use_the_invariant_culture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            using var run = new RealRun();

            ExecutionResult result = await run.RunInlineAsync("printf '%s' \"$1\"", variables: [("Ratio", 0.1 + 0.2)]);

            Assert.Equal("0.30000000000000004", result.StandardOutput);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [BashFact]
    public async Task A_declared_value_that_was_not_given_is_an_empty_argument_that_keeps_the_positions()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunDeclaredAsync(
            "printf '[%s][%s][%s]' \"$1\" \"$2\" \"$3\"",
            [Text("A"), Text("B"), Text("C")],
            ("A", "a"),
            ("C", "c"));

        Assert.Equal("[a][][c]", result.StandardOutput);
    }

    [BashFact]
    public async Task Non_ascii_text_goes_in_and_comes_out_unchanged()
    {
        using var run = new RealRun();
        const string value = "héllo ✓ 日本語";

        ExecutionResult result = await run.RunInlineAsync("printf '%s' \"$1 / ünï\"", variables: [("Value", value)]);

        Assert.Equal(value + " / ünï", result.StandardOutput);
    }

    // ------------------------------------------------------------------
    // Exit codes and errors
    // ------------------------------------------------------------------

    [BashFact]
    public async Task A_script_calling_exit_3_yields_exit_code_3()
    {
        using var run = new RealRun();
        run.WriteScript("three.sh", "echo before\nexit 3\n");

        ExecutionResult result = await run.RunFileAsync("three.sh");

        Assert.Equal(3, result.ExitCode);
        Assert.Null(result.ErrorCode);
        Assert.Contains("before", result.StandardOutput, StringComparison.Ordinal);
    }

    [BashFact]
    public async Task A_script_that_ends_normally_yields_exit_code_0_and_no_error_output()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync("echo fine");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("fine", result.StandardOutput.Trim());
        Assert.Equal(string.Empty, result.StandardError);
    }

    [BashFact]
    public async Task An_error_message_on_standard_error_comes_back_with_a_non_zero_exit_code()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync("echo 'boom from the script' >&2\nexit 1");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Null(result.ErrorCode);
        Assert.Contains("boom from the script", result.StandardError, StringComparison.Ordinal);
    }

    [BashFact]
    public async Task A_syntax_error_in_the_script_yields_a_non_zero_exit_code_and_a_message()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync("if [ ; then");

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEqual(string.Empty, result.StandardError.Trim());
    }

    [BashFact]
    public async Task A_failing_command_with_set_e_stops_the_script()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync("set -e\nfalse\necho not reached");

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("not reached", result.StandardOutput, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Context, timeout, cleanup
    // ------------------------------------------------------------------

    [BashFact]
    public async Task The_run_context_is_visible_to_the_script()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunWithEnvironmentAsync(
            "printf '%s|%s' \"$QA_RUN_ID\" \"$QA_COMMAND_ID\"",
            new Dictionary<string, string> { ["QA_RUN_ID"] = "real-run", ["QA_COMMAND_ID"] = "bash_test" });

        Assert.Equal("real-run|bash_test", result.StandardOutput);
    }

    [BashFact]
    public async Task The_script_runs_non_interactively_and_does_not_wait_for_input()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync("case $- in *i*) printf interactive;; *) printf batch;; esac\nread -r line || true\nprintf ':%s' \"$line\"");

        Assert.Equal("batch:", result.StandardOutput);
    }

    [BashFact]
    public async Task A_run_that_exceeds_its_timeout_is_killed_and_reported_as_timeout()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync("sleep 60", TimeSpan.FromSeconds(2));

        Assert.Equal(ErrorCodes.Timeout, result.ErrorCode);
        Assert.Empty(run.LeftoverRunDirectories);
    }

    [BashFact]
    public async Task Temporary_files_are_gone_after_every_kind_of_run()
    {
        using var run = new RealRun();
        run.WriteScript("fail.sh", "exit 9\n");

        await run.RunInlineAsync("echo ok", variables: [("Secret", "hunter2")]);
        await run.RunInlineAsync("exit 4");
        await run.RunFileAsync("fail.sh");

        Assert.Empty(run.LeftoverRunDirectories);
    }

    [BashFact]
    public async Task Windows_line_endings_in_inline_code_do_not_end_every_command_in_a_carriage_return()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync("printf one\r\nprintf two\r\n");

        Assert.Equal("onetwo", result.StandardOutput);
    }
}
