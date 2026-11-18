using QoffeeAutomat.Platform.Linux.Metrics.SystemMetrics;

namespace QoffeeAutomat.Platform.Linux.Tests.Metrics;

/// <summary>The parsers of the kernel's text files, fed with synthetic content in the format of a real kernel (PLAN.md S06, T801). They run on every system.</summary>
[Trait("Category", "SystemMetrics")]
public sealed class ProcFileParserTests
{
    [Fact]
    public void Cpu_times_are_the_sum_line_with_idle_and_iowait_counted_as_idle()
    {
        const string stat = """
            cpu  4705 150 1120 16250 520 0 76 0 0 0
            cpu0 1130 40 300 4000 130 0 20 0 0 0
            intr 1462898
            btime 1763380000
            """;

        CpuTimes? times = ProcFileParser.ParseCpuTimes(stat);

        // user + nice + system + idle + iowait + irq + softirq + steal; the guest times are already part of user and nice.
        Assert.Equal(new CpuTimes(Idle: 16250 + 520, Total: 22821), times);
    }

    [Fact]
    public void Cpu_times_of_an_old_kernel_with_four_fields_still_parse()
    {
        Assert.Equal(new CpuTimes(850, 1000), ProcFileParser.ParseCpuTimes("cpu  100 0 50 850\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("cpu0 1 2 3 4 5\n")]
    [InlineData("cpu  1 2 3\n")]
    [InlineData("cpu  1 2 x 4 5\n")]
    public void Cpu_times_that_are_missing_or_broken_are_null(string stat)
    {
        Assert.Null(ProcFileParser.ParseCpuTimes(stat));
    }

    [Fact]
    public void Memory_uses_mem_available_and_converts_kilobytes_to_bytes()
    {
        const string meminfo = """
            MemTotal:       16777216 kB
            MemFree:         1234567 kB
            MemAvailable:    4194304 kB
            Buffers:          123456 kB
            Cached:          4000000 kB
            HugePages_Total:       0
            """;

        MemoryStatus? memory = ProcFileParser.ParseMemory(meminfo);

        Assert.Equal(new MemoryStatus(16UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024), memory);
    }

    [Fact]
    public void Memory_of_a_kernel_without_mem_available_adds_free_buffers_and_cache()
    {
        const string meminfo = "MemTotal: 8388608 kB\nMemFree: 1000 kB\nBuffers: 200 kB\nCached: 800 kB\n";

        Assert.Equal(new MemoryStatus(8388608UL * 1024, 2000UL * 1024), ProcFileParser.ParseMemory(meminfo));
    }

    [Theory]
    [InlineData("")]
    [InlineData("MemFree: 1000 kB\n")]
    [InlineData("MemTotal: 0 kB\nMemAvailable: 0 kB\n")]
    [InlineData("MemTotal: 1000 kB\n")]
    public void Memory_that_is_missing_or_unusable_is_null(string meminfo)
    {
        Assert.Null(ProcFileParser.ParseMemory(meminfo));
    }

    [Fact]
    public void Mounts_resolve_the_octal_escapes_of_the_kernel()
    {
        const string mounts = """
            /dev/nvme0n1p2 / ext4 rw,relatime,errors=remount-ro 0 0
            /dev/sdb1 /mnt/data\040disk xfs rw,relatime 0 0
            /dev/sdc1 /mnt/back\134slash ext4 rw 0 0
            broken
            """;

        IReadOnlyList<MountEntry> entries = ProcFileParser.ParseMounts(mounts);

        MountEntry[] expected =
        [
            new MountEntry("/dev/nvme0n1p2", "/", "ext4"),
            new MountEntry("/dev/sdb1", "/mnt/data disk", "xfs"),
            new MountEntry("/dev/sdc1", @"/mnt/back\slash", "ext4"),
        ];
        Assert.Equal(expected, entries);
    }

    [Fact]
    public void Net_dev_skips_the_headers_and_reads_the_receive_and_transmit_bytes()
    {
        const string netDev = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
                lo:  123456     1000    0    0    0     0          0         0   123456     1000    0    0    0     0       0          0
             enp3s0: 9876543    50000    0    0    0     0          0       100  1234567    30000    0    0    0     0       0          0
            wlan0:555555 4000 0 0 0 0 0 0 444444 3000 0 0 0 0 0 0
            short: 1 2 3
            """;

        IReadOnlyList<NetDevEntry> entries = ProcFileParser.ParseNetDev(netDev);

        NetDevEntry[] expected =
        [
            new NetDevEntry("lo", 123456, 123456),
            new NetDevEntry("enp3s0", 9876543, 1234567),
            new NetDevEntry("wlan0", 555555, 444444),
        ];
        Assert.Equal(expected, entries);
    }

    [Fact]
    public void Net_dev_counters_above_the_range_of_a_long_are_clamped()
    {
        const string netDev = "  eth0: 18446744073709551615 1 0 0 0 0 0 0 5 1 0 0 0 0 0 0\n";

        NetDevEntry entry = Assert.Single(ProcFileParser.ParseNetDev(netDev));

        Assert.Equal((long.MaxValue, 5L), (entry.ReceivedBytes, entry.SentBytes));
    }

    [Fact]
    public void Uptime_is_the_first_number_in_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(12345.67), ProcFileParser.ParseUptime("12345.67 98765.43\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage\n")]
    [InlineData("-5 3\n")]
    [InlineData("NaN 0\n")]
    public void Uptime_that_does_not_parse_is_null(string uptime)
    {
        Assert.Null(ProcFileParser.ParseUptime(uptime));
    }

    [Fact]
    public void Os_release_understands_the_shell_quoting_and_skips_comments()
    {
        const string osRelease = """"
            # a comment
            NAME="Ubuntu"
            VERSION_ID='24.04'
            ID=ubuntu
            PRETTY_NAME="Say \"hi\" and \\ done"
            not a pair
            """";

        IReadOnlyDictionary<string, string> values = ProcFileParser.ParseOsRelease(osRelease);

        Assert.Equal("Ubuntu", values["NAME"]);
        Assert.Equal("24.04", values["VERSION_ID"]);
        Assert.Equal("ubuntu", values["ID"]);
        Assert.Equal(@"Say ""hi"" and \ done", values["PRETTY_NAME"]);
        Assert.Equal(4, values.Count);
    }

    [Fact]
    public void The_default_route_is_the_one_with_the_lowest_metric_that_is_up()
    {
        string route =
            "Iface\tDestination\tGateway \tFlags\tRefCnt\tUse\tMetric\tMask\t\tMTU\tWindow\tIRTT\n"
            + "wlan0\t00000000\t0100A8C0\t0003\t0\t0\t600\t00000000\t0\t0\t0\n"
            + "enp3s0\t00000000\t0100A8C0\t0003\t0\t0\t100\t00000000\t0\t0\t0\n"
            + "tun0\t00000000\t0100000A\t0002\t0\t0\t10\t00000000\t0\t0\t0\n"
            + "enp3s0\t0000A8C0\t00000000\t0001\t0\t0\t100\t00FFFFFF\t0\t0\t0\n";

        Assert.Equal("enp3s0", ProcFileParser.ParseDefaultRouteInterface(route));
    }

    [Fact]
    public void Without_a_default_route_there_is_no_interface()
    {
        string route =
            "Iface\tDestination\tGateway \tFlags\tRefCnt\tUse\tMetric\tMask\t\tMTU\tWindow\tIRTT\n"
            + "enp3s0\t0000A8C0\t00000000\t0001\t0\t0\t100\t00FFFFFF\t0\t0\t0\n";

        Assert.Null(ProcFileParser.ParseDefaultRouteInterface(route));
        Assert.Null(ProcFileParser.ParseDefaultRouteInterface(string.Empty));
    }
}
