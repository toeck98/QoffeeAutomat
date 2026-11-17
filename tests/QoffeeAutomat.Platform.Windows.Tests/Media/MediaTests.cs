using System.Globalization;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Tests.Actions;
using QoffeeAutomat.Platform.Windows.Tests.Metrics;
using QoffeeAutomat.SessionHost.Actions;
using QoffeeAutomat.SessionHost.Media;
using QoffeeAutomat.SessionHost.Metrics;

namespace QoffeeAutomat.Platform.Windows.Tests.Media;

/// <summary>A media session that the test controls: what it reads, what it answers to requests, and what was asked.</summary>
internal sealed class FakeMediaSource : IMediaSessionSource
{
    public MediaSessionReading? Reading { get; set; } = MediaSessionReading.None;

    public MediaCommandResult Result { get; set; } = MediaCommandResult.Accepted;

    public Exception? ReadFailure { get; set; }

    public int Reads { get; private set; }

    public List<string> Sent { get; } = [];

    public MediaSessionReading? Read()
    {
        Reads++;
        if (ReadFailure is not null)
        {
            throw ReadFailure;
        }

        return Reading;
    }

    public ValueTask<MediaCommandResult> SendAsync(string action, CancellationToken cancellationToken)
    {
        Sent.Add(action);
        return ValueTask.FromResult(Result);
    }
}

/// <summary>
/// The logic behind the media player of Home Assistant on the PC side (PLAN.md S11, T716): which value each metric gets, when the
/// position is published, and what <c>media.control</c> does with and without a session. The Windows Runtime itself
/// (<see cref="WindowsMediaSessionSource"/>) is not tested here; it only passes readings on.
/// </summary>
[Trait("Category", "Media")]
public sealed class MediaTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static MediaSessionReading Reading(
        MediaPlayback playback = MediaPlayback.Playing,
        string title = "Song",
        double? duration = 200,
        double? position = 10,
        DateTimeOffset? updated = null)
    {
        return new MediaSessionReading(playback, "Spotify.exe", title, "Artist", "Album", duration, position, updated ?? Start);
    }

    // ------------------------------------------------------------------
    // The tracker
    // ------------------------------------------------------------------

    [Fact]
    public void The_position_is_the_position_at_the_moment_of_the_sample_that_noticed_the_track()
    {
        var tracker = new MediaSessionTracker();

        // The player reported 10 s two seconds ago and has been playing since: the position now is 12 s.
        MediaSnapshot? snapshot = tracker.Observe(Reading(position: 10, updated: Start.AddSeconds(-2)), Start);

        Assert.NotNull(snapshot);
        Assert.Equal(MediaStates.Playing, snapshot.State);
        Assert.Equal(12d, snapshot.PositionSeconds);
        Assert.Equal(200d, snapshot.DurationSeconds);
        Assert.Equal(("Song", "Artist", "Album", "Spotify"), (snapshot.Title, snapshot.Artist, snapshot.Album, snapshot.App));
    }

    [Fact]
    public void A_track_that_plays_on_does_not_change_the_published_position()
    {
        var tracker = new MediaSessionTracker();
        MediaSessionReading reading = Reading(position: 10, updated: Start.AddSeconds(-2));

        MediaSnapshot first = tracker.Observe(reading, Start)!;
        MediaSnapshot later = tracker.Observe(reading, Start.AddSeconds(2))!;
        MediaSnapshot muchLater = tracker.Observe(reading, Start.AddSeconds(60))!;

        // The client interpolates; nothing new is published while the track plays as predicted.
        Assert.Equal(12d, first.PositionSeconds);
        Assert.Equal(12d, later.PositionSeconds);
        Assert.Equal(12d, muchLater.PositionSeconds);
    }

    [Fact]
    public void A_jump_in_the_position_is_a_change()
    {
        var tracker = new MediaSessionTracker();
        tracker.Observe(Reading(position: 10, updated: Start.AddSeconds(-2)), Start);

        // Two seconds later the player reports 100 s: somebody sought.
        MediaSnapshot sought = tracker.Observe(Reading(position: 100, updated: Start.AddSeconds(2)), Start.AddSeconds(2))!;

        Assert.Equal(100d, sought.PositionSeconds);
    }

    [Fact]
    public void A_small_difference_is_not_a_jump()
    {
        var tracker = new MediaSessionTracker();
        tracker.Observe(Reading(position: 10, updated: Start), Start);

        // The player rounds its position: one second off against the prediction stays within the tolerance.
        MediaSnapshot snapshot = tracker.Observe(Reading(position: 13, updated: Start.AddSeconds(2)), Start.AddSeconds(2))!;

        Assert.Equal(10d, snapshot.PositionSeconds);
    }

    [Fact]
    public void Another_track_starts_a_new_position()
    {
        var tracker = new MediaSessionTracker();
        tracker.Observe(Reading(title: "One", position: 150, updated: Start), Start);

        MediaSnapshot next = tracker.Observe(Reading(title: "Two", position: 0, updated: Start.AddSeconds(2)), Start.AddSeconds(2))!;

        Assert.Equal("Two", next.Title);
        Assert.Equal(0d, next.PositionSeconds);
    }

    [Fact]
    public void A_change_of_the_playback_state_re_anchors_the_position()
    {
        var tracker = new MediaSessionTracker();
        tracker.Observe(Reading(position: 10, updated: Start), Start);

        MediaSnapshot paused = tracker.Observe(Reading(MediaPlayback.Paused, position: 31, updated: Start.AddSeconds(20)), Start.AddSeconds(22))!;
        MediaSnapshot stillPaused = tracker.Observe(Reading(MediaPlayback.Paused, position: 31, updated: Start.AddSeconds(20)), Start.AddSeconds(60))!;

        // A paused track does not move on, so the position is the reported one and stays.
        Assert.Equal(MediaStates.Paused, paused.State);
        Assert.Equal(31d, paused.PositionSeconds);
        Assert.Equal(31d, stillPaused.PositionSeconds);
    }

    [Fact]
    public void A_position_never_exceeds_the_duration()
    {
        var tracker = new MediaSessionTracker();

        MediaSnapshot snapshot = tracker.Observe(Reading(duration: 100, position: 99, updated: Start.AddSeconds(-30)), Start)!;

        Assert.Equal(100d, snapshot.PositionSeconds);
    }

    [Fact]
    public void A_program_without_a_timeline_has_neither_duration_nor_position()
    {
        var tracker = new MediaSessionTracker();

        MediaSnapshot snapshot = tracker.Observe(Reading(duration: null, position: null), Start)!;

        Assert.Null(snapshot.DurationSeconds);
        Assert.Null(snapshot.PositionSeconds);
        Assert.Equal(MediaStates.Playing, snapshot.State);
    }

    [Fact]
    public void Without_a_session_the_state_is_idle_and_everything_else_is_empty()
    {
        var tracker = new MediaSessionTracker();

        MediaSnapshot snapshot = tracker.Observe(MediaSessionReading.None, Start)!;

        Assert.Equal(MediaStates.Idle, snapshot.State);
        Assert.Null(snapshot.Title);
        Assert.Null(snapshot.Artist);
        Assert.Null(snapshot.Album);
        Assert.Null(snapshot.App);
        Assert.Null(snapshot.DurationSeconds);
        Assert.Null(snapshot.PositionSeconds);
    }

    [Fact]
    public void A_stopped_or_closed_session_is_idle()
    {
        var tracker = new MediaSessionTracker();

        Assert.Equal(MediaStates.Idle, tracker.Observe(Reading(MediaPlayback.Idle), Start)!.State);
    }

    [Fact]
    public void A_source_that_cannot_tell_makes_everything_unavailable()
    {
        var tracker = new MediaSessionTracker();
        tracker.Observe(Reading(), Start);

        Assert.Null(tracker.Observe(null, Start.AddSeconds(2)));
    }

    // ------------------------------------------------------------------
    // The names of programs
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("chrome.exe", "chrome")]
    [InlineData("MSEdge", "MSEdge")]
    [InlineData(@"C:\Program Files\VideoLAN\VLC\vlc.EXE", "vlc")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "ZuneMusic")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
    [InlineData("Contoso.Player_abc123!App", "Player")]
    [InlineData("  Winamp.exe  ", "Winamp")]
    public void The_name_of_a_program_is_derived_from_its_application_id(string id, string expected)
    {
        Assert.Equal(expected, MediaAppNames.From(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".exe")]
    public void An_id_without_a_name_gives_no_name(string? id)
    {
        Assert.Null(MediaAppNames.From(id));
    }

    // ------------------------------------------------------------------
    // The metrics
    // ------------------------------------------------------------------

    [Fact]
    public void The_probe_answers_the_media_metrics_from_the_session_and_passes_every_other_metric_on()
    {
        var inner = new CountingProbe();
        inner.Set(SessionMetricIds.Volume, 40.0);
        var source = new FakeMediaSource { Reading = Reading(position: 10, updated: Start) };
        var probe = new MediaMetricProbe(inner, source, new ManualTime { Now = Start });

        Assert.Equal(40.0, probe.Read(SessionMetricIds.Volume));
        Assert.Equal(0, source.Reads);
        Assert.Equal(MediaStates.Playing, probe.Read(SessionMetricIds.MediaState));
        Assert.Equal("Song", probe.Read(SessionMetricIds.MediaTitle));
        Assert.Equal("Artist", probe.Read(SessionMetricIds.MediaArtist));
        Assert.Equal("Album", probe.Read(SessionMetricIds.MediaAlbum));
        Assert.Equal("Spotify", probe.Read(SessionMetricIds.MediaApp));
        Assert.Equal(200d, probe.Read(SessionMetricIds.MediaDuration));
        Assert.Equal(10d, probe.Read(SessionMetricIds.MediaPosition));
        Assert.Equal("2026-11-16T12:00:00Z", probe.Read(SessionMetricIds.MediaPositionUpdatedAt));
        Assert.Equal(0, inner.ReadsOf(SessionMetricIds.MediaState));
    }

    [Fact]
    public void The_time_of_the_position_is_the_time_of_the_change_and_not_the_time_of_the_sample()
    {
        var source = new FakeMediaSource { Reading = Reading(position: 10, updated: Start) };
        var time = new ManualTime { Now = Start.AddMilliseconds(400) };
        var probe = new MediaMetricProbe(NoUserMetricProbe.Instance, source, time);

        object? first = probe.Read(SessionMetricIds.MediaPositionUpdatedAt);
        time.Now = Start.AddSeconds(30);
        object? later = probe.Read(SessionMetricIds.MediaPositionUpdatedAt);
        source.Reading = Reading(position: 300, updated: Start.AddSeconds(32));
        time.Now = Start.AddSeconds(32);
        object? afterSeek = probe.Read(SessionMetricIds.MediaPositionUpdatedAt);

        // A client that learns the position at any later moment is told when it was valid: the snapshot of a new
        // subscription carries the same pair, so the client does not mistake an old position for a fresh one.
        Assert.Equal("2026-11-16T12:00:00Z", first);
        Assert.Equal("2026-11-16T12:00:00Z", later);
        Assert.Equal("2026-11-16T12:00:32Z", afterSeek);
    }

    [Fact]
    public void Without_a_timeline_there_is_no_position_and_no_time_of_the_position()
    {
        var source = new FakeMediaSource { Reading = Reading(duration: null, position: null) };
        var probe = new MediaMetricProbe(NoUserMetricProbe.Instance, source, new ManualTime { Now = Start });

        Assert.Null(probe.Read(SessionMetricIds.MediaPosition));
        Assert.Null(probe.Read(SessionMetricIds.MediaPositionUpdatedAt));
        Assert.Equal(MediaStates.Playing, probe.Read(SessionMetricIds.MediaState));
    }

    [Theory]
    [InlineData("2026-11-16T12:00:00+00:00", "2026-11-16T12:00:00Z")]
    [InlineData("2026-11-16T14:30:15.900+02:00", "2026-11-16T12:30:15Z")]
    public void Timestamps_are_written_in_utc_with_whole_seconds_like_every_timestamp_metric(string moment, string expected)
    {
        Assert.Equal(expected, MediaMetricProbe.FormatTimestamp(DateTimeOffset.Parse(moment, CultureInfo.InvariantCulture)));
        Assert.Null(MediaMetricProbe.FormatTimestamp(null));
    }

    [Fact]
    public void The_media_metrics_are_all_ids_of_the_catalog_that_the_probe_knows()
    {
        string[] media = [.. SessionMetricCatalog.SessionHostSide.Select(descriptor => descriptor.Id).Where(id => id.StartsWith("media.", StringComparison.Ordinal))];

        Assert.Equal(8, media.Length);
        Assert.All(media, id => Assert.True(MediaMetricProbe.IsMediaMetric(id), id));
        Assert.False(MediaMetricProbe.IsMediaMetric(SessionMetricIds.Volume));
    }

    [Fact]
    public void Reading_all_metrics_of_one_sample_reads_the_session_once()
    {
        var source = new FakeMediaSource { Reading = Reading() };
        var time = new ManualTime { Now = Start };
        var probe = new MediaMetricProbe(NoUserMetricProbe.Instance, source, time);

        foreach (string id in SessionMetricCatalog.SessionHostSide.Select(descriptor => descriptor.Id).Where(MediaMetricProbe.IsMediaMetric))
        {
            probe.Read(id);
        }

        Assert.Equal(1, source.Reads);

        time.Now = Start.AddSeconds(2);
        probe.Read(SessionMetricIds.MediaState);

        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public void The_position_metric_only_changes_when_something_changed()
    {
        var source = new FakeMediaSource { Reading = Reading(position: 10, updated: Start) };
        var time = new ManualTime { Now = Start };
        var probe = new MediaMetricProbe(NoUserMetricProbe.Instance, source, time);

        object? first = probe.Read(SessionMetricIds.MediaPosition);
        time.Now = Start.AddSeconds(2);
        object? second = probe.Read(SessionMetricIds.MediaPosition);
        source.Reading = Reading(MediaPlayback.Paused, position: 12, updated: Start.AddSeconds(2));
        time.Now = Start.AddSeconds(4);
        object? afterPause = probe.Read(SessionMetricIds.MediaPosition);

        Assert.Equal(10d, first);
        Assert.Equal(10d, second);
        Assert.Equal(12d, afterPause);
    }

    [Fact]
    public void Without_a_session_the_state_is_idle()
    {
        var probe = new MediaMetricProbe(NoUserMetricProbe.Instance, new FakeMediaSource(), new ManualTime { Now = Start });

        Assert.Equal(MediaStates.Idle, probe.Read(SessionMetricIds.MediaState));
        Assert.Null(probe.Read(SessionMetricIds.MediaTitle));
    }

    [Fact]
    public void A_source_that_cannot_tell_or_fails_makes_the_media_metrics_unavailable()
    {
        var notReady = new FakeMediaSource { Reading = null };
        var broken = new FakeMediaSource { ReadFailure = new InvalidOperationException("The media session is broken.") };

        Assert.Null(new MediaMetricProbe(NoUserMetricProbe.Instance, notReady, new ManualTime { Now = Start }).Read(SessionMetricIds.MediaState));
        Assert.Null(new MediaMetricProbe(NoUserMetricProbe.Instance, broken, new ManualTime { Now = Start }).Read(SessionMetricIds.MediaState));
    }

    // ------------------------------------------------------------------
    // media.control
    // ------------------------------------------------------------------

    private static (MediaController Controller, FakeMediaSource Source, List<string> Keys) NewController()
    {
        var source = new FakeMediaSource();
        var keys = new List<string>();
        return (new MediaController(source, keys.Add), source, keys);
    }

    [Theory]
    [InlineData("play")]
    [InlineData("pause")]
    [InlineData("play_pause")]
    [InlineData("next")]
    [InlineData("previous")]
    [InlineData("stop")]
    public async Task A_session_that_accepts_the_request_gets_it_and_no_key_is_pressed(string action)
    {
        (MediaController controller, FakeMediaSource source, List<string> keys) = NewController();
        source.Reading = Reading();

        ExecutionResult result = await controller.ControlAsync(action, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([action], source.Sent);
        Assert.Empty(keys);
    }

    [Theory]
    [InlineData("play_pause", "play_pause")]
    [InlineData("next", "next")]
    [InlineData("previous", "previous")]
    [InlineData("stop", "stop")]
    public async Task Without_a_session_the_request_falls_back_to_the_media_key(string action, string key)
    {
        (MediaController controller, FakeMediaSource source, List<string> keys) = NewController();
        source.Result = MediaCommandResult.NoSession;

        ExecutionResult result = await controller.ControlAsync(action, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([key], keys);
        Assert.Contains("no media session", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_session_that_refuses_gets_the_media_key_instead()
    {
        (MediaController controller, FakeMediaSource source, List<string> keys) = NewController();
        source.Result = MediaCommandResult.Refused;
        source.Reading = Reading();

        ExecutionResult result = await controller.ControlAsync("next", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["next"], keys);
        Assert.Contains("refused", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Play_without_a_session_presses_the_toggle_because_nothing_can_be_told()
    {
        (MediaController controller, FakeMediaSource source, List<string> keys) = NewController();
        source.Result = MediaCommandResult.NoSession;

        await controller.ControlAsync("play", CancellationToken.None);

        Assert.Equal(["play_pause"], keys);
    }

    [Fact]
    public async Task Pause_without_a_session_presses_nothing_because_a_toggle_could_start_music()
    {
        (MediaController controller, FakeMediaSource source, List<string> keys) = NewController();
        source.Result = MediaCommandResult.NoSession;

        ExecutionResult result = await controller.ControlAsync("pause", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(keys);
    }

    [Fact]
    public async Task Pause_of_a_track_that_plays_but_whose_session_refuses_presses_the_toggle()
    {
        (MediaController controller, FakeMediaSource source, List<string> keys) = NewController();
        source.Result = MediaCommandResult.Refused;
        source.Reading = Reading(MediaPlayback.Playing);

        await controller.ControlAsync("pause", CancellationToken.None);

        Assert.Equal(["play_pause"], keys);
    }

    [Fact]
    public async Task Play_and_pause_are_idempotent_when_the_state_already_is_the_wanted_one()
    {
        (MediaController controller, FakeMediaSource source, List<string> keys) = NewController();
        source.Result = MediaCommandResult.Refused;

        source.Reading = Reading(MediaPlayback.Playing);
        ExecutionResult play = await controller.ControlAsync("play", CancellationToken.None);
        source.Reading = Reading(MediaPlayback.Paused);
        ExecutionResult pause = await controller.ControlAsync("pause", CancellationToken.None);

        Assert.True(play.Succeeded);
        Assert.True(pause.Succeeded);
        Assert.Empty(keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rewind")]
    [InlineData("PLAY")]
    public async Task An_unknown_action_is_refused_before_anything_happens(string? action)
    {
        (MediaController controller, FakeMediaSource source, List<string> keys) = NewController();

        ExecutionResult result = await controller.ControlAsync(action, CancellationToken.None);

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(source.Sent);
        Assert.Empty(keys);
    }

    [Fact]
    public async Task The_action_of_the_session_host_runs_the_controller_and_the_media_key_reaches_the_desktop()
    {
        var desktop = new FakeDesktopNativeApi();
        var source = new FakeMediaSource { Result = MediaCommandResult.NoSession };
        var provider = new UserBuiltinActions(desktop, new FakeAudioEndpoint(), new FakeProcessControl(), media: source);

        ExecutionResult result = await provider.ExecuteAsync(
            UserBuiltinActions.MediaControl,
            ActionTestData.Vars((MediaControlSpec.ActionParameter, "next")),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["next"], desktop.MediaKeys);
    }

    [Fact]
    public async Task Without_a_media_session_the_action_says_so()
    {
        var provider = new UserBuiltinActions(new FakeDesktopNativeApi(), new FakeAudioEndpoint(), new FakeProcessControl());

        ExecutionResult result = await provider.ExecuteAsync(
            UserBuiltinActions.MediaControl,
            ActionTestData.Vars((MediaControlSpec.ActionParameter, "play")),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("media session", result.StandardError, StringComparison.Ordinal);
    }
}
