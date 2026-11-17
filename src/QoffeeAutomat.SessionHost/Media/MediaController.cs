using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.SessionHost.Media;

/// <summary>
/// The action <c>media.control</c> (PLAN.md S11, T716): the request goes to the current system media session, the one that the
/// volume flyout of Windows shows, and only when that is impossible the media keys are pressed instead (what
/// <c>media.key</c> does). Playing and pausing are idempotent: <c>play</c> on a track that plays and <c>pause</c> on one that
/// does not is a success that changes nothing, which a media player in Home Assistant relies on when it sends the same
/// request twice. The media keys only know a toggle, so the fallback for <c>play</c> and <c>pause</c> presses it when the state
/// says that this does what was asked; without any session <c>pause</c> presses nothing, because a toggle could start music.
/// </summary>
public sealed class MediaController
{
    private readonly IMediaSessionSource _source;
    private readonly Action<string> _pressKey;

    /// <summary>Creates the controller.</summary>
    /// <param name="source">The media session.</param>
    /// <param name="pressKey">Presses a media key by the names of <c>media.key</c> (<c>play_pause</c>, <c>next</c>, <c>previous</c>, <c>stop</c>); it may throw like <see cref="QoffeeAutomat.SessionHost.Actions.IDesktopNativeApi.SendMediaKey"/>.</param>
    public MediaController(IMediaSessionSource source, Action<string> pressKey)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pressKey);

        _source = source;
        _pressKey = pressKey;
    }

    /// <summary>Carries the action out.</summary>
    /// <param name="action">One of <see cref="MediaControlSpec.Actions"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Success, or <see cref="ErrorCodes.InvalidVars"/> for an unknown action.</returns>
    public async ValueTask<ExecutionResult> ControlAsync(string? action, CancellationToken cancellationToken)
    {
        if (action is null || !MediaControlSpec.Actions.Contains(action))
        {
            return ExecutionResult.Failure(ErrorCodes.InvalidVars, "Action must be one of play, pause, play_pause, next, previous and stop.");
        }

        MediaCommandResult sent = await _source.SendAsync(action, cancellationToken).ConfigureAwait(false);
        if (sent == MediaCommandResult.Accepted)
        {
            return Done("The request " + action + " was sent to the media session.");
        }

        MediaPlayback playback = ReadPlayback();
        string? key = KeyFor(action, playback);
        if (key is null)
        {
            return Done(action == MediaControlSpec.Play
                ? "The media session is already playing."
                : "Nothing is playing.");
        }

        _pressKey(key);
        return Done(sent == MediaCommandResult.NoSession
            ? "There is no media session; the media key " + key + " was pressed."
            : "The media session refused the request " + action + "; the media key " + key + " was pressed.");
    }

    /// <summary>The media key that does what the action asks, or null when there is nothing to press.</summary>
    private static string? KeyFor(string action, MediaPlayback playback)
    {
        return action switch
        {
            MediaControlSpec.PlayPause => "play_pause",
            MediaControlSpec.Next => "next",
            MediaControlSpec.Previous => "previous",
            MediaControlSpec.Stop => "stop",
            MediaControlSpec.Play => playback == MediaPlayback.Playing ? null : "play_pause",
            MediaControlSpec.Pause => playback == MediaPlayback.Playing ? "play_pause" : null,
            _ => null,
        };
    }

    private static ExecutionResult Done(string message) => new(0, message, string.Empty);

    private MediaPlayback ReadPlayback()
    {
#pragma warning disable CA1031 // A source that fails tells nothing; the fallback then acts as if nothing played.
        try
        {
            return _source.Read()?.Playback ?? MediaPlayback.Idle;
        }
        catch (Exception)
        {
            return MediaPlayback.Idle;
        }
#pragma warning restore CA1031
    }
}
