namespace QoffeeAutomat.Core.Execution.Bash;

/// <summary>The outcome of looking for bash: the executable, or an explanation that is safe to show to the user.</summary>
public sealed class BashResolution
{
    private BashResolution(string? path, string? reason)
    {
        Path = path;
        Reason = reason;
    }

    /// <summary>The full path of bash; null when it was not found.</summary>
    public string? Path { get; }

    /// <summary>Why bash was not found; null when <see cref="Path"/> is set.</summary>
    public string? Reason { get; }

    /// <summary>True when bash was found.</summary>
    public bool IsFound => Path is not null;

    /// <summary>Creates a successful resolution.</summary>
    /// <param name="path">The full path of bash.</param>
    /// <returns>The resolution.</returns>
    public static BashResolution Found(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new BashResolution(path, null);
    }

    /// <summary>Creates a failed resolution.</summary>
    /// <param name="reason">What is missing and what the user can do about it.</param>
    /// <returns>The resolution.</returns>
    public static BashResolution Missing(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new BashResolution(null, reason);
    }
}

/// <summary>Finds the bash executable for the <c>bash</c> action (PLAN.md S17, T803).</summary>
public interface IBashLocator
{
    /// <summary>Looks for bash. Called for every run, so installing bash later takes effect without a restart.</summary>
    /// <returns>The path or the reason for its absence; never throws.</returns>
    BashResolution Resolve();
}

/// <summary>
/// The default <see cref="IBashLocator"/>: the first <c>bash</c> on the <c>PATH</c>, then the usual locations. Only the file system and
/// the <c>PATH</c> are consulted; nothing is started. The <c>PATH</c> of a systemd service is short, so the fixed locations matter.
/// </summary>
public sealed class BashLocator : IBashLocator
{
    private static readonly string[] FixedLocations = ["/usr/bin/bash", "/bin/bash", "/usr/local/bin/bash", "/opt/homebrew/bin/bash"];

    private readonly Func<string, string?> _environment;
    private readonly Func<string, bool> _fileExists;

    /// <summary>Creates a locator for this computer.</summary>
    public BashLocator()
        : this(Environment.GetEnvironmentVariable, File.Exists)
    {
    }

    /// <summary>Creates a locator over an arbitrary file system and environment, for tests.</summary>
    /// <param name="environment">Reads an environment variable; null when it is not set.</param>
    /// <param name="fileExists">Tells whether a file exists.</param>
    public BashLocator(Func<string, string?> environment, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(fileExists);

        _environment = environment;
        _fileExists = fileExists;
    }

    /// <inheritdoc />
    public BashResolution Resolve()
    {
        if (FindOnPath() is { } onPath)
        {
            return BashResolution.Found(onPath);
        }

        foreach (string candidate in FixedLocations)
        {
            if (_fileExists(candidate))
            {
                return BashResolution.Found(candidate);
            }
        }

        return BashResolution.Missing("bash was not found on this computer. Install the package 'bash' and make sure it is on the PATH of the agent.");
    }

    private string? FindOnPath()
    {
        string? path = _environment("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = directory.Trim();
            if (trimmed.Length == 0 || !Path.IsPathRooted(trimmed))
            {
                // A relative directory on the PATH would run whatever the working directory holds.
                continue;
            }

            string candidate = Path.Combine(trimmed, "bash");
            if (_fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
