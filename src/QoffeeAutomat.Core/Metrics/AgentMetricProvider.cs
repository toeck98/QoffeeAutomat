using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Updates;

namespace QoffeeAutomat.Core.Metrics;

/// <summary>The ids of the metrics that the agent reports about itself.</summary>
public static class AgentMetricIds
{
    /// <summary>True while the agent is paused (maintenance mode): every command is rejected with <c>paused</c>.</summary>
    public const string Paused = "agent.paused";

    /// <summary>The version of the running agent (T704).</summary>
    public const string Version = "agent.version";

    /// <summary>The newest published release; unavailable while it is not known (T704).</summary>
    public const string LatestVersion = "agent.latest_version";

    /// <summary>The address of the release page of <see cref="LatestVersion"/> (T704).</summary>
    public const string ReleaseUrl = "agent.release_url";

    /// <summary>The release notes of <see cref="LatestVersion"/>, shortened (T704).</summary>
    public const string ReleaseNotes = "agent.release_notes";

    /// <summary>The metrics that follow the <see cref="IUpdateStatusSource"/>.</summary>
    public static IReadOnlyList<string> Updates { get; } = [Version, LatestVersion, ReleaseUrl, ReleaseNotes];
}

/// <summary>
/// Reports the global pause flag (<see cref="IAgentPause"/>, maintenance mode) as the binary metric
/// <see cref="AgentMetricIds.Paused"/> (PLAN.md S06, T705), and, when it has an <see cref="IUpdateStatusSource"/>, the installed and the
/// newest version with the release page and notes (<see cref="AgentMetricIds.Version"/> and the three after it, S13, S16, T704). The
/// metrics are event-driven: whoever changes the flag or the status asks the metric engine for a fresh sample
/// (<c>MetricEngine.RequestSample</c>), so Home Assistant sees the change at once. The matching switch is the system command
/// <see cref="SystemCommands.Maintenance"/>, the matching update is <see cref="SystemCommands.AgentUpdate"/>. The version metrics are
/// disabled in Home Assistant by default: the update entity follows them.
/// </summary>
public sealed class AgentMetricProvider : IMetricProvider
{
    private static readonly MetricDescriptor PausedDescriptor = new(
        AgentMetricIds.Paused,
        "Maintenance mode",
        "binary_sensor",
        "bool",
        TranslationKey: "agent_paused",
        EntityCategory: "diagnostic",
        Icon: "mdi:wrench",
        EnabledDefault: true,
        IntervalSeconds: 0,
        Context: "system");

    private static readonly MetricDescriptor[] UpdateDescriptors =
    [
        TextMetric(AgentMetricIds.Version, "Agent version", "agent_version", "mdi:tag"),
        TextMetric(AgentMetricIds.LatestVersion, "Latest agent version", "agent_latest_version", "mdi:tag-arrow-up"),
        TextMetric(AgentMetricIds.ReleaseUrl, "Release page", "agent_release_url", "mdi:link-variant"),
        TextMetric(AgentMetricIds.ReleaseNotes, "Release notes", "agent_release_notes", "mdi:text-box-outline"),
    ];

    private readonly IAgentPause _pause;
    private readonly IUpdateStatusSource? _updates;

    /// <summary>Creates the provider.</summary>
    /// <param name="pause">The pause flag.</param>
    /// <param name="updates">The installed and the newest version; null leaves the version metrics out.</param>
    public AgentMetricProvider(IAgentPause pause, IUpdateStatusSource? updates = null)
    {
        ArgumentNullException.ThrowIfNull(pause);

        _pause = pause;
        _updates = updates;
    }

    /// <inheritdoc />
    public IReadOnlyList<MetricDescriptor> Describe() => _updates is null ? [PausedDescriptor] : [PausedDescriptor, .. UpdateDescriptors];

    /// <inheritdoc />
    public ValueTask<IReadOnlyDictionary<string, object?>> SampleAsync(
        IReadOnlyCollection<string> metricIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metricIds);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (metricIds.Contains(AgentMetricIds.Paused, StringComparer.Ordinal))
        {
            values[AgentMetricIds.Paused] = _pause.IsPaused;
        }

        if (_updates is not null)
        {
            UpdateStatus status = _updates.Current;
            AddIfAsked(values, metricIds, AgentMetricIds.Version, status.InstalledVersion);
            AddIfAsked(values, metricIds, AgentMetricIds.LatestVersion, status.LatestVersion);
            AddIfAsked(values, metricIds, AgentMetricIds.ReleaseUrl, status.ReleaseUrl);
            AddIfAsked(values, metricIds, AgentMetricIds.ReleaseNotes, status.ReleaseNotes);
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, object?>>(values);
    }

    private static void AddIfAsked(Dictionary<string, object?> values, IReadOnlyCollection<string> asked, string id, string? value)
    {
        if (asked.Contains(id, StringComparer.Ordinal))
        {
            values[id] = string.IsNullOrEmpty(value) ? null : value;
        }
    }

    private static MetricDescriptor TextMetric(string id, string name, string translationKey, string icon)
    {
        return new MetricDescriptor(
            id,
            name,
            "sensor",
            "string",
            TranslationKey: translationKey,
            EntityCategory: "diagnostic",
            Icon: icon,
            EnabledDefault: false,
            IntervalSeconds: 0,
            Context: "system");
    }
}
