namespace QoffeeAutomat.Platform.Linux.Metrics.SystemMetrics;

/// <summary>Cumulative processor times from the line <c>cpu</c> of <c>/proc/stat</c>, in clock ticks.</summary>
/// <param name="Idle">Time spent idle, including waiting for input and output.</param>
/// <param name="Total">All time: user, nice, system, idle, iowait, irq, softirq and steal.</param>
public readonly record struct CpuTimes(ulong Idle, ulong Total);

/// <summary>Physical memory from <c>/proc/meminfo</c>.</summary>
/// <param name="TotalBytes">Installed physical memory as the kernel reports it.</param>
/// <param name="AvailableBytes">Memory that can be used without swapping (<c>MemAvailable</c>).</param>
public readonly record struct MemoryStatus(ulong TotalBytes, ulong AvailableBytes);

/// <summary>A mounted file system on a local disk.</summary>
/// <param name="MountPoint">The absolute path where it is mounted.</param>
/// <param name="TotalBytes">The size.</param>
/// <param name="FreeBytes">The space that is free for ordinary users.</param>
public sealed record VolumeStatus(string MountPoint, long TotalBytes, long FreeBytes);

/// <summary>A physical network interface that is up, with its cumulative byte counters.</summary>
/// <param name="Id">The interface name, stable while the interface exists.</param>
/// <param name="Name">The name as shown to the user, for example <c>enp3s0</c> or <c>wlan0</c>.</param>
/// <param name="ReceivedBytes">Bytes received since the interface came up.</param>
/// <param name="SentBytes">Bytes sent since the interface came up.</param>
public sealed record NetworkAdapterStatus(string Id, string Name, long ReceivedBytes, long SentBytes);

/// <summary>The battery of a laptop or tablet.</summary>
/// <param name="LevelPercent">The charge, 0 to 100; null when the kernel does not report it.</param>
/// <param name="IsCharging">A battery is charging.</param>
public sealed record BatteryStatus(double? LevelPercent, bool IsCharging);

/// <summary>
/// What the system metrics read from Linux (PLAN.md S06, T801): the seam between <see cref="LinuxSystemMetricProvider"/> and the
/// operating system. Every member returns null, an empty list or a value that means "not there" instead of throwing, so that a
/// metric that the kernel cannot deliver is unavailable and the others are unaffected.
/// </summary>
public interface ILinuxSystemSources
{
    /// <summary>Reads the processor times.</summary>
    /// <returns>The times; null when they cannot be read.</returns>
    CpuTimes? ReadCpuTimes();

    /// <summary>Reads the physical memory.</summary>
    /// <returns>The memory; null when it cannot be read.</returns>
    MemoryStatus? ReadMemory();

    /// <summary>Lists the file systems of local disks.</summary>
    /// <returns>The volumes; pseudo, network and removable-media file systems are not listed, and a disk that is mounted several times appears once.</returns>
    IReadOnlyList<VolumeStatus> ReadVolumes();

    /// <summary>Lists the physical network interfaces that are up.</summary>
    /// <returns>The adapters; loopback, bridges, tunnels and other virtual interfaces are not listed.</returns>
    IReadOnlyList<NetworkAdapterStatus> ReadAdapters();

    /// <summary>Finds the IPv4 address of the connection that leads to the network.</summary>
    /// <returns>The address in dotted notation; null when the computer has no connection.</returns>
    string? ReadPrimaryIPv4();

    /// <summary>Reads the battery.</summary>
    /// <returns>The battery; null on a computer without one.</returns>
    BatteryStatus? ReadBattery();

    /// <summary>Reads the time since the computer started, suspend included.</summary>
    /// <returns>The uptime.</returns>
    TimeSpan ReadUptime();

    /// <summary>Describes the operating system.</summary>
    /// <returns>For example <c>Ubuntu 24.04.1 LTS (Linux 6.8.0-51-generic)</c>; null when it cannot be determined.</returns>
    string? ReadOsVersion();

    /// <summary>Tells whether the system waits for a restart.</summary>
    /// <returns>True or false; null when the system does not have the directories that hold the marker.</returns>
    bool? ReadPendingReboot();

    /// <summary>Reads the identifier that the kernel assigns to every start of the computer.</summary>
    /// <returns>The identifier; null when the kernel does not provide one.</returns>
    string? ReadKernelBootId();
}
