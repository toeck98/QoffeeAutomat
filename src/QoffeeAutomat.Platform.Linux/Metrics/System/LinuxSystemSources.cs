using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace QoffeeAutomat.Platform.Linux.Metrics.SystemMetrics;

/// <summary>The size and free space of a file system, as <c>statvfs</c> reports them.</summary>
/// <param name="TotalBytes">The size.</param>
/// <param name="FreeBytes">The space that is free for ordinary users.</param>
public readonly record struct VolumeSpace(long TotalBytes, long FreeBytes);

/// <summary>
/// Reads the system metrics from a Linux computer (PLAN.md S06, T801): <c>/proc/stat</c>, <c>/proc/meminfo</c>, <c>/proc/mounts</c>,
/// <c>/proc/net/dev</c>, <c>/proc/net/route</c>, <c>/proc/uptime</c>, <c>/sys/class/net</c>, <c>/sys/class/power_supply</c>,
/// <c>/etc/os-release</c> and the reboot marker files. Nothing here throws: a file that is missing or unreadable gives null, an
/// empty list or "not there", as <see cref="ILinuxSystemSources"/> demands.
/// <para>
/// All paths are relative to a root directory (<c>/</c> in production), so that tests point the class at a directory with
/// synthetic <c>proc</c>, <c>sys</c> and <c>etc</c> content. The two operating system calls that no file provides, the size of a
/// file system and the address of an interface, are injected as well.
/// </para>
/// </summary>
public sealed class LinuxSystemSources : ILinuxSystemSources
{
    private static readonly string[] FixedFileSystems =
    [
        "ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "f2fs", "jfs", "reiserfs", "reiser4", "bcachefs",
        "vfat", "fat", "msdos", "exfat", "ntfs", "ntfs3", "fuseblk", "hfsplus",
    ];

    /// <summary>Automounted removable media and the mounts of container runtimes are not volumes of the computer.</summary>
    private static readonly string[] IgnoredMountPrefixes =
    [
        "/run/", "/media/", "/snap/", "/var/snap/", "/var/lib/docker/", "/var/lib/containers/", "/var/lib/kubelet/", "/var/lib/lxcfs/",
    ];

    private static readonly string[] RebootMarkers =
    [
        "run/reboot-required", // Debian, Ubuntu (update-notifier, unattended-upgrades)
        "var/run/reboot-required",
        "run/reboot-needed", // openSUSE (zypper), also written by some immutable distributions
        "var/run/reboot-needed",
    ];

    private readonly string _root;
    private readonly Func<string, VolumeSpace?> _readSpace;
    private readonly Func<string, string?> _ipv4OfInterface;

    /// <summary>Creates the sources.</summary>
    /// <param name="rootDirectory">The directory that holds <c>proc</c>, <c>sys</c> and <c>etc</c>; null or empty means <c>/</c>.</param>
    /// <param name="readSpace">Returns the size and free space of the file system at a mount point; null asks the operating system.</param>
    /// <param name="ipv4OfInterface">Returns the first IPv4 address of a network interface, or null; null asks the operating system.</param>
    public LinuxSystemSources(
        string? rootDirectory = null,
        Func<string, VolumeSpace?>? readSpace = null,
        Func<string, string?>? ipv4OfInterface = null)
    {
        _root = string.IsNullOrEmpty(rootDirectory) ? "/" : rootDirectory;
        _readSpace = readSpace ?? ReadSpaceFromSystem;
        _ipv4OfInterface = ipv4OfInterface ?? ReadIPv4FromSystem;
    }

    /// <inheritdoc />
    public CpuTimes? ReadCpuTimes()
    {
        return ReadFile("proc/stat") is { } content ? ProcFileParser.ParseCpuTimes(content) : null;
    }

    /// <inheritdoc />
    public MemoryStatus? ReadMemory()
    {
        return ReadFile("proc/meminfo") is { } content ? ProcFileParser.ParseMemory(content) : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<VolumeStatus> ReadVolumes()
    {
        if (ReadFile("proc/mounts") is not { } content)
        {
            return [];
        }

        var devices = new HashSet<string>(StringComparer.Ordinal);
        var volumes = new List<VolumeStatus>();

        // The shortest mount point first: a device that is mounted several times (btrfs subvolumes, bind mounts) is reported
        // once, under the path that is the nearest to the root. OrderBy is stable, so equal lengths keep the order of the file.
        foreach (MountEntry mount in ProcFileParser.ParseMounts(content).OrderBy(entry => entry.MountPoint.Length))
        {
            if (!IsFixedVolume(mount) || !devices.Add(mount.Device))
            {
                continue;
            }

            if (_readSpace(mount.MountPoint) is { TotalBytes: > 0 } space)
            {
                volumes.Add(new VolumeStatus(mount.MountPoint, space.TotalBytes, Math.Clamp(space.FreeBytes, 0, space.TotalBytes)));
            }
        }

        return volumes;
    }

    /// <inheritdoc />
    public IReadOnlyList<NetworkAdapterStatus> ReadAdapters()
    {
        if (ReadFile("proc/net/dev") is not { } content)
        {
            return [];
        }

        var adapters = new List<NetworkAdapterStatus>();
        foreach (NetDevEntry entry in ProcFileParser.ParseNetDev(content))
        {
            if (IsPhysicalAdapterThatIsUp(entry.Name))
            {
                adapters.Add(new NetworkAdapterStatus(entry.Name, entry.Name, entry.ReceivedBytes, entry.SentBytes));
            }
        }

        return adapters;
    }

    /// <inheritdoc />
    public string? ReadPrimaryIPv4()
    {
        if (ReadFile("proc/net/route") is { } route
            && ProcFileParser.ParseDefaultRouteInterface(route) is { } gateway
            && _ipv4OfInterface(gateway) is { } address)
        {
            return address;
        }

        // A computer without a default route (an isolated network) still has an address on the network it is connected to.
        foreach (NetworkAdapterStatus adapter in ReadAdapters().OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            if (_ipv4OfInterface(adapter.Id) is { } fallbackAddress)
            {
                return fallbackAddress;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public BatteryStatus? ReadBattery()
    {
        string supplies = Path.Combine(_root, "sys/class/power_supply");
        string[] entries;
        try
        {
            entries = Directory.Exists(supplies) ? [.. Directory.EnumerateFileSystemEntries(supplies).Order(StringComparer.Ordinal)] : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var levels = new List<double>();
        bool found = false;
        bool charging = false;
        foreach (string entry in entries)
        {
            // A battery of the computer: type Battery. The batteries of peripherals (a wireless mouse) have the scope Device.
            if (!string.Equals(ReadAbsolute(Path.Combine(entry, "type"))?.Trim(), "Battery", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ReadAbsolute(Path.Combine(entry, "scope"))?.Trim(), "Device", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            found = true;
            if (ReadAbsolute(Path.Combine(entry, "capacity"))?.Trim() is { } capacity
                && double.TryParse(capacity, NumberStyles.Float, CultureInfo.InvariantCulture, out double level)
                && level is >= 0 and <= 100)
            {
                levels.Add(level);
            }

            charging |= string.Equals(ReadAbsolute(Path.Combine(entry, "status"))?.Trim(), "Charging", StringComparison.OrdinalIgnoreCase);
        }

        return found ? new BatteryStatus(levels.Count > 0 ? levels.Average() : null, charging) : null;
    }

    /// <inheritdoc />
    public TimeSpan ReadUptime()
    {
        if (ReadFile("proc/uptime") is { } content && ProcFileParser.ParseUptime(content) is { } uptime)
        {
            return uptime;
        }

        return TimeSpan.FromMilliseconds(Environment.TickCount64);
    }

    /// <inheritdoc />
    public string? ReadOsVersion()
    {
        string? release = ReadFile("etc/os-release") ?? ReadFile("usr/lib/os-release");
        string? distribution = null;
        if (release is not null)
        {
            IReadOnlyDictionary<string, string> values = ProcFileParser.ParseOsRelease(release);
            distribution = NonEmpty(values.GetValueOrDefault("PRETTY_NAME"))
                ?? NonEmpty(values.GetValueOrDefault("NAME") + " " + values.GetValueOrDefault("VERSION_ID"));
        }

        string? kernel = NonEmpty(ReadFile("proc/sys/kernel/osrelease")?.Trim());
        return (distribution, kernel) switch
        {
            (null, null) => null,
            (null, _) => "Linux " + kernel,
            (_, null) => distribution,
            _ => distribution + " (Linux " + kernel + ")",
        };
    }

    /// <inheritdoc />
    public bool? ReadPendingReboot()
    {
        if (!Directory.Exists(Path.Combine(_root, "run")) && !Directory.Exists(Path.Combine(_root, "var/run")))
        {
            return null;
        }

        return RebootMarkers.Any(marker => File.Exists(Path.Combine(_root, marker)));
    }

    /// <inheritdoc />
    public string? ReadKernelBootId()
    {
        return NonEmpty(ReadFile("proc/sys/kernel/random/boot_id")?.Trim());
    }

    private static string? NonEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static bool IsFixedVolume(MountEntry mount)
    {
        // Loop devices carry snap packages and disk images, not storage of the computer.
        if (!mount.MountPoint.StartsWith('/') || mount.Device.StartsWith("/dev/loop", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (string prefix in IgnoredMountPrefixes)
        {
            if (mount.MountPoint.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
        }

        // In a container the root file system is an overlay; it is the only overlay that is a volume of this computer.
        return FixedFileSystems.Contains(mount.FileSystemType, StringComparer.Ordinal)
            || (string.Equals(mount.FileSystemType, "overlay", StringComparison.Ordinal) && string.Equals(mount.MountPoint, "/", StringComparison.Ordinal));
    }

    /// <summary>
    /// A physical interface has a <c>device</c> entry in <c>/sys/class/net/&lt;name&gt;</c>; loopback, bridges, tunnels, VLANs and
    /// container interfaces have none. It is up when its operational state is <c>up</c>, or <c>unknown</c> (drivers that do not
    /// report a state, USB tethering).
    /// </summary>
    private bool IsPhysicalAdapterThatIsUp(string name)
    {
        if (name is "." or ".." || name.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        string directory = Path.Combine(_root, "sys/class/net", name);
        if (!Directory.Exists(Path.Combine(directory, "device")))
        {
            return false;
        }

        string? state = ReadAbsolute(Path.Combine(directory, "operstate"))?.Trim();
        return string.Equals(state, "up", StringComparison.OrdinalIgnoreCase) || string.Equals(state, "unknown", StringComparison.OrdinalIgnoreCase);
    }

    private string? ReadFile(string relativePath) => ReadAbsolute(Path.Combine(_root, relativePath));

    private static string? ReadAbsolute(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static VolumeSpace? ReadSpaceFromSystem(string mountPoint)
    {
        try
        {
            // DriveInfo asks the kernel with statvfs; the available space is the one that is free for ordinary users.
            var drive = new DriveInfo(mountPoint);
            return drive.IsReady ? new VolumeSpace(drive.TotalSize, drive.AvailableFreeSpace) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? ReadIPv4FromSystem(string interfaceName)
    {
        try
        {
            foreach (NetworkInterface candidate in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!string.Equals(candidate.Name, interfaceName, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation address in candidate.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return address.Address.ToString();
                    }
                }
            }
        }
        catch (Exception exception) when (exception is NetworkInformationException or SocketException or PlatformNotSupportedException or IOException)
        {
            return null;
        }

        return null;
    }
}
