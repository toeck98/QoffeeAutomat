using System.Globalization;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Linux.Metrics.SystemMetrics;

/// <summary>Settings of <see cref="LinuxSystemMetricProvider"/>.</summary>
public sealed class LinuxSystemMetricOptions
{
    /// <summary>
    /// How often the volumes, network adapters and the battery are looked at to find out whether the set of metrics changed;
    /// <see cref="Timeout.InfiniteTimeSpan"/> turns the check off (tests call <see cref="LinuxSystemMetricProvider.Refresh"/>).
    /// </summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A rate (CPU, network) needs two readings. When there is no recent first reading, the provider takes one and waits this
    /// long before it takes the second, so that a newly subscribed metric has a value at once instead of after its interval.
    /// Zero skips the wait: the first sample then has no value.
    /// </summary>
    public TimeSpan RateWarmup { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>A first reading older than this is not used for a rate (nobody was sampling for a while); the provider takes a new one.</summary>
    public TimeSpan BaselineStale { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// The built-in system metrics of a Linux computer (PLAN.md S06, S17, T801): CPU usage, memory, free and used space of every
/// local file system, receive and transmit rate of every physical network interface that is up, primary IPv4 address, battery
/// (only on computers that have one), uptime, last boot, distribution, and pending restart. The ids are those of the Windows
/// provider where the meaning is the same (see <see cref="LinuxMetricIds"/>); the GPU usage has no Linux counterpart yet.
/// <list type="bullet">
/// <item>Volumes and interfaces come and go (a USB disk, a docking station). The provider looks again every <see cref="LinuxSystemMetricOptions.RefreshInterval"/>, and when the set differs it raises <see cref="DescriptorsChanged"/>, so that the metric engine rebuilds its catalog and the manifest revision changes.</item>
/// <item>Whatever Linux cannot deliver is unavailable (null); one broken source never affects the others.</item>
/// <item>Rates are computed from the difference of two readings; see <see cref="LinuxSystemMetricOptions.RateWarmup"/>.</item>
/// <item>All timing uses the injected <see cref="TimeProvider"/>; all values come from <see cref="ILinuxSystemSources"/>.</item>
/// </list>
/// </summary>
public sealed class LinuxSystemMetricProvider : IDynamicMetricProvider, IDisposable
{
    private const double BytesPerGibibyte = 1024.0 * 1024.0 * 1024.0;
    private const double BytesPerKilobyte = 1000.0;
    private const string Diagnostic = "diagnostic";
    private const string Measurement = "measurement";
    private const string DiskPrefix = "disk.";

    private static readonly string[] MemoryIds = [LinuxMetricIds.MemoryUsedPercent, LinuxMetricIds.MemoryUsed, LinuxMetricIds.MemoryTotal];

    private readonly object _inventoryGate = new();
    private readonly object _sampleGate = new();
    private readonly ILinuxSystemSources _sources;
    private readonly TimeProvider _time;
    private readonly LinuxBootTime _boot;
    private readonly LinuxSystemMetricOptions _options;
    private readonly ITimer? _refreshTimer;
    private readonly Dictionary<string, NetworkBaseline> _networkBaselines = new(StringComparer.Ordinal);
    private Inventory _inventory;
    private MetricDescriptor[] _descriptors;
    private CpuBaseline? _cpuBaseline;

    /// <summary>Creates the provider and reads which volumes, interfaces and battery exist.</summary>
    /// <param name="sources">Reads Linux.</param>
    /// <param name="time">The clock for the rates and the check for changes.</param>
    /// <param name="boot">Knows when the computer started; null creates one from <paramref name="time"/> and <paramref name="sources"/>.</param>
    /// <param name="options">Tuning; null uses the defaults.</param>
    public LinuxSystemMetricProvider(
        ILinuxSystemSources sources,
        TimeProvider time,
        LinuxBootTime? boot = null,
        LinuxSystemMetricOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(time);

        _sources = sources;
        _time = time;
        _boot = boot ?? new LinuxBootTime(time, sources.ReadUptime, sources.ReadKernelBootId());
        _options = options ?? new LinuxSystemMetricOptions();
        _inventory = ReadInventory();
        _descriptors = BuildDescriptors(_inventory);

        if (_options.RefreshInterval != Timeout.InfiniteTimeSpan)
        {
            _refreshTimer = time.CreateTimer(_ => RefreshQuietly(), null, _options.RefreshInterval, _options.RefreshInterval);
        }
    }

    /// <inheritdoc />
    public event EventHandler? DescriptorsChanged;

    /// <inheritdoc />
    public IReadOnlyList<MetricDescriptor> Describe()
    {
        lock (_inventoryGate)
        {
            return _descriptors;
        }
    }

    /// <summary>
    /// Looks at the volumes, interfaces and battery again. When one appeared or disappeared, <see cref="Describe"/> returns the
    /// new set and <see cref="DescriptorsChanged"/> is raised. The provider calls this on a timer; it can also be called when
    /// something is known to have changed.
    /// </summary>
    public void Refresh()
    {
        Inventory next = ReadInventory();
        bool changed;
        lock (_inventoryGate)
        {
            changed = !string.Equals(next.Signature, _inventory.Signature, StringComparison.Ordinal);
            if (changed)
            {
                _inventory = next;
                _descriptors = BuildDescriptors(next);
            }
        }

        if (changed)
        {
            DescriptorsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, object?>> SampleAsync(
        IReadOnlyCollection<string> metricIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metricIds);

        var wanted = new HashSet<string>(metricIds, StringComparer.Ordinal);
        Inventory inventory;
        lock (_inventoryGate)
        {
            inventory = _inventory;
        }

        // The readings are quick but they are file reads: keep them off the thread of the metric engine.
        bool warmUp = await Task.Run(() => TakeBaselines(wanted, inventory), cancellationToken).ConfigureAwait(false);
        if (warmUp)
        {
            await Task.Delay(_options.RateWarmup, _time, cancellationToken).ConfigureAwait(false);
        }

        return await Task.Run(() => SampleCore(wanted, inventory), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the check for changes.</summary>
    public void Dispose()
    {
        _refreshTimer?.Dispose();
    }

    private static string UniqueSlug(string slug, HashSet<string> used)
    {
        string candidate = slug;
        for (int number = 2; !used.Add(candidate); number++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{slug}_{number}");
        }

        return candidate;
    }

    private static double? Percent(double part, double whole)
    {
        return whole > 0 ? Math.Clamp(part / whole * 100.0, 0, 100) : null;
    }

    private static double? Rate(long current, long before, double seconds)
    {
        long difference = current - before;
        return difference >= 0 ? difference / seconds / BytesPerKilobyte : null;
    }

    private static MetricDescriptor Number(
        string id,
        string name,
        string translationKey,
        string? unit,
        double interval,
        int precision,
        string? deviceClass = null,
        double? deadband = null,
        string? entityCategory = Diagnostic,
        string? icon = null,
        string? stateClass = Measurement)
    {
        return new MetricDescriptor(
            id,
            name,
            "sensor",
            "number",
            TranslationKey: translationKey,
            Unit: unit,
            DeviceClass: deviceClass,
            StateClass: stateClass,
            EntityCategory: entityCategory,
            Icon: icon,
            Precision: precision,
            EnabledDefault: true,
            IntervalSeconds: interval,
            Deadband: deadband,
            Context: "system");
    }

    private static MetricDescriptor Text(string id, string name, string translationKey, string? deviceClass, double interval, string? icon)
    {
        return new MetricDescriptor(
            id,
            name,
            "sensor",
            deviceClass == "timestamp" ? "timestamp" : "string",
            TranslationKey: translationKey,
            DeviceClass: deviceClass,
            EntityCategory: Diagnostic,
            Icon: icon,
            EnabledDefault: true,
            IntervalSeconds: interval,
            Context: "system");
    }

    private static MetricDescriptor Flag(string id, string name, string translationKey, string? deviceClass, double interval, string? icon, string? entityCategory)
    {
        return new MetricDescriptor(
            id,
            name,
            "binary_sensor",
            "bool",
            TranslationKey: translationKey,
            DeviceClass: deviceClass,
            EntityCategory: entityCategory,
            Icon: icon,
            EnabledDefault: true,
            IntervalSeconds: interval,
            Context: "system");
    }

    private static MetricDescriptor[] BuildDescriptors(Inventory inventory)
    {
        var descriptors = new List<MetricDescriptor>
        {
            Number(LinuxMetricIds.CpuUsage, "CPU usage", "cpu_usage", "%", interval: 2, precision: 0, deadband: 1, icon: "mdi:cpu-64-bit"),
            Number(LinuxMetricIds.MemoryUsedPercent, "Memory usage", "memory_used_percent", "%", interval: 5, precision: 0, deadband: 1, icon: "mdi:memory"),
            Number(LinuxMetricIds.MemoryUsed, "Memory used", "memory_used", "GiB", interval: 5, precision: 2, deviceClass: "data_size", deadband: 0.05, icon: "mdi:memory"),
            Number(LinuxMetricIds.MemoryTotal, "Memory total", "memory_total", "GiB", interval: 0, precision: 2, deviceClass: "data_size", stateClass: null, icon: "mdi:memory"),
        };

        foreach (VolumeEntry volume in inventory.Volumes)
        {
            descriptors.Add(Number(LinuxMetricIds.DiskFree(volume.Slug), $"Volume {volume.MountPoint} free space", "disk_free", "GiB", interval: 30, precision: 1, deviceClass: "data_size", deadband: 0.1, icon: "mdi:harddisk"));
            descriptors.Add(Number(LinuxMetricIds.DiskUsedPercent(volume.Slug), $"Volume {volume.MountPoint} used", "disk_used_percent", "%", interval: 30, precision: 1, deadband: 0.5, icon: "mdi:harddisk"));
        }

        foreach (AdapterEntry adapter in inventory.Adapters)
        {
            descriptors.Add(Number(LinuxMetricIds.NetworkReceive(adapter.Slug), $"{adapter.Name} receive rate", "net_receive", "kB/s", interval: 2, precision: 1, deviceClass: "data_rate", deadband: 1, icon: "mdi:download-network"));
            descriptors.Add(Number(LinuxMetricIds.NetworkTransmit(adapter.Slug), $"{adapter.Name} transmit rate", "net_transmit", "kB/s", interval: 2, precision: 1, deviceClass: "data_rate", deadband: 1, icon: "mdi:upload-network"));
        }

        descriptors.Add(Text(LinuxMetricIds.PrimaryIPv4, "IPv4 address", "net_ipv4", null, interval: 30, icon: "mdi:ip-network"));

        if (inventory.HasBattery)
        {
            descriptors.Add(Number(LinuxMetricIds.BatteryLevel, "Battery level", "battery_level", "%", interval: 30, precision: 0, deviceClass: "battery", deadband: 1, entityCategory: null));
            descriptors.Add(Flag(LinuxMetricIds.BatteryCharging, "Battery charging", "battery_charging", "battery_charging", interval: 30, icon: null, entityCategory: null));
        }

        descriptors.Add(Number(LinuxMetricIds.Uptime, "Uptime", "system_uptime", "s", interval: 60, precision: 0, deviceClass: "duration", stateClass: null, icon: "mdi:timer-outline"));
        descriptors.Add(Text(LinuxMetricIds.LastBoot, "Last boot", "system_last_boot", "timestamp", interval: 0, icon: "mdi:restart"));
        descriptors.Add(Text(LinuxMetricIds.OsVersion, "Operating system", "system_os_version", null, interval: 0, icon: "mdi:linux"));
        descriptors.Add(Flag(LinuxMetricIds.PendingReboot, "Restart pending", "system_pending_reboot", null, interval: 60, icon: "mdi:restart-alert", entityCategory: Diagnostic));
        return [.. descriptors];
    }

    private Inventory ReadInventory()
    {
        // Sorted, so that the slugs of volumes and interfaces that collide do not depend on the order Linux lists them in.
        var usedVolumeSlugs = new HashSet<string>(StringComparer.Ordinal);
        var volumes = new List<VolumeEntry>();
        foreach (VolumeStatus volume in _sources.ReadVolumes().OrderBy(item => item.MountPoint, StringComparer.Ordinal))
        {
            volumes.Add(new VolumeEntry(volume.MountPoint, UniqueSlug(LinuxMetricIds.MountSlug(volume.MountPoint), usedVolumeSlugs)));
        }

        var usedAdapterSlugs = new HashSet<string>(StringComparer.Ordinal);
        var adapters = new List<AdapterEntry>();
        foreach (NetworkAdapterStatus adapter in _sources.ReadAdapters().OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            adapters.Add(new AdapterEntry(adapter.Id, adapter.Name, UniqueSlug(LinuxMetricIds.AdapterSlug(adapter.Name), usedAdapterSlugs)));
        }

        return new Inventory(volumes, adapters, _sources.ReadBattery() is not null);
    }

    private void RefreshQuietly()
    {
        try
        {
            Refresh();
        }
#pragma warning disable CA1031 // A timer callback must not throw; the next tick looks again.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Nothing to do: the metrics keep their previous set until a reading succeeds.
        }
    }

    private bool IsFresh(DateTimeOffset? takenAt, DateTimeOffset now)
    {
        return takenAt is { } at && now >= at && now - at <= _options.BaselineStale;
    }

    /// <summary>Takes the first reading of every rate that has no recent one; true when the caller has to wait before it samples.</summary>
    private bool TakeBaselines(HashSet<string> wanted, Inventory inventory)
    {
        if (_options.RateWarmup <= TimeSpan.Zero)
        {
            return false;
        }

        DateTimeOffset now = _time.GetUtcNow();
        bool taken = false;
        lock (_sampleGate)
        {
            if (wanted.Contains(LinuxMetricIds.CpuUsage) && !IsFresh(_cpuBaseline?.At, now) && _sources.ReadCpuTimes() is { } times)
            {
                _cpuBaseline = new CpuBaseline(times, now);
                taken = true;
            }

            bool rates = inventory.Adapters.Any(adapter =>
                wanted.Contains(LinuxMetricIds.NetworkReceive(adapter.Slug)) || wanted.Contains(LinuxMetricIds.NetworkTransmit(adapter.Slug)));
            if (rates && inventory.Adapters.Any(adapter => !IsFresh(_networkBaselines.GetValueOrDefault(adapter.Id)?.At, now)))
            {
                RememberAdapters(_sources.ReadAdapters(), now);
                taken = true;
            }
        }

        return taken;
    }

    private void RememberAdapters(IReadOnlyList<NetworkAdapterStatus> adapters, DateTimeOffset now)
    {
        _networkBaselines.Clear();
        foreach (NetworkAdapterStatus adapter in adapters)
        {
            _networkBaselines[adapter.Id] = new NetworkBaseline(adapter.ReceivedBytes, adapter.SentBytes, now);
        }
    }

    private Dictionary<string, object?> SampleCore(HashSet<string> wanted, Inventory inventory)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        DateTimeOffset now = _time.GetUtcNow();
        lock (_sampleGate)
        {
            if (wanted.Contains(LinuxMetricIds.CpuUsage))
            {
                values[LinuxMetricIds.CpuUsage] = SampleCpu(now);
            }

            if (MemoryIds.Any(wanted.Contains))
            {
                SampleMemory(wanted, values);
            }

            if (wanted.Any(id => id.StartsWith(DiskPrefix, StringComparison.Ordinal)))
            {
                SampleVolumes(wanted, inventory, values);
            }

            if (inventory.Adapters.Any(adapter =>
                wanted.Contains(LinuxMetricIds.NetworkReceive(adapter.Slug)) || wanted.Contains(LinuxMetricIds.NetworkTransmit(adapter.Slug))))
            {
                SampleNetwork(wanted, inventory, now, values);
            }

            if (wanted.Contains(LinuxMetricIds.PrimaryIPv4))
            {
                values[LinuxMetricIds.PrimaryIPv4] = _sources.ReadPrimaryIPv4();
            }

            if (wanted.Contains(LinuxMetricIds.BatteryLevel) || wanted.Contains(LinuxMetricIds.BatteryCharging))
            {
                BatteryStatus? battery = _sources.ReadBattery();
                if (wanted.Contains(LinuxMetricIds.BatteryLevel))
                {
                    values[LinuxMetricIds.BatteryLevel] = battery?.LevelPercent;
                }

                if (wanted.Contains(LinuxMetricIds.BatteryCharging))
                {
                    values[LinuxMetricIds.BatteryCharging] = battery?.IsCharging;
                }
            }

            if (wanted.Contains(LinuxMetricIds.Uptime))
            {
                values[LinuxMetricIds.Uptime] = _sources.ReadUptime().TotalSeconds;
            }

            if (wanted.Contains(LinuxMetricIds.LastBoot))
            {
                values[LinuxMetricIds.LastBoot] = _boot.BootTime;
            }

            if (wanted.Contains(LinuxMetricIds.OsVersion))
            {
                values[LinuxMetricIds.OsVersion] = _sources.ReadOsVersion();
            }

            if (wanted.Contains(LinuxMetricIds.PendingReboot))
            {
                values[LinuxMetricIds.PendingReboot] = _sources.ReadPendingReboot();
            }
        }

        return values;
    }

    private double? SampleCpu(DateTimeOffset now)
    {
        CpuTimes? reading = _sources.ReadCpuTimes();
        CpuBaseline? previous = _cpuBaseline;
        if (reading is not { } current)
        {
            _cpuBaseline = null;
            return null;
        }

        _cpuBaseline = new CpuBaseline(current, now);
        if (previous is null || !IsFresh(previous.At, now) || current.Total < previous.Times.Total || current.Idle < previous.Times.Idle)
        {
            return null;
        }

        ulong total = current.Total - previous.Times.Total;
        ulong idle = current.Idle - previous.Times.Idle;
        return total == 0 ? null : Percent(total - Math.Min(idle, total), total);
    }

    private void SampleMemory(HashSet<string> wanted, Dictionary<string, object?> values)
    {
        if (_sources.ReadMemory() is not { } memory)
        {
            foreach (string id in MemoryIds)
            {
                SetIfWanted(wanted, values, id, null);
            }

            return;
        }

        double used = memory.TotalBytes - (double)Math.Min(memory.AvailableBytes, memory.TotalBytes);
        SetIfWanted(wanted, values, LinuxMetricIds.MemoryUsedPercent, Percent(used, memory.TotalBytes));
        SetIfWanted(wanted, values, LinuxMetricIds.MemoryUsed, used / BytesPerGibibyte);
        SetIfWanted(wanted, values, LinuxMetricIds.MemoryTotal, memory.TotalBytes / BytesPerGibibyte);
    }

    private void SampleVolumes(HashSet<string> wanted, Inventory inventory, Dictionary<string, object?> values)
    {
        Dictionary<string, VolumeStatus> current = [];
        foreach (VolumeStatus volume in _sources.ReadVolumes())
        {
            current[volume.MountPoint] = volume;
        }

        foreach (VolumeEntry entry in inventory.Volumes)
        {
            current.TryGetValue(entry.MountPoint, out VolumeStatus? volume);
            double? free = volume?.FreeBytes / BytesPerGibibyte;
            double? usedPercent = volume is null ? null : Percent(volume.TotalBytes - volume.FreeBytes, volume.TotalBytes);
            SetIfWanted(wanted, values, LinuxMetricIds.DiskFree(entry.Slug), free);
            SetIfWanted(wanted, values, LinuxMetricIds.DiskUsedPercent(entry.Slug), usedPercent);
        }
    }

    private void SampleNetwork(HashSet<string> wanted, Inventory inventory, DateTimeOffset now, Dictionary<string, object?> values)
    {
        IReadOnlyList<NetworkAdapterStatus> current = _sources.ReadAdapters();
        foreach (AdapterEntry entry in inventory.Adapters)
        {
            double? receive = null;
            double? transmit = null;
            NetworkAdapterStatus? adapter = current.FirstOrDefault(item => string.Equals(item.Id, entry.Id, StringComparison.Ordinal));
            if (adapter is not null
                && _networkBaselines.TryGetValue(entry.Id, out NetworkBaseline? before)
                && IsFresh(before.At, now)
                && (now - before.At).TotalSeconds is var seconds and > 0)
            {
                receive = Rate(adapter.ReceivedBytes, before.Received, seconds);
                transmit = Rate(adapter.SentBytes, before.Sent, seconds);
            }

            SetIfWanted(wanted, values, LinuxMetricIds.NetworkReceive(entry.Slug), receive);
            SetIfWanted(wanted, values, LinuxMetricIds.NetworkTransmit(entry.Slug), transmit);
        }

        RememberAdapters(current, now);
    }

    private static void SetIfWanted(HashSet<string> wanted, Dictionary<string, object?> values, string id, double? value)
    {
        if (wanted.Contains(id))
        {
            values[id] = value;
        }
    }

    private sealed record CpuBaseline(CpuTimes Times, DateTimeOffset At);

    private sealed record NetworkBaseline(long Received, long Sent, DateTimeOffset At);

    private sealed record VolumeEntry(string MountPoint, string Slug);

    private sealed record AdapterEntry(string Id, string Name, string Slug);

    private sealed record Inventory(IReadOnlyList<VolumeEntry> Volumes, IReadOnlyList<AdapterEntry> Adapters, bool HasBattery)
    {
        /// <summary>Changes exactly when a volume, an interface (or its name) or the battery appears or disappears.</summary>
        public string Signature { get; } =
            string.Join('|', Volumes.Select(volume => volume.MountPoint + "=" + volume.Slug))
            + "#"
            + string.Join('|', Adapters.Select(adapter => adapter.Id + "=" + adapter.Slug + "=" + adapter.Name))
            + "#"
            + (HasBattery ? "battery" : "none");
    }
}
