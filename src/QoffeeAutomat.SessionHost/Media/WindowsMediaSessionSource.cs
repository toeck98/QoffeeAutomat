using System.Runtime.InteropServices;
using QoffeeAutomat.Contracts.Config;
using Windows.Media.Control;

namespace QoffeeAutomat.SessionHost.Media;

/// <summary>
/// The <see cref="IMediaSessionSource"/> of a Windows session (PLAN.md S11, T716): the system media transport controls
/// (<c>GlobalSystemMediaTransportControlsSessionManager</c>), through which Windows knows what plays in Spotify, a browser, the
/// media player and every other program that announces itself there. This is the only class that touches the Windows Runtime; it
/// is kept thin so that everything with logic in it can be tested without a player.
/// </summary>
/// <remarks>
/// The source starts when it is first read or first asked to send a request, never earlier, so a PC on which nobody subscribed to
/// a media metric does not even load the API (D-007). The title, artist and album change by event and are cached (reading them is
/// asynchronous); the playback state and the timeline are read at the moment of the sample, which is cheap. A session that goes away
/// while it is read is "no session". Without the API (a Windows that lacks it) every read is null and the metrics are unavailable.
/// </remarks>
public sealed class WindowsMediaSessionSource : IMediaSessionSource, IDisposable
{
    /// <summary>How long a control request waits for the manager when the source did not run yet.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly ISessionHostLog _log;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private TrackText? _text;
    private Task? _starting;
    private bool _disposed;

    /// <summary>Creates the source; nothing is loaded yet.</summary>
    /// <param name="log">The diagnostics log.</param>
    public WindowsMediaSessionSource(ISessionHostLog? log = null)
    {
        _log = log ?? NullSessionHostLog.Instance;
    }

    /// <inheritdoc />
    public MediaSessionReading? Read()
    {
        EnsureStarted();

        GlobalSystemMediaTransportControlsSession? session;
        TrackText? text;
        lock (_gate)
        {
            if (_manager is null)
            {
                return null;
            }

            session = _session;
            text = _text;
        }

        if (session is null)
        {
            return MediaSessionReading.None;
        }

#pragma warning disable CA1031 // A session that closes while it is read is "no session"; this method does not throw.
        try
        {
            GlobalSystemMediaTransportControlsSessionPlaybackInfo playback = session.GetPlaybackInfo();
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();

            double? duration = null;
            double? position = null;
            DateTimeOffset? updated = null;
            double length = (timeline.EndTime - timeline.StartTime).TotalSeconds;
            if (length > 0)
            {
                duration = length;
                position = Math.Max(0, (timeline.Position - timeline.StartTime).TotalSeconds);
                updated = timeline.LastUpdatedTime.Year > 1 ? timeline.LastUpdatedTime : null;
            }

            return new MediaSessionReading(
                ToPlayback(playback.PlaybackStatus),
                session.SourceAppUserModelId,
                text?.Title,
                text?.Artist,
                text?.Album,
                duration,
                position,
                updated,
                playback.PlaybackRate ?? 1.0);
        }
        catch (Exception)
        {
            return MediaSessionReading.None;
        }
#pragma warning restore CA1031
    }

    /// <inheritdoc />
    public async ValueTask<MediaCommandResult> SendAsync(string action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        // A request can come before any metric was read, so the source may not have started: wait for it, but not for long.
        Task? starting = EnsureStarted();
        if (starting is not null)
        {
            try
            {
                await starting.WaitAsync(StartTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return MediaCommandResult.NoSession;
            }
        }

        GlobalSystemMediaTransportControlsSession? session;
        lock (_gate)
        {
            session = _session;
        }

        if (session is null)
        {
            return MediaCommandResult.NoSession;
        }

#pragma warning disable CA1031 // A session that refuses or closes gives the media keys their turn.
        try
        {
            bool accepted = action switch
            {
                MediaControlSpec.Play => await session.TryPlayAsync().AsTask(cancellationToken).ConfigureAwait(false),
                MediaControlSpec.Pause => await session.TryPauseAsync().AsTask(cancellationToken).ConfigureAwait(false),
                MediaControlSpec.PlayPause => await session.TryTogglePlayPauseAsync().AsTask(cancellationToken).ConfigureAwait(false),
                MediaControlSpec.Next => await session.TrySkipNextAsync().AsTask(cancellationToken).ConfigureAwait(false),
                MediaControlSpec.Previous => await session.TrySkipPreviousAsync().AsTask(cancellationToken).ConfigureAwait(false),
                MediaControlSpec.Stop => await session.TryStopAsync().AsTask(cancellationToken).ConfigureAwait(false),
                _ => false,
            };
            return accepted ? MediaCommandResult.Accepted : MediaCommandResult.Refused;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Warning("The media session did not take the request " + action + ": " + exception.Message);
            return MediaCommandResult.Refused;
        }
#pragma warning restore CA1031
    }

    /// <summary>Stops listening to the media session.</summary>
    public void Dispose()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        GlobalSystemMediaTransportControlsSession? session;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            manager = _manager;
            session = _session;
            _manager = null;
            _session = null;
            _text = null;
        }

#pragma warning disable CA1031 // Unsubscribing from a session that is gone fails, and that is fine.
        try
        {
            if (session is not null)
            {
                session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            }

            if (manager is not null)
            {
                manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            }
        }
        catch (Exception)
        {
            // Nothing to do: the process ends right after this.
        }
#pragma warning restore CA1031
    }

    private static MediaPlayback ToPlayback(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
    {
        return status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlayback.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlayback.Paused,
            _ => MediaPlayback.Idle,
        };
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    /// <summary>Starts the manager once; returns the task of the start, or null when the source is disposed.</summary>
    private Task? EnsureStarted()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            _starting ??= Task.Run(StartAsync);
            return _starting;
        }
    }

    private async Task StartAsync()
    {
#pragma warning disable CA1031 // Without the API the media metrics stay unavailable; the SessionHost must not stop for it.
        try
        {
            GlobalSystemMediaTransportControlsSessionManager manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _manager = manager;
            }

            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            Bind(manager.GetCurrentSession());
            _log.Info("Reading the media session.");
        }
        catch (Exception exception)
        {
            _log.Warning("The media session of Windows is not available: " + exception.Message);
        }
#pragma warning restore CA1031
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        Bind(sender.GetCurrentSession());
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _ = RefreshTextAsync(sender);
    }

    /// <summary>Follows another session (or none): moves the event subscription and fetches the title of the new one.</summary>
    private void Bind(GlobalSystemMediaTransportControlsSession? session)
    {
        GlobalSystemMediaTransportControlsSession? previous;
        lock (_gate)
        {
            if (_disposed || ReferenceEquals(_session, session))
            {
                return;
            }

            previous = _session;
            _session = session;
            _text = null;
        }

#pragma warning disable CA1031 // A session that is already gone cannot be subscribed to; it is then "no session" on the next read.
        try
        {
            if (previous is not null)
            {
                previous.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            }

            if (session is not null)
            {
                session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            _log.Warning("Could not follow the media session: " + exception.Message);
        }
#pragma warning restore CA1031

        if (session is not null)
        {
            _ = RefreshTextAsync(session);
        }
    }

    private async Task RefreshTextAsync(GlobalSystemMediaTransportControlsSession session)
    {
#pragma warning disable CA1031 // A session that closes while its properties are read keeps the old text until the next event.
        try
        {
            GlobalSystemMediaTransportControlsSessionMediaProperties properties = await session.TryGetMediaPropertiesAsync();
            var text = new TrackText(NullIfEmpty(properties.Title), NullIfEmpty(properties.Artist), NullIfEmpty(properties.AlbumTitle));
            lock (_gate)
            {
                if (ReferenceEquals(_session, session))
                {
                    _text = text;
                }
            }
        }
        catch (Exception)
        {
            // See above.
        }
#pragma warning restore CA1031
    }

    /// <summary>The text of the current track, which Windows reports asynchronously.</summary>
    private sealed record TrackText(string? Title, string? Artist, string? Album);
}
