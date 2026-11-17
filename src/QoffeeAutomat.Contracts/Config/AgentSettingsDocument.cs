using System.Text.Json.Serialization;

namespace QoffeeAutomat.Contracts.Config;

/// <summary>
/// The agent settings, <c>config/settings.json</c> (PLAN.md S06, S08): name, ports, remote UI,
/// logging and retention. Members that are absent use the defaults in <see cref="AgentSettingsDefaults"/>.
/// </summary>
/// <param name="SchemaVersion">Version of the document schema.</param>
/// <param name="Name">The agent name shown in Home Assistant.</param>
/// <param name="UiPort">Port of the loopback listener for UI and API.</param>
/// <param name="LinkPort">Port of the HTTPS listener for QoffeeLink and pairing.</param>
/// <param name="RemoteUi">Whether the UI and API are also served on the link listener.</param>
/// <param name="Zeroconf">Whether the agent announces itself on the local network (mDNS/DNS-SD, T216); absent means on.</param>
/// <param name="TrayIcon">Whether the SessionHost shows the tray icon (T310); absent means on.</param>
/// <param name="Logging">Logging settings.</param>
/// <param name="Retention">Retention settings.</param>
/// <param name="Prometheus">The optional Prometheus endpoint (T709); absent means off.</param>
/// <param name="ReverseLink">The optional reverse link to Home Assistant for roaming devices (T710); absent means off.</param>
/// <param name="Revision">Revision, incremented on every save.</param>
/// <param name="UpdatedAt">Time of the last save.</param>
/// <param name="Origin">Where the document was authored.</param>
/// <param name="Hotkeys">The global hotkeys that raise the event <c>hotkey_pressed</c> (T708); absent means none.</param>
/// <param name="HardwareSensors">The optional hardware sensors that need a kernel driver (T707, ADR 0002); absent means off.</param>
/// <param name="Updates">The daily check for a new release (T704, D-046); absent means on, once a day.</param>
public sealed record AgentSettingsDocument(
    int SchemaVersion,
    string Name,
    int? UiPort = null,
    int? LinkPort = null,
    bool? RemoteUi = null,
    bool? Zeroconf = null,
    bool? TrayIcon = null,
    LoggingSettings? Logging = null,
    RetentionSettings? Retention = null,
    PrometheusSettings? Prometheus = null,
    ReverseLinkSettings? ReverseLink = null,
    int? Revision = null,
    DateTimeOffset? UpdatedAt = null,
    string? Origin = null,
    IReadOnlyList<HotkeySettings>? Hotkeys = null,
    HardwareSensorSettings? HardwareSensors = null,
    UpdateSettings? Updates = null) : IConfigDocument
{
    /// <summary>The port of the UI listener, or the default.</summary>
    [JsonIgnore]
    public int EffectiveUiPort => UiPort ?? AgentSettingsDefaults.UiPort;

    /// <summary>Whether the zeroconf advertisement is on (the default).</summary>
    [JsonIgnore]
    public bool EffectiveZeroconf => Zeroconf ?? true;

    /// <summary>Whether the tray icon is shown (the default).</summary>
    [JsonIgnore]
    public bool EffectiveTrayIcon => TrayIcon ?? true;

    /// <summary>Whether the Prometheus endpoint <c>/metrics</c> is on; off by default (T709).</summary>
    [JsonIgnore]
    public bool EffectivePrometheusEnabled => Prometheus?.Enabled ?? false;

    /// <summary>Whether the agent dials Home Assistant (the reverse link); off by default (T710).</summary>
    [JsonIgnore]
    public bool EffectiveReverseLinkEnabled => ReverseLink?.Enabled ?? false;

    /// <summary>Whether the hardware sensors (temperatures, fans, power draw) are on; off by default (T707).</summary>
    [JsonIgnore]
    public bool EffectiveHardwareSensorsEnabled => HardwareSensors?.Enabled ?? false;

    /// <summary>Whether the agent asks GitHub for a newer release; on by default (T704, D-046).</summary>
    [JsonIgnore]
    public bool EffectiveUpdateCheck => Updates?.Check ?? true;

    /// <summary>The hours between two checks for a newer release, or the default of one day (T704).</summary>
    [JsonIgnore]
    public int EffectiveUpdateIntervalHours => Updates?.IntervalHours ?? AgentSettingsDefaults.UpdateIntervalHours;

    /// <summary>The port of the link listener, or the default.</summary>
    [JsonIgnore]
    public int EffectiveLinkPort => LinkPort ?? AgentSettingsDefaults.LinkPort;
}

/// <summary>Logging settings.</summary>
/// <param name="Level">One of trace, debug, information, warning, error, critical.</param>
public sealed record LoggingSettings(string? Level = null);

/// <summary>Retention settings.</summary>
/// <param name="RunHistoryDays">Days to keep the run history.</param>
/// <param name="AuditLogDays">Days to keep the audit log.</param>
public sealed record RetentionSettings(int? RunHistoryDays = null, int? AuditLogDays = null);

/// <summary>
/// The optional Prometheus endpoint <c>GET /metrics</c> on the HTTPS listener (PLAN.md S08, T709). It is off by default,
/// needs the separate read-only token of <c>POST /api/v1/prometheus/token</c>, and exposes only the numeric and boolean
/// metrics that are listed here.
/// </summary>
/// <param name="Enabled">Whether the endpoint answers; absent means off.</param>
/// <param name="Metrics">The ids of the metrics to expose, for example <c>cpu.usage</c>; absent or empty exposes none. Metrics that are not numeric or boolean are skipped.</param>
public sealed record PrometheusSettings(bool? Enabled = null, IReadOnlyList<string>? Metrics = null);

/// <summary>
/// The reverse link (PLAN.md S07, ADR 0003, T710): the agent dials a WebSocket endpoint of the Home Assistant integration
/// instead of waiting for Home Assistant to dial it, so a roaming device behind NAT is reachable. The credential is not
/// part of the document; it is kept in the secret store under <c>reverse_link.token</c>.
/// </summary>
/// <param name="Enabled">Whether the agent dials; absent means off.</param>
/// <param name="Url">The <c>wss://</c> URL of the endpoint of the integration (a <c>ws://</c> URL only for a loopback host).</param>
/// <param name="Fingerprint">The SHA-256 fingerprint (64 hexadecimal digits) of the certificate to accept; absent means the normal certificate validation.</param>
public sealed record ReverseLinkSettings(bool? Enabled = null, string? Url = null, string? Fingerprint = null);

/// <summary>
/// The optional hardware sensors (PLAN.md D-023, D-045, ADR 0002, T707): temperatures, fan speeds and power draw of the processor, graphics
/// card, mainboard and drives, read through LibreHardwareMonitorLib. Reading them needs a <b>kernel driver</b> (PawnIO) that the user installs
/// separately. While the setting is off the agent loads neither the library nor a driver handle.
/// </summary>
/// <param name="Enabled">Whether the sensors are read; absent means off.</param>
public sealed record HardwareSensorSettings(bool? Enabled = null);

/// <summary>
/// A global hotkey (PLAN.md S16, T708). The SessionHost of the user's session registers the key combination; pressing it raises
/// the event <c>hotkey_pressed</c> with the data <c>name</c>, so Home Assistant can react to the keyboard of the PC.
/// </summary>
/// <param name="Name">The name that the event carries; <c>^[a-z][a-z0-9_]{0,62}$</c>, unique in the list.</param>
/// <param name="Keys">The key combination, for example <c>Ctrl+Alt+F9</c>; see <see cref="HotkeyGesture"/>.</param>
/// <param name="Enabled">False keeps the entry but does not register it; absent means on.</param>
public sealed record HotkeySettings(string Name, string Keys, bool? Enabled = null)
{
    /// <summary>Whether the hotkey is registered (the default).</summary>
    [JsonIgnore]
    public bool EffectiveEnabled => Enabled ?? true;
}

/// <summary>
/// The check for a newer release (PLAN.md S09 item 13, D-046, T704). It is the only request that the agent makes to the internet on
/// its own: a <c>GET</c> of the GitHub releases API of the project. While <see cref="Check"/> is false the agent makes no such request
/// (the action <c>agent.update</c>, which a person starts, still contacts GitHub).
/// </summary>
/// <param name="Check">Whether the agent checks; absent means on.</param>
/// <param name="IntervalHours">The hours between two checks, <see cref="AgentSettingsDefaults.MinUpdateIntervalHours"/> to <see cref="AgentSettingsDefaults.MaxUpdateIntervalHours"/>; absent means 24.</param>
public sealed record UpdateSettings(bool? Check = null, int? IntervalHours = null);

/// <summary>Default values of the agent settings (D-018, O-03).</summary>
public static class AgentSettingsDefaults
{
    /// <summary>Default port of the loopback UI and API listener.</summary>
    public const int UiPort = 8480;

    /// <summary>Default port of the QoffeeLink listener.</summary>
    public const int LinkPort = 8481;

    /// <summary>Default hours between two checks for a newer release (T704).</summary>
    public const int UpdateIntervalHours = 24;

    /// <summary>The shortest interval between two checks; GitHub allows 60 anonymous requests per hour, and a release is not that urgent.</summary>
    public const int MinUpdateIntervalHours = 1;

    /// <summary>The longest interval between two checks (30 days).</summary>
    public const int MaxUpdateIntervalHours = 720;
}
