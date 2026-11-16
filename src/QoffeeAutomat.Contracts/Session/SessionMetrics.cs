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

    /// <summary>Sensor: the number of attached displays.</summary>
    public const string DisplayCount = "display.count";

    /// <summary>Sensor: the resolution of the primary display, for example <c>2560x1440</c>.</summary>
    public const string PrimaryResolution = "display.primary_resolution";
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
