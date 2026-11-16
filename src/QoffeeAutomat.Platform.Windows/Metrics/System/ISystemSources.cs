namespace QoffeeAutomat.Platform.Windows.Metrics.SystemMetrics;

/// <summary>Cumulative processor times from <c>GetSystemTimes</c>, in 100 ns units; the kernel time includes the idle time.</summary>
/// <param name="Idle">Time spent idle.</param>
/// <param name="Kernel">Time spent in kernel mode, including idle.</param>
/// <param name="User">Time spent in user mode.</param>
public readonly record struct SystemTimes(ulong Idle, ulong Kernel, ulong User);

/// <summary>Physical memory from <c>GlobalMemoryStatusEx</c>.</summary>
/// <param name="TotalBytes">Installed physical memory.</param>
/// <param name="AvailableBytes">Physical memory that can be used without paging.</param>
public readonly record struct MemoryStatus(ulong TotalBytes, ulong AvailableBytes);

/// <summary>A fixed volume that is ready.</summary>
/// <param name="Letter">The drive letter, upper case.</param>
/// <param name="TotalBytes">The size.</param>
/// <param name="FreeBytes">The free space.</param>
public sealed record VolumeStatus(char Letter, long TotalBytes, long FreeBytes);

/// <summary>A physical network adapter that is up, with its cumulative byte counters.</summary>
/// <param name="Id">The adapter id (a GUID string), stable while the adapter exists.</param>
/// <param name="Name">The name as Windows shows it, for example <c>Ethernet</c> or <c>Wi-Fi</c>.</param>
/// <param name="ReceivedBytes">Bytes received since the adapter came up.</param>
/// <param name="SentBytes">Bytes sent since the adapter came up.</param>
public sealed record NetworkAdapterStatus(string Id, string Name, long ReceivedBytes, long SentBytes);

/// <summary>The battery of a laptop or tablet.</summary>
/// <param name="LevelPercent">The charge, 0 to 100; null when Windows does not know it.</param>
/// <param name="IsCharging">The battery is charging.</param>
public sealed record BatteryStatus(double? LevelPercent, bool IsCharging);

/// <summary>
/// What the system metrics read from Windows (PLAN.md S06, T304): the seam between <see cref="WindowsSystemMetricProvider"/> and
/// the operating system. Every member returns null, an empty list or a value that means "not there" instead of throwing,
/// so that a metric that Windows cannot deliver is unavailable and the others are unaffected.
/// </summary>
public interface ISystemSources
{
    /// <summary>Reads the processor times.</summary>
    /// <returns>The times; null when they cannot be read.</returns>
    SystemTimes? ReadSystemTimes();

    /// <summary>Reads the physical memory.</summary>
    /// <returns>The memory; null when it cannot be read.</returns>
    MemoryStatus? ReadMemory();

    /// <summary>Lists the fixed volumes that are ready.</summary>
    /// <returns>The volumes; removable drives, network drives and optical drives are not listed.</returns>
    IReadOnlyList<VolumeStatus> ReadVolumes();

    /// <summary>Lists the physical network adapters that are up.</summary>
    /// <returns>The adapters; virtual adapters, loopback and tunnels are not listed.</returns>
    IReadOnlyList<NetworkAdapterStatus> ReadAdapters();

    /// <summary>Finds the IPv4 address of the connection that leads to the network.</summary>
    /// <returns>The address in dotted notation; null when the computer has no connection.</returns>
    string? ReadPrimaryIPv4();

    /// <summary>Reads the battery.</summary>
    /// <returns>The battery; null on a computer without one.</returns>
    BatteryStatus? ReadBattery();

    /// <summary>Reads the utilisation of the GPU. The first call only takes a baseline.</summary>
    /// <returns>Percent of the busiest engine, 0 to 100; null when the counters are not available or there is no baseline yet.</returns>
    double? ReadGpuUsage();

    /// <summary>Reads the time since the computer started.</summary>
    /// <returns>The uptime.</returns>
    TimeSpan ReadUptime();

    /// <summary>Describes the operating system.</summary>
    /// <returns>For example <c>Windows 11 Pro 24H2 (10.0.26100.4061)</c>; null when it cannot be determined.</returns>
    string? ReadOsVersion();

    /// <summary>Tells whether Windows waits for a restart.</summary>
    /// <returns>True or false; null when the registry cannot be read.</returns>
    bool? ReadPendingReboot();
}
