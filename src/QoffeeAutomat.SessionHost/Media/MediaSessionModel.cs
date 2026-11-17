namespace QoffeeAutomat.SessionHost.Media;

/// <summary>How the current media session plays, reduced to what the metrics and the controls need.</summary>
public enum MediaPlayback
{
    /// <summary>There is no session, or it neither plays nor is paused (stopped, closed, changing track).</summary>
    Idle,

    /// <summary>A track is playing.</summary>
    Playing,

    /// <summary>A track is paused.</summary>
    Paused,
}

/// <summary>
/// What Windows says about the current system media session at one moment (PLAN.md S11, T716). The type is the seam between
/// the Windows Runtime (<see cref="WindowsMediaSessionSource"/>) and the logic that is tested without it
/// (<see cref="MediaSessionTracker"/>, <see cref="MediaMetricProbe"/>, <see cref="MediaController"/>).
/// </summary>
/// <param name="Playback">The playback state.</param>
/// <param name="AppId">The application user model id of the program that owns the session, for example <c>Spotify.exe</c>.</param>
/// <param name="Title">The title of the track.</param>
/// <param name="Artist">The artist.</param>
/// <param name="Album">The album.</param>
/// <param name="DurationSeconds">The length of the track; null for a stream or a program that reports no timeline.</param>
/// <param name="PositionSeconds">The position in the track at <paramref name="PositionUpdatedAt"/>; null when there is no timeline.</param>
/// <param name="PositionUpdatedAt">When the program last reported the position; null when unknown (the position then counts as current).</param>
/// <param name="PlaybackRate">The speed of playback, 1 for normal speed.</param>
public sealed record MediaSessionReading(
    MediaPlayback Playback,
    string? AppId,
    string? Title,
    string? Artist,
    string? Album,
    double? DurationSeconds,
    double? PositionSeconds,
    DateTimeOffset? PositionUpdatedAt,
    double PlaybackRate = 1.0)
{
    /// <summary>The reading of a computer where no program offers a media session.</summary>
    public static MediaSessionReading None { get; } = new(MediaPlayback.Idle, null, null, null, null, null, null, null);
}

/// <summary>How a <see cref="IMediaSessionSource"/> answered a control request.</summary>
public enum MediaCommandResult
{
    /// <summary>There is no media session to address.</summary>
    NoSession,

    /// <summary>The session accepted the request.</summary>
    Accepted,

    /// <summary>There is a session, but it refused the request or failed (it does not support it, or it closed).</summary>
    Refused,
}

/// <summary>
/// Reads and controls the current system media session. The real implementation is <see cref="WindowsMediaSessionSource"/>;
/// tests use a fake. Neither method throws for a problem of the session: a session that goes away is "no session".
/// </summary>
public interface IMediaSessionSource
{
    /// <summary>Reads the current session without blocking.</summary>
    /// <returns>
    /// The reading; <see cref="MediaSessionReading.None"/> when there is no session; null when the source cannot tell (it is
    /// still starting, or the Windows media transport controls are not available), which makes the metrics unavailable.
    /// </returns>
    MediaSessionReading? Read();

    /// <summary>Sends a control request to the current session.</summary>
    /// <param name="action">One of <see cref="QoffeeAutomat.Contracts.Config.MediaControlSpec.Actions"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>What the session did with it.</returns>
    ValueTask<MediaCommandResult> SendAsync(string action, CancellationToken cancellationToken);
}

/// <summary>The metrics of the media session at one sample (<see cref="MediaSessionTracker"/>).</summary>
/// <param name="State">One of <see cref="QoffeeAutomat.Contracts.Session.MediaStates"/>.</param>
/// <param name="Title">The title, or null.</param>
/// <param name="Artist">The artist, or null.</param>
/// <param name="Album">The album, or null.</param>
/// <param name="App">The friendly name of the program, or null.</param>
/// <param name="DurationSeconds">The length of the track in whole seconds, or null.</param>
/// <param name="PositionSeconds">The position in whole seconds as of the last change of track, state or position (a jump), or null.</param>
/// <param name="PositionAt">The moment at which <paramref name="PositionSeconds"/> was valid, or null when there is no position.</param>
public sealed record MediaSnapshot(
    string State,
    string? Title,
    string? Artist,
    string? Album,
    string? App,
    double? DurationSeconds,
    double? PositionSeconds,
    DateTimeOffset? PositionAt = null);
