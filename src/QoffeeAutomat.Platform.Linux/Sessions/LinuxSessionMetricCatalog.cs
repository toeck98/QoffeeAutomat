using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Session;

namespace QoffeeAutomat.Platform.Linux.Sessions;

/// <summary>
/// The desktop metrics that the Linux session helper samples (PLAN.md S06, S17, T803): the ids and descriptors are those of Windows
/// (<see cref="SessionMetricCatalog.SessionHostSide"/>), so Home Assistant sees the same entities. A metric that Windows has and the Linux
/// desktop cannot deliver is not listed and so is reported as unsupported by its absence: the title and process of the foreground window
/// (Wayland shows them to nobody), the microphone and webcam in use, the displays, and the audio devices. A listed metric is unavailable
/// (null) while the desktop lacks the tool that reads it (<c>wpctl</c> or <c>pactl</c> for the volume, <c>xprintidle</c> or the GNOME shell
/// for the idle time, <c>playerctl</c> for the media player).
/// </summary>
public static class LinuxSessionMetricCatalog
{
    /// <summary>The ids that the helper samples, in the order of the Windows catalog.</summary>
    public static IReadOnlyList<string> Ids { get; } =
    [
        SessionMetricIds.IdleSeconds,
        SessionMetricIds.Volume,
        SessionMetricIds.Muted,
        SessionMetricIds.MediaState,
        SessionMetricIds.MediaTitle,
        SessionMetricIds.MediaArtist,
        SessionMetricIds.MediaAlbum,
        SessionMetricIds.MediaApp,
        SessionMetricIds.MediaDuration,
        SessionMetricIds.MediaPosition,
        SessionMetricIds.MediaPositionUpdatedAt,
    ];

    /// <summary>The descriptors of <see cref="Ids"/>.</summary>
    public static IReadOnlyList<MetricDescriptor> HelperSide { get; } =
        [.. SessionMetricCatalog.SessionHostSide.Where(descriptor => Ids.Contains(descriptor.Id, StringComparer.Ordinal))];

    /// <summary>Tells whether the helper samples a metric.</summary>
    /// <param name="metricId">The metric id.</param>
    /// <returns>True for the ids in <see cref="Ids"/>.</returns>
    public static bool IsHelperMetric(string metricId)
    {
        ArgumentNullException.ThrowIfNull(metricId);
        return Ids.Contains(metricId, StringComparer.Ordinal);
    }
}
