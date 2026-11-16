using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Platform.Windows.Metrics.SystemMetrics;

namespace QoffeeAutomat.Platform.Windows.Tests.Metrics;

/// <summary>Windows as the system metrics see it, with values that the test sets.</summary>
internal sealed class FakeSystemSources : ISystemSources
{
    private int _gpuReads;

    public SystemTimes? Times { get; set; }

    public MemoryStatus? Memory { get; set; }

    public IReadOnlyList<VolumeStatus> Volumes { get; set; } = [];

    public IReadOnlyList<NetworkAdapterStatus> Adapters { get; set; } = [];

    public string? PrimaryIPv4 { get; set; }

    public BatteryStatus? Battery { get; set; }

    public double? Gpu { get; set; }

    public TimeSpan Uptime { get; set; } = TimeSpan.FromHours(30);

    public string? OsVersion { get; set; }

    public bool? PendingReboot { get; set; }

    /// <summary>When set, every read of the processor times advances them by this much (idle, kernel, user).</summary>
    public (ulong Idle, ulong Kernel, ulong User)? AutoAdvance { get; set; }

    public int GpuReads => Volatile.Read(ref _gpuReads);

    public SystemTimes? ReadSystemTimes()
    {
        if (AutoAdvance is { } step && Times is { } current)
        {
            Times = new SystemTimes(current.Idle + step.Idle, current.Kernel + step.Kernel, current.User + step.User);
        }

        return Times;
    }

    public MemoryStatus? ReadMemory() => Memory;

    public IReadOnlyList<VolumeStatus> ReadVolumes() => Volumes;

    public IReadOnlyList<NetworkAdapterStatus> ReadAdapters() => Adapters;

    public string? ReadPrimaryIPv4() => PrimaryIPv4;

    public BatteryStatus? ReadBattery() => Battery;

    public double? ReadGpuUsage()
    {
        Interlocked.Increment(ref _gpuReads);
        return Gpu;
    }

    public TimeSpan ReadUptime() => Uptime;

    public string? ReadOsVersion() => OsVersion;

    public bool? ReadPendingReboot() => PendingReboot;
}

/// <summary>Builders for the system metric tests.</summary>
internal static class SystemMetricTestData
{
    public const long Gibibyte = 1024L * 1024 * 1024;

    public const ulong GibibyteBytes = 1024UL * 1024 * 1024;

    public static VolumeStatus Volume(char letter, long totalGibibytes, long freeGibibytes) =>
        new(letter, totalGibibytes * Gibibyte, freeGibibytes * Gibibyte);

    public static NetworkAdapterStatus Adapter(string id, string name, long received = 0, long sent = 0) =>
        new(id, name, received, sent);

    public static SystemMetricOptions NoTimerNoWarmup() => new()
    {
        RefreshInterval = Timeout.InfiniteTimeSpan,
        RateWarmup = TimeSpan.Zero,
    };

    public static WindowsSystemMetricProvider Provider(FakeSystemSources sources, ManualTime time, SystemMetricOptions? options = null) =>
        new(sources, time, boot: null, options ?? NoTimerNoWarmup());

    public static MetricDescriptor Descriptor(WindowsSystemMetricProvider provider, string id) =>
        provider.Describe().Single(descriptor => descriptor.Id == id);

    public static async Task<IReadOnlyDictionary<string, object?>> SampleAsync(WindowsSystemMetricProvider provider, params string[] ids) =>
        await provider.SampleAsync(ids, CancellationToken.None);
}
