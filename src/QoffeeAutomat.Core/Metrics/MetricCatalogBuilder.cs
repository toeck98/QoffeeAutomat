using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Metrics;

/// <summary>A metric together with the provider that samples it; the provider is null when nobody can (a custom sensor without sampler).</summary>
internal sealed record CatalogItem(MetricCatalogEntry Entry, IMetricProvider? Owner);

/// <summary>
/// Merges provider descriptors with the overrides of <c>config/metrics.json</c> and adds the custom
/// sensors of <c>config/sensors/</c> (PLAN.md S06). Pure function of its inputs, so the engine can
/// rebuild the catalog whenever the configuration changes.
/// </summary>
internal static class MetricCatalogBuilder
{
    public const string CustomPrefix = "custom.";

    public static List<CatalogItem> Build(
        List<(MetricDescriptor Descriptor, IMetricProvider Owner)> builtIns,
        IMetricProvider? customSensors,
        IConfigView config,
        MetricEngineOptions options)
    {
        MetricOverridesDocument? overrides = config.GetDocument<MetricOverridesDocument>(ConfigIds.MetricOverrides);
        var items = new List<CatalogItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach ((MetricDescriptor descriptor, IMetricProvider owner) in builtIns)
        {
            if (!seen.Add(descriptor.Id))
            {
                continue;
            }

            MetricOverride? metricOverride = null;
            overrides?.Overrides.TryGetValue(descriptor.Id, out metricOverride);
            items.Add(new CatalogItem(MergeBuiltIn(descriptor, metricOverride, options), owner));
        }

        foreach (SensorDocument sensor in config.List<SensorDocument>())
        {
            string id = CustomPrefix + sensor.Id;
            if (seen.Add(id))
            {
                items.Add(new CatalogItem(FromSensor(id, sensor, options), customSensors));
            }
        }

        return items;
    }

    private static MetricCatalogEntry MergeBuiltIn(MetricDescriptor descriptor, MetricOverride? metricOverride, MetricEngineOptions options)
    {
        double interval = NormalizeInterval(metricOverride?.IntervalSeconds ?? descriptor.IntervalSeconds, options);
        double? deadband = NormalizeDeadband(metricOverride?.Deadband ?? descriptor.Deadband);
        MetricDescriptor merged = descriptor with { IntervalSeconds = interval, Deadband = deadband };
        return new MetricCatalogEntry(
            merged,
            metricOverride?.Enabled ?? true,
            metricOverride?.ExposeToHa ?? true,
            interval,
            deadband,
            IsCustom: false);
    }

    /// <summary>The metric of a custom sensor document, with the interval that really applies (script sensors: at least <see cref="MetricEngineOptions.MinimumScriptIntervalSeconds"/>).</summary>
    public static MetricCatalogEntry FromSensor(string id, SensorDocument sensor, MetricEngineOptions options)
    {
        double interval = EffectiveSensorInterval(sensor, options);
        double? deadband = NormalizeDeadband(sensor.Deadband);
        var descriptor = new MetricDescriptor(
            id,
            sensor.Name,
            sensor.Kind,
            sensor.ValueType,
            Unit: sensor.Unit,
            DeviceClass: sensor.DeviceClass,
            StateClass: sensor.StateClass,
            EntityCategory: sensor.EntityCategory,
            Icon: sensor.Icon,
            Precision: sensor.Precision,
            Options: sensor.Options,
            EnabledDefault: sensor.EnabledDefault,
            IntervalSeconds: interval,
            Deadband: deadband,
            Context: "system");
        return new MetricCatalogEntry(descriptor, sensor.Enabled, sensor.ExposeToHa, interval, deadband, IsCustom: true);
    }

    /// <summary>
    /// The sampling interval of a custom sensor: null and invalid values become the default, zero stays zero
    /// (event-driven), positive values get the minimum, which is higher for script sensors.
    /// </summary>
    public static double EffectiveSensorInterval(SensorDocument sensor, MetricEngineOptions options)
    {
        bool isScript = sensor.Source is PowerShellSensorSource;
        double minimum = isScript
            ? Math.Max(options.MinimumIntervalSeconds, options.MinimumScriptIntervalSeconds)
            : options.MinimumIntervalSeconds;
        double interval = NormalizeInterval(sensor.IntervalSeconds, options, minimum);

        // The default must respect the minimum of a script sensor as well.
        return interval > 0 ? Math.Max(interval, minimum) : interval;
    }

    /// <summary>Null and invalid values become the default, zero stays zero (event-driven), positive values get the minimum.</summary>
    private static double NormalizeInterval(double? seconds, MetricEngineOptions options)
    {
        return NormalizeInterval(seconds, options, options.MinimumIntervalSeconds);
    }

    private static double NormalizeInterval(double? seconds, MetricEngineOptions options, double minimum)
    {
        if (seconds is not { } value || !double.IsFinite(value) || value < 0)
        {
            return options.DefaultIntervalSeconds;
        }

        if (value == 0)
        {
            return 0;
        }

        return Math.Max(value, minimum);
    }

    private static double? NormalizeDeadband(double? deadband)
    {
        return deadband is { } value && double.IsFinite(value) && value > 0 ? value : null;
    }
}

/// <summary>Exposes the user metrics of the session bridge as a provider (context <c>user</c>).</summary>
internal sealed class SessionMetricProvider : IMetricProvider
{
    private readonly ISessionBridge _bridge;

    public SessionMetricProvider(ISessionBridge bridge)
    {
        _bridge = bridge;
    }

    public IReadOnlyList<MetricDescriptor> Describe()
    {
        return _bridge.DescribeUserMetrics();
    }

    public ValueTask<IReadOnlyDictionary<string, object?>> SampleAsync(
        IReadOnlyCollection<string> metricIds,
        CancellationToken cancellationToken)
    {
        return _bridge.SampleUserMetricsAsync(metricIds, cancellationToken);
    }
}
