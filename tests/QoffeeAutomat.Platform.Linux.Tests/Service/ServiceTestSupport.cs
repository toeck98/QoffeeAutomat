using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using QoffeeAutomat.Platform.Linux.Service;

namespace QoffeeAutomat.Platform.Linux.Tests.Service;

/// <summary>A notifier that records what would be sent to systemd.</summary>
internal sealed class RecordingNotifier : ISystemdNotifier
{
    private readonly object _gate = new();
    private readonly List<string> _messages = [];

    public bool IsEnabled => true;

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    public bool Notify(string state)
    {
        lock (_gate)
        {
            _messages.Add(state);
        }

        return true;
    }
}

/// <summary>The events of the host, raised by the test.</summary>
internal sealed class FakeApplicationLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource _started = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _stopped = new();

    public CancellationToken ApplicationStarted => _started.Token;

    public CancellationToken ApplicationStopping => _stopping.Token;

    public CancellationToken ApplicationStopped => _stopped.Token;

    public int StopRequests { get; private set; }

    public void RaiseStarted() => _started.Cancel();

    public void StopApplication()
    {
        StopRequests++;
        _stopping.Cancel();
    }

    public void Dispose()
    {
        _started.Dispose();
        _stopping.Dispose();
        _stopped.Dispose();
    }
}

/// <summary>A file system in memory: directories and files with modes and owners.</summary>
internal sealed class FakeUnixFileSystem : IUnixFileSystem
{
    public Dictionary<string, UnixFileMode> Directories { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, UnixFileMode> Files { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, uint> Owners { get; } = new(StringComparer.Ordinal);

    public List<string> Chowned { get; } = [];

    public bool ChownFails { get; set; }

    public bool DirectoryExists(string path) => Directories.ContainsKey(path);

    public void CreateDirectory(string path, UnixFileMode mode)
    {
        Directories[path] = mode;
        Owners.TryAdd(path, 0);
    }

    public UnixFileMode GetMode(string path) => Directories.TryGetValue(path, out UnixFileMode mode) ? mode : Files[path];

    public void SetMode(string path, UnixFileMode mode)
    {
        if (Directories.ContainsKey(path))
        {
            Directories[path] = mode;
        }
        else
        {
            Files[path] = mode;
        }
    }

    public IReadOnlyList<string> ListFiles(string path) =>
        [.. Files.Keys.Where(file => string.Equals(Path.GetDirectoryName(file), path, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    public uint? GetOwner(string path) => Owners.TryGetValue(path, out uint owner) ? owner : null;

    public bool SetOwnerToRoot(string path)
    {
        if (ChownFails)
        {
            return false;
        }

        Chowned.Add(path);
        Owners[path] = 0;
        return true;
    }
}

/// <summary>The default layout of PLAN.md S05 and a layout below one directory.</summary>
internal static class TestLayouts
{
    public static LinuxDataDirectories System() => new(
        "/etc/qoffeeautomat",
        "/etc/qoffeeautomat/scripts",
        "/var/lib/qoffeeautomat/secrets",
        "/var/lib/qoffeeautomat",
        "/var/log/qoffeeautomat");

    public static LinuxDataDirectories Below(string root) => new(
        Path.Combine(root, "config"),
        Path.Combine(root, "scripts"),
        Path.Combine(root, "secrets"),
        Path.Combine(root, "data"),
        Path.Combine(root, "logs"),
        root);
}

/// <summary>A <c>systemctl</c> that records its calls and answers from a table.</summary>
internal sealed class FakeSystemctl : ISystemctl
{
    public List<string> Calls { get; } = [];

    public UnitStatus Status { get; set; } = new("qoffeeautomat.service", "not-found", "inactive", "dead", string.Empty, 0, string.Empty);

    public string? FailOn { get; set; }

    public Task DaemonReloadAsync(CancellationToken cancellationToken) => Record("daemon-reload");

    public Task EnableAsync(string unit, CancellationToken cancellationToken) => Record("enable " + unit);

    public Task DisableAsync(string unit, CancellationToken cancellationToken) => Record("disable " + unit);

    public Task StartAsync(string unit, CancellationToken cancellationToken) => Record("start " + unit);

    public Task StopAsync(string unit, CancellationToken cancellationToken) => Record("stop " + unit);

    public Task<UnitStatus> QueryAsync(string unit, CancellationToken cancellationToken)
    {
        Calls.Add("show " + unit);
        return Task.FromResult(Status with { Name = unit });
    }

    private Task Record(string call)
    {
        Calls.Add(call);
        return call == FailOn ? throw new SystemctlException($"'systemctl {call}' failed with exit code 1: simulated failure") : Task.CompletedTask;
    }
}

/// <summary>A program runner that answers from a fixed result and records every call.</summary>
internal sealed class FakeCommandRunner : ICommandRunner
{
    public List<(string FileName, string[] Arguments)> Calls { get; } = [];

    public CommandResult? Result { get; set; } = new(0, string.Empty, string.Empty);

    public ValueTask<CommandResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add((fileName, [.. arguments]));
        return ValueTask.FromResult(Result);
    }
}

/// <summary>The signals of logind, written by the test.</summary>
internal sealed class FakeSignalSource : ILogindSignalSource
{
    private readonly Channel<LogindSignal> _channel = Channel.CreateUnbounded<LogindSignal>();

    public int Watches { get; private set; }

    public void Send(LogindSignal signal) => _channel.Writer.TryWrite(signal);

    public void End() => _channel.Writer.TryComplete();

    public async IAsyncEnumerable<LogindSignal> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Watches++;
        await foreach (LogindSignal signal in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return signal;
        }
    }
}

/// <summary>A lock that remembers whether it was released, and tells the shared log.</summary>
internal sealed class FakeLock : IDisposable
{
    private readonly List<string> _log;

    public FakeLock(List<string> log)
    {
        _log = log;
    }

    public bool Released { get; private set; }

    public void Dispose()
    {
        Released = true;
        _log.Add("lock released");
    }
}

/// <summary>An inhibitor that hands out <see cref="FakeLock"/>s, or refuses.</summary>
internal sealed class FakeInhibitor : ILogindInhibitor
{
    public List<string> Log { get; } = [];

    public List<FakeLock> Locks { get; } = [];

    public bool Refuse { get; set; }

    public ValueTask<IDisposable?> AcquireDelayLockAsync(string reason, CancellationToken cancellationToken)
    {
        if (Refuse)
        {
            Log.Add("lock refused");
            return ValueTask.FromResult<IDisposable?>(null);
        }

        var held = new FakeLock(Log);
        Locks.Add(held);
        Log.Add("lock acquired");
        return ValueTask.FromResult<IDisposable?>(held);
    }
}

/// <summary>Finds files of the repository from the directory of the test assembly.</summary>
internal static class RepositoryFiles
{
    public static string Find(string relativePath)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QoffeeAutomat.slnx")))
            {
                return Path.Combine(directory.FullName, relativePath);
            }
        }

        throw new InvalidOperationException("The repository root (QoffeeAutomat.slnx) was not found above " + AppContext.BaseDirectory);
    }
}
