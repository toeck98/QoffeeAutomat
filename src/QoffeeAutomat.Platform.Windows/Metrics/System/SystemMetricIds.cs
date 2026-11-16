using System.Globalization;
using System.Text;

namespace QoffeeAutomat.Platform.Windows.Metrics.SystemMetrics;

/// <summary>The ids of the Windows system metrics (PLAN.md S06, T304). Ids are stable: they are part of the Home Assistant unique id.</summary>
public static class SystemMetricIds
{
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

    /// <summary>The operating system and its build.</summary>
    public const string OsVersion = "system.os_version";

    /// <summary>Windows waits for a restart.</summary>
    public const string PendingReboot = "system.pending_reboot";

    /// <summary>Utilisation of the busiest GPU engine, 0 to 100 percent.</summary>
    public const string GpuUsage = "gpu.usage";

    /// <summary>The id of the free space of a drive, for example <c>disk.c.free</c>.</summary>
    /// <param name="letter">The drive letter.</param>
    /// <returns>The metric id.</returns>
    public static string DiskFree(char letter) => $"disk.{char.ToLowerInvariant(letter)}.free";

    /// <summary>The id of the used space of a drive in percent, for example <c>disk.c.used_percent</c>.</summary>
    /// <param name="letter">The drive letter.</param>
    /// <returns>The metric id.</returns>
    public static string DiskUsedPercent(char letter) => $"disk.{char.ToLowerInvariant(letter)}.used_percent";

    /// <summary>The id of the receive rate of an adapter, for example <c>net.ethernet.rx</c>.</summary>
    /// <param name="slug">The adapter slug, see <see cref="AdapterSlug"/>.</param>
    /// <returns>The metric id.</returns>
    public static string NetworkReceive(string slug) => $"net.{slug}.rx";

    /// <summary>The id of the transmit rate of an adapter, for example <c>net.ethernet.tx</c>.</summary>
    /// <param name="slug">The adapter slug, see <see cref="AdapterSlug"/>.</param>
    /// <returns>The metric id.</returns>
    public static string NetworkTransmit(string slug) => $"net.{slug}.tx";

    /// <summary>
    /// Turns the name of a network adapter into the part of a metric id: lower case letters and digits, other characters
    /// become single underscores (<c>Wi-Fi</c> becomes <c>wi_fi</c>). A name without letters or digits becomes <c>adapter</c>.
    /// </summary>
    /// <param name="adapterName">The name of the adapter as Windows shows it.</param>
    /// <returns>The slug.</returns>
    public static string AdapterSlug(string adapterName)
    {
        ArgumentNullException.ThrowIfNull(adapterName);

        var slug = new StringBuilder(adapterName.Length);
        foreach (char character in adapterName)
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

        return slug.Length == 0 ? "adapter" : slug.ToString();
    }
}
