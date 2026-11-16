using System.Text;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Platform;
using static QoffeeAutomat.Core.Tests.Execution.PowerShell.PowerShellTestSupport;

namespace QoffeeAutomat.Core.Tests.Execution.PowerShell;

/// <summary>
/// The acceptance tests of PLAN.md T208 with a real engine. The tests that need <c>pwsh</c> are reported as
/// skipped where it is not installed; the ones that need <c>powershell.exe</c> run on Windows only.
/// </summary>
[Trait("Category", "PowerShell")]
public sealed class PowerShellExecutionTests
{
    private static readonly string HostileWithoutCarriageReturn = PowerShellWrapperTests.Hostile;

    /// <summary>A real runner and a real process launcher over a temporary scripts and run directory.</summary>
    private sealed class RealRun : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly RoutingProcessLauncher _launcher = new(new ProcessRunner());

        public RealRun()
        {
            Directory.CreateDirectory(ScriptsDirectory);
            Runner = new PowerShellRunner(
                new PowerShellEngineLocator(),
                new PowerShellOptions { ScriptsDirectory = ScriptsDirectory, TempRoot = TempRoot });
        }

        public string ScriptsDirectory => _temp.Combine("scripts");

        public string TempRoot => _temp.Combine("run");

        public string Combine(string name) => _temp.Combine(name);

        public PowerShellRunner Runner { get; }

        public string[] LeftoverRunDirectories => Directory.Exists(TempRoot) ? Directory.GetDirectories(TempRoot) : [];

        public void WriteScript(string name, string content)
        {
            // Without byte order mark, as the config store writes script files.
            File.WriteAllBytes(Path.Combine(ScriptsDirectory, name), new UTF8Encoding(false).GetBytes(content));
        }

        public async Task<ExecutionResult> RunInlineAsync(string code, string engine, TimeSpan? timeout = null, (string Name, object Value)[]? variables = null)
        {
            return await RunAsync(new PowerShellAction(PowerShellSources.Inline, Code: code, Engine: engine), timeout, variables);
        }

        public async Task<ExecutionResult> RunFileAsync(string script, string engine, TimeSpan? timeout = null, (string Name, object Value)[]? variables = null)
        {
            return await RunAsync(new PowerShellAction(PowerShellSources.File, Script: script, Engine: engine), timeout, variables);
        }

        public void Dispose()
        {
            _temp.Dispose();
        }

        private async Task<ExecutionResult> RunAsync(PowerShellAction action, TimeSpan? timeout, (string Name, object Value)[]? variables)
        {
            CommandDocument command = Command(action);
            ActionContext context = Context(
                command,
                _launcher,
                Vars(variables ?? []),
                timeout ?? TimeSpan.FromSeconds(50),
                new Dictionary<string, string> { ["QA_RUN_ID"] = "real-run", ["QA_COMMAND_ID"] = "ps_test" });
            var handler = new PowerShellActionHandler(Runner);
            return await handler.ExecuteAsync(context, CancellationToken.None).AsTask().WaitAsync(Patience);
        }
    }

    // ------------------------------------------------------------------
    // Strings arrive unchanged and are never executed (pwsh)
    // ------------------------------------------------------------------

    [PwshFact]
    public async Task A_hostile_string_reaches_a_script_file_unchanged_and_is_not_executed_in_pwsh()
    {
        using var run = new RealRun();
        string canary = run.Combine("canary.txt");
        string value = HostileWithoutCarriageReturn + $"; New-Item -ItemType File -Path '{canary}' ; $(New-Item -ItemType File -Path '{canary}')";
        run.WriteScript("echo.ps1", "param([string]$Value)\n[Console]::Out.Write($Value)\n");

        ExecutionResult result = await run.RunFileAsync("echo.ps1", PowerShellEngines.Pwsh, variables: [("Value", value)]);

        Assert.Null(result.ErrorCode);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(value, result.StandardOutput);
        Assert.False(File.Exists(canary), "The value was executed as code.");
        Assert.Empty(run.LeftoverRunDirectories);
    }

    [PwshFact]
    public async Task A_hostile_string_reaches_inline_code_unchanged_in_pwsh()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync(
            "param([string]$Value)\n[Console]::Out.Write($Value)",
            PowerShellEngines.Pwsh,
            variables: [("Value", HostileWithoutCarriageReturn)]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(HostileWithoutCarriageReturn, result.StandardOutput);
    }

    [PwshFact]
    public async Task Strings_that_look_like_dates_stay_strings_in_pwsh()
    {
        using var run = new RealRun();
        string[] values = ["2024-01-01T00:00:00Z", "/Date(0)/", "2024-02-30", "12:30", "1e3", "true", "null", "0x10"];

        foreach (string value in values)
        {
            ExecutionResult result = await run.RunInlineAsync(
                "param($Value)\n[Console]::Out.Write($Value.GetType().FullName + '|' + $Value)",
                PowerShellEngines.Pwsh,
                variables: [("Value", value)]);

            Assert.Equal("System.String|" + value, result.StandardOutput);
        }
    }

    [PwshFact]
    public async Task Non_ascii_text_goes_in_and_comes_out_unchanged_in_pwsh()
    {
        using var run = new RealRun();
        const string value = "héllo ✓ 日本語";

        ExecutionResult result = await run.RunInlineAsync(
            "param([string]$Value)\n[Console]::Out.Write($Value + ' / ' + 'ünï')",
            PowerShellEngines.Pwsh,
            variables: [("Value", value)]);

        Assert.Equal(value + " / ünï", result.StandardOutput);
    }

    // ------------------------------------------------------------------
    // Types
    // ------------------------------------------------------------------

    [PwshFact]
    public async Task Int_float_and_bool_arrive_with_the_matching_powershell_types_in_pwsh()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync(
            "param($I, $Big, $D, $Whole, $T, $F, $S)\n"
            + "$parts = @($I, $Big, $D, $Whole, $T, $F, $S) | ForEach-Object { $_.GetType().FullName }\n"
            + "[Console]::Out.Write(($parts -join '|'))",
            PowerShellEngines.Pwsh,
            variables:
            [
                ("I", 42L),
                ("Big", 5_000_000_000L),
                ("D", 1.5),
                ("Whole", 3.0),
                ("T", true),
                ("F", false),
                ("S", "text"),
            ]);

        Assert.Equal(
            "System.Int32|System.Int64|System.Double|System.Double|System.Boolean|System.Boolean|System.String",
            result.StandardOutput);
    }

    [PwshFact]
    public async Task Values_keep_their_exact_numeric_value_and_false_stays_false_in_pwsh()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync(
            "param([int]$Count, [double]$Ratio, [bool]$Flag, [bool]$Off)\n"
            + "[Console]::Out.Write(($Count + 1).ToString() + '|' + $Ratio.ToString([cultureinfo]::InvariantCulture) + '|' + $Flag + '|' + $Off + '|' + (-not $Off))",
            PowerShellEngines.Pwsh,
            variables: [("Count", 41L), ("Ratio", 0.1 + 0.2), ("Flag", true), ("Off", false)]);

        Assert.Equal("42|0.30000000000000004|True|False|True", result.StandardOutput);
    }

    // ------------------------------------------------------------------
    // Exit codes and errors
    // ------------------------------------------------------------------

    [PwshFact]
    public async Task A_script_calling_exit_3_yields_exit_code_3_in_pwsh()
    {
        using var run = new RealRun();
        run.WriteScript("three.ps1", "Write-Output 'before'\nexit 3\n");

        ExecutionResult result = await run.RunFileAsync("three.ps1", PowerShellEngines.Pwsh);

        Assert.Equal(3, result.ExitCode);
        Assert.Null(result.ErrorCode);
        Assert.Contains("before", result.StandardOutput, StringComparison.Ordinal);
    }

    [PwshFact]
    public async Task A_script_that_ends_normally_yields_exit_code_0_in_pwsh()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync("Write-Output 'fine'", PowerShellEngines.Pwsh);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("fine", result.StandardOutput.Trim());
        Assert.Equal(string.Empty, result.StandardError);
    }

    [PwshFact]
    public async Task A_thrown_error_yields_a_non_zero_exit_code_and_stderr_text_in_pwsh()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync("throw 'boom from the script'", PowerShellEngines.Pwsh);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Null(result.ErrorCode);
        Assert.Contains("boom from the script", result.StandardError, StringComparison.Ordinal);
    }

    [PwshFact]
    public async Task A_terminating_error_from_a_cmdlet_yields_a_non_zero_exit_code_in_pwsh()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync(
            "Get-Item -LiteralPath '/definitely/not/here/nor/there' -ErrorAction Stop",
            PowerShellEngines.Pwsh);

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEqual(string.Empty, result.StandardError.Trim());
    }

    [PwshFact]
    public async Task A_variable_that_the_script_does_not_declare_fails_the_run_with_a_message_in_pwsh()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync(
            "param($Known)\n'x'",
            PowerShellEngines.Pwsh,
            variables: [("Known", "a"), ("Unknown", "b")]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unknown", result.StandardError, StringComparison.Ordinal);
    }

    [PwshFact]
    public async Task A_syntax_error_in_the_script_yields_a_non_zero_exit_code_in_pwsh()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync("if ($true {", PowerShellEngines.Pwsh);

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEqual(string.Empty, result.StandardError.Trim());
    }

    // ------------------------------------------------------------------
    // Context, timeout, cleanup
    // ------------------------------------------------------------------

    [PwshFact]
    public async Task The_run_context_is_visible_to_the_script_in_pwsh()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync(
            "[Console]::Out.Write($env:QA_RUN_ID + '|' + $env:QA_COMMAND_ID + '|' + (Test-Path -LiteralPath $env:QA_PARAMS_FILE))",
            PowerShellEngines.Pwsh);

        Assert.Equal("real-run|ps_test|True", result.StandardOutput);
    }

    [PwshFact]
    public async Task The_script_runs_without_profile_and_does_not_wait_for_input_in_pwsh()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync(
            "$null = Read-Host 'name'",
            PowerShellEngines.Pwsh);

        Assert.NotEqual(0, result.ExitCode);
    }

    [PwshFact]
    public async Task A_run_that_exceeds_its_timeout_is_killed_and_reported_as_timeout_in_pwsh()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync("Start-Sleep -Seconds 60", PowerShellEngines.Pwsh, TimeSpan.FromSeconds(2));

        Assert.Equal(ErrorCodes.Timeout, result.ErrorCode);
        Assert.Empty(run.LeftoverRunDirectories);
    }

    [PwshFact]
    public async Task Temporary_files_are_gone_after_every_kind_of_run_in_pwsh()
    {
        using var run = new RealRun();
        run.WriteScript("fail.ps1", "exit 9\n");

        await run.RunInlineAsync("'ok'", PowerShellEngines.Pwsh);
        await run.RunInlineAsync("throw 'x'", PowerShellEngines.Pwsh);
        await run.RunFileAsync("fail.ps1", PowerShellEngines.Pwsh);

        Assert.Empty(run.LeftoverRunDirectories);
    }

    [PwshFact]
    public async Task The_auto_engine_resolves_to_pwsh_when_it_is_installed()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync("[Console]::Out.Write($PSVersionTable.PSVersion.Major)", PowerShellEngines.Auto);

        Assert.True(int.Parse(result.StandardOutput, System.Globalization.CultureInfo.InvariantCulture) >= 7);
    }

    // ------------------------------------------------------------------
    // Windows PowerShell 5.1 (Windows only)
    // ------------------------------------------------------------------

    [WindowsPowerShellFact]
    public async Task A_hostile_string_reaches_a_script_file_unchanged_and_is_not_executed_in_windows_powershell()
    {
        using var run = new RealRun();
        string canary = run.Combine("canary.txt");
        string value = HostileWithoutCarriageReturn + $"; New-Item -ItemType File -Path '{canary}' ; $(New-Item -ItemType File -Path '{canary}')";
        run.WriteScript("echo.ps1", "param([string]$Value)\n[Console]::Out.Write($Value)\n");

        ExecutionResult result = await run.RunFileAsync("echo.ps1", PowerShellEngines.PowerShell, variables: [("Value", value)]);

        Assert.Null(result.ErrorCode);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(value, result.StandardOutput);
        Assert.False(File.Exists(canary), "The value was executed as code.");
        Assert.Empty(run.LeftoverRunDirectories);
    }

    [WindowsPowerShellFact]
    public async Task Strings_that_look_like_dates_stay_strings_in_windows_powershell()
    {
        using var run = new RealRun();

        foreach (string value in new[] { "2024-01-01T00:00:00Z", "/Date(0)/" })
        {
            ExecutionResult result = await run.RunInlineAsync(
                "param($Value)\n[Console]::Out.Write($Value.GetType().FullName + '|' + $Value)",
                PowerShellEngines.PowerShell,
                variables: [("Value", value)]);

            Assert.Equal("System.String|" + value, result.StandardOutput);
        }
    }

    [WindowsPowerShellFact]
    public async Task Int_float_and_bool_arrive_with_the_matching_powershell_types_in_windows_powershell()
    {
        using var run = new RealRun();

        ExecutionResult result = await run.RunInlineAsync(
            "param($I, $Big, $D, $T, $F)\n"
            + "$parts = @($I, $Big, $D, $T, $F) | ForEach-Object { $_.GetType().FullName }\n"
            + "[Console]::Out.Write(($parts -join '|'))",
            PowerShellEngines.PowerShell,
            variables: [("I", 42L), ("Big", 5_000_000_000L), ("D", 1.5), ("T", true), ("F", false)]);

        Assert.Equal("System.Int32|System.Int64|System.Double|System.Boolean|System.Boolean", result.StandardOutput);
    }

    [WindowsPowerShellFact]
    public async Task Exit_codes_and_thrown_errors_work_in_windows_powershell()
    {
        using var run = new RealRun();
        run.WriteScript("three.ps1", "exit 3\n");

        ExecutionResult exit = await run.RunFileAsync("three.ps1", PowerShellEngines.PowerShell);
        ExecutionResult thrown = await run.RunInlineAsync("throw 'boom'", PowerShellEngines.PowerShell);

        Assert.Equal(3, exit.ExitCode);
        Assert.NotEqual(0, thrown.ExitCode);
        Assert.Contains("boom", thrown.StandardError, StringComparison.Ordinal);
    }

    [WindowsPowerShellFact]
    public async Task A_non_ascii_script_file_without_byte_order_mark_is_read_as_utf8_in_windows_powershell()
    {
        using var run = new RealRun();
        run.WriteScript("umlaut.ps1", "[Console]::Out.Write('héllo ✓')\n");

        ExecutionResult result = await run.RunFileAsync("umlaut.ps1", PowerShellEngines.PowerShell);

        Assert.Equal("héllo ✓", result.StandardOutput);
    }
}
