using System.Security.AccessControl;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Security;

namespace QoffeeAutomat.Platform.Windows.Tests;

/// <summary>A fact that runs on Windows only; elsewhere it is reported as skipped.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsFactAttribute : FactAttribute
{
    /// <summary>Creates the attribute.</summary>
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Runs on Windows only.";
        }
    }
}

/// <summary>A fact that needs Windows and an elevated process (it creates services, directory permissions and the like).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ElevatedWindowsFactAttribute : FactAttribute
{
    /// <summary>Creates the attribute.</summary>
    public ElevatedWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Runs on Windows only.";
        }
        else if (!Environment.IsPrivilegedProcess)
        {
            Skip = "Needs an elevated process (administrator).";
        }
    }
}

/// <summary>A clock that moves when the test says so.</summary>
internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A temporary directory that is removed with the object.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qa-win-" + Guid.NewGuid().ToString("N")[..12]);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A directory that the test protected may need an elevated process to delete; leave it for the temp cleaner.
        }
    }
}

/// <summary>An in-memory <see cref="IFileSystemAcl"/>: the permissions of every path that was not set are those of a protected directory.</summary>
internal sealed class FakeAcl : IFileSystemAcl
{
    public static readonly AclSnapshot Protected = new(
        WellKnownAccounts.Administrators,
        [
            new AclRule(WellKnownAccounts.LocalSystem, true, FileSystemRights.FullControl),
            new AclRule(WellKnownAccounts.Administrators, true, FileSystemRights.FullControl),
        ]);

    private readonly Dictionary<string, AclSnapshot?> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Path, IReadOnlyList<AclGrant> Grants)> Protections { get; } = [];

    public bool ReadAll { get; set; } = true;

    public void Set(string path, AclSnapshot? snapshot) => _snapshots[System.IO.Path.GetFullPath(path)] = snapshot;

    public AclSnapshot? Read(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (_snapshots.TryGetValue(full, out AclSnapshot? snapshot))
        {
            return snapshot;
        }

        return ReadAll || Directory.Exists(full) || File.Exists(full) ? Protected : null;
    }

    public void Protect(string path, IReadOnlyList<AclGrant> grants)
    {
        string full = System.IO.Path.GetFullPath(path);
        Protections.Add((full, grants));
        _snapshots[full] = new AclSnapshot(
            WellKnownAccounts.Administrators,
            [.. grants.Select(grant => new AclRule(grant.Sid, true, grant.Rights))]);
    }
}

/// <summary>Finds a PowerShell engine that is never started.</summary>
internal sealed class FakeEngineLocator : IPowerShellEngineLocator
{
    public PowerShellEngineResolution Resolve(string engine)
    {
        return PowerShellEngineResolution.Found(new PowerShellEngine(PowerShellEngines.Pwsh, "pwsh"));
    }
}

/// <summary>A launcher that counts the runs and starts nothing.</summary>
internal sealed class RecordingLauncher : IProcessLauncher
{
    public int Launches { get; private set; }

    public ValueTask<ExecutionResult> LaunchAsync(string runAs, ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        Launches++;
        return ValueTask.FromResult(new ExecutionResult(0, string.Empty, string.Empty));
    }
}

/// <summary>A logger that keeps what it is given.</summary>
internal sealed class ListLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
    }
}
