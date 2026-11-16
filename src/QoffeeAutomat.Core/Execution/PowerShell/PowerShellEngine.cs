using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Core.Execution.PowerShell;

/// <summary>A PowerShell executable that was found on this computer (PLAN.md D-011).</summary>
/// <param name="Kind">
/// <see cref="PowerShellEngines.Pwsh"/> for PowerShell 7 or newer, <see cref="PowerShellEngines.PowerShell"/>
/// for Windows PowerShell 5.1.
/// </param>
/// <param name="Path">The full path of the executable.</param>
public sealed record PowerShellEngine(string Kind, string Path)
{
    /// <summary>True for Windows PowerShell 5.1, which reads a script without byte order mark as ANSI instead of UTF-8.</summary>
    public bool IsWindowsPowerShell => string.Equals(Kind, PowerShellEngines.PowerShell, StringComparison.Ordinal);
}

/// <summary>The outcome of looking for a PowerShell engine: the engine, or an explanation that is safe to show to the user.</summary>
public sealed class PowerShellEngineResolution
{
    private PowerShellEngineResolution(PowerShellEngine? engine, string? reason)
    {
        Engine = engine;
        Reason = reason;
    }

    /// <summary>The engine; null when none was found.</summary>
    public PowerShellEngine? Engine { get; }

    /// <summary>Why no engine was found; null when <see cref="Engine"/> is set.</summary>
    public string? Reason { get; }

    /// <summary>True when an engine was found.</summary>
    public bool IsFound => Engine is not null;

    /// <summary>Creates a successful resolution.</summary>
    /// <param name="engine">The engine.</param>
    /// <returns>The resolution.</returns>
    public static PowerShellEngineResolution Found(PowerShellEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return new PowerShellEngineResolution(engine, null);
    }

    /// <summary>Creates a failed resolution.</summary>
    /// <param name="reason">What is missing and what the user can do about it.</param>
    /// <returns>The resolution.</returns>
    public static PowerShellEngineResolution Missing(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new PowerShellEngineResolution(null, reason);
    }
}

/// <summary>Finds the PowerShell executable for the <c>engine</c> member of a PowerShell action or sensor.</summary>
public interface IPowerShellEngineLocator
{
    /// <summary>Looks for an engine. Called for every run, so installing PowerShell later takes effect without a restart.</summary>
    /// <param name="engine">One of <see cref="PowerShellEngines"/>.</param>
    /// <returns>The engine or the reason for its absence; never throws.</returns>
    PowerShellEngineResolution Resolve(string engine);
}

/// <summary>
/// The default <see cref="IPowerShellEngineLocator"/>. <c>auto</c> prefers <c>pwsh</c> (PowerShell 7) and falls
/// back to <c>powershell.exe</c> (Windows PowerShell 5.1) on Windows; <c>pwsh</c> and <c>powershell</c> demand
/// that engine. Only the file system and the <c>PATH</c> are consulted: nothing is started.
/// </summary>
public sealed class PowerShellEngineLocator : IPowerShellEngineLocator
{
    private readonly Func<string, string?> _environment;
    private readonly Func<string, bool> _fileExists;
    private readonly bool _isWindows;

    /// <summary>Creates a locator for this computer.</summary>
    public PowerShellEngineLocator()
        : this(Environment.GetEnvironmentVariable, File.Exists, OperatingSystem.IsWindows())
    {
    }

    /// <summary>Creates a locator over an arbitrary file system and environment, for tests.</summary>
    /// <param name="environment">Reads an environment variable; null when it is not set.</param>
    /// <param name="fileExists">Tells whether a file exists.</param>
    /// <param name="isWindows">True to search the Windows locations and to use <c>.exe</c> names.</param>
    public PowerShellEngineLocator(Func<string, string?> environment, Func<string, bool> fileExists, bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(fileExists);

        _environment = environment;
        _fileExists = fileExists;
        _isWindows = isWindows;
    }

    /// <inheritdoc />
    public PowerShellEngineResolution Resolve(string engine)
    {
        switch (engine)
        {
            case PowerShellEngines.Auto:
                return ResolveAuto();
            case PowerShellEngines.Pwsh:
                return FindPwsh() is { } pwsh
                    ? PowerShellEngineResolution.Found(new PowerShellEngine(PowerShellEngines.Pwsh, pwsh))
                    : PowerShellEngineResolution.Missing(PwshMissing);
            case PowerShellEngines.PowerShell:
                if (!_isWindows)
                {
                    return PowerShellEngineResolution.Missing(
                        "The engine 'powershell' (Windows PowerShell 5.1) exists only on Windows. Use the engine 'pwsh' or 'auto' on this computer.");
                }

                return FindWindowsPowerShell() is { } windowsPowerShell
                    ? PowerShellEngineResolution.Found(new PowerShellEngine(PowerShellEngines.PowerShell, windowsPowerShell))
                    : PowerShellEngineResolution.Missing("Windows PowerShell (powershell.exe) was not found on this computer.");
            default:
                return PowerShellEngineResolution.Missing($"Unknown PowerShell engine '{engine}'. Use 'auto', 'pwsh' or 'powershell'.");
        }
    }

    private static string PwshMissing =>
        "PowerShell 7 (pwsh) was not found on this computer. Install PowerShell 7 from https://aka.ms/powershell and make sure pwsh is on the PATH.";

    private PowerShellEngineResolution ResolveAuto()
    {
        if (FindPwsh() is { } pwsh)
        {
            return PowerShellEngineResolution.Found(new PowerShellEngine(PowerShellEngines.Pwsh, pwsh));
        }

        if (_isWindows && FindWindowsPowerShell() is { } windowsPowerShell)
        {
            return PowerShellEngineResolution.Found(new PowerShellEngine(PowerShellEngines.PowerShell, windowsPowerShell));
        }

        return PowerShellEngineResolution.Missing(_isWindows
            ? "No PowerShell was found: neither pwsh (PowerShell 7) nor powershell.exe (Windows PowerShell 5.1). Install PowerShell 7 from https://aka.ms/powershell."
            : PwshMissing);
    }

    private string? FindPwsh()
    {
        string fileName = _isWindows ? "pwsh.exe" : "pwsh";
        if (FindOnPath(fileName) is { } onPath)
        {
            return onPath;
        }

        foreach (string candidate in _isWindows ? WindowsPwshLocations() : UnixPwshLocations())
        {
            if (_fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private string? FindWindowsPowerShell()
    {
        string? root = _environment("SystemRoot") ?? _environment("windir");
        if (!string.IsNullOrEmpty(root))
        {
            string candidate = Path.Combine(root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (_fileExists(candidate))
            {
                return candidate;
            }
        }

        return FindOnPath("powershell.exe");
    }

    private IEnumerable<string> WindowsPwshLocations()
    {
        foreach (string variable in new[] { "ProgramFiles", "ProgramW6432" })
        {
            if (_environment(variable) is { Length: > 0 } programFiles)
            {
                yield return Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
            }
        }
    }

    private static IEnumerable<string> UnixPwshLocations()
    {
        yield return "/opt/microsoft/powershell/7/pwsh";
        yield return "/usr/local/bin/pwsh";
        yield return "/usr/bin/pwsh";
        yield return "/snap/bin/pwsh";
    }

    private string? FindOnPath(string fileName)
    {
        string? path = _environment("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                continue;
            }

            string candidate = Path.Combine(trimmed, fileName);
            if (_fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
