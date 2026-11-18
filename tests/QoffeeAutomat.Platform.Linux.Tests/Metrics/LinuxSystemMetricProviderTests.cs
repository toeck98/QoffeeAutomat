using System.Text.RegularExpressions;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Platform.Linux.Metrics.SystemMetrics;
using static QoffeeAutomat.Platform.Linux.Tests.SystemMetricTestData;

namespace QoffeeAutomat.Platform.Linux.Tests.Metrics;

/// <summary>
/// The Linux system metric provider with fake sources (PLAN.md S06, T801): descriptors, values, rates and the metrics that come
/// and go. These tests run on every operating system.
/// </summary>
[Trait("Category", "SystemMetrics")]
public sealed partial class LinuxSystemMetricProviderTests
{
    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z0-9_]+)+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex MetricIdPattern();

    private static FakeLinuxSources Computer() => new()
    {
        Volumes = [Volume("/", 500, 125)],
        Adapters = [Adapter("enp3s0")],
        Battery = new BatteryStatus(80, IsCharging: true),
        Memory = new MemoryStatus(16UL * GibibyteBytes, 4UL * GibibyteBytes),
        PrimaryIPv4 = "192.168.1.20",
        OsVersion = "Ubuntu 24.04.1 LTS (Linux 6.8.0-51-generic)",
        PendingReboot = false,
        Times = new CpuTimes(0, 0),
        BootId = "0f8e7c3a-9a5b-4d5e-8c21-3f1b2a6d7e90",
    };

    private static MetricDescriptor Descriptor(LinuxSystemMetricProvider provider, string id) =>
        provider.Describe().Single(descriptor => descriptor.Id == id);

    // ------------------------------------------------------------------
    // Descriptors
    // ------------------------------------------------------------------

    [Fact]
    public void The_descriptors_use_home_assistant_classes_units_state_classes_and_entity_categories()
    {
        using LinuxSystemMetricProvider provider = Provider(Computer(), new ManualTime());

        MetricDescriptor cpu = Descriptor(provider, "cpu.usage");
        Assert.Equal(("sensor", "number", "%", "measurement", "diagnostic"), (cpu.Kind, cpu.ValueType, cpu.Unit, cpu.StateClass, cpu.EntityCategory));

        MetricDescriptor used = Descriptor(provider, "memory.used");
        Assert.Equal(("GiB", "data_size", "measurement"), (used.Unit, used.DeviceClass, used.StateClass));
        Assert.Equal("%", Descriptor(provider, "memory.used_percent").Unit);
        Assert.Equal("data_size", Descriptor(provider, "memory.total").DeviceClass);

        MetricDescriptor free = Descriptor(provider, "disk.root.free");
        Assert.Equal(("GiB", "data_size", "measurement", "diagnostic"), (free.Unit, free.DeviceClass, free.StateClass, free.EntityCategory));
        Assert.Equal("%", Descriptor(provider, "disk.root.used_percent").Unit);

        MetricDescriptor receive = Descriptor(provider, "net.enp3s0.rx");
        Assert.Equal(("kB/s", "data_rate", "measurement"), (receive.Unit, receive.DeviceClass, receive.StateClass));
        Assert.Equal("data_rate", Descriptor(provider, "net.enp3s0.tx").DeviceClass);

        MetricDescriptor level = Descriptor(provider, "battery.level");
        Assert.Equal(("%", "battery", "measurement"), (level.Unit, level.DeviceClass, level.StateClass));
        Assert.Null(level.EntityCategory);
        MetricDescriptor charging = Descriptor(provider, "battery.charging");
        Assert.Equal(("binary_sensor", "bool", "battery_charging"), (charging.Kind, charging.ValueType, charging.DeviceClass));

        MetricDescriptor uptime = Descriptor(provider, "system.uptime");
        Assert.Equal(("s", "duration"), (uptime.Unit, uptime.DeviceClass));
        Assert.Null(uptime.StateClass);
        MetricDescriptor boot = Descriptor(provider, "system.last_boot");
        Assert.Equal(("timestamp", "timestamp"), (boot.ValueType, boot.DeviceClass));
        Assert.Equal("string", Descriptor(provider, "system.os_version").ValueType);
        Assert.Equal(("binary_sensor", "bool"), (Descriptor(provider, "system.pending_reboot").Kind, Descriptor(provider, "system.pending_reboot").ValueType));
        Assert.Equal("string", Descriptor(provider, "net.ipv4").ValueType);
    }

    [Fact]
    public void Every_descriptor_is_complete_enabled_by_default_and_in_the_system_context()
    {
        using LinuxSystemMetricProvider provider = Provider(Computer(), new ManualTime());

        foreach (MetricDescriptor descriptor in provider.Describe())
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Name), descriptor.Id);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.TranslationKey), descriptor.Id);
            Assert.True(descriptor.EnabledDefault, descriptor.Id);
            Assert.Equal("system", descriptor.Context);
            Assert.True(descriptor.IntervalSeconds >= 0, descriptor.Id);
        }
    }

    [Fact]
    public void The_ids_are_well_formed_and_unique()
    {
        FakeLinuxSources computer = Computer();
        computer.Volumes = [Volume("/", 500, 125), Volume("/home", 1000, 900), Volume("/mnt/data disk", 2000, 1000)];
        computer.Adapters = [Adapter("enp3s0"), Adapter("wlan0")];
        using LinuxSystemMetricProvider provider = Provider(computer, new ManualTime());

        string[] ids = [.. provider.Describe().Select(descriptor => descriptor.Id)];

        Assert.All(ids, id => Assert.Matches(MetricIdPattern(), id));
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("disk.mnt_data_disk.free", ids);
        Assert.Contains("net.wlan0.tx", ids);
    }

    [Fact]
    public void There_is_no_gpu_metric_on_linux_yet()
    {
        using LinuxSystemMetricProvider provider = Provider(Computer(), new ManualTime());

        Assert.DoesNotContain(provider.Describe(), descriptor => descriptor.Id.StartsWith("gpu.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_computer_without_battery_volumes_or_adapters_describes_neither_battery_nor_volume_nor_rate_metrics()
    {
        var computer = new FakeLinuxSources { Battery = null };
        using LinuxSystemMetricProvider provider = Provider(computer, new ManualTime());

        string[] ids = [.. provider.Describe().Select(descriptor => descriptor.Id)];

        Assert.DoesNotContain(ids, id => id.StartsWith("battery.", StringComparison.Ordinal));
        Assert.DoesNotContain(ids, id => id.StartsWith("disk.", StringComparison.Ordinal));
        Assert.DoesNotContain(ids, id => id.EndsWith(".rx", StringComparison.Ordinal) || id.EndsWith(".tx", StringComparison.Ordinal));
        Assert.Contains("cpu.usage", ids);
        Assert.Contains("net.ipv4", ids);
        Assert.Contains("system.pending_reboot", ids);
    }

    [Fact]
    public void Volumes_and_adapters_with_the_same_slug_get_a_number()
    {
        var computer = new FakeLinuxSources
        {
            Volumes = [Volume("/mnt/a-b", 10, 5), Volume("/mnt/a b", 10, 5)],
            Adapters = [Adapter("eth-0"), Adapter("eth_0")],
        };
        using LinuxSystemMetricProvider provider = Provider(computer, new ManualTime());

        string[] ids = [.. provider.Describe().Select(descriptor => descriptor.Id)];

        // Sorted by mount point and by name, so the numbers do not depend on the order in which Linux lists them.
        Assert.Contains("disk.mnt_a_b.free", ids);
        Assert.Contains("disk.mnt_a_b_2.free", ids);
        Assert.Contains("net.eth_0.rx", ids);
        Assert.Contains("net.eth_0_2.rx", ids);
    }

    // ------------------------------------------------------------------
    // Values
    // ------------------------------------------------------------------

    [Fact]
    public async Task Memory_is_total_minus_available()
    {
        using LinuxSystemMetricProvider provider = Provider(Computer(), new ManualTime());

        IReadOnlyDictionary<string, object?> values = await SampleAsync(provider, "memory.used", "memory.used_percent", "memory.total");

        Assert.Equal(12.0, Assert.IsType<double>(values["memory.used"]), precision: 6);
        Assert.Equal(75.0, Assert.IsType<double>(values["memory.used_percent"]), precision: 6);
        Assert.Equal(16.0, Assert.IsType<double>(values["memory.total"]), precision: 6);
    }

    [Fact]
    public async Task Memory_that_cannot_be_read_is_unavailable_without_affecting_other_metrics()
    {
        FakeLinuxSources computer = Computer();
        computer.Memory = null;
        using LinuxSystemMetricProvider provider = Provider(computer, new ManualTime());

        IReadOnlyDictionary<string, object?> values = await SampleAsync(provider, "memory.used", "memory.total", "net.ipv4");

        Assert.Null(values["memory.used"]);
        Assert.Null(values["memory.total"]);
        Assert.Equal("192.168.1.20", values["net.ipv4"]);
    }

    [Fact]
    public async Task Cpu_usage_is_the_busy_share_between_two_readings_and_the_first_reading_has_no_value()
    {
        var time = new ManualTime();
        FakeLinuxSources computer = Computer();
        computer.Times = new CpuTimes(1000, 2000);
        using LinuxSystemMetricProvider provider = Provider(computer, time);

        Assert.Null((await SampleAsync(provider, "cpu.usage"))["cpu.usage"]);

        time.Now += TimeSpan.FromSeconds(2);
        computer.Times = new CpuTimes(1300, 2400);
        Assert.Equal(25.0, Assert.IsType<double>((await SampleAsync(provider, "cpu.usage"))["cpu.usage"]), precision: 6);

        // Nobody sampled for longer than the baseline lives: the next reading starts over.
        time.Now += TimeSpan.FromMinutes(5);
        computer.Times = new CpuTimes(1400, 2500);
        Assert.Null((await SampleAsync(provider, "cpu.usage"))["cpu.usage"]);
    }

    [Fact]
    public async Task Cpu_usage_without_any_progress_of_the_counters_is_unavailable()
    {
        var time = new ManualTime();
        FakeLinuxSources computer = Computer();
        computer.Times = new CpuTimes(1000, 2000);
        using LinuxSystemMetricProvider provider = Provider(computer, time);
        _ = await SampleAsync(provider, "cpu.usage");

        time.Now += TimeSpan.FromSeconds(2);

        Assert.Null((await SampleAsync(provider, "cpu.usage"))["cpu.usage"]);
    }

    [Fact]
    public async Task A_first_sample_with_the_warmup_takes_a_baseline_and_has_a_value_at_once()
    {
        FakeLinuxSources computer = Computer();
        computer.Times = new CpuTimes(0, 0);
        computer.AutoAdvance = (Idle: 75, Total: 100);
        var options = new LinuxSystemMetricOptions { RefreshInterval = Timeout.InfiniteTimeSpan, RateWarmup = TimeSpan.FromMilliseconds(10) };
        using LinuxSystemMetricProvider provider = Provider(computer, new ManualTime(), options);

        IReadOnlyDictionary<string, object?> values = await SampleAsync(provider, "cpu.usage");

        Assert.Equal(25.0, Assert.IsType<double>(values["cpu.usage"]), precision: 6);
    }

    [Fact]
    public async Task Disk_values_are_free_gibibytes_and_used_percent()
    {
        using LinuxSystemMetricProvider provider = Provider(Computer(), new ManualTime());

        IReadOnlyDictionary<string, object?> values = await SampleAsync(provider, "disk.root.free", "disk.root.used_percent");

        Assert.Equal(125.0, Assert.IsType<double>(values["disk.root.free"]), precision: 6);
        Assert.Equal(75.0, Assert.IsType<double>(values["disk.root.used_percent"]), precision: 6);
    }

    [Fact]
    public async Task A_volume_that_vanished_is_unavailable_until_the_set_of_metrics_is_refreshed()
    {
        FakeLinuxSources computer = Computer();
        using LinuxSystemMetricProvider provider = Provider(computer, new ManualTime());
        computer.Volumes = [];

        IReadOnlyDictionary<string, object?> values = await SampleAsync(provider, "disk.root.free", "disk.root.used_percent");

        Assert.Null(values["disk.root.free"]);
        Assert.Null(values["disk.root.used_percent"]);
    }

    [Fact]
    public async Task Network_rates_are_kilobytes_per_second_between_two_readings()
    {
        var time = new ManualTime();
        FakeLinuxSources computer = Computer();
        computer.Adapters = [Adapter("enp3s0", received: 1000, sent: 2000)];
        using LinuxSystemMetricProvider provider = Provider(computer, time);

        IReadOnlyDictionary<string, object?> first = await SampleAsync(provider, "net.enp3s0.rx", "net.enp3s0.tx");
        Assert.Null(first["net.enp3s0.rx"]);
        Assert.Null(first["net.enp3s0.tx"]);

        time.Now += TimeSpan.FromSeconds(2);
        computer.Adapters = [Adapter("enp3s0", received: 5000, sent: 3000)];
        IReadOnlyDictionary<string, object?> second = await SampleAsync(provider, "net.enp3s0.rx", "net.enp3s0.tx");

        Assert.Equal(2.0, Assert.IsType<double>(second["net.enp3s0.rx"]), precision: 6);
        Assert.Equal(0.5, Assert.IsType<double>(second["net.enp3s0.tx"]), precision: 6);
    }

    [Fact]
    public async Task A_counter_that_went_backwards_gives_no_rate()
    {
        var time = new ManualTime();
        FakeLinuxSources computer = Computer();
        computer.Adapters = [Adapter("enp3s0", received: 5000, sent: 5000)];
        using LinuxSystemMetricProvider provider = Provider(computer, time);
        _ = await SampleAsync(provider, "net.enp3s0.rx");

        time.Now += TimeSpan.FromSeconds(2);
        computer.Adapters = [Adapter("enp3s0", received: 100, sent: 100)];

        Assert.Null((await SampleAsync(provider, "net.enp3s0.rx"))["net.enp3s0.rx"]);
    }

    [Fact]
    public async Task Battery_level_and_charging_come_from_the_source()
    {
        using LinuxSystemMetricProvider provider = Provider(Computer(), new ManualTime());

        IReadOnlyDictionary<string, object?> values = await SampleAsync(provider, "battery.level", "battery.charging");

        Assert.Equal(80.0, Assert.IsType<double>(values["battery.level"]), precision: 6);
        Assert.True(Assert.IsType<bool>(values["battery.charging"]));
    }

    [Fact]
    public async Task Uptime_last_boot_os_version_and_pending_reboot_come_from_the_source()
    {
        var time = new ManualTime();
        FakeLinuxSources computer = Computer();
        computer.Uptime = TimeSpan.FromHours(30);
        computer.PendingReboot = true;
        using LinuxSystemMetricProvider provider = Provider(computer, time);

        IReadOnlyDictionary<string, object?> values =
            await SampleAsync(provider, "system.uptime", "system.last_boot", "system.os_version", "system.pending_reboot");

        Assert.Equal(108000.0, Assert.IsType<double>(values["system.uptime"]), precision: 6);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 6, 0, 0, TimeSpan.Zero), Assert.IsType<DateTimeOffset>(values["system.last_boot"]));
        Assert.Equal("Ubuntu 24.04.1 LTS (Linux 6.8.0-51-generic)", values["system.os_version"]);
        Assert.True(Assert.IsType<bool>(values["system.pending_reboot"]));
    }

    [Fact]
    public async Task A_pending_reboot_that_cannot_be_determined_is_unavailable()
    {
        FakeLinuxSources computer = Computer();
        computer.PendingReboot = null;
        using LinuxSystemMetricProvider provider = Provider(computer, new ManualTime());

        Assert.Null((await SampleAsync(provider, "system.pending_reboot"))["system.pending_reboot"]);
    }

    [Fact]
    public async Task Only_the_asked_metrics_are_returned_and_unknown_ids_are_ignored()
    {
        using LinuxSystemMetricProvider provider = Provider(Computer(), new ManualTime());

        IReadOnlyDictionary<string, object?> values = await SampleAsync(provider, "net.ipv4", "does.not_exist");

        Assert.Equal("net.ipv4", Assert.Single(values).Key);
    }

    // ------------------------------------------------------------------
    // Metrics that come and go
    // ------------------------------------------------------------------

    [Fact]
    public void A_volume_that_appears_changes_the_catalog_and_raises_the_event_once()
    {
        FakeLinuxSources computer = Computer();
        using LinuxSystemMetricProvider provider = Provider(computer, new ManualTime());
        int raised = 0;
        provider.DescriptorsChanged += (_, _) => raised++;

        provider.Refresh();
        Assert.Equal(0, raised);

        computer.Volumes = [Volume("/", 500, 125), Volume("/home", 1000, 900)];
        provider.Refresh();
        provider.Refresh();

        Assert.Equal(1, raised);
        Assert.Contains(provider.Describe(), descriptor => descriptor.Id == "disk.home.free");
    }

    [Fact]
    public void A_battery_that_is_plugged_in_or_removed_changes_the_catalog()
    {
        FakeLinuxSources computer = Computer();
        computer.Battery = null;
        using LinuxSystemMetricProvider provider = Provider(computer, new ManualTime());
        int raised = 0;
        provider.DescriptorsChanged += (_, _) => raised++;
        Assert.DoesNotContain(provider.Describe(), descriptor => descriptor.Id == "battery.level");

        computer.Battery = new BatteryStatus(50, IsCharging: false);
        provider.Refresh();

        Assert.Equal(1, raised);
        Assert.Contains(provider.Describe(), descriptor => descriptor.Id == "battery.level");
    }

    [Fact]
    public void An_adapter_that_goes_down_removes_its_rate_metrics()
    {
        FakeLinuxSources computer = Computer();
        using LinuxSystemMetricProvider provider = Provider(computer, new ManualTime());
        Assert.Contains(provider.Describe(), descriptor => descriptor.Id == "net.enp3s0.rx");

        computer.Adapters = [];
        provider.Refresh();

        Assert.DoesNotContain(provider.Describe(), descriptor => descriptor.Id == "net.enp3s0.rx");
    }

    [Fact]
    public void A_provider_with_the_default_options_checks_on_a_timer_and_can_be_disposed()
    {
        using LinuxSystemMetricProvider provider = new(Computer(), new ManualTime());

        Assert.NotEmpty(provider.Describe());
    }

    // ------------------------------------------------------------------
    // Boot time
    // ------------------------------------------------------------------

    [Fact]
    public void The_boot_id_uses_the_kernel_identifier_and_otherwise_the_start_time()
    {
        var time = new ManualTime();

        var withKernelId = new LinuxBootTime(time, () => TimeSpan.FromHours(1), "0f8e7c3a-9a5b");
        var withoutKernelId = new LinuxBootTime(time, () => TimeSpan.FromHours(1));

        Assert.Equal("linux-0f8e7c3a-9a5b", withKernelId.BootId);
        long minutes = new DateTimeOffset(2026, 11, 16, 11, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds() / 60;
        Assert.Equal("linux-" + minutes, withoutKernelId.BootId);
    }

    [Fact]
    public void The_boot_time_ignores_measurement_noise_but_follows_a_clock_correction()
    {
        var time = new ManualTime();
        TimeSpan uptime = TimeSpan.FromHours(1);
        var boot = new LinuxBootTime(time, () => uptime);
        DateTimeOffset first = boot.BootTime;

        // The uptime grows with the clock; a small jitter between the two is not published.
        time.Now += TimeSpan.FromMinutes(10);
        uptime += TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(30);
        Assert.Equal(first, boot.BootTime);

        // The time service set the clock back by an hour: the start time follows.
        time.Now -= TimeSpan.FromHours(1);
        Assert.Equal(first - TimeSpan.FromHours(1) - TimeSpan.FromSeconds(30), boot.BootTime);
    }
}
