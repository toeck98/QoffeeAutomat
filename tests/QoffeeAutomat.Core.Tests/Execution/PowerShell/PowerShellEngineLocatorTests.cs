using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Execution.PowerShell;

namespace QoffeeAutomat.Core.Tests.Execution.PowerShell;

/// <summary>Engine detection (PLAN.md T208): auto prefers pwsh, falls back to powershell.exe on Windows, and a missing engine gives a clear error.</summary>
[Trait("Category", "PowerShell")]
public sealed class PowerShellEngineLocatorTests
{
    private static readonly string PwshDirectory = Path.Combine("tools", "pwsh");
    private static readonly string PwshWindows = Path.Combine(PwshDirectory, "pwsh.exe");
    private static readonly string PwshUnix = Path.Combine(PwshDirectory, "pwsh");
    private static readonly string SystemRoot = Path.Combine("win", "root");
    private static readonly string WindowsPowerShellPath = Path.Combine(SystemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

    private static PowerShellEngineLocator Locator(
        bool isWindows,
        IEnumerable<string> files,
        string? path = null,
        string? systemRoot = null,
        string? programFiles = null)
    {
        var existing = new HashSet<string>(files, StringComparer.Ordinal);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (path is not null)
        {
            environment["PATH"] = path;
        }

        if (systemRoot is not null)
        {
            environment["SystemRoot"] = systemRoot;
        }

        if (programFiles is not null)
        {
            environment["ProgramFiles"] = programFiles;
        }

        return new PowerShellEngineLocator(name => environment.GetValueOrDefault(name), existing.Contains, isWindows);
    }

    [Fact]
    public void Auto_prefers_pwsh_when_both_engines_are_installed()
    {
        PowerShellEngineLocator locator = Locator(true, [PwshWindows, WindowsPowerShellPath], PwshDirectory, SystemRoot);

        PowerShellEngineResolution result = locator.Resolve(PowerShellEngines.Auto);

        PowerShellEngine engine = Assert.IsType<PowerShellEngine>(result.Engine);
        Assert.Equal(PowerShellEngines.Pwsh, engine.Kind);
        Assert.Equal(PwshWindows, engine.Path);
        Assert.False(engine.IsWindowsPowerShell);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Auto_falls_back_to_windows_powershell_on_windows()
    {
        PowerShellEngineLocator locator = Locator(true, [WindowsPowerShellPath], path: string.Empty, systemRoot: SystemRoot);

        PowerShellEngineResolution result = locator.Resolve(PowerShellEngines.Auto);

        PowerShellEngine engine = Assert.IsType<PowerShellEngine>(result.Engine);
        Assert.Equal(PowerShellEngines.PowerShell, engine.Kind);
        Assert.Equal(WindowsPowerShellPath, engine.Path);
        Assert.True(engine.IsWindowsPowerShell);
    }

    [Fact]
    public void Auto_on_windows_finds_windows_powershell_on_the_path_when_the_system_root_is_unknown()
    {
        string directory = Path.Combine("somewhere", "bin");
        string exe = Path.Combine(directory, "powershell.exe");
        PowerShellEngineLocator locator = Locator(true, [exe], path: directory);

        PowerShellEngine engine = Assert.IsType<PowerShellEngine>(locator.Resolve(PowerShellEngines.Auto).Engine);

        Assert.Equal(exe, engine.Path);
    }

    [Fact]
    public void Auto_without_any_engine_names_what_to_install()
    {
        PowerShellEngineResolution windows = Locator(true, [], PwshDirectory, SystemRoot).Resolve(PowerShellEngines.Auto);
        PowerShellEngineResolution unix = Locator(false, [], PwshDirectory).Resolve(PowerShellEngines.Auto);

        Assert.Null(windows.Engine);
        Assert.Contains("pwsh", windows.Reason, StringComparison.Ordinal);
        Assert.Contains("powershell.exe", windows.Reason, StringComparison.Ordinal);
        Assert.Null(unix.Engine);
        Assert.Contains("PowerShell 7", unix.Reason, StringComparison.Ordinal);
        Assert.Contains("not found", unix.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Auto_on_unix_never_uses_windows_powershell()
    {
        PowerShellEngineLocator locator = Locator(false, [WindowsPowerShellPath], PwshDirectory, SystemRoot);

        PowerShellEngineResolution result = locator.Resolve(PowerShellEngines.Auto);

        Assert.False(result.IsFound);
    }

    [Fact]
    public void Auto_on_unix_uses_pwsh_from_the_path()
    {
        PowerShellEngineLocator locator = Locator(false, [PwshUnix], PwshDirectory);

        PowerShellEngine engine = Assert.IsType<PowerShellEngine>(locator.Resolve(PowerShellEngines.Auto).Engine);

        Assert.Equal(PowerShellEngines.Pwsh, engine.Kind);
        Assert.Equal(PwshUnix, engine.Path);
    }

    [Fact]
    public void Pwsh_is_found_in_its_default_install_location_when_it_is_not_on_the_path()
    {
        string programFiles = Path.Combine("pf");
        string installed = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        PowerShellEngineLocator windows = Locator(true, [installed], path: "elsewhere", programFiles: programFiles);
        PowerShellEngineLocator unix = Locator(false, ["/opt/microsoft/powershell/7/pwsh"], path: "elsewhere");

        Assert.Equal(installed, Assert.IsType<PowerShellEngine>(windows.Resolve(PowerShellEngines.Pwsh).Engine).Path);
        Assert.Equal("/opt/microsoft/powershell/7/pwsh", Assert.IsType<PowerShellEngine>(unix.Resolve(PowerShellEngines.Pwsh).Engine).Path);
    }

    [Fact]
    public void Explicit_pwsh_does_not_fall_back_to_windows_powershell()
    {
        PowerShellEngineLocator locator = Locator(true, [WindowsPowerShellPath], path: string.Empty, systemRoot: SystemRoot);

        PowerShellEngineResolution result = locator.Resolve(PowerShellEngines.Pwsh);

        Assert.Null(result.Engine);
        Assert.Contains("PowerShell 7", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_powershell_uses_windows_powershell_even_when_pwsh_exists()
    {
        PowerShellEngineLocator locator = Locator(true, [PwshWindows, WindowsPowerShellPath], PwshDirectory, SystemRoot);

        PowerShellEngine engine = Assert.IsType<PowerShellEngine>(locator.Resolve(PowerShellEngines.PowerShell).Engine);

        Assert.Equal(PowerShellEngines.PowerShell, engine.Kind);
    }

    [Fact]
    public void Explicit_powershell_on_unix_fails_with_an_explanation()
    {
        PowerShellEngineLocator locator = Locator(false, [PwshUnix], PwshDirectory);

        PowerShellEngineResolution result = locator.Resolve(PowerShellEngines.PowerShell);

        Assert.Null(result.Engine);
        Assert.Contains("only on Windows", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_powershell_missing_on_windows_fails_with_an_explanation()
    {
        PowerShellEngineResolution result = Locator(true, [PwshWindows], PwshDirectory, SystemRoot).Resolve(PowerShellEngines.PowerShell);

        Assert.Null(result.Engine);
        Assert.Contains("powershell.exe", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_engine_name_is_rejected()
    {
        PowerShellEngineResolution result = Locator(true, [PwshWindows], PwshDirectory).Resolve("bash");

        Assert.Null(result.Engine);
        Assert.Contains("'bash'", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_path_is_searched_in_order_and_empty_or_quoted_entries_are_tolerated()
    {
        string first = Path.Combine("first");
        string second = Path.Combine("second");
        string path = string.Join(Path.PathSeparator, string.Empty, "\"" + first + "\"", second);
        PowerShellEngineLocator locator = Locator(false, [Path.Combine(first, "pwsh"), Path.Combine(second, "pwsh")], path);

        PowerShellEngine engine = Assert.IsType<PowerShellEngine>(locator.Resolve(PowerShellEngines.Pwsh).Engine);

        Assert.Equal(Path.Combine(first, "pwsh"), engine.Path);
    }

    [Fact]
    public void Resolution_has_either_an_engine_or_a_reason()
    {
        PowerShellEngineResolution found = PowerShellEngineResolution.Found(new PowerShellEngine(PowerShellEngines.Pwsh, "pwsh"));
        PowerShellEngineResolution missing = PowerShellEngineResolution.Missing("nope");

        Assert.True(found.IsFound);
        Assert.Null(found.Reason);
        Assert.False(missing.IsFound);
        Assert.Equal("nope", missing.Reason);
        Assert.Throws<ArgumentException>(() => PowerShellEngineResolution.Missing(" "));
    }
}
