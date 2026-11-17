using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Validation;
using QoffeeAutomat.Platform.Windows.Metrics.HardwareSensors;
using QoffeeAutomat.Platform.Windows.Tests.Actions;
using QoffeeAutomat.Platform.Windows.Tests.Sessions;

namespace QoffeeAutomat.Platform.Windows.Tests.Metrics.Hardware;

/// <summary>
/// The optional hardware sensors (PLAN.md D-023, D-045, ADR 0002, T707). The hardware library and the kernel driver cannot be used on a
/// build machine, so everything here runs against a fake source; the real library and the real driver are checked by hand on a Windows
/// desktop (human_verify). What these tests do prove is the part that the acceptance of T707 is about: with the setting off nothing
/// is opened, not even the library is loaded; turning it on opens the source once; turning it off disposes it.
/// </summary>
[Trait("Category", "Hardware")]
public sealed partial class HardwareSensorTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ManualTime _time = new();

    public void Dispose() => _temp.Dispose();

    // ---- With the setting off nothing is loaded -----------------------------------------------------------------------------------

    [Fact]
    public async Task With_no_setting_the_source_is_never_opened_and_the_driver_is_never_looked_at()
    {
        using ConfigStore store = await OpenStoreAsync();
        var factory = new FakeFactory();
        var probe = new FakeProbe(installed: true);
        using var provider = NewProvider(store, factory, probe);

        await provider.StartAsync(CancellationToken.None);
        provider.Reconcile();
        provider.Reconcile();
        IReadOnlyDictionary<string, object?> sampled = await provider.SampleAsync(["hardware.intelcpu.0.temperature.0"], CancellationToken.None);
        await provider.StopAsync(CancellationToken.None);

        Assert.Equal(0, factory.OpenCalls);
        Assert.Equal(0, probe.Calls);
        Assert.Empty(provider.Describe());
        Assert.Empty(sampled);
        HardwareSensorStatus status = provider.GetStatus();
        Assert.True(status.Supported);
        Assert.False(status.Enabled);
        Assert.Equal(HardwareSensorStates.Off, status.State);
        Assert.Null(status.DriverInstalled);
        Assert.Equal(0, status.SensorCount);
    }

    [Fact]
    public async Task With_the_setting_explicitly_off_the_source_is_never_opened()
    {
        using ConfigStore store = await OpenStoreAsync();
        await SaveAsync(store, new HardwareSensorSettings(false));
        var factory = new FakeFactory();
        using var provider = NewProvider(store, factory, new FakeProbe(installed: true));

        await provider.StartAsync(CancellationToken.None);
        provider.Reconcile();
        await provider.StopAsync(CancellationToken.None);

        Assert.Equal(0, factory.OpenCalls);
        Assert.Empty(provider.Describe());
    }

    [Fact]
    public async Task With_the_setting_off_the_real_wiring_does_not_load_the_hardware_library()
    {
        // The production factory and the production probe, with the setting off. Constructing and starting the provider, and everything
        // that the host does with it, must not load LibreHardwareMonitorLib (and so cannot reach the driver either).
        using ConfigStore store = await OpenStoreAsync();
        using var provider = new HardwareMetricProvider(
            store,
            new LibreHardwareSourceFactory(),
            new PawnIoDriverProbe(),
            _time,
            NullLogger.Instance,
            new HardwareSensorOptions { RefreshInterval = Timeout.InfiniteTimeSpan });

        await provider.StartAsync(CancellationToken.None);
        provider.Reconcile();
        _ = provider.Describe();
        _ = provider.GetStatus();
        _ = await provider.SampleAsync(["hardware.intelcpu.0.temperature.0"], CancellationToken.None);
        await provider.StopAsync(CancellationToken.None);

        Assert.Empty(LoadedHardwareAssemblies());
    }

    [Fact]
    public async Task The_container_offers_the_provider_as_status_source_with_the_real_factory_and_loads_nothing_while_the_setting_is_off()
    {
        using ConfigStore store = await OpenStoreAsync();
        using ServiceProvider services = WindowsPlatformTestHost.Build(
            _temp.Path,
            new FakePowerNativeApi(),
            new FakeElevatedNativeApi(),
            new FakeAcl(),
            collection => collection.AddSingleton<IConfigStore>(store));

        HardwareMetricProvider provider = services.GetRequiredService<HardwareMetricProvider>();
        IHardwareSensorStatusSource status = services.GetRequiredService<IHardwareSensorStatusSource>();
        IHardwareSourceFactory factory = services.GetRequiredService<IHardwareSourceFactory>();

        Assert.Same(provider, status);
        Assert.IsType<LibreHardwareSourceFactory>(factory);
        Assert.IsType<PawnIoDriverProbe>(services.GetRequiredService<IHardwareDriverProbe>());
        Assert.Equal(HardwareSensorStates.Off, status.GetStatus().State);
        Assert.Empty(provider.Describe());
        Assert.Empty(LoadedHardwareAssemblies());
    }

    // ---- Turning it on and off ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Turning_the_setting_on_opens_the_source_once_and_describes_the_sensors_without_enabling_them_by_default()
    {
        using ConfigStore store = await OpenStoreAsync();
        var source = new FakeSource(Cpu, Fan, Power, DriveTemperature);
        var factory = new FakeFactory(source);
        var probe = new FakeProbe(installed: true);
        using var provider = NewProvider(store, factory, probe);
        int changes = 0;
        provider.DescriptorsChanged += (_, _) => Interlocked.Increment(ref changes);
        await provider.StartAsync(CancellationToken.None);
        Assert.Equal(0, factory.OpenCalls);

        await SaveAsync(store, new HardwareSensorSettings(true));
        await TestWait.UntilAsync(() => provider.Describe().Count == 4, "the sensors");

        await TestWait.UntilAsync(() => Volatile.Read(ref changes) == 1, "the change notification");
        Assert.Equal(1, factory.OpenCalls);
        Dictionary<string, MetricDescriptor> byId = provider.Describe().ToDictionary(descriptor => descriptor.Id);

        MetricDescriptor temperature = byId["hardware.intelcpu.0.temperature.0"];
        Assert.Equal("Intel Core i7-12700K CPU Package temperature", temperature.Name);
        Assert.Equal("sensor", temperature.Kind);
        Assert.Equal("number", temperature.ValueType);
        Assert.Equal("°C", temperature.Unit);
        Assert.Equal("temperature", temperature.DeviceClass);
        Assert.Equal("measurement", temperature.StateClass);
        Assert.Equal("diagnostic", temperature.EntityCategory);
        Assert.Equal("system", temperature.Context);
        Assert.False(temperature.EnabledDefault);

        MetricDescriptor fan = byId["hardware.lpc.nct6798d.0.fan.1"];
        Assert.Equal("rpm", fan.Unit);
        Assert.Null(fan.DeviceClass);
        Assert.Equal("NCT6798D Fan #1", fan.Name);

        MetricDescriptor power = byId["hardware.intelcpu.0.power.0"];
        Assert.Equal("W", power.Unit);
        Assert.Equal("power", power.DeviceClass);

        Assert.All(provider.Describe(), descriptor =>
        {
            Assert.Matches(MetricIdPattern(), descriptor.Id);
            Assert.False(descriptor.EnabledDefault);
        });

        HardwareSensorStatus status = provider.GetStatus();
        Assert.True(status.Enabled);
        Assert.Equal(HardwareSensorStates.Running, status.State);
        Assert.True(status.DriverInstalled);
        Assert.Equal("PawnIO", status.Driver);
        Assert.Equal(4, status.SensorCount);

        // The settings change and the first look at the start can both reconcile; what matters is that the driver was looked at.
        Assert.True(probe.Calls >= 1);

        await provider.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_sample_returns_the_values_and_null_for_a_sensor_without_one()
    {
        using ConfigStore store = await OpenStoreAsync();
        var source = new FakeSource(Cpu, Fan with { Value = null }, Power with { Value = double.NaN }, DriveTemperature with { Value = 41.456 });
        using var provider = NewProvider(store, new FakeFactory(source), new FakeProbe(installed: true));
        await SaveAsync(store, new HardwareSensorSettings(true));
        await provider.StartAsync(CancellationToken.None);
        await TestWait.UntilAsync(() => provider.Describe().Count == 4, "the sensors");

        IReadOnlyDictionary<string, object?> values = await provider.SampleAsync(
            ["hardware.intelcpu.0.temperature.0", "hardware.lpc.nct6798d.0.fan.1", "hardware.intelcpu.0.power.0", "hardware.nvme.0.temperature.0", "cpu.usage"],
            CancellationToken.None);

        Assert.Equal(34.5, values["hardware.intelcpu.0.temperature.0"]);
        Assert.Null(values["hardware.lpc.nct6798d.0.fan.1"]);
        Assert.Null(values["hardware.intelcpu.0.power.0"]);
        Assert.Equal(41.46, values["hardware.nvme.0.temperature.0"]);
        Assert.False(values.ContainsKey("cpu.usage"));

        await provider.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Turning_the_setting_off_disposes_the_source_removes_the_metrics_and_turning_it_on_again_opens_a_new_one()
    {
        using ConfigStore store = await OpenStoreAsync();
        var first = new FakeSource(Cpu);
        var second = new FakeSource(Cpu);
        var factory = new FakeFactory(first, second);
        using var provider = NewProvider(store, factory, new FakeProbe(installed: true));
        int changes = 0;
        provider.DescriptorsChanged += (_, _) => Interlocked.Increment(ref changes);
        await provider.StartAsync(CancellationToken.None);
        await SaveAsync(store, new HardwareSensorSettings(true));
        await TestWait.UntilAsync(() => provider.Describe().Count == 1, "the sensor");
        Assert.False(first.Disposed);

        await SaveAsync(store, null);
        await TestWait.UntilAsync(() => provider.Describe().Count == 0, "the metrics to go");

        Assert.True(first.Disposed);
        await TestWait.UntilAsync(() => Volatile.Read(ref changes) == 2, "the change notification");
        HardwareSensorStatus off = provider.GetStatus();
        Assert.False(off.Enabled);
        Assert.Equal(HardwareSensorStates.Off, off.State);
        Assert.Null(off.DriverInstalled);
        Assert.Empty(await provider.SampleAsync(["hardware.intelcpu.0.temperature.0"], CancellationToken.None));

        await SaveAsync(store, new HardwareSensorSettings(true));
        await TestWait.UntilAsync(() => provider.Describe().Count == 1, "the sensor again");

        Assert.Equal(2, factory.OpenCalls);
        Assert.False(second.Disposed);
        await provider.StopAsync(CancellationToken.None);
        Assert.True(second.Disposed);
    }

    [Fact]
    public async Task Stopping_the_agent_disposes_an_open_source()
    {
        using ConfigStore store = await OpenStoreAsync();
        var source = new FakeSource(Cpu);
        using var provider = NewProvider(store, new FakeFactory(source), new FakeProbe(installed: true));
        await SaveAsync(store, new HardwareSensorSettings(true));
        await provider.StartAsync(CancellationToken.None);
        await TestWait.UntilAsync(() => provider.Describe().Count == 1, "the sensor");

        await provider.StopAsync(CancellationToken.None);

        Assert.True(source.Disposed);
        Assert.Empty(provider.Describe());
        provider.Reconcile();
        Assert.Equal(1, source.DisposeCalls);
    }

    // ---- Driver and failures ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_driver_is_reported_and_picked_up_when_it_appears()
    {
        using ConfigStore store = await OpenStoreAsync();
        var withoutDriver = new FakeSource(DriveTemperature);
        var withDriver = new FakeSource(DriveTemperature, Cpu);
        var factory = new FakeFactory(withoutDriver, withDriver);
        var probe = new FakeProbe(installed: false);
        using var provider = NewProvider(store, factory, probe);
        await SaveAsync(store, new HardwareSensorSettings(true));
        await provider.StartAsync(CancellationToken.None);
        await TestWait.UntilAsync(() => provider.GetStatus().State == HardwareSensorStates.DriverMissing, "the missing driver");

        HardwareSensorStatus missing = provider.GetStatus();
        Assert.False(missing.DriverInstalled);
        Assert.Equal(1, missing.SensorCount);
        Assert.Contains("PawnIO", missing.Message, StringComparison.Ordinal);

        // Without a change the refresh does not reopen anything.
        provider.Reconcile();
        Assert.Equal(1, factory.OpenCalls);

        probe.Installed = true;
        provider.Reconcile();

        Assert.True(withoutDriver.Disposed);
        Assert.Equal(2, factory.OpenCalls);
        HardwareSensorStatus running = provider.GetStatus();
        Assert.Equal(HardwareSensorStates.Running, running.State);
        Assert.Equal(2, running.SensorCount);
        await provider.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_library_that_cannot_be_opened_is_a_state_and_is_tried_again()
    {
        using ConfigStore store = await OpenStoreAsync();
        var factory = new FakeFactory(new FakeSource(Cpu)) { FailNext = new InvalidOperationException("The library is too old.") };
        using var provider = NewProvider(store, factory, new FakeProbe(installed: true));
        await SaveAsync(store, new HardwareSensorSettings(true));
        await provider.StartAsync(CancellationToken.None);
        await TestWait.UntilAsync(() => provider.GetStatus().State == HardwareSensorStates.Failed, "the failure");

        HardwareSensorStatus failed = provider.GetStatus();
        Assert.Equal("The library is too old.", failed.Message);
        Assert.Equal(0, failed.SensorCount);
        Assert.Empty(provider.Describe());

        provider.Reconcile();

        Assert.Equal(HardwareSensorStates.Running, provider.GetStatus().State);
        Assert.Single(provider.Describe());
        Assert.Equal(2, factory.OpenCalls);
        await provider.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_read_that_throws_closes_the_source_and_reports_the_failure()
    {
        using ConfigStore store = await OpenStoreAsync();
        var broken = new FakeSource(Cpu);
        var healthy = new FakeSource(Cpu);
        var factory = new FakeFactory(broken, healthy);
        using var provider = NewProvider(store, factory, new FakeProbe(installed: true));
        await SaveAsync(store, new HardwareSensorSettings(true));
        await provider.StartAsync(CancellationToken.None);
        await TestWait.UntilAsync(() => provider.Describe().Count == 1, "the sensor");

        broken.ReadFailure = new IOException("The driver does not answer.");
        provider.Reconcile();

        Assert.True(broken.Disposed);
        HardwareSensorStatus failed = provider.GetStatus();
        Assert.Equal(HardwareSensorStates.Failed, failed.State);
        Assert.Equal("The driver does not answer.", failed.Message);
        Assert.Empty(provider.Describe());

        provider.Reconcile();

        Assert.Equal(HardwareSensorStates.Running, provider.GetStatus().State);
        await provider.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_new_sensor_changes_the_descriptors_and_an_unchanged_set_does_not()
    {
        using ConfigStore store = await OpenStoreAsync();
        var source = new FakeSource(Cpu);
        using var provider = NewProvider(store, new FakeFactory(source), new FakeProbe(installed: true));
        await SaveAsync(store, new HardwareSensorSettings(true));
        await provider.StartAsync(CancellationToken.None);
        await TestWait.UntilAsync(() => provider.Describe().Count == 1, "the sensor");
        int changes = 0;
        provider.DescriptorsChanged += (_, _) => Interlocked.Increment(ref changes);

        provider.Reconcile();
        Assert.Equal(0, Volatile.Read(ref changes));

        source.Readings = [Cpu, DriveTemperature];
        provider.Reconcile();

        Assert.Equal(1, Volatile.Read(ref changes));
        Assert.Equal(2, provider.Describe().Count);
        await provider.StopAsync(CancellationToken.None);
    }

    // ---- Ids and names ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/intelcpu/0/temperature/0", "hardware.intelcpu.0.temperature.0")]
    [InlineData("/gpu-nvidia/0/power/0", "hardware.gpu-nvidia.0.power.0")]
    [InlineData("/lpc/NCT6798D/0/fan/1", "hardware.lpc.nct6798d.0.fan.1")]
    [InlineData("/nvme/0/temperature/0", "hardware.nvme.0.temperature.0")]
    [InlineData("/hdd/Disk 1/temperature/0", "hardware.hdd.disk_1.temperature.0")]
    public void The_metric_id_follows_the_identifier_of_the_sensor(string path, string expected)
    {
        string id = HardwareSensorIds.FromPath(path);

        Assert.Equal(expected, id);
        Assert.Matches(MetricIdPattern(), id);
    }

    [Fact]
    public void Paths_that_collapse_to_the_same_id_get_a_suffix()
    {
        HardwareSensorReading[] readings =
        [
            Cpu with { Path = "/hdd/a b/temperature/0" },
            Cpu with { Path = "/hdd/a_b/temperature/0" },
            Cpu with { Path = "/hdd/a:b/temperature/0" },
        ];

        IReadOnlyList<string> ids = HardwareSensorIds.Assign(readings);

        Assert.Equal(["hardware.hdd.a_b.temperature.0", "hardware.hdd.a_b.temperature.0_2", "hardware.hdd.a_b.temperature.0_3"], ids);
    }

    [Theory]
    [InlineData(HardwareSensorKind.Temperature, "CPU Package", "Intel CPU", "Intel CPU CPU Package temperature")]
    [InlineData(HardwareSensorKind.Temperature, "Temperature", "Samsung SSD 980", "Samsung SSD 980 Temperature")]
    [InlineData(HardwareSensorKind.Fan, "Fan #2", "NCT6798D", "NCT6798D Fan #2")]
    [InlineData(HardwareSensorKind.Fan, "GPU", "NVIDIA RTX 4070", "NVIDIA RTX 4070 GPU fan speed")]
    [InlineData(HardwareSensorKind.Power, "Package", "AMD Ryzen 7 5800X", "AMD Ryzen 7 5800X Package power")]
    [InlineData(HardwareSensorKind.Power, "GPU Power", "NVIDIA RTX 4070", "NVIDIA RTX 4070 GPU Power")]
    public void The_display_name_says_what_is_measured_once(HardwareSensorKind kind, string sensor, string hardware, string expected)
    {
        var reading = new HardwareSensorReading("/x/0/y/0", HardwareCategory.Cpu, hardware, sensor, kind, 1);

        Assert.Equal(expected, HardwareSensorIds.DisplayName(reading));
    }

    // ---- The library guard (no library needed) -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("0.9.5", true)]
    [InlineData("0.9.5.0", true)]
    [InlineData("0.9.6+abcdef", true)]
    [InlineData("0.10.0-beta.1", true)]
    [InlineData("1.0.0", true)]
    [InlineData("0.9.4", false)]
    [InlineData("0.9.4.0", false)]
    [InlineData("0.8.9", false)]
    [InlineData("", false)]
    [InlineData("not a version", false)]
    public void Only_a_library_without_the_winring0_driver_is_accepted(string informationalVersion, bool accepted)
    {
        Version? version = HardwareLibraryGuard.ParseVersion(informationalVersion, assemblyVersion: null);

        Assert.Equal(accepted, HardwareLibraryGuard.IsAcceptable(version));
    }

    [Fact]
    public void An_unknown_version_is_refused_and_the_assembly_version_is_the_fallback()
    {
        Assert.False(HardwareLibraryGuard.IsAcceptable(null));
        Assert.False(HardwareLibraryGuard.IsAcceptable(HardwareLibraryGuard.ParseVersion(null, new Version(0, 0, 0, 0))));
        Assert.True(HardwareLibraryGuard.IsAcceptable(HardwareLibraryGuard.ParseVersion(null, new Version(0, 9, 5, 0))));
        Assert.False(HardwareLibraryGuard.IsAcceptable(HardwareLibraryGuard.ParseVersion("garbage", new Version(0, 9, 4, 0))));
    }

    // ---- The setting ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_setting_is_off_unless_it_says_otherwise()
    {
        var none = new AgentSettingsDocument(DocumentSchema.CurrentVersion, "PC");
        var empty = none with { HardwareSensors = new HardwareSensorSettings() };
        var off = none with { HardwareSensors = new HardwareSensorSettings(false) };
        var on = none with { HardwareSensors = new HardwareSensorSettings(true) };

        Assert.False(none.EffectiveHardwareSensorsEnabled);
        Assert.False(empty.EffectiveHardwareSensorsEnabled);
        Assert.False(off.EffectiveHardwareSensorsEnabled);
        Assert.True(on.EffectiveHardwareSensorsEnabled);
        Assert.True(DocumentValidator.ValidateSettings(on).IsValid);
    }

    // ---- Support -----------------------------------------------------------------------------------------------------------------------

    private static readonly HardwareSensorReading Cpu = new("/intelcpu/0/temperature/0", HardwareCategory.Cpu, "Intel Core i7-12700K", "CPU Package", HardwareSensorKind.Temperature, 34.5);

    private static readonly HardwareSensorReading Fan = new("/lpc/nct6798d/0/fan/1", HardwareCategory.Mainboard, "NCT6798D", "Fan #1", HardwareSensorKind.Fan, 812);

    private static readonly HardwareSensorReading Power = new("/intelcpu/0/power/0", HardwareCategory.Cpu, "Intel Core i7-12700K", "CPU Package", HardwareSensorKind.Power, 18.2);

    private static readonly HardwareSensorReading DriveTemperature = new("/nvme/0/temperature/0", HardwareCategory.Storage, "Samsung SSD 980", "Temperature", HardwareSensorKind.Temperature, 38);

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z0-9_-]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex MetricIdPattern();

    private static string[] LoadedHardwareAssemblies()
    {
        return
        [
            .. AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetName().Name ?? string.Empty)
                .Where(name => name.StartsWith("LibreHardwareMonitor", StringComparison.OrdinalIgnoreCase)),
        ];
    }

    private HardwareMetricProvider NewProvider(ConfigStore store, FakeFactory factory, FakeProbe probe)
    {
        return new HardwareMetricProvider(
            store,
            factory,
            probe,
            _time,
            NullLogger.Instance,
            new HardwareSensorOptions { RefreshInterval = Timeout.InfiniteTimeSpan });
    }

    private async Task<ConfigStore> OpenStoreAsync()
    {
        var store = new ConfigStore(
            new ConfigStoreOptions
            {
                ConfigDirectory = Path.Combine(_temp.Path, "config"),
                ScriptsDirectory = Path.Combine(_temp.Path, "scripts"),
                EnableWatcher = false,
            },
            _time);
        await store.LoadAsync(CancellationToken.None);
        return store;
    }

    private static async Task SaveAsync(ConfigStore store, HardwareSensorSettings? hardware)
    {
        AgentSettingsDocument? current = store.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings);
        AgentSettingsDocument settings = (current ?? new AgentSettingsDocument(DocumentSchema.CurrentVersion, "Gaming-PC")) with { HardwareSensors = hardware };
        Assert.True(DocumentValidator.ValidateSettings(settings).IsValid);
        await store.Local.SaveAsync(settings, expectedRevision: current?.Revision, CancellationToken.None);
    }

    private sealed class FakeProbe(bool installed) : IHardwareDriverProbe
    {
        private int _calls;

        public bool Installed { get; set; } = installed;

        public int Calls => Volatile.Read(ref _calls);

        public string DriverName => "PawnIO";

        public bool IsInstalled()
        {
            Interlocked.Increment(ref _calls);
            return Installed;
        }
    }

    private sealed class FakeSource(params HardwareSensorReading[] readings) : IHardwareSource
    {
        private int _disposeCalls;

        public IReadOnlyList<HardwareSensorReading> Readings { get; set; } = readings;

        public Exception? ReadFailure { get; set; }

        public bool Disposed => Volatile.Read(ref _disposeCalls) > 0;

        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public IReadOnlyList<HardwareSensorReading> Read()
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            return ReadFailure is null ? Readings : throw ReadFailure;
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCalls);
    }

    private sealed class FakeFactory(params FakeSource[] sources) : IHardwareSourceFactory
    {
        private readonly Queue<FakeSource> _sources = new(sources);
        private int _openCalls;

        public int OpenCalls => Volatile.Read(ref _openCalls);

        /// <summary>When set, the next call to <see cref="Open"/> throws this once.</summary>
        public Exception? FailNext { get; set; }

        public IHardwareSource Open()
        {
            Interlocked.Increment(ref _openCalls);
            if (FailNext is { } failure)
            {
                FailNext = null;
                throw failure;
            }

            return _sources.Count > 0 ? _sources.Dequeue() : new FakeSource();
        }
    }
}
