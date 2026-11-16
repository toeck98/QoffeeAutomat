using System.Text.RegularExpressions;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Platform.Windows.Metrics.SystemMetrics;
using static QoffeeAutomat.Platform.Windows.Tests.Metrics.SystemMetricTestData;

namespace QoffeeAutomat.Platform.Windows.Tests.Metrics;

/// <summary>
/// The Windows system metric provider with fake sources (PLAN.md S06, T304): descriptors, values, rates and the metrics that
/// come and go. These tests run on every operating system.
/// </summary>
[Trait("Category", "SystemMetrics")]
public sealed partial class SystemMetricProviderTests
{
    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z0-9_]+)+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex MetricIdPattern();

    private static FakeSystemSources Computer() => new()
    {
        Volumes = [Volume('C', 500, 125)],
        Adapters = [Adapter("{A}", "Ethernet")],
        Battery = new BatteryStatus(80, IsCharging: true),
        Memory = new MemoryStatus(16UL * GibibyteBytes, 4UL * GibibyteBytes),
        PrimaryIPv4 = "192.168.1.20",
        OsVersion = "Windows 11 Pro 24H2 (10.0.26100.4061)",
        PendingReboot = false,
        Times = new SystemTimes(0, 0, 0),
    };

    // ------------------------------------------------------------------
    // Descriptors
    // ------------------------------------------------------------------

    [Fact]
    public void The_descriptors_use_home_assistant_classes_units_state_classes_and_entity_categories()
    {
        using WindowsSystemMetricProvider provider = Provider(Computer(), new ManualTime());

        MetricDescriptor cpu = Descriptor(provider, "cpu.usage");
        Assert.Equal(("sensor", "number", "%", "measurement", "diagnostic"), (cpu.Kind, cpu.ValueType, cpu.Unit, cpu.StateClass, cpu.EntityCategory));

        MetricDescriptor used = Descriptor(provider, "memory.used");
        Assert.Equal(("GiB", "data_size", "measurement"), (used.Unit, used.DeviceClass, used.StateClass));
        Assert.Equal("%", Descriptor(provider, "memory.used_percent").Unit);
        Assert.Equal("data_size", Descriptor(provider, "memory.total").DeviceClass);

        MetricDescriptor free = Descriptor(provider, "disk.c.free");
        Assert.Equal(("GiB", "data_size", "measurement", "diagnostic"), (free.Unit, free.DeviceClass, free.StateClass, free.EntityCategory));
        Assert.Equal("%", Descriptor(provider, "disk.c.used_percent").Unit);

        MetricDescriptor receive = Descriptor(provider, "net.ethernet.rx");
        Assert.Equal(("kB/s", "data_rate", "measurement"), (receive.Unit, receive.DeviceClass, receive.StateClass));
        Assert.Equal("data_rate", Descriptor(provider, "net.ethernet.tx").DeviceClass);

        MetricDescriptor level = Descriptor(provider, "battery.level");
        Assert.Equal(("%", "battery", "measurement"), (level.Unit, level.DeviceClass, level.StateClass));
        Assert.Null(level.EntityCategory);
        MetricDescriptor charging = Descriptor(provider, "battery.charging");
        Assert.Equal(("binary_sensor", "bool", "battery_charging"), (charging.Kind, charging.ValueType, charging.DeviceClass));

        MetricDescriptor uptime = Descriptor(provider, "system.uptime");
        Assert.Equal(("s", "duration", "diagnostic"), (uptime.Unit, uptime.DeviceClass, uptime.EntityCategory));
        MetricDescriptor boot = Descriptor(provider, "system.last_boot");
        Assert.Equal(("timestamp", "timestamp", "diagnostic"), (boot.ValueType, boot.DeviceClass, boot.EntityCategory));
        Assert.Equal("diagnostic", Descriptor(provider, "system.os_version").EntityCategory);
        MetricDescriptor reboot = Descriptor(provider, "system.pending_reboot");
        Assert.Equal(("binary_sensor", "bool", "diagnostic"), (reboot.Kind, reboot.ValueType, reboot.EntityCategory));
        Assert.Equal("%", Descriptor(provider, "gpu.usage").Unit);
        Assert.Equal("string", Descriptor(provider, "net.ipv4").ValueType);
    }

    [Fact]
    public void Every_descriptor_has_a_stable_id_a_translation_key_a_system_context_and_a_valid_interval()
    {
        using WindowsSystemMetricProvider provider = Provider(Computer(), new ManualTime());

        IReadOnlyList<MetricDescriptor> descriptors = provider.Describe();

        Assert.Equal(descriptors.Count, descriptors.Select(descriptor => descriptor.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(descriptors, descriptor =>
        {
            Assert.Matches(MetricIdPattern(), descriptor.Id);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.TranslationKey));
            Assert.Equal("system", descriptor.Context);
            Assert.True(descriptor.IntervalSeconds >= 0);
            Assert.True(descriptor.EnabledDefault);
        });
        Assert.True(descriptors.Count >= 15, $"Expected at least 15 metrics, got {descriptors.Count}.");
    }

    [Fact]
    public void A_computer_without_a_battery_has_no_battery_metrics()
    {
        FakeSystemSources sources = Computer();
        sources.Battery = null;
        using WindowsSystemMetricProvider provider = Provider(sources, new ManualTime());

        Assert.DoesNotContain(provider.Describe(), descriptor => descriptor.Id.StartsWith("battery.", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // CPU, memory
    // ------------------------------------------------------------------

    [Fact]
    public async Task Cpu_usage_is_the_busy_share_of_the_difference_between_two_readings()
    {
        FakeSystemSources sources = Computer();
        var time = new ManualTime();
        using WindowsSystemMetricProvider provider = Provider(sources, time);

        IReadOnlyDictionary<string, object?> first = await SampleAsync(provider, "cpu.usage");
        sources.Times = new SystemTimes(Idle: 600, Kernel: 1000, User: 500);
        time.Now += TimeSpan.FromSeconds(2);
        IReadOnlyDictionary<string, object?> second = await SampleAsync(provider, "cpu.usage");

        // The first sample only takes the baseline. Of the 1500 units that passed, 600 were idle: 60 percent busy.
        Assert.Null(first["cpu.usage"]);
        Assert.Equal(60.0, Assert.IsType<double>(second["cpu.usage"]), 6);
    }

    [Fact]
    public async Task Cpu_usage_stays_between_zero_and_one_hundred_percent()
    {
        FakeSystemSources sources = Computer();
        var time = new ManualTime();
        using WindowsSystemMetricProvider provider = Provider(sources, time);
        await SampleAsync(provider, "cpu.usage");

        sources.Times = new SystemTimes(Idle: 0, Kernel: 1000, User: 1000);
        time.Now += TimeSpan.FromSeconds(2);
        double busy = Assert.IsType<double>((await SampleAsync(provider, "cpu.usage"))["cpu.usage"]);
        sources.Times = new SystemTimes(Idle: 5000, Kernel: 6000, User: 1000);
        time.Now += TimeSpan.FromSeconds(2);
        double idle = Assert.IsType<double>((await SampleAsync(provider, "cpu.usage"))["cpu.usage"]);

        Assert.Equal(100.0, busy, 6);
        Assert.Equal(0.0, idle, 6);
    }

    [Fact]
    public async Task A_reading_that_is_too_old_is_not_used_as_the_baseline_of_a_rate()
    {
        FakeSystemSources sources = Computer();
        var time = new ManualTime();
        using WindowsSystemMetricProvider provider = Provider(sources, time);
        await SampleAsync(provider, "cpu.usage");

        sources.Times = new SystemTimes(Idle: 600, Kernel: 1000, User: 500);
        time.Now += TimeSpan.FromMinutes(10);
        IReadOnlyDictionary<string, object?> stale = await SampleAsync(provider, "cpu.usage");

        Assert.Null(stale["cpu.usage"]);
    }

    [Fact]
    public async Task Cpu_usage_is_unavailable_when_windows_cannot_tell_and_comes_back_afterwards()
    {
        FakeSystemSources sources = Computer();
        sources.Times = null;
        var time = new ManualTime();
        using WindowsSystemMetricProvider provider = Provider(sources, time);

        Assert.Null((await SampleAsync(provider, "cpu.usage"))["cpu.usage"]);

        sources.Times = new SystemTimes(0, 0, 0);
        await SampleAsync(provider, "cpu.usage");
        sources.Times = new SystemTimes(Idle: 100, Kernel: 200, User: 100);
        time.Now += TimeSpan.FromSeconds(2);
        Assert.Equal(66.666666, Assert.IsType<double>((await SampleAsync(provider, "cpu.usage"))["cpu.usage"]), 5);
    }

    [Fact]
    public async Task With_the_warm_up_a_newly_subscribed_cpu_metric_has_a_value_at_the_first_sample()
    {
        FakeSystemSources sources = Computer();
        sources.AutoAdvance = (Idle: 300, Kernel: 500, User: 500);
        using WindowsSystemMetricProvider provider = Provider(
            sources,
            new ManualTime(),
            new SystemMetricOptions { RefreshInterval = Timeout.InfiniteTimeSpan, RateWarmup = TimeSpan.FromMilliseconds(5) });

        IReadOnlyDictionary<string, object?> values = await SampleAsync(provider, "cpu.usage");

        // Every reading adds 1000 units of which 300 are idle.
        Assert.Equal(70.0, Assert.IsType<double>(values["cpu.usage"]), 6);
    }

    [Fact]
    public async Task Memory_is_reported_as_used_percent_used_and_total()
    {
        using WindowsSystemMetricProvider provider = Provider(Computer(), new ManualTime());

        IReadOnlyDictionary<string, object?> values = await SampleAsync(provider, "memory.used_percent", "memory.used", "memory.total");

        Assert.Equal(75.0, Assert.IsType<double>(values["memory.used_percent"]), 6);
        Assert.Equal(12.0, Assert.IsType<double>(values["memory.used"]), 6);
        Assert.Equal(16.0, Assert.IsType<double>(values["memory.total"]), 6);
    }

    [Fact]
    public async Task Memory_is_unavailable_when_windows_cannot_tell()
    {
        FakeSystemSources sources = Computer();
        sources.Memory = null;
        using WindowsSystemMetricProvider provider = Provider(sources, new ManualTime());

        IReadOnlyDictionary<string, object?> values = await SampleAsync(provider, "memory.used_percent", "memory.used", "memory.total");

        Assert.All(values.Values, value => Assert.Null(value));
        Assert.Equal(3, values.Count);
    }

    // ------------------------------------------------------------------
    // Volumes
    // ------------------------------------------------------------------

    [Fact]
    public async Task Every_fixed_volume_has_a_free_and_a_used_percent_metric_by_drive_letter()
    {
        FakeSystemSources sources = Computer();
        sources.Volumes = [Volume('C', 500, 125), Volume('D', 1000, 900)];
        using WindowsSystemMetricProvider provider = Provider(sources, new ManualTime());

        IReadOnlyDictionary<string, object?> values = await SampleAsync(provider, "disk.c.free", "disk.c.used_percent", "disk.d.free", "disk.d.used_percent");

        Assert.Equal(125.0, Assert.IsType<double>(values["disk.c.free"]), 6);
        Assert.Equal(75.0, Assert.IsType<double>(values["disk.c.used_percent"]), 6);
        Assert.Equal(900.0, Assert.IsType<double>(values["disk.d.free"]), 6);
        Assert.Equal(10.0, Assert.IsType<double>(values["disk.d.used_percent"]), 6);
        Assert.Equal("Drive D: free space", Descriptor(provider, "disk.d.free").Name);
    }

    [Fact]
    public async Task A_volume_that_appears_or_disappears_changes_the_metrics_and_raises_descriptors_changed()
    {
        FakeSystemSources sources = Computer();
        using WindowsSystemMetricProvider provider = Provider(sources, new ManualTime());
        int changes = 0;
        provider.DescriptorsChanged += (_, _) => Interlocked.Increment(ref changes);

        provider.Refresh();
        Assert.Equal(0, Volatile.Read(ref changes));
        Assert.DoesNotContain(provider.Describe(), descriptor => descriptor.Id.StartsWith("disk.e.", StringComparison.Ordinal));

        sources.Volumes = [Volume('C', 500, 125), Volume('E', 2000, 1500)];
        provider.Refresh();
        Assert.Equal(1, Volatile.Read(ref changes));
        Assert.Contains(provider.Describe(), descriptor => descriptor.Id == "disk.e.free");
        Assert.Contains(provider.Describe(), descriptor => descriptor.Id == "disk.e.used_percent");

        // Looking again without a change raises nothing.
        provider.Refresh();
        Assert.Equal(1, Volatile.Read(ref changes));

        // Until the removal is noticed the metric is unavailable instead of showing the value of a volume that is gone.
        sources.Volumes = [Volume('C', 500, 125)];
        Assert.Null((await SampleAsync(provider, "disk.e.free"))["disk.e.free"]);
        provider.Refresh();
        Assert.Equal(2, Volatile.Read(ref changes));
        Assert.DoesNotContain(provider.Describe(), descriptor => descriptor.Id.StartsWith("disk.e.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_timer_notices_a_new_volume_without_anybody_calling_refresh()
    {
        FakeSystemSources sources = Computer();
        using WindowsSystemMetricProvider provider = Provider(
            sources,
            new ManualTime(),
            new SystemMetricOptions { RefreshInterval = TimeSpan.FromMilliseconds(20), RateWarmup = TimeSpan.Zero });
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.DescriptorsChanged += (_, _) => changed.TrySetResult();

        sources.Volumes = [Volume('C', 500, 125), Volume('F', 100, 50)];

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains(provider.Describe(), descriptor => descriptor.Id == "disk.f.free");
    }

    // ------------------------------------------------------------------
    // Network
    // ------------------------------------------------------------------

    [Fact]
    public async Task Network_rates_are_the_byte_difference_per_second_in_kilobytes()
    {
        FakeSystemSources sources = Computer();
        sources.Adapters = [Adapter("{A}", "Ethernet", received: 1_000_000, sent: 500_000)];
        var time = new ManualTime();
        using WindowsSystemMetricProvider provider = Provider(sources, time);

        IReadOnlyDictionary<string, object?> first = await SampleAsync(provider, "net.ethernet.rx", "net.ethernet.tx");
        sources.Adapters = [Adapter("{A}", "Ethernet", received: 3_000_000, sent: 1_000_000)];
        time.Now += TimeSpan.FromSeconds(2);
        IReadOnlyDictionary<string, object?> second = await SampleAsync(provider, "net.ethernet.rx", "net.ethernet.tx");

        Assert.Null(first["net.ethernet.rx"]);
        Assert.Null(first["net.ethernet.tx"]);
        Assert.Equal(1000.0, Assert.IsType<double>(second["net.ethernet.rx"]), 6);
        Assert.Equal(250.0, Assert.IsType<double>(second["net.ethernet.tx"]), 6);
    }

    [Fact]
    public async Task A_counter_that_went_backwards_or_an_adapter_that_went_down_is_unavailable()
    {
        FakeSystemSources sources = Computer();
        sources.Adapters = [Adapter("{A}", "Ethernet", received: 5_000_000, sent: 5_000_000)];
        var time = new ManualTime();
        using WindowsSystemMetricProvider provider = Provider(sources, time);
        await SampleAsync(provider, "net.ethernet.rx");

        sources.Adapters = [Adapter("{A}", "Ethernet", received: 100, sent: 100)];
        time.Now += TimeSpan.FromSeconds(2);
        IReadOnlyDictionary<string, object?> reset = await SampleAsync(provider, "net.ethernet.rx", "net.ethernet.tx");

        sources.Adapters = [];
        time.Now += TimeSpan.FromSeconds(2);
        IReadOnlyDictionary<string, object?> down = await SampleAsync(provider, "net.ethernet.rx", "net.ethernet.tx");

        Assert.Null(reset["net.ethernet.rx"]);
        Assert.Null(reset["net.ethernet.tx"]);
        Assert.Null(down["net.ethernet.rx"]);
        Assert.Null(down["net.ethernet.tx"]);
    }

    [Fact]
    public void A_new_adapter_adds_its_metrics_and_adapters_with_the_same_name_get_distinct_ids()
    {
        FakeSystemSources sources = Computer();
        sources.Adapters = [Adapter("{B}", "Ethernet"), Adapter("{A}", "Ethernet"), Adapter("{C}", "Wi-Fi")];
        using WindowsSystemMetricProvider provider = Provider(sources, new ManualTime());
        int changes = 0;
        provider.DescriptorsChanged += (_, _) => Interlocked.Increment(ref changes);

        string[] ids = [.. provider.Describe().Select(descriptor => descriptor.Id).Where(id => id.StartsWith("net.", StringComparison.Ordinal) && id != "net.ipv4")];

        // Sorted by adapter id, so {A} is "ethernet" and {B} is "ethernet_2" whatever order Windows lists them in.
        Assert.Equal(
            ["net.ethernet.rx", "net.ethernet.tx", "net.ethernet_2.rx", "net.ethernet_2.tx", "net.wi_fi.rx", "net.wi_fi.tx"],
            ids.Order(StringComparer.Ordinal));

        sources.Adapters = [.. sources.Adapters, Adapter("{D}", "USB Ethernet")];
        provider.Refresh();
        Assert.Equal(1, Volatile.Read(ref changes));
        Assert.Contains(provider.Describe(), descriptor => descriptor.Id == "net.usb_ethernet.rx");
    }

    // ------------------------------------------------------------------
    // The rest
    // ------------------------------------------------------------------

    [Fact]
    public async Task Address_battery_operating_system_and_pending_restart_come_from_windows()
    {
        FakeSystemSources sources = Computer();
        using WindowsSystemMetricProvider provider = Provider(sources, new ManualTime());

        IReadOnlyDictionary<string, object?> values = await SampleAsync(
            provider,
            "net.ipv4",
            "battery.level",
            "battery.charging",
            "system.os_version",
            "system.pending_reboot");

        Assert.Equal("192.168.1.20", values["net.ipv4"]);
        Assert.Equal(80.0, values["battery.level"]);
        Assert.Equal(true, values["battery.charging"]);
        Assert.Equal("Windows 11 Pro 24H2 (10.0.26100.4061)", values["system.os_version"]);
        Assert.Equal(false, values["system.pending_reboot"]);
    }

    [Fact]
    public async Task Uptime_and_last_boot_follow_the_uptime_and_the_clock()
    {
        FakeSystemSources sources = Computer();
        sources.Uptime = TimeSpan.FromHours(30);
        var time = new ManualTime();
        using WindowsSystemMetricProvider provider = Provider(sources, time);

        IReadOnlyDictionary<string, object?> values = await SampleAsync(provider, "system.uptime", "system.last_boot");

        Assert.Equal(TimeSpan.FromHours(30).TotalSeconds, values["system.uptime"]);
        Assert.Equal(time.Now - TimeSpan.FromHours(30), Assert.IsType<DateTimeOffset>(values["system.last_boot"]));
    }

    [Fact]
    public async Task The_gpu_is_unavailable_without_counters_and_its_baseline_is_taken_once_with_the_warm_up()
    {
        FakeSystemSources sources = Computer();
        sources.Gpu = null;
        using WindowsSystemMetricProvider provider = Provider(
            sources,
            new ManualTime(),
            new SystemMetricOptions { RefreshInterval = Timeout.InfiniteTimeSpan, RateWarmup = TimeSpan.FromMilliseconds(1) });

        Assert.Null((await SampleAsync(provider, "gpu.usage"))["gpu.usage"]);
        Assert.Equal(2, sources.GpuReads);

        sources.Gpu = 37.5;
        Assert.Equal(37.5, (await SampleAsync(provider, "gpu.usage"))["gpu.usage"]);
        Assert.Equal(3, sources.GpuReads);
    }

    [Fact]
    public async Task Unknown_ids_are_ignored_and_a_metric_that_nobody_asks_for_is_not_read()
    {
        FakeSystemSources sources = Computer();
        sources.Gpu = 10;
        using WindowsSystemMetricProvider provider = Provider(sources, new ManualTime());

        IReadOnlyDictionary<string, object?> values = await SampleAsync(provider, "does.not.exist", "memory.total");

        Assert.Equal(["memory.total"], values.Keys);
        Assert.Equal(0, sources.GpuReads);
    }

    // ------------------------------------------------------------------
    // Boot time
    // ------------------------------------------------------------------

    [Fact]
    public void The_boot_id_is_fixed_at_the_start_and_the_boot_time_ignores_noise_but_follows_a_clock_correction()
    {
        var time = new ManualTime();
        TimeSpan uptime = TimeSpan.FromHours(30);
        var boot = new WindowsBootTime(time, () => uptime);
        string bootId = boot.BootId;
        DateTimeOffset first = boot.BootTime;

        // Measurement noise of a few seconds changes neither.
        time.Now += TimeSpan.FromSeconds(7);
        uptime += TimeSpan.FromSeconds(7) + TimeSpan.FromMilliseconds(900);
        Assert.Equal(first, boot.BootTime);

        // The time service corrects the clock by ten minutes: the boot time follows, the boot id does not.
        time.Now += TimeSpan.FromMinutes(10);
        DateTimeOffset corrected = boot.BootTime;
        Assert.True((corrected - (first + TimeSpan.FromMinutes(10))).Duration() <= TimeSpan.FromSeconds(2));
        Assert.Equal(bootId, boot.BootId);
        Assert.StartsWith("win-", bootId, StringComparison.Ordinal);
    }

    [Fact]
    public void A_different_start_of_the_operating_system_gives_a_different_boot_id()
    {
        var time = new ManualTime();
        var first = new WindowsBootTime(time, () => TimeSpan.FromHours(30));
        var second = new WindowsBootTime(time, () => TimeSpan.FromHours(2));
        var sameStart = new WindowsBootTime(time, () => TimeSpan.FromHours(30));

        Assert.NotEqual(first.BootId, second.BootId);
        Assert.Equal(first.BootId, sameStart.BootId);
    }
}
