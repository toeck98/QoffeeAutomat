using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Contracts.Session;

/// <summary>
/// The ids of the metrics that belong to the interactive user session (PLAN.md S06, S11, T305). The agent knows the
/// first three from the session notifications and the connection of the SessionHost; the SessionHost samples the others
/// while the agent is subscribed to them (<c>metrics_subscribe</c>, <c>metrics_state</c>).
/// </summary>
public static class SessionMetricIds
{
    /// <summary>Binary sensor: the SessionHost of the active session accepts user commands.</summary>
    public const string Ready = "session.ready";

    /// <summary>Binary sensor: the session is locked.</summary>
    public const string Locked = "session.locked";

    /// <summary>Sensor: the logged-in user.</summary>
    public const string User = "session.user";

    /// <summary>Sensor: seconds since the last keyboard or mouse input.</summary>
    public const string IdleSeconds = "session.idle_seconds";

    /// <summary>Sensor: the title of the foreground window (off by default, privacy).</summary>
    public const string WindowTitle = "window.title";

    /// <summary>Sensor: the process name of the foreground window (off by default, privacy).</summary>
    public const string WindowProcess = "window.process";

    /// <summary>Sensor: the master volume of the default playback device, 0 to 100.</summary>
    public const string Volume = "audio.volume";

    /// <summary>Binary sensor: the default playback device is muted.</summary>
    public const string Muted = "audio.muted";

    /// <summary>Binary sensor: an application uses the microphone.</summary>
    public const string MicrophoneInUse = "privacy.microphone_in_use";

    /// <summary>Binary sensor: an application uses the webcam.</summary>
    public const string WebcamInUse = "privacy.webcam_in_use";

    /// <summary>
    /// Enum sensor: the current default playback device (T713). Its options are the active playback devices; they are not part
    /// of the static catalog but come from the SessionHost (<c>metric_options</c>), so they follow the devices of the PC.
    /// </summary>
    public const string AudioOutputDevice = "audio.output_device";

    /// <summary>Enum sensor: the current default recording device (T713). Its options are the active recording devices, like <see cref="AudioOutputDevice"/>.</summary>
    public const string AudioInputDevice = "audio.input_device";

    /// <summary>Sensor: the number of attached displays.</summary>
    public const string DisplayCount = "display.count";

    /// <summary>Sensor: the resolution of the primary display, for example <c>2560x1440</c>.</summary>
    public const string PrimaryResolution = "display.primary_resolution";

    /// <summary>Enum sensor: the state of the current system media session, <c>playing</c>, <c>paused</c> or <c>idle</c> (T716, off by default).</summary>
    public const string MediaState = "media.state";

    /// <summary>Sensor: the title of the current track (T716, off by default).</summary>
    public const string MediaTitle = "media.title";

    /// <summary>Sensor: the artist of the current track (T716, off by default).</summary>
    public const string MediaArtist = "media.artist";

    /// <summary>Sensor: the album of the current track (T716, off by default).</summary>
    public const string MediaAlbum = "media.album";

    /// <summary>Sensor: the program that owns the media session, for example <c>Spotify</c> (T716, off by default).</summary>
    public const string MediaApp = "media.app";

    /// <summary>Sensor: the length of the current track in seconds (T716, off by default).</summary>
    public const string MediaDuration = "media.duration";

    /// <summary>
    /// Sensor: the position in the current track in seconds (T716, off by default). It is only published when the track, the
    /// playback state or, by a jump (seek), the position changes; it is the position at the moment that
    /// <see cref="MediaPositionUpdatedAt"/> names, and a client interpolates from there while the track plays.
    /// </summary>
    public const string MediaPosition = "media.position";

    /// <summary>
    /// Timestamp sensor: the moment (UTC, whole seconds) at which <see cref="MediaPosition"/> was valid (T716, off by default).
    /// It is the timestamp that goes with the position: a value is only sent when it changes, so a client that subscribes later
    /// (or again) receives the position of the last change together with the time of that change and not with the time of the
    /// snapshot, which would make the position look newer than it is.
    /// </summary>
    public const string MediaPositionUpdatedAt = "media.position_updated_at";
}

/// <summary>The values of the metric <see cref="SessionMetricIds.MediaState"/> (T716).</summary>
public static class MediaStates
{
    /// <summary>A track is playing.</summary>
    public const string Playing = "playing";

    /// <summary>A track is paused.</summary>
    public const string Paused = "paused";

    /// <summary>There is no media session, or it neither plays nor is paused (stopped, closed, changing track).</summary>
    public const string Idle = "idle";

    /// <summary>All values, in the order the manifest announces them.</summary>
    public static IReadOnlyList<string> All { get; } = [Playing, Paused, Idle];
}

/// <summary>The session metrics whose options are not fixed but reported by the SessionHost (T713).</summary>
public static class DynamicSessionMetrics
{
    /// <summary>The ids of the metrics with dynamic options.</summary>
    public static IReadOnlyList<string> Ids { get; } = [SessionMetricIds.AudioOutputDevice, SessionMetricIds.AudioInputDevice];

    /// <summary>Tells whether a metric takes its options from the SessionHost.</summary>
    /// <param name="metricId">The metric id.</param>
    /// <returns>True for the ids in <see cref="Ids"/>.</returns>
    public static bool Has(string metricId)
    {
        ArgumentNullException.ThrowIfNull(metricId);
        return Ids.Contains(metricId, StringComparer.Ordinal);
    }
}

/// <summary>
/// The descriptors of the session metrics, shared by the agent (which announces them in the manifest) and the SessionHost
/// (which samples the ones in <see cref="SessionHostSide"/> at the intervals given here).
/// </summary>
public static class SessionMetricCatalog
{
    /// <summary>The metrics that the agent reports from what it knows itself; they need no sampling in the session.</summary>
    public static IReadOnlyList<MetricDescriptor> AgentSide { get; } =
    [
        new MetricDescriptor(
            SessionMetricIds.Ready,
            "Session ready",
            "binary_sensor",
            "bool",
            TranslationKey: "session_ready",
            EntityCategory: "diagnostic",
            Icon: "mdi:account-check",
            EnabledDefault: true,
            IntervalSeconds: 0,
            Context: "system"),
        new MetricDescriptor(
            SessionMetricIds.Locked,
            "Session locked",
            "binary_sensor",
            "bool",
            TranslationKey: "session_locked",
            Icon: "mdi:lock",
            EnabledDefault: true,
            IntervalSeconds: 0,
            Context: "user"),
        new MetricDescriptor(
            SessionMetricIds.User,
            "Logged-in user",
            "sensor",
            "string",
            TranslationKey: "session_user",
            EntityCategory: "diagnostic",
            Icon: "mdi:account",
            EnabledDefault: true,
            IntervalSeconds: 0,
            Context: "user"),
    ];

    /// <summary>The metrics that the SessionHost samples. Window title and process are off by default (PLAN.md S06).</summary>
    public static IReadOnlyList<MetricDescriptor> SessionHostSide { get; } =
    [
        new MetricDescriptor(
            SessionMetricIds.IdleSeconds,
            "Idle time",
            "sensor",
            "number",
            TranslationKey: "session_idle_seconds",
            Unit: "s",
            DeviceClass: "duration",
            StateClass: "measurement",
            Precision: 0,
            EnabledDefault: true,
            IntervalSeconds: 5,
            Context: "user"),
        new MetricDescriptor(
            SessionMetricIds.WindowTitle,
            "Active window title",
            "sensor",
            "string",
            TranslationKey: "window_title",
            Icon: "mdi:application",
            EnabledDefault: false,
            IntervalSeconds: 2,
            Context: "user"),
        new MetricDescriptor(
            SessionMetricIds.WindowProcess,
            "Active window process",
            "sensor",
            "string",
            TranslationKey: "window_process",
            Icon: "mdi:application-cog",
            EnabledDefault: false,
            IntervalSeconds: 2,
            Context: "user"),
        new MetricDescriptor(
            SessionMetricIds.Volume,
            "Master volume",
            "sensor",
            "number",
            TranslationKey: "audio_volume",
            Unit: "%",
            StateClass: "measurement",
            Icon: "mdi:volume-high",
            Precision: 0,
            EnabledDefault: true,
            IntervalSeconds: 2,
            Context: "user"),
        new MetricDescriptor(
            SessionMetricIds.Muted,
            "Muted",
            "binary_sensor",
            "bool",
            TranslationKey: "audio_muted",
            Icon: "mdi:volume-off",
            EnabledDefault: true,
            IntervalSeconds: 2,
            Context: "user"),

        // The audio devices (T713): enum metrics whose options are the active devices. The options of these two descriptors are
        // empty here (the member is left out); the agent fills them in from the device list that the SessionHost reports. The
        // value follows the device notifications at once; the interval is only the safety net for a notification that got lost.
        new MetricDescriptor(
            SessionMetricIds.AudioOutputDevice,
            "Audio output device",
            "sensor",
            "enum",
            TranslationKey: "audio_output_device",
            Icon: "mdi:speaker",
            EnabledDefault: true,
            IntervalSeconds: 30,
            Context: "user"),
        new MetricDescriptor(
            SessionMetricIds.AudioInputDevice,
            "Audio input device",
            "sensor",
            "enum",
            TranslationKey: "audio_input_device",
            Icon: "mdi:microphone-settings",
            EnabledDefault: true,
            IntervalSeconds: 30,
            Context: "user"),
        new MetricDescriptor(
            SessionMetricIds.MicrophoneInUse,
            "Microphone in use",
            "binary_sensor",
            "bool",
            TranslationKey: "privacy_microphone_in_use",
            Icon: "mdi:microphone",
            EnabledDefault: true,
            IntervalSeconds: 5,
            Context: "user"),
        new MetricDescriptor(
            SessionMetricIds.WebcamInUse,
            "Webcam in use",
            "binary_sensor",
            "bool",
            TranslationKey: "privacy_webcam_in_use",
            Icon: "mdi:webcam",
            EnabledDefault: true,
            IntervalSeconds: 5,
            Context: "user"),
        new MetricDescriptor(
            SessionMetricIds.DisplayCount,
            "Display count",
            "sensor",
            "number",
            TranslationKey: "display_count",
            StateClass: "measurement",
            EntityCategory: "diagnostic",
            Icon: "mdi:monitor-multiple",
            Precision: 0,
            EnabledDefault: true,
            IntervalSeconds: 30,
            Context: "user"),
        new MetricDescriptor(
            SessionMetricIds.PrimaryResolution,
            "Primary display resolution",
            "sensor",
            "string",
            TranslationKey: "display_primary_resolution",
            EntityCategory: "diagnostic",
            Icon: "mdi:monitor",
            EnabledDefault: true,
            IntervalSeconds: 30,
            Context: "user"),

        // The media session (T716): off by default, one entity each; the media player entity of Home Assistant subscribes to them
        // while it is enabled.
        new MetricDescriptor(
            SessionMetricIds.MediaState,
            "Media state",
            "sensor",
            "enum",
            TranslationKey: "media_state",
            Icon: "mdi:play-pause",
            Options: MediaStates.All,
            EnabledDefault: false,
            IntervalSeconds: 2,
            Context: "user"),
        new MetricDescriptor(
            SessionMetricIds.MediaTitle,
            "Media title",
            "sensor",
            "string",
            TranslationKey: "media_title",
            Icon: "mdi:music",
            EnabledDefault: false,
            IntervalSeconds: 2,
            Context: "user"),
        new MetricDescriptor(
            SessionMetricIds.MediaArtist,
            "Media artist",
            "sensor",
            "string",
            TranslationKey: "media_artist",
            Icon: "mdi:account-music",
            EnabledDefault: false,
            IntervalSeconds: 2,
            Context: "user"),
        new MetricDescriptor(
            SessionMetricIds.MediaAlbum,
            "Media album",
            "sensor",
            "string",
            TranslationKey: "media_album",
            Icon: "mdi:album",
            EnabledDefault: false,
            IntervalSeconds: 2,
            Context: "user"),
        new MetricDescriptor(
            SessionMetricIds.MediaApp,
            "Media app",
            "sensor",
            "string",
            TranslationKey: "media_app",
            Icon: "mdi:application-brackets-outline",
            EnabledDefault: false,
            IntervalSeconds: 2,
            Context: "user"),
        new MetricDescriptor(
            SessionMetricIds.MediaDuration,
            "Media duration",
            "sensor",
            "number",
            TranslationKey: "media_duration",
            Unit: "s",
            DeviceClass: "duration",
            Icon: "mdi:timer-outline",
            Precision: 0,
            EnabledDefault: false,
            IntervalSeconds: 2,
            Context: "user"),
        new MetricDescriptor(
            SessionMetricIds.MediaPosition,
            "Media position",
            "sensor",
            "number",
            TranslationKey: "media_position",
            Unit: "s",
            DeviceClass: "duration",
            Icon: "mdi:progress-clock",
            Precision: 0,
            EnabledDefault: false,
            IntervalSeconds: 2,
            Context: "user"),
        new MetricDescriptor(
            SessionMetricIds.MediaPositionUpdatedAt,
            "Media position time",
            "sensor",
            "timestamp",
            TranslationKey: "media_position_updated_at",
            DeviceClass: "timestamp",
            Icon: "mdi:clock-check-outline",
            EnabledDefault: false,
            IntervalSeconds: 2,
            Context: "user"),
    ];

    /// <summary>Every session metric, the agent-side ones first.</summary>
    public static IReadOnlyList<MetricDescriptor> All { get; } = [.. AgentSide, .. SessionHostSide];

    /// <summary>Tells whether the SessionHost samples a metric.</summary>
    /// <param name="metricId">The metric id.</param>
    /// <returns>True for the ids of <see cref="SessionHostSide"/>.</returns>
    public static bool IsSessionHostMetric(string metricId)
    {
        ArgumentNullException.ThrowIfNull(metricId);
        return SessionHostSide.Any(descriptor => string.Equals(descriptor.Id, metricId, StringComparison.Ordinal));
    }

    /// <summary>The default sampling interval of a SessionHost metric.</summary>
    /// <param name="metricId">The metric id.</param>
    /// <returns>The interval in seconds; 5 for an id that is not in <see cref="SessionHostSide"/>.</returns>
    public static double IntervalSecondsOf(string metricId)
    {
        ArgumentNullException.ThrowIfNull(metricId);

        MetricDescriptor? descriptor = SessionHostSide.FirstOrDefault(item => string.Equals(item.Id, metricId, StringComparison.Ordinal));
        return descriptor?.IntervalSeconds is { } seconds and > 0 ? seconds : 5;
    }
}
