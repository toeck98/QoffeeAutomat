namespace QoffeeAutomat.SessionHost.Metrics;

/// <summary>
/// Reads the user metrics of this session (PLAN.md S06, T305): idle time, foreground window, audio, microphone and webcam
/// use, displays. The seam between the <see cref="UserMetricSampler"/> and the Windows APIs.
/// </summary>
public interface IUserMetricProbe
{
    /// <summary>Reads one metric of <see cref="QoffeeAutomat.Contracts.Session.SessionMetricCatalog.SessionHostSide"/>.</summary>
    /// <param name="metricId">The metric id.</param>
    /// <returns>A <see cref="double"/>, <see cref="bool"/> or <see cref="string"/>; null when the metric is unavailable (no audio device, no window, unknown id). The method does not throw.</returns>
    object? Read(string metricId);
}

/// <summary>A probe that knows nothing: every metric is unavailable.</summary>
public sealed class NoUserMetricProbe : IUserMetricProbe
{
    /// <summary>The only instance.</summary>
    public static NoUserMetricProbe Instance { get; } = new();

    /// <inheritdoc />
    public object? Read(string metricId) => null;
}
