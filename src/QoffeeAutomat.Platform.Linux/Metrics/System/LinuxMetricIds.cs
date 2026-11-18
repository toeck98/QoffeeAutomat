using System.Globalization;
using System.Text;

namespace QoffeeAutomat.Platform.Linux.Metrics.SystemMetrics;

/// <summary>
/// The ids of the Linux system metrics (PLAN.md S06, S17, T801). A metric that means the same as on Windows has the same id
/// (the constants carry the same names as <c>SystemMetricIds</c> of the Windows platform, and a test compares the two catalogs);
/// the metrics that exist per volume or per adapter use the same patterns, with a mount point slug instead of a drive letter.
/// Ids are stable: they are part of the Home Assistant unique id.
/// </summary>
public static class LinuxMetricIds
{
    /// <summary>The slug of the root file system, <c>/</c>, in the ids of the volume metrics (<c>disk.root.free</c>).</summary>
    public const string RootVolumeSlug = "root";

    /// <summary>Processor utilisation of the whole computer, 0 to 100 percent.</summary>
    public const string CpuUsage = "cpu.usage";

    /// <summary>Physical memory in use, percent.</summary>
    public const string MemoryUsedPercent = "memory.used_percent";

    /// <summary>Physical memory in use, GiB.</summary>
    public const string MemoryUsed = "memory.used";

    /// <summary>Installed physical memory, GiB.</summary>
    public const string MemoryTotal = "memory.total";

    /// <summary>Primary IPv4 address.</summary>
    public const string PrimaryIPv4 = "net.ipv4";

    /// <summary>Battery charge, percent. Only described on computers with a battery.</summary>
    public const string BatteryLevel = "battery.level";

    /// <summary>The battery is charging. Only described on computers with a battery.</summary>
    public const string BatteryCharging = "battery.charging";

    /// <summary>Seconds since the computer started.</summary>
    public const string Uptime = "system.uptime";

    /// <summary>When the computer started.</summary>
    public const string LastBoot = "system.last_boot";

    /// <summary>The distribution and the kernel.</summary>
    public const string OsVersion = "system.os_version";

    /// <summary>The system waits for a restart (a package update asked for one).</summary>
    public const string PendingReboot = "system.pending_reboot";

    /// <summary>The id of the free space of a volume, for example <c>disk.root.free</c>.</summary>
    /// <param name="slug">The volume slug, see <see cref="MountSlug"/>.</param>
    /// <returns>The metric id.</returns>
    public static string DiskFree(string slug) => $"disk.{slug}.free";

    /// <summary>The id of the used space of a volume in percent, for example <c>disk.root.used_percent</c>.</summary>
    /// <param name="slug">The volume slug, see <see cref="MountSlug"/>.</param>
    /// <returns>The metric id.</returns>
    public static string DiskUsedPercent(string slug) => $"disk.{slug}.used_percent";

    /// <summary>The id of the receive rate of an adapter, for example <c>net.eth0.rx</c>.</summary>
    /// <param name="slug">The adapter slug, see <see cref="AdapterSlug"/>.</param>
    /// <returns>The metric id.</returns>
    public static string NetworkReceive(string slug) => $"net.{slug}.rx";

    /// <summary>The id of the transmit rate of an adapter, for example <c>net.eth0.tx</c>.</summary>
    /// <param name="slug">The adapter slug, see <see cref="AdapterSlug"/>.</param>
    /// <returns>The metric id.</returns>
    public static string NetworkTransmit(string slug) => $"net.{slug}.tx";

    /// <summary>
    /// Turns the name of a network interface into the part of a metric id: lower case letters and digits, other characters
    /// become single underscores. Same rule as on Windows. A name without letters or digits becomes <c>adapter</c>.
    /// </summary>
    /// <param name="adapterName">The name of the interface, for example <c>enp3s0</c> or <c>wlan0</c>.</param>
    /// <returns>The slug.</returns>
    public static string AdapterSlug(string adapterName)
    {
        ArgumentNullException.ThrowIfNull(adapterName);
        return Slugify(adapterName, "adapter");
    }

    /// <summary>
    /// Turns a mount point into the part of a metric id: <c>/</c> becomes <see cref="RootVolumeSlug"/>, <c>/home</c> becomes
    /// <c>home</c>, <c>/mnt/data disk</c> becomes <c>mnt_data_disk</c>. A mount point without letters or digits becomes <c>volume</c>.
    /// </summary>
    /// <param name="mountPoint">The absolute path of the mount point.</param>
    /// <returns>The slug.</returns>
    public static string MountSlug(string mountPoint)
    {
        ArgumentNullException.ThrowIfNull(mountPoint);
        return mountPoint.Trim() is "/" ? RootVolumeSlug : Slugify(mountPoint, "volume");
    }

    private static string Slugify(string text, string fallback)
    {
        var slug = new StringBuilder(text.Length);
        foreach (char character in text)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(char.ToLower(character, CultureInfo.InvariantCulture));
            }
            else if (slug.Length > 0 && slug[^1] != '_')
            {
                slug.Append('_');
            }
        }

        while (slug.Length > 0 && slug[^1] == '_')
        {
            slug.Length--;
        }

        return slug.Length == 0 ? fallback : slug.ToString();
    }
}
