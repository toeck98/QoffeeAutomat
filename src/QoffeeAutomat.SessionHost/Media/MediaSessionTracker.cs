using System.Globalization;
using QoffeeAutomat.Contracts.Session;

namespace QoffeeAutomat.SessionHost.Media;

/// <summary>
/// Turns the readings of the media session into the metric values (PLAN.md S11, T716). Most values pass through; the position
/// does not: it is <b>published only when something changes</b> (another track, another playback state, or a jump in the
/// position, for example a seek) and otherwise keeps its last value. The value is the position at the moment of the sample that
/// noticed the change, and that moment is part of the snapshot (<see cref="MediaSnapshot.PositionAt"/>, the metric
/// <c>media.position_updated_at</c>), so a client interpolates while the track plays, also when it learns the position later.
/// A position that moved on as playing goes is not a change; a jump of more than <see cref="SeekTolerance"/> against what the
/// anchor predicts is.
/// </summary>
public sealed class MediaSessionTracker
{
    /// <summary>How far the reported position may differ from the predicted one before it counts as a jump.</summary>
    public static readonly TimeSpan SeekTolerance = TimeSpan.FromSeconds(3);

    private string? _key;
    private double? _anchor;
    private DateTimeOffset _anchorAt;
    private double _rate = 1.0;

    /// <summary>Observes the session once.</summary>
    /// <param name="reading">The reading; null when the source cannot tell, which resets the tracker.</param>
    /// <param name="now">The time of the sample.</param>
    /// <returns>The metric values; null when they are unavailable.</returns>
    public MediaSnapshot? Observe(MediaSessionReading? reading, DateTimeOffset now)
    {
        if (reading is null)
        {
            _key = null;
            _anchor = null;
            return null;
        }

        double? current = CurrentPosition(reading, now);
        string key = KeyOf(reading);
        bool changed = _key is null || !string.Equals(_key, key, StringComparison.Ordinal);
        if (!changed)
        {
            bool timelineAppeared = (_anchor is null) != (current is null);
            bool jumped = current is { } actual
                && _anchor is { } anchor
                && Math.Abs(actual - Predict(anchor, reading.Playback, now)) > SeekTolerance.TotalSeconds;
            changed = timelineAppeared || jumped;
        }

        if (changed)
        {
            _key = key;
            _anchor = current;
            _anchorAt = now;
            _rate = reading.PlaybackRate > 0 ? reading.PlaybackRate : 1.0;
        }

        return new MediaSnapshot(
            ToState(reading.Playback),
            reading.Title,
            reading.Artist,
            reading.Album,
            MediaAppNames.From(reading.AppId),
            reading.DurationSeconds is { } duration ? Math.Floor(duration) : null,
            _anchor is { } position ? Math.Floor(position) : null,
            _anchor is null ? null : _anchorAt);
    }

    private static string ToState(MediaPlayback playback)
    {
        return playback switch
        {
            MediaPlayback.Playing => MediaStates.Playing,
            MediaPlayback.Paused => MediaStates.Paused,
            _ => MediaStates.Idle,
        };
    }

    private static string KeyOf(MediaSessionReading reading)
    {
        // The parts are joined with a character that does not occur in titles; the duration is part of the identity because a
        // program that reports it late (after the title) has started a new track as far as a client is concerned.
        return string.Join(
            '\u001f',
            reading.Playback.ToString(),
            reading.AppId,
            reading.Title,
            reading.Artist,
            reading.Album,
            reading.DurationSeconds is { } duration ? Math.Floor(duration).ToString(CultureInfo.InvariantCulture) : string.Empty);
    }

    /// <summary>The position now: what the program reported, moved on by the time since it reported it while the track plays.</summary>
    private static double? CurrentPosition(MediaSessionReading reading, DateTimeOffset now)
    {
        if (reading.PositionSeconds is not { } reported)
        {
            return null;
        }

        double position = reported;
        if (reading.Playback == MediaPlayback.Playing && reading.PositionUpdatedAt is { } updated)
        {
            double rate = reading.PlaybackRate > 0 ? reading.PlaybackRate : 1.0;
            position += Math.Max(0, (now - updated).TotalSeconds) * rate;
        }

        if (reading.DurationSeconds is { } duration && position > duration)
        {
            position = duration;
        }

        return Math.Max(0, position);
    }

    private double Predict(double anchor, MediaPlayback playback, DateTimeOffset now)
    {
        return playback == MediaPlayback.Playing ? anchor + (Math.Max(0, (now - _anchorAt).TotalSeconds) * _rate) : anchor;
    }
}
