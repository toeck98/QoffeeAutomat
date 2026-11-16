using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Manifest;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Platform.Windows.Metrics.SystemMetrics;
using QoffeeAutomat.Platform.Windows.Service;
using static QoffeeAutomat.Platform.Windows.Tests.Metrics.SystemMetricTestData;
using LinkManifest = QoffeeAutomat.Contracts.Link.Manifest;

namespace QoffeeAutomat.Platform.Windows.Tests.Metrics;

/// <summary>
/// The helpers behind the system metrics, the manifest revision when volumes come and go, and the real Windows sources
/// (PLAN.md S06, T304). The tests of the real sources run on Windows only.
/// </summary>
[Trait("Category", "SystemMetrics")]
public sealed class SystemMetricSourcesTests
{
    // ------------------------------------------------------------------
    // Helpers that run everywhere
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Ethernet", "ethernet")]
    [InlineData("Wi-Fi", "wi_fi")]
    [InlineData("Ethernet 2", "ethernet_2")]
    [InlineData("vEthernet (Default Switch)", "vethernet_default_switch")]
    [InlineData("LAN-Verbindung*", "lan_verbindung")]
    [InlineData("  --  ", "adapter")]
    [InlineData("", "adapter")]
    public void The_slug_of_an_adapter_is_a_valid_part_of_a_metric_id(string name, string expected)
    {
        Assert.Equal(expected, SystemMetricIds.AdapterSlug(name));
    }

    [Fact]
    public void The_ids_of_volume_and_adapter_metrics_are_lower_case_and_follow_the_plan()
    {
        Assert.Equal("disk.c.free", SystemMetricIds.DiskFree('C'));
        Assert.Equal("disk.d.used_percent", SystemMetricIds.DiskUsedPercent('D'));
        Assert.Equal("net.ethernet.rx", SystemMetricIds.NetworkReceive("ethernet"));
        Assert.Equal("net.ethernet.tx", SystemMetricIds.NetworkTransmit("ethernet"));
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet, "Ethernet", "Intel(R) Ethernet Connection I219-V", true)]
    [InlineData(NetworkInterfaceType.GigabitEthernet, "Ethernet 2", "Realtek PCIe GbE Family Controller", true)]
    [InlineData(NetworkInterfaceType.Wireless80211, "Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", true)]
    [InlineData(NetworkInterfaceType.Ethernet, "vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", false)]
    [InlineData(NetworkInterfaceType.Ethernet, "Ethernet 3", "TAP-Windows Adapter V9", false)]
    [InlineData(NetworkInterfaceType.Ethernet, "Local Area Connection* 1", "Microsoft Wi-Fi Direct Virtual Adapter", false)]
    [InlineData(NetworkInterfaceType.Ethernet, "VMware Network Adapter VMnet1", "VMware Virtual Ethernet Adapter for VMnet1", false)]
    [InlineData(NetworkInterfaceType.Ethernet, "Proton VPN", "WireGuard Tunnel", false)]
    [InlineData(NetworkInterfaceType.Loopback, "Loopback Pseudo-Interface 1", "Software Loopback Interface 1", false)]
    [InlineData(NetworkInterfaceType.Tunnel, "Teredo Tunneling Pseudo-Interface", "Teredo Tunneling Pseudo-Interface", false)]
    [InlineData(NetworkInterfaceType.Ppp, "Dial-up", "WAN Miniport (PPPOE)", false)]
    public void Only_physical_adapters_are_listed(NetworkInterfaceType type, string name, string description, bool expected)
    {
        Assert.Equal(expected, NetworkAdapterFilter.IsPhysical(type, name, description));
    }

    [Fact]
    public void The_primary_address_is_the_one_of_the_adapter_that_leads_to_the_network()
    {
        AdapterAddresses virtualSwitch = new(IsUp: true, IsPhysical: false, IsLoopbackOrTunnel: false, HasGateway: false, ["172.28.0.1"]);
        AdapterAddresses wifi = new(IsUp: true, IsPhysical: true, IsLoopbackOrTunnel: false, HasGateway: true, ["192.168.1.20"]);
        AdapterAddresses ethernet = new(IsUp: true, IsPhysical: true, IsLoopbackOrTunnel: false, HasGateway: false, ["10.0.0.5"]);
        AdapterAddresses loopback = new(IsUp: true, IsPhysical: false, IsLoopbackOrTunnel: true, HasGateway: false, ["127.0.0.1"]);
        AdapterAddresses down = new(IsUp: false, IsPhysical: true, IsLoopbackOrTunnel: false, HasGateway: true, ["192.168.9.9"]);
        AdapterAddresses linkLocal = new(IsUp: true, IsPhysical: true, IsLoopbackOrTunnel: false, HasGateway: true, ["169.254.3.4"]);

        Assert.Equal("192.168.1.20", NetworkAddressSelector.SelectPrimary([loopback, virtualSwitch, ethernet, wifi, down]));
        Assert.Equal("10.0.0.5", NetworkAddressSelector.SelectPrimary([virtualSwitch, ethernet]));
        Assert.Equal("172.28.0.1", NetworkAddressSelector.SelectPrimary([virtualSwitch, loopback]));
        Assert.Null(NetworkAddressSelector.SelectPrimary([loopback, down, linkLocal]));
        Assert.Null(NetworkAddressSelector.SelectPrimary([]));
    }

    [Fact]
    public void The_description_of_the_operating_system_names_windows_11_correctly()
    {
        Version fallback = new(10, 0, 19045);

        Assert.Equal("Windows 11 Pro 24H2 (10.0.26100.4061)", OsVersionFormatter.Format("Windows 10 Pro", "24H2", 26100, 4061, fallback));
        Assert.Equal("Windows 10 Home 22H2 (10.0.19045.5131)", OsVersionFormatter.Format("Windows 10 Home", "22H2", 19045, 5131, fallback));
        Assert.Equal("Windows Server 2025 Standard (10.0.26100)", OsVersionFormatter.Format("Windows Server 2025 Standard", null, 26100, null, fallback));
        Assert.Equal("Windows (10.0.19045)", OsVersionFormatter.Format(null, "", null, null, fallback));
    }

    [Fact]
    public void The_engine_of_a_gpu_counter_instance_is_the_instance_without_the_process()
    {
        Assert.Equal(
            "luid_0x00000000_0x0000F3A1_phys_0_eng_0_engtype_3D",
            GpuUsageReader.EngineKey("pid_4711_luid_0x00000000_0x0000F3A1_phys_0_eng_0_engtype_3D"));
        Assert.Equal("unexpected", GpuUsageReader.EngineKey("unexpected"));
    }

    [Fact]
    public void The_platform_info_takes_its_boot_id_from_the_boot_time_that_the_metric_reports()
    {
        var time = new ManualTime();
        var boot = new WindowsBootTime(time, () => TimeSpan.FromHours(30));

        using var info = new WindowsPlatformInfo(Path.Combine(Path.GetTempPath(), "qa-agent-id-unused.txt"), time, bootTime: boot);

        Assert.Equal(boot.BootId, info.BootId);
        Assert.Equal(
            "win-" + (boot.BootTime.ToUnixTimeSeconds() / 60).ToString(System.Globalization.CultureInfo.InvariantCulture),
            info.BootId);
    }

    // ------------------------------------------------------------------
    // The manifest follows the volumes
    // ------------------------------------------------------------------

    [Fact]
    public async Task Plugging_in_or_removing_a_volume_changes_the_manifest_revision()
    {
        var time = new ManualTime();
        var sources = new FakeSystemSources { Volumes = [Volume('C', 500, 125)], Times = new SystemTimes(0, 0, 0) };
        using var temp = new TempDirectory();
        using var store = new ConfigStore(
            new ConfigStoreOptions
            {
                ConfigDirectory = Path.Combine(temp.Path, "config"),
                ScriptsDirectory = Path.Combine(temp.Path, "scripts"),
                EnableWatcher = false,
            },
            time);
        await store.LoadAsync(CancellationToken.None);
        using WindowsSystemMetricProvider provider = Provider(sources, time);
        using var registry = new CommandRegistry(store);
        await using var engine = new MetricEngine(store, [provider], time);
        using var manifest = new ManifestService(
            registry,
            engine,
            () => new AgentInfo("5b0c6c1e-8e0a-4f6e-9a57-3d2f3a1c9b77", "Gaming-PC", "1.0.0", "windows", "x64", "GAMING-PC", ["A4:B1:C1:00:11:22"], "10.0.26200", new Uri("https://gaming-pc.local:8481/")));
        var announced = new List<string>();
        manifest.Changed += (_, change) =>
        {
            lock (announced)
            {
                announced.Add(change.Rev);
            }
        };
        string withC = manifest.Current.Rev;
        Assert.DoesNotContain(manifest.Current.Metrics, metric => metric.Id == "disk.e.free");

        // A USB disk appears: the provider notices, the engine rebuilds its catalog, the manifest has a new revision.
        sources.Volumes = [Volume('C', 500, 125), Volume('E', 2000, 1500)];
        provider.Refresh();
        await engine.PollAsync(CancellationToken.None);
        LinkManifest withE = manifest.Current;

        Assert.NotEqual(withC, withE.Rev);
        Assert.Contains(withE.Metrics, metric => metric.Id == "disk.e.free");
        Assert.Contains(withE.Metrics, metric => metric.Id == "disk.e.used_percent");

        // And it is gone again.
        sources.Volumes = [Volume('C', 500, 125)];
        provider.Refresh();
        await engine.PollAsync(CancellationToken.None);
        LinkManifest withoutE = manifest.Current;

        Assert.NotEqual(withE.Rev, withoutE.Rev);
        Assert.DoesNotContain(withoutE.Metrics, metric => metric.Id.StartsWith("disk.e.", StringComparison.Ordinal));
        Assert.Contains(withoutE.Metrics, metric => metric.Id == "disk.c.free");

        // The link server learns about both changes through the manifest service (manifest_changed).
        lock (announced)
        {
            Assert.Equal([withE.Rev, withoutE.Rev], announced);
        }
    }

    // ------------------------------------------------------------------
    // The real Windows
    // ------------------------------------------------------------------

    [WindowsFact]
    public async Task The_real_sources_return_plausible_values_or_nothing()
    {
        var sources = new WindowsSystemSources();

        SystemTimes? before = sources.ReadSystemTimes();
        await Task.Delay(300);
        SystemTimes? after = sources.ReadSystemTimes();
        MemoryStatus? memory = sources.ReadMemory();
        IReadOnlyList<VolumeStatus> volumes = sources.ReadVolumes();
        IReadOnlyList<NetworkAdapterStatus> adapters = sources.ReadAdapters();
        string? address = sources.ReadPrimaryIPv4();
        BatteryStatus? battery = sources.ReadBattery();
        string? os = sources.ReadOsVersion();
        _ = sources.ReadPendingReboot();

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.True(after.Value.Kernel >= before.Value.Kernel && after.Value.User >= before.Value.User);
        Assert.NotNull(memory);
        Assert.True(memory.Value.TotalBytes > 0 && memory.Value.AvailableBytes <= memory.Value.TotalBytes);
        VolumeStatus system = Assert.Single(volumes, volume => volume.Letter == char.ToUpperInvariant(Path.GetPathRoot(Environment.SystemDirectory)![0]));
        Assert.True(system.TotalBytes > 0 && system.FreeBytes >= 0 && system.FreeBytes <= system.TotalBytes);
        Assert.All(volumes, volume => Assert.True(volume.TotalBytes > 0 && volume.FreeBytes <= volume.TotalBytes));
        Assert.All(adapters, adapter =>
        {
            Assert.False(string.IsNullOrWhiteSpace(adapter.Id));
            Assert.False(string.IsNullOrWhiteSpace(adapter.Name));
            Assert.True(adapter.ReceivedBytes >= 0 && adapter.SentBytes >= 0);
        });
        if (address is not null)
        {
            Assert.True(IPAddress.TryParse(address, out IPAddress? parsed) && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        }

        if (battery is { LevelPercent: { } level })
        {
            Assert.InRange(level, 0, 100);
        }

        Assert.False(string.IsNullOrWhiteSpace(os));
        Assert.Contains("Windows", os, StringComparison.Ordinal);
        Assert.InRange(sources.ReadUptime(), TimeSpan.FromSeconds(1), TimeSpan.FromDays(3650));
    }

    [WindowsFact]
    public async Task Every_metric_of_the_real_provider_has_a_plausible_value_or_is_unavailable()
    {
        using var provider = new WindowsSystemMetricProvider(
            new WindowsSystemSources(),
            TimeProvider.System,
            options: new SystemMetricOptions { RefreshInterval = Timeout.InfiniteTimeSpan, RateWarmup = TimeSpan.FromMilliseconds(300) });
        MetricDescriptor[] descriptors = [.. provider.Describe()];
        string[] ids = [.. descriptors.Select(descriptor => descriptor.Id)];

        IReadOnlyDictionary<string, object?> values = await provider.SampleAsync(ids, CancellationToken.None);

        Assert.Equal(ids.Order(StringComparer.Ordinal), values.Keys.Order(StringComparer.Ordinal));
        foreach (MetricDescriptor descriptor in descriptors)
        {
            object? value = values[descriptor.Id];
            if (value is null)
            {
                // A clean unavailable state is allowed for anything that depends on the hardware or on counters.
                continue;
            }

            switch (descriptor.ValueType)
            {
                case "number":
                    double number = Assert.IsType<double>(value);
                    Assert.True(double.IsFinite(number) && number >= 0, descriptor.Id + " = " + number);
                    if (descriptor.Unit == "%")
                    {
                        Assert.InRange(number, 0, 100);
                    }

                    break;
                case "bool":
                    Assert.IsType<bool>(value);
                    break;
                case "timestamp":
                    DateTimeOffset moment = Assert.IsType<DateTimeOffset>(value);
                    Assert.True(moment < DateTimeOffset.UtcNow && moment > DateTimeOffset.UtcNow.AddYears(-20), descriptor.Id + " = " + moment);
                    break;
                default:
                    Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(value)), descriptor.Id);
                    break;
            }
        }

        // What every Windows computer can tell.
        foreach (string id in new[] { "cpu.usage", "memory.used_percent", "memory.used", "memory.total", "system.uptime", "system.last_boot", "system.os_version" })
        {
            Assert.NotNull(values[id]);
        }
    }

    [WindowsFact]
    public async Task Sampling_the_metrics_once_per_interval_stays_within_the_cpu_budget()
    {
        using var provider = new WindowsSystemMetricProvider(
            new WindowsSystemSources(),
            TimeProvider.System,
            options: new SystemMetricOptions { RefreshInterval = Timeout.InfiniteTimeSpan, RateWarmup = TimeSpan.Zero });
        MetricDescriptor[] descriptors = [.. provider.Describe()];
        string[] ids = [.. descriptors.Select(descriptor => descriptor.Id)];

        // Two passes give the rates their baselines and warm up the counters and the JIT.
        await provider.SampleAsync(ids, CancellationToken.None);
        await Task.Delay(300);
        await provider.SampleAsync(ids, CancellationToken.None);

        // PLAN.md S14: an idle agent with 20 subscribed metrics uses less than 1 % of one core. The share of one core that the
        // provider takes is the sum over the metrics of (time to sample it) / (interval at which it is sampled).
        const int passes = 5;
        double load = 0;
        foreach (MetricDescriptor descriptor in descriptors.Where(item => item.IntervalSeconds > 0))
        {
            var watch = Stopwatch.StartNew();
            for (int pass = 0; pass < passes; pass++)
            {
                await provider.SampleAsync([descriptor.Id], CancellationToken.None);
            }

            watch.Stop();
            load += watch.Elapsed.TotalSeconds / passes / descriptor.IntervalSeconds!.Value;
        }

        Assert.True(load < 0.01, $"Sampling {descriptors.Length} metrics at their intervals would use {load:P2} of one core; the budget is 1 %.");
    }
}
