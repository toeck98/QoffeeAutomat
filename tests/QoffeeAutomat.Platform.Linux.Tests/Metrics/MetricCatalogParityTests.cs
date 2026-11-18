using System.Reflection;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Platform.Linux.Metrics.Session;
using QoffeeAutomat.Platform.Linux.Metrics.SystemMetrics;
using QoffeeAutomat.Platform.Windows.Metrics.SystemMetrics;
using static QoffeeAutomat.Platform.Linux.Tests.SystemMetricTestData;

namespace QoffeeAutomat.Platform.Linux.Tests.Metrics;

/// <summary>
/// The Linux metric catalog against the Windows one (PLAN.md S06, S17, T801): a metric that means the same has the same id, so that
/// a command, a dashboard or a hub that refers to <c>cpu.usage</c> works on both systems. The Windows ids are the constants of the
/// Windows <c>SystemMetricIds</c>, compiled into this test assembly (see the project file).
/// </summary>
[Trait("Category", "SystemMetrics")]
public sealed class MetricCatalogParityTests
{
    /// <summary>
    /// The Windows metrics that have no Linux counterpart yet. Adding a Windows metric makes the tests below fail until it is either
    /// implemented for Linux or named here, so the two catalogs never drift apart by accident.
    /// </summary>
    private static readonly string[] WindowsOnly = [nameof(SystemMetricIds.GpuUsage)];

    /// <summary>The constants of <see cref="LinuxMetricIds"/> that are not metric ids.</summary>
    private static readonly string[] NotAMetricId = [nameof(LinuxMetricIds.RootVolumeSlug)];

    private static SortedDictionary<string, string> Constants(Type type)
    {
        var constants = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (field.IsLiteral && field.FieldType == typeof(string) && field.GetRawConstantValue() is string value)
            {
                constants[field.Name] = value;
            }
        }

        return constants;
    }

    [Fact]
    public void Every_windows_metric_id_exists_on_linux_with_the_same_name_and_value_unless_it_is_listed_as_windows_only()
    {
        SortedDictionary<string, string> windows = Constants(typeof(SystemMetricIds));
        SortedDictionary<string, string> linux = Constants(typeof(LinuxMetricIds));

        Assert.NotEmpty(windows);
        foreach ((string name, string id) in windows.Where(entry => !WindowsOnly.Contains(entry.Key, StringComparer.Ordinal)))
        {
            Assert.True(linux.TryGetValue(name, out string? linuxId), $"Linux has no metric id {name} (Windows: {id}).");
            Assert.Equal(id, linuxId);
        }
    }

    [Fact]
    public void The_windows_only_list_is_exact()
    {
        SortedDictionary<string, string> windows = Constants(typeof(SystemMetricIds));
        SortedDictionary<string, string> linux = Constants(typeof(LinuxMetricIds));

        string[] missingOnLinux = [.. windows.Keys.Where(name => !linux.ContainsKey(name))];
        string[] declared = [.. WindowsOnly.Order(StringComparer.Ordinal)];

        Assert.Equal(declared, missingOnLinux);
    }

    [Fact]
    public void Linux_has_no_metric_id_that_windows_lacks()
    {
        SortedDictionary<string, string> windows = Constants(typeof(SystemMetricIds));
        SortedDictionary<string, string> linux = Constants(typeof(LinuxMetricIds));

        string[] onlyOnLinux = [.. linux.Keys.Where(name => !windows.ContainsKey(name) && !NotAMetricId.Contains(name, StringComparer.Ordinal))];

        Assert.Empty(onlyOnLinux);
    }

    [Fact]
    public void The_per_volume_and_per_adapter_ids_follow_the_windows_patterns()
    {
        Assert.Equal(SystemMetricIds.DiskFree('c'), LinuxMetricIds.DiskFree("c"));
        Assert.Equal(SystemMetricIds.DiskUsedPercent('c'), LinuxMetricIds.DiskUsedPercent("c"));
        Assert.Equal(SystemMetricIds.NetworkReceive("ethernet"), LinuxMetricIds.NetworkReceive("ethernet"));
        Assert.Equal(SystemMetricIds.NetworkTransmit("ethernet"), LinuxMetricIds.NetworkTransmit("ethernet"));
    }

    [Theory]
    [InlineData("Ethernet")]
    [InlineData("Wi-Fi")]
    [InlineData("Intel(R) Ethernet Connection (7) I219-V")]
    [InlineData("enp3s0")]
    [InlineData("wlan0")]
    [InlineData("---")]
    [InlineData("")]
    public void The_adapter_slug_is_computed_like_on_windows(string name)
    {
        Assert.Equal(SystemMetricIds.AdapterSlug(name), LinuxMetricIds.AdapterSlug(name));
    }

    [Theory]
    [InlineData("/", "root")]
    [InlineData("/home", "home")]
    [InlineData("/boot/efi", "boot_efi")]
    [InlineData("/mnt/data disk", "mnt_data_disk")]
    [InlineData("/Mnt/USB-Stick_2", "mnt_usb_stick_2")]
    [InlineData("///", "volume")]
    public void A_mount_point_becomes_a_slug(string mountPoint, string expected)
    {
        Assert.Equal(expected, LinuxMetricIds.MountSlug(mountPoint));
    }

    [Fact]
    public void The_catalog_of_a_linux_computer_is_the_catalog_of_a_windows_computer_without_the_gpu()
    {
        // A volume at /d and an adapter called Ethernet: the slugs are the ones that Windows would build from drive D and from
        // the adapter Ethernet, so the whole set of ids can be derived with the Windows functions.
        var computer = new FakeLinuxSources
        {
            Volumes = [Volume("/d", 100, 50)],
            Adapters = [Adapter("Ethernet")],
            Battery = new BatteryStatus(50, IsCharging: false),
        };
        using LinuxSystemMetricProvider provider = Provider(computer, new ManualTime());

        List<string> expected =
        [
            .. Constants(typeof(SystemMetricIds)).Where(entry => !WindowsOnly.Contains(entry.Key, StringComparer.Ordinal)).Select(entry => entry.Value),
            SystemMetricIds.DiskFree('d'),
            SystemMetricIds.DiskUsedPercent('d'),
            SystemMetricIds.NetworkReceive("ethernet"),
            SystemMetricIds.NetworkTransmit("ethernet"),
        ];

        string[] expectedIds = [.. expected.Order(StringComparer.Ordinal)];
        string[] actualIds = [.. provider.Describe().Select(descriptor => descriptor.Id).Order(StringComparer.Ordinal)];
        Assert.Equal(expectedIds, actualIds);
    }

    [Fact]
    public void The_session_metrics_are_the_shared_ones_of_the_contracts()
    {
        var provider = new LinuxSessionMetricProvider(new FakeSessionSource([]));

        MetricDescriptor[] expected =
        [
            .. SessionMetricCatalog.AgentSide.Where(descriptor => descriptor.Id is SessionMetricIds.Locked or SessionMetricIds.User),
        ];

        MetricDescriptor[] actual = [.. provider.Describe()];
        string[] ids = [.. actual.Select(descriptor => descriptor.Id).Order(StringComparer.Ordinal)];
        string[] expectedIds = ["session.locked", "session.user"];

        Assert.Equal(expected, actual);
        Assert.Equal(expectedIds, ids);
    }
}
