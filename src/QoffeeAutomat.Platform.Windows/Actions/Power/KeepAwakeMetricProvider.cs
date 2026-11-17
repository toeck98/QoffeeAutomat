using Microsoft.Extensions.Hosting;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Windows.Actions.Power;

/// <summary>
/// The binary metric <c>power.keep_awake</c> (PLAN.md S06, S16, T715): true exactly while the agent holds the power request of
/// <see cref="KeepAwakeService"/>. It is the real state behind the Home Assistant switch of the default command
/// <c>power_keep_awake</c>, so the switch turns off by itself when the time is over. The metric is event-driven (interval 0): the
/// <see cref="KeepAwakeMetricRelay"/> asks the metric engine for a new sample whenever the state changes. It is on in Home Assistant
/// by default, because a switch that has a state metric needs it.
/// </summary>
public sealed class KeepAwakeMetricProvider : IMetricProvider
{
    // Written as an explicit descriptor instantiation so that scripts/docs/generate_reference.py finds it for the metrics reference.
    private static readonly MetricDescriptor[] Descriptors =
    [
        new MetricDescriptor(
            PowerActionSpec.KeepAwakeMetric,
            "Keep awake",
            "binary_sensor",
            "bool",
            TranslationKey: "power_keep_awake",
            Icon: "mdi:coffee",
            EnabledDefault: true,
            IntervalSeconds: 0,
            Context: "system"),
    ];

    private readonly KeepAwakeService _keepAwake;

    /// <summary>Creates the provider.</summary>
    /// <param name="keepAwake">The state that the metric reports.</param>
    public KeepAwakeMetricProvider(KeepAwakeService keepAwake)
    {
        ArgumentNullException.ThrowIfNull(keepAwake);

        _keepAwake = keepAwake;
    }

    /// <inheritdoc />
    public IReadOnlyList<MetricDescriptor> Describe() => Descriptors;

    /// <inheritdoc />
    public ValueTask<IReadOnlyDictionary<string, object?>> SampleAsync(
        IReadOnlyCollection<string> metricIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metricIds);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (metricIds.Contains(PowerActionSpec.KeepAwakeMetric, StringComparer.Ordinal))
        {
            values[PowerActionSpec.KeepAwakeMetric] = _keepAwake.IsOn;
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, object?>>(values);
    }
}

/// <summary>
/// Publishes a change of <see cref="KeepAwakeService"/> at once (a request for a fresh sample of <c>power.keep_awake</c>) and
/// gives the power request back when the agent stops, so the request really ends with the agent (T715).
/// </summary>
public sealed class KeepAwakeMetricRelay : IHostedService
{
    private readonly KeepAwakeService _keepAwake;
    private readonly Action<string>? _requestSample;

    /// <summary>Creates the relay.</summary>
    /// <param name="keepAwake">The service to follow.</param>
    /// <param name="requestSample">Asks the metric engine for a fresh sample of a metric (<c>MetricEngine.RequestSample</c>); null only gives the request back on stop.</param>
    public KeepAwakeMetricRelay(KeepAwakeService keepAwake, Action<string>? requestSample)
    {
        ArgumentNullException.ThrowIfNull(keepAwake);

        _keepAwake = keepAwake;
        _requestSample = requestSample;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _keepAwake.Changed += OnChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _keepAwake.Changed -= OnChanged;
        _keepAwake.Release();
        return Task.CompletedTask;
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        _requestSample?.Invoke(PowerActionSpec.KeepAwakeMetric);
    }
}
