using QoffeeAutomat.Platform.Linux.Metrics.Session;
using QoffeeAutomat.Platform.Linux.Metrics.SystemMetrics;

namespace QoffeeAutomat.Platform.Linux.Tests;

/// <summary>A fact that reads the real computer and therefore runs on Linux only; elsewhere it is reported as skipped.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LinuxHostFactAttribute : FactAttribute
{
    /// <summary>Creates the attribute.</summary>
    public LinuxHostFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Runs on Linux only.";
        }
    }
}

/// <summary>A clock that moves when the test says so.</summary>
internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A temporary directory that stands in for <c>/</c>: the test writes synthetic <c>proc</c>, <c>sys</c>, <c>etc</c> and <c>run</c> files into it.</summary>
internal sealed class FixtureRoot : IDisposable
{
    public FixtureRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), "qa-linux-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Write(string relativePath, string content)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void CreateDirectory(string relativePath)
    {
        Directory.CreateDirectory(Path.Combine(Root, relativePath));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The temp cleaner takes what is left.
        }
    }
}

/// <summary>Linux as the system metrics see it, with values that the test sets.</summary>
internal sealed class FakeLinuxSources : ILinuxSystemSources
{
    public CpuTimes? Times { get; set; }

    public MemoryStatus? Memory { get; set; }

    public IReadOnlyList<VolumeStatus> Volumes { get; set; } = [];

    public IReadOnlyList<NetworkAdapterStatus> Adapters { get; set; } = [];

    public string? PrimaryIPv4 { get; set; }

    public BatteryStatus? Battery { get; set; }

    public TimeSpan Uptime { get; set; } = TimeSpan.FromHours(30);

    public string? OsVersion { get; set; }

    public bool? PendingReboot { get; set; }

    public string? BootId { get; set; }

    /// <summary>When set, every read of the processor times advances them by this much (idle, total).</summary>
    public (ulong Idle, ulong Total)? AutoAdvance { get; set; }

    public CpuTimes? ReadCpuTimes()
    {
        if (AutoAdvance is { } step && Times is { } current)
        {
            Times = new CpuTimes(current.Idle + step.Idle, current.Total + step.Total);
        }

        return Times;
    }

    public MemoryStatus? ReadMemory() => Memory;

    public IReadOnlyList<VolumeStatus> ReadVolumes() => Volumes;

    public IReadOnlyList<NetworkAdapterStatus> ReadAdapters() => Adapters;

    public string? ReadPrimaryIPv4() => PrimaryIPv4;

    public BatteryStatus? ReadBattery() => Battery;

    public TimeSpan ReadUptime() => Uptime;

    public string? ReadOsVersion() => OsVersion;

    public bool? ReadPendingReboot() => PendingReboot;

    public string? ReadKernelBootId() => BootId;
}

/// <summary>A source of sessions with a fixed answer.</summary>
internal sealed class FakeSessionSource : ILogindSessionSource
{
    public FakeSessionSource(IReadOnlyList<LogindSession>? sessions)
    {
        Sessions = sessions;
    }

    public IReadOnlyList<LogindSession>? Sessions { get; set; }

    public int Calls { get; private set; }

    public ValueTask<IReadOnlyList<LogindSession>?> ListSessionsAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return ValueTask.FromResult(Sessions);
    }
}

/// <summary>A command runner that answers from a table and records every call.</summary>
internal sealed class FakeProcessReader : IProcessOutputReader
{
    private readonly Dictionary<string, string> _outputs = new(StringComparer.Ordinal);

    public List<(string FileName, string[] Arguments)> Calls { get; } = [];

    /// <summary>
    /// Answers the calls with this key: <c>list-sessions</c>, or <c>show-session &lt;id&gt;</c> for the properties of a session. Every
    /// other call yields null, which is what a program that is not installed or that failed gives.
    /// </summary>
    public void Answer(string key, string output) => _outputs[key] = output;

    public ValueTask<string?> ReadStandardOutputAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Calls.Add((fileName, arguments.ToArray()));
        string key = arguments.Count > 1 && arguments[0] == "show-session"
            ? "show-session " + arguments[1]
            : arguments.Count > 0 ? arguments[0] : string.Empty;
        return ValueTask.FromResult(_outputs.TryGetValue(key, out string? output) ? output : null);
    }
}

/// <summary>Builders for the system metric tests.</summary>
internal static class SystemMetricTestData
{
    public const long Gibibyte = 1024L * 1024 * 1024;

    public const ulong GibibyteBytes = 1024UL * 1024 * 1024;

    public static VolumeStatus Volume(string mountPoint, long totalGibibytes, long freeGibibytes) =>
        new(mountPoint, totalGibibytes * Gibibyte, freeGibibytes * Gibibyte);

    public static NetworkAdapterStatus Adapter(string name, long received = 0, long sent = 0) =>
        new(name, name, received, sent);

    public static LinuxSystemMetricOptions NoTimerNoWarmup() => new()
    {
        RefreshInterval = Timeout.InfiniteTimeSpan,
        RateWarmup = TimeSpan.Zero,
    };

    public static LinuxSystemMetricProvider Provider(FakeLinuxSources sources, ManualTime time, LinuxSystemMetricOptions? options = null) =>
        new(sources, time, boot: null, options ?? NoTimerNoWarmup());

    public static async Task<IReadOnlyDictionary<string, object?>> SampleAsync(LinuxSystemMetricProvider provider, params string[] ids) =>
        await provider.SampleAsync(ids, CancellationToken.None);

    public static LogindSession Session(
        string id,
        string user = "tim",
        string? seat = "seat0",
        string sessionClass = "user",
        string? type = "wayland",
        string state = "active",
        bool active = true,
        bool remote = false,
        bool? locked = false) =>
        new(id, user, 1000, seat, sessionClass, type, state, active, remote, locked);
}
