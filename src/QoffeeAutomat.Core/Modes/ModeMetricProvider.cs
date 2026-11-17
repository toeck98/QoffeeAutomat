using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Modes;

/// <summary>
/// Reports the current mode of every enabled mode group that is exposed to Home Assistant as the enum metric <c>mode.&lt;groupId&gt;</c>
/// (PLAN.md S06, D-037, T717). The metric is event-driven: whoever changes a state asks the metric engine for a fresh sample (the host
/// connects <see cref="ModeEngine.StateChanged"/> to <c>MetricEngine.RequestSample</c>), so Home Assistant sees the new mode when the enter steps
/// have finished, not before. While the state is unknown the metric has no value and is unavailable. The set of metrics follows the
/// groups: a group that is created, edited, enabled, disabled or deleted makes the engine describe the provider again, which changes
/// the manifest revision.
/// </summary>
public sealed class ModeMetricProvider : IDynamicMetricProvider, IDisposable
{
    private const string DefaultIcon = "mdi:monitor-dashboard";

    private readonly ModeEngine _modes;

    /// <summary>Creates the provider.</summary>
    /// <param name="modes">The mode engine.</param>
    public ModeMetricProvider(ModeEngine modes)
    {
        ArgumentNullException.ThrowIfNull(modes);

        _modes = modes;
        _modes.GroupsChanged += OnGroupsChanged;
    }

    /// <inheritdoc />
    public event EventHandler? DescriptorsChanged;

    /// <inheritdoc />
    public IReadOnlyList<MetricDescriptor> Describe()
    {
        return
        [
            .. _modes.Groups
                .Where(group => group.Enabled && group.ExposeToHa)
                .Select(group => new MetricDescriptor(
                    ModeNames.MetricId(group.Id),
                    group.Name,
                    "sensor",
                    "enum",
                    Icon: group.Icon ?? DefaultIcon,
                    Options: [.. group.Modes.Select(mode => mode.Id)],
                    EnabledDefault: true,
                    IntervalSeconds: 0,
                    Context: "system")),
        ];
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyDictionary<string, object?>> SampleAsync(IReadOnlyCollection<string> metricIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metricIds);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string metricId in metricIds)
        {
            if (metricId.StartsWith(ModeNames.MetricPrefix, StringComparison.Ordinal))
            {
                string groupId = metricId[ModeNames.MetricPrefix.Length..];
                if (_modes.Find(groupId) is not null)
                {
                    values[metricId] = _modes.GetCurrent(groupId);
                }
            }
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, object?>>(values);
    }

    /// <summary>Stops following the mode engine.</summary>
    public void Dispose()
    {
        _modes.GroupsChanged -= OnGroupsChanged;
    }

    private void OnGroupsChanged(object? sender, EventArgs e)
    {
        DescriptorsChanged?.Invoke(this, EventArgs.Empty);
    }
}
