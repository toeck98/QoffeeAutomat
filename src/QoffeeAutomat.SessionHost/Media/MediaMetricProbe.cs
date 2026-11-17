using System.Globalization;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.SessionHost.Metrics;

namespace QoffeeAutomat.SessionHost.Media;

/// <summary>
/// The <see cref="IUserMetricProbe"/> that adds the media metrics (<c>media.state</c>, <c>media.title</c>, <c>media.artist</c>,
/// <c>media.album</c>, <c>media.app</c>, <c>media.duration</c>, <c>media.position</c> and <c>media.position_updated_at</c>, PLAN.md S06, S11, T716) to the probe of the
/// session: it answers those ids from the <see cref="IMediaSessionSource"/> and passes every other id to the inner probe. The
/// sampler asks for the metrics one by one, so the source is read once and the result is shared by the reads that follow within
/// a quarter of a second; the <see cref="MediaSessionTracker"/> then sees one observation per sample and decides when the
/// position has changed. Nothing here runs while nobody subscribes to a media metric (D-007): the source starts on the first
/// read.
/// </summary>
public sealed class MediaMetricProbe : IUserMetricProbe
{
    private static readonly TimeSpan ReuseWindow = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly IUserMetricProbe _inner;
    private readonly IMediaSessionSource _source;
    private readonly TimeProvider _time;
    private readonly MediaSessionTracker _tracker = new();
    private MediaSnapshot? _snapshot;
    private DateTimeOffset _observedAt;
    private bool _observed;

    /// <summary>Creates the probe.</summary>
    /// <param name="inner">Answers every metric that is not a media metric.</param>
    /// <param name="source">Reads the media session.</param>
    /// <param name="time">The clock for the position; null uses the system clock.</param>
    public MediaMetricProbe(IUserMetricProbe inner, IMediaSessionSource source, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(source);

        _inner = inner;
        _source = source;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Tells whether a metric id is one of the media metrics.</summary>
    /// <param name="metricId">The metric id.</param>
    /// <returns>True for the ids <c>media.*</c> of <see cref="SessionMetricIds"/>.</returns>
    public static bool IsMediaMetric(string metricId)
    {
        ArgumentNullException.ThrowIfNull(metricId);

        return metricId is SessionMetricIds.MediaState
            or SessionMetricIds.MediaTitle
            or SessionMetricIds.MediaArtist
            or SessionMetricIds.MediaAlbum
            or SessionMetricIds.MediaApp
            or SessionMetricIds.MediaDuration
            or SessionMetricIds.MediaPosition
            or SessionMetricIds.MediaPositionUpdatedAt;
    }

    /// <inheritdoc />
    public object? Read(string metricId)
    {
        ArgumentNullException.ThrowIfNull(metricId);

        if (!IsMediaMetric(metricId))
        {
            return _inner.Read(metricId);
        }

        MediaSnapshot? snapshot = Observe();
        if (snapshot is null)
        {
            return null;
        }

        return metricId switch
        {
            SessionMetricIds.MediaState => snapshot.State,
            SessionMetricIds.MediaTitle => snapshot.Title,
            SessionMetricIds.MediaArtist => snapshot.Artist,
            SessionMetricIds.MediaAlbum => snapshot.Album,
            SessionMetricIds.MediaApp => snapshot.App,
            SessionMetricIds.MediaDuration => snapshot.DurationSeconds,
            SessionMetricIds.MediaPosition => snapshot.PositionSeconds,
            SessionMetricIds.MediaPositionUpdatedAt => FormatTimestamp(snapshot.PositionAt),
            _ => null,
        };
    }

    /// <summary>Writes a moment the way the agent writes every timestamp metric: UTC, whole seconds, <c>yyyy-MM-ddTHH:mm:ssZ</c>.</summary>
    /// <param name="moment">The moment; null gives null.</param>
    /// <returns>The text.</returns>
    public static string? FormatTimestamp(DateTimeOffset? moment)
    {
        return moment?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    private MediaSnapshot? Observe()
    {
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (_observed && now - _observedAt < ReuseWindow)
            {
                return _snapshot;
            }

            MediaSessionReading? reading;
#pragma warning disable CA1031 // A source that fails makes the media metrics unavailable; the probe does not throw.
            try
            {
                reading = _source.Read();
            }
            catch (Exception)
            {
                reading = null;
            }
#pragma warning restore CA1031

            _snapshot = _tracker.Observe(reading, now);
            _observedAt = now;
            _observed = true;
            return _snapshot;
        }
    }
}
