using QoffeeAutomat.Platform.Linux.Metrics.SystemMetrics;

namespace QoffeeAutomat.Platform.Linux.Tests.Metrics;

/// <summary>
/// <see cref="LinuxSystemSources"/> on a directory with synthetic <c>proc</c>, <c>sys</c> and <c>etc</c> files (PLAN.md S06, T801):
/// what is read, what is left out and what happens when optional hardware is absent. These tests run on every system.
/// </summary>
[Trait("Category", "SystemMetrics")]
public sealed class LinuxSystemSourcesTests
{
    private const string Mounts = """
        sysfs /sys sysfs rw,nosuid,nodev,noexec,relatime 0 0
        proc /proc proc rw,nosuid,nodev,noexec,relatime 0 0
        udev /dev devtmpfs rw,nosuid,relatime,size=8037064k 0 0
        tmpfs /run tmpfs rw,nosuid,nodev,noexec,relatime,size=1609456k 0 0
        /dev/nvme0n1p2 / ext4 rw,relatime,errors=remount-ro 0 0
        /dev/nvme0n1p1 /boot/efi vfat rw,relatime,fmask=0077 0 0
        /dev/sda1 /home ext4 rw,relatime 0 0
        /dev/sdb1 /mnt/data\040disk xfs rw,relatime 0 0
        /dev/sda1 /home/tim/bind ext4 rw,relatime 0 0
        /dev/loop3 /snap/core22/1380 squashfs ro,nodev,relatime 0 0
        //server/share /mnt/nas cifs rw,relatime 0 0
        /dev/sdc1 /media/tim/USB vfat rw,relatime 0 0
        /dev/sdd1 /run/media/tim/Stick exfat rw,relatime 0 0
        overlay /var/lib/docker/overlay2/abc/merged overlay rw,relatime 0 0
        """;

    private const string NetDev = """
        Inter-|   Receive                                                |  Transmit
         face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
            lo:  123456     1000    0    0    0     0          0         0   123456     1000    0    0    0     0       0          0
         enp3s0: 9876543    50000    0    0    0     0          0       100  1234567    30000    0    0    0     0       0          0
          wlan0:  555555     4000    0    0    0     0          0         0   444444     3000    0    0    0     0       0          0
         wwan0:  777        10    0    0    0     0          0         0   888        10    0    0    0     0       0          0
        docker0:       0        0    0    0    0     0          0         0        0        0    0    0    0     0       0          0
         veth12:     100       10    0    0    0     0          0         0      200       20    0    0    0     0       0          0
        """;

    private static Func<string, VolumeSpace?> SpaceOf(Dictionary<string, VolumeSpace> spaces)
    {
        return mountPoint => spaces.TryGetValue(mountPoint, out VolumeSpace space) ? space : null;
    }

    private static string RouteVia(string name, int metric)
    {
        return "Iface\tDestination\tGateway \tFlags\tRefCnt\tUse\tMetric\tMask\t\tMTU\tWindow\tIRTT\n"
            + name + "\t00000000\t0100A8C0\t0003\t0\t0\t" + metric + "\t00000000\t0\t0\t0\n";
    }

    // ------------------------------------------------------------------
    // CPU, memory, uptime
    // ------------------------------------------------------------------

    [Fact]
    public void Cpu_memory_and_uptime_are_read_from_proc()
    {
        using var root = new FixtureRoot();
        root.Write("proc/stat", "cpu  4705 150 1120 16250 520 0 76 0 0 0\ncpu0 1 1 1 1 1 0 0 0 0 0\n");
        root.Write("proc/meminfo", "MemTotal: 16777216 kB\nMemAvailable: 4194304 kB\n");
        root.Write("proc/uptime", "12345.67 98765.43\n");
        var sources = new LinuxSystemSources(root.Root);

        Assert.Equal(new CpuTimes(16770, 22821), sources.ReadCpuTimes());
        Assert.Equal(new MemoryStatus(16UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024), sources.ReadMemory());
        Assert.Equal(TimeSpan.FromSeconds(12345.67), sources.ReadUptime());
    }

    [Fact]
    public void Files_that_are_missing_give_null_and_never_throw()
    {
        using var root = new FixtureRoot();
        var sources = new LinuxSystemSources(root.Root, readSpace: _ => null, ipv4OfInterface: _ => null);

        Assert.Null(sources.ReadCpuTimes());
        Assert.Null(sources.ReadMemory());
        Assert.Empty(sources.ReadVolumes());
        Assert.Empty(sources.ReadAdapters());
        Assert.Null(sources.ReadPrimaryIPv4());
        Assert.Null(sources.ReadBattery());
        Assert.Null(sources.ReadOsVersion());
        Assert.Null(sources.ReadPendingReboot());
        Assert.Null(sources.ReadKernelBootId());
        Assert.True(sources.ReadUptime() >= TimeSpan.Zero);
    }

    // ------------------------------------------------------------------
    // Volumes
    // ------------------------------------------------------------------

    [Fact]
    public void Volumes_are_the_local_disks_and_each_disk_appears_once()
    {
        using var root = new FixtureRoot();
        root.Write("proc/mounts", Mounts);
        var spaces = new Dictionary<string, VolumeSpace>(StringComparer.Ordinal)
        {
            ["/"] = new VolumeSpace(500L * SystemMetricTestData.Gibibyte, 125L * SystemMetricTestData.Gibibyte),
            ["/boot/efi"] = new VolumeSpace(512L * 1024 * 1024, 500L * 1024 * 1024),
            ["/home"] = new VolumeSpace(1000L * SystemMetricTestData.Gibibyte, 900L * SystemMetricTestData.Gibibyte),
            ["/mnt/data disk"] = new VolumeSpace(2000L * SystemMetricTestData.Gibibyte, 1000L * SystemMetricTestData.Gibibyte),
        };
        var sources = new LinuxSystemSources(root.Root, SpaceOf(spaces));

        IReadOnlyList<VolumeStatus> volumes = sources.ReadVolumes();

        // Not listed: the pseudo file systems, the snap loop device, the network share, removable media, the container overlay and
        // the second mount (a bind mount) of /dev/sda1.
        string[] mountPoints = [.. volumes.Select(volume => volume.MountPoint).Order(StringComparer.Ordinal)];
        string[] expected = ["/", "/boot/efi", "/home", "/mnt/data disk"];
        Assert.Equal(expected, mountPoints);
        VolumeStatus rootVolume = volumes.Single(volume => volume.MountPoint == "/");
        Assert.Equal((500L * SystemMetricTestData.Gibibyte, 125L * SystemMetricTestData.Gibibyte), (rootVolume.TotalBytes, rootVolume.FreeBytes));
    }

    [Fact]
    public void A_volume_that_cannot_be_measured_or_has_no_size_is_left_out()
    {
        using var root = new FixtureRoot();
        root.Write("proc/mounts", "/dev/sda1 / ext4 rw 0 0\n/dev/sdb1 /data ext4 rw 0 0\n/dev/sdc1 /empty ext4 rw 0 0\n");
        var spaces = new Dictionary<string, VolumeSpace>(StringComparer.Ordinal)
        {
            ["/"] = new VolumeSpace(100, 40),
            ["/empty"] = new VolumeSpace(0, 0),
        };
        var sources = new LinuxSystemSources(root.Root, SpaceOf(spaces));

        VolumeStatus volume = Assert.Single(sources.ReadVolumes());

        Assert.Equal("/", volume.MountPoint);
    }

    [Fact]
    public void In_a_container_the_overlay_root_is_the_volume()
    {
        using var root = new FixtureRoot();
        root.Write("proc/mounts", "overlay / overlay rw,relatime 0 0\noverlay /other overlay rw 0 0\n");
        var spaces = new Dictionary<string, VolumeSpace>(StringComparer.Ordinal)
        {
            ["/"] = new VolumeSpace(100, 40),
            ["/other"] = new VolumeSpace(100, 40),
        };
        var sources = new LinuxSystemSources(root.Root, SpaceOf(spaces));

        Assert.Equal("/", Assert.Single(sources.ReadVolumes()).MountPoint);
    }

    [Fact]
    public void A_free_space_above_the_size_is_clamped_to_the_size()
    {
        using var root = new FixtureRoot();
        root.Write("proc/mounts", "/dev/sda1 / ext4 rw 0 0\n");
        var spaces = new Dictionary<string, VolumeSpace>(StringComparer.Ordinal) { ["/"] = new VolumeSpace(100, 400) };
        var sources = new LinuxSystemSources(root.Root, SpaceOf(spaces));

        Assert.Equal(100, Assert.Single(sources.ReadVolumes()).FreeBytes);
    }

    // ------------------------------------------------------------------
    // Network
    // ------------------------------------------------------------------

    [Fact]
    public void Adapters_are_the_physical_interfaces_that_are_up()
    {
        using var root = new FixtureRoot();
        root.Write("proc/net/dev", NetDev);
        root.CreateDirectory("sys/class/net/enp3s0/device");
        root.Write("sys/class/net/enp3s0/operstate", "up\n");
        root.CreateDirectory("sys/class/net/wlan0/device");
        root.Write("sys/class/net/wlan0/operstate", "down\n");
        root.CreateDirectory("sys/class/net/wwan0/device");
        root.Write("sys/class/net/wwan0/operstate", "unknown\n");
        root.Write("sys/class/net/docker0/operstate", "up\n");
        root.Write("sys/class/net/lo/operstate", "unknown\n");
        root.CreateDirectory("sys/class/net/veth12");
        var sources = new LinuxSystemSources(root.Root);

        IReadOnlyList<NetworkAdapterStatus> adapters = sources.ReadAdapters();

        // wlan0 is down; docker0, lo and veth12 are virtual (no device entry).
        NetworkAdapterStatus[] expected =
        [
            new NetworkAdapterStatus("enp3s0", "enp3s0", 9876543, 1234567),
            new NetworkAdapterStatus("wwan0", "wwan0", 777, 888),
        ];
        Assert.Equal(expected, adapters);
    }

    [Fact]
    public void The_primary_address_is_the_one_of_the_interface_of_the_default_route()
    {
        using var root = new FixtureRoot();
        root.Write("proc/net/route", RouteVia("enp3s0", 100));
        var addresses = new Dictionary<string, string>(StringComparer.Ordinal) { ["enp3s0"] = "192.168.0.20", ["wlan0"] = "192.168.0.21" };
        var sources = new LinuxSystemSources(root.Root, ipv4OfInterface: name => addresses.GetValueOrDefault(name));

        Assert.Equal("192.168.0.20", sources.ReadPrimaryIPv4());
    }

    [Fact]
    public void Without_a_default_route_the_address_of_the_first_adapter_that_is_up_is_used()
    {
        using var root = new FixtureRoot();
        root.Write("proc/net/dev", NetDev);
        root.CreateDirectory("sys/class/net/enp3s0/device");
        root.Write("sys/class/net/enp3s0/operstate", "up\n");
        var addresses = new Dictionary<string, string>(StringComparer.Ordinal) { ["enp3s0"] = "10.0.0.5" };
        var sources = new LinuxSystemSources(root.Root, ipv4OfInterface: name => addresses.GetValueOrDefault(name));

        Assert.Equal("10.0.0.5", sources.ReadPrimaryIPv4());
    }

    [Fact]
    public void Without_a_connection_there_is_no_address()
    {
        using var root = new FixtureRoot();
        root.Write("proc/net/route", RouteVia("enp3s0", 100));
        var sources = new LinuxSystemSources(root.Root, ipv4OfInterface: _ => null);

        Assert.Null(sources.ReadPrimaryIPv4());
    }

    // ------------------------------------------------------------------
    // Battery
    // ------------------------------------------------------------------

    [Fact]
    public void The_battery_is_read_from_the_power_supply_class_and_ignores_the_charger_and_peripherals()
    {
        using var root = new FixtureRoot();
        root.Write("sys/class/power_supply/BAT0/type", "Battery\n");
        root.Write("sys/class/power_supply/BAT0/capacity", "80\n");
        root.Write("sys/class/power_supply/BAT0/status", "Charging\n");
        root.Write("sys/class/power_supply/AC/type", "Mains\n");
        root.Write("sys/class/power_supply/AC/online", "1\n");
        root.Write("sys/class/power_supply/hidpp_battery_0/type", "Battery\n");
        root.Write("sys/class/power_supply/hidpp_battery_0/scope", "Device\n");
        root.Write("sys/class/power_supply/hidpp_battery_0/capacity", "5\n");
        root.Write("sys/class/power_supply/hidpp_battery_0/status", "Discharging\n");
        var sources = new LinuxSystemSources(root.Root);

        Assert.Equal(new BatteryStatus(80, IsCharging: true), sources.ReadBattery());
    }

    [Fact]
    public void Several_batteries_average_their_charge_and_charge_when_one_does()
    {
        using var root = new FixtureRoot();
        root.Write("sys/class/power_supply/BAT0/type", "Battery\n");
        root.Write("sys/class/power_supply/BAT0/capacity", "80\n");
        root.Write("sys/class/power_supply/BAT0/status", "Discharging\n");
        root.Write("sys/class/power_supply/BAT1/type", "Battery\n");
        root.Write("sys/class/power_supply/BAT1/capacity", "60\n");
        root.Write("sys/class/power_supply/BAT1/status", "Charging\n");
        var sources = new LinuxSystemSources(root.Root);

        Assert.Equal(new BatteryStatus(70, IsCharging: true), sources.ReadBattery());
    }

    [Fact]
    public void A_battery_that_reports_no_charge_has_an_unknown_level()
    {
        using var root = new FixtureRoot();
        root.Write("sys/class/power_supply/BAT0/type", "Battery\n");
        root.Write("sys/class/power_supply/BAT0/capacity", "unknown\n");
        root.Write("sys/class/power_supply/BAT0/status", "Not charging\n");
        var sources = new LinuxSystemSources(root.Root);

        Assert.Equal(new BatteryStatus(null, IsCharging: false), sources.ReadBattery());
    }

    [Fact]
    public void A_computer_without_a_battery_has_none()
    {
        using var root = new FixtureRoot();
        root.Write("sys/class/power_supply/AC/type", "Mains\n");
        root.Write("sys/class/power_supply/AC/online", "1\n");
        var sources = new LinuxSystemSources(root.Root);

        Assert.Null(sources.ReadBattery());
    }

    // ------------------------------------------------------------------
    // Operating system, reboot marker, boot id
    // ------------------------------------------------------------------

    [Fact]
    public void The_operating_system_is_the_pretty_name_and_the_kernel()
    {
        using var root = new FixtureRoot();
        root.Write("etc/os-release", "PRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\nNAME=\"Ubuntu\"\nVERSION_ID=\"24.04\"\n");
        root.Write("proc/sys/kernel/osrelease", "6.8.0-51-generic\n");
        var sources = new LinuxSystemSources(root.Root);

        Assert.Equal("Ubuntu 24.04.1 LTS (Linux 6.8.0-51-generic)", sources.ReadOsVersion());
    }

    [Fact]
    public void Without_a_pretty_name_the_name_and_version_are_used_and_usr_lib_is_the_fallback_file()
    {
        using var root = new FixtureRoot();
        root.Write("usr/lib/os-release", "NAME=\"Fedora Linux\"\nVERSION_ID=41\n");
        root.Write("proc/sys/kernel/osrelease", "6.11.4\n");
        var sources = new LinuxSystemSources(root.Root);

        Assert.Equal("Fedora Linux 41 (Linux 6.11.4)", sources.ReadOsVersion());
    }

    [Fact]
    public void The_kernel_alone_or_the_distribution_alone_is_still_a_description()
    {
        using var kernelOnly = new FixtureRoot();
        kernelOnly.Write("proc/sys/kernel/osrelease", "6.8.0\n");
        using var distributionOnly = new FixtureRoot();
        distributionOnly.Write("etc/os-release", "PRETTY_NAME=\"Alpine Linux v3.20\"\n");

        Assert.Equal("Linux 6.8.0", new LinuxSystemSources(kernelOnly.Root).ReadOsVersion());
        Assert.Equal("Alpine Linux v3.20", new LinuxSystemSources(distributionOnly.Root).ReadOsVersion());
    }

    [Theory]
    [InlineData("run/reboot-required", true)]
    [InlineData("var/run/reboot-required", true)]
    [InlineData("run/reboot-needed", true)]
    [InlineData("run/unrelated", false)]
    public void A_reboot_marker_means_a_restart_is_pending(string file, bool expected)
    {
        using var root = new FixtureRoot();
        root.CreateDirectory("run");
        root.Write(file, "*** System restart required ***\n");
        var sources = new LinuxSystemSources(root.Root);

        Assert.Equal(expected, sources.ReadPendingReboot());
    }

    [Fact]
    public void The_pending_reboot_is_unknown_on_a_system_without_run_directories()
    {
        using var root = new FixtureRoot();

        Assert.Null(new LinuxSystemSources(root.Root).ReadPendingReboot());
    }

    [Fact]
    public void The_kernel_boot_id_is_read_and_trimmed()
    {
        using var root = new FixtureRoot();
        root.Write("proc/sys/kernel/random/boot_id", "0f8e7c3a-9a5b-4d5e-8c21-3f1b2a6d7e90\n");

        Assert.Equal("0f8e7c3a-9a5b-4d5e-8c21-3f1b2a6d7e90", new LinuxSystemSources(root.Root).ReadKernelBootId());
    }
}
