namespace QoffeeAutomat.SessionHost.Metrics;

/// <summary>What changed in a source of event-driven metrics.</summary>
/// <param name="MetricIds">The metrics whose value or options changed; the sampler reads them again at once.</param>
/// <param name="OptionsChanged">True when the options of at least one metric changed, so that the agent has to learn them again (<c>metric_options</c>).</param>
public sealed record MetricChangeNotice(IReadOnlyCollection<string> MetricIds, bool OptionsChanged);

/// <summary>
/// A source of metrics that change by events of the operating system and whose options are not fixed (PLAN.md S06, S11, T713),
/// for example the audio devices. The session client asks it for the options after the handshake and whenever it reports a
/// change, and has the sampler read the changed metrics at once instead of at the next interval.
/// </summary>
public interface IMetricChangeSource
{
    /// <summary>The options of the metrics that have dynamic options, by metric id.</summary>
    /// <returns>The current option sets; a metric without options has an empty list. The method does not throw.</returns>
    IReadOnlyDictionary<string, IReadOnlyList<string>> CurrentOptions();

    /// <summary>
    /// Starts watching and reports every change until the subscription is disposed. The operating system is only asked to send
    /// notifications while somebody watches (D-007).
    /// </summary>
    /// <param name="onChange">Called on any thread after something changed; it must return quickly and not throw.</param>
    /// <returns>Ends the subscription when disposed.</returns>
    IDisposable Watch(Action<MetricChangeNotice> onChange);
}
