using System.Text.Json;
using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Contracts.Api;

/// <summary>Response of <c>GET /api/v1/commands</c>.</summary>
/// <param name="Commands">All commands, enabled or not, ordered by id.</param>
public sealed record CommandList(IReadOnlyList<CommandDocument> Commands);

/// <summary>Response of <c>GET /api/v1/sensors</c>.</summary>
/// <param name="Sensors">All custom sensors, ordered by id.</param>
public sealed record SensorList(IReadOnlyList<SensorDocument> Sensors);

/// <summary>Body of <c>PATCH /api/v1/commands/{id}</c>: only the members that are present change.</summary>
/// <param name="Enabled">Enables or disables the command.</param>
/// <param name="ExposeToHa">Shows the command to Home Assistant or hides it.</param>
public sealed record CommandPatch(bool? Enabled = null, bool? ExposeToHa = null);

/// <summary>A script file in a listing.</summary>
/// <param name="Name">The file name in the scripts directory.</param>
/// <param name="ETag">SHA-256 of the file bytes in lower-case hex; the value to send in <c>If-Match</c>.</param>
/// <param name="ModifiedAt">The time of the last modification.</param>
public sealed record ScriptSummary(string Name, string ETag, DateTimeOffset ModifiedAt);

/// <summary>Response of <c>GET /api/v1/scripts</c>.</summary>
/// <param name="Scripts">The script files, ordered by name.</param>
public sealed record ScriptSummaryList(IReadOnlyList<ScriptSummary> Scripts);

/// <summary>A script file with its text (<c>GET /api/v1/scripts/{name}</c>).</summary>
/// <param name="Name">The file name.</param>
/// <param name="Content">The text; UTF-8, a leading byte order mark is removed.</param>
/// <param name="ETag">SHA-256 of the file bytes in lower-case hex.</param>
/// <param name="ModifiedAt">The time of the last modification.</param>
public sealed record ScriptContent(string Name, string Content, string ETag, DateTimeOffset ModifiedAt);

/// <summary>Body of <c>PUT /api/v1/scripts/{name}</c>.</summary>
/// <param name="Content">The new text of the script (at most 1 MiB).</param>
public sealed record ScriptWriteRequest(string Content);

/// <summary>A metric of the catalog with its effective settings and, when somebody samples it, its current value (<c>GET /api/v1/metrics</c>).</summary>
/// <param name="Id">The metric id.</param>
/// <param name="Name">The display name.</param>
/// <param name="Kind"><c>sensor</c> or <c>binary_sensor</c>.</param>
/// <param name="ValueType">The type of the value.</param>
/// <param name="Context"><c>system</c> or <c>user</c>.</param>
/// <param name="Unit">The unit.</param>
/// <param name="DeviceClass">The Home Assistant device class.</param>
/// <param name="StateClass">The Home Assistant state class.</param>
/// <param name="EntityCategory">The Home Assistant entity category.</param>
/// <param name="Icon">The icon.</param>
/// <param name="Precision">The number of decimals.</param>
/// <param name="Options">The allowed values of an enum metric.</param>
/// <param name="EnabledDefault">Whether the metric is on unless an override says otherwise.</param>
/// <param name="Enabled">Whether the metric is sampled when somebody subscribes to it.</param>
/// <param name="ExposeToHa">Whether the metric appears in the manifest.</param>
/// <param name="IntervalSeconds">The sampling interval while subscribed; zero means event-driven.</param>
/// <param name="Deadband">The minimum change before a new value is published.</param>
/// <param name="Custom">True for the metric of a custom sensor.</param>
/// <param name="HasValue">True when the metric was sampled since somebody last subscribed to it.</param>
/// <param name="Value">The last published value; absent when the metric is unavailable or has no value yet.</param>
public sealed record MetricInfo(
    string Id,
    string Name,
    string Kind,
    string ValueType,
    string? Context,
    string? Unit,
    string? DeviceClass,
    string? StateClass,
    string? EntityCategory,
    string? Icon,
    int? Precision,
    IReadOnlyList<string>? Options,
    bool? EnabledDefault,
    bool Enabled,
    bool ExposeToHa,
    double IntervalSeconds,
    double? Deadband,
    bool Custom,
    bool HasValue,
    JsonElement? Value);

/// <summary>Response of <c>GET /api/v1/metrics</c>.</summary>
/// <param name="Metrics">The metrics, as the engine lists them.</param>
public sealed record MetricList(IReadOnlyList<MetricInfo> Metrics);

/// <summary>The outcome of a dry run of a sensor draft (<c>POST /api/v1/sensors/test</c>).</summary>
/// <param name="Succeeded">True when the sensor produced a value.</param>
/// <param name="Value">The value; absent when the sensor failed.</param>
/// <param name="Error">Why the sensor failed; absent when it succeeded.</param>
/// <param name="DurationMs">How long the sample took.</param>
public sealed record SensorTestResult(bool Succeeded, JsonElement? Value, string? Error, long DurationMs);

/// <summary>Body of <c>PUT /api/v1/sensors/{id}/value</c>: the new value of a sensor of type <c>state</c> (T714).</summary>
/// <param name="Value">
/// The value: a number for the value type <c>number</c>, a boolean for <c>bool</c>, a string for <c>string</c>, <c>timestamp</c> (ISO 8601) and
/// <c>enum</c> (one of the options). A string is also accepted for a number or a boolean, as the command line sends it.
/// </param>
public sealed record SensorValueRequest(JsonElement Value);

/// <summary>The value of a sensor of type <c>state</c> (<c>GET</c> and <c>PUT /api/v1/sensors/{id}/value</c>, T714).</summary>
/// <param name="Id">The sensor id.</param>
/// <param name="Value">The value as it is published (rounded to the precision of the sensor); absent when the sensor has none, because nothing was set and it has no initial value.</param>
/// <param name="UpdatedAt">When the value was set; absent while the sensor shows its initial value or none.</param>
public sealed record SensorValue(string Id, JsonElement? Value, DateTimeOffset? UpdatedAt);

/// <summary>Response of <c>GET /api/v1/prometheus</c>: the state of the optional Prometheus endpoint (T709).</summary>
/// <param name="Enabled">Whether <c>GET /metrics</c> on the HTTPS listener answers (the setting <c>prometheus.enabled</c>).</param>
/// <param name="TokenSet">Whether a read-only token exists; without one every request is refused.</param>
/// <param name="Metrics">The ids of the metrics that are exported (the setting <c>prometheus.metrics</c>).</param>
public sealed record PrometheusStatus(bool Enabled, bool TokenSet, IReadOnlyList<string> Metrics);

/// <summary>Response of <c>POST /api/v1/prometheus/token</c>: the new read-only token of the Prometheus endpoint (T709).</summary>
/// <param name="Token">The token. It is shown only in this answer; the agent keeps a hash and cannot show it again.</param>
public sealed record PrometheusToken(string Token);

/// <summary>The platform as shown in the status.</summary>
/// <param name="Kind">The selected platform: <c>windows</c>, <c>linux</c> or <c>simulated</c>.</param>
/// <param name="Os">The operating system family.</param>
/// <param name="OsVersion">The operating system version.</param>
/// <param name="Architecture">The CPU architecture.</param>
/// <param name="Hostname">The host name.</param>
public sealed record PlatformStatus(string Kind, string Os, string OsVersion, string Architecture, string Hostname);

/// <summary>The session of the interactive user as the agent sees it.</summary>
/// <param name="Ready">The session helper accepts user commands.</param>
/// <param name="UserName">The logged-in user; absent when nobody is logged in.</param>
/// <param name="Locked">The session is locked.</param>
/// <param name="UserIsAdministrator">The user is an administrator.</param>
public sealed record SessionStatus(bool Ready, string? UserName, bool Locked, bool UserIsAdministrator);

/// <summary>QoffeeLink as shown in the status.</summary>
/// <param name="Port">The port of the HTTPS link listener.</param>
/// <param name="PairedClients">The number of paired clients.</param>
/// <param name="Connections">The number of open QoffeeLink connections.</param>
public sealed record LinkStatus(int Port, int PairedClients, int Connections);

/// <summary>What became of one global hotkey of the settings (T708).</summary>
/// <param name="Name">The name of the hotkey.</param>
/// <param name="Keys">The key combination as configured.</param>
/// <param name="State">
/// <c>registered</c>, <c>conflict</c> (another program or another hotkey already uses the combination) or <c>invalid</c> (the combination
/// cannot be registered).
/// </param>
/// <param name="Message">An explanation when the state is not <c>registered</c>.</param>
public sealed record HotkeyStatusEntry(string Name, string Keys, string State, string? Message = null);

/// <summary>Response of <c>GET /api/v1/hotkeys</c>: the outcome of the last registration, so that the UI can report conflicts.</summary>
/// <param name="Supported">False on a platform without global hotkeys; the settings are kept but nothing is registered.</param>
/// <param name="Hotkeys">One entry per enabled hotkey that a session helper has answered for; empty when none has yet (nobody is logged on).</param>
public sealed record HotkeyStatusResponse(bool Supported, IReadOnlyList<HotkeyStatusEntry> Hotkeys);

/// <summary>Values of <see cref="HardwareSensorStatus.State"/> (T707).</summary>
public static class HardwareSensorStates
{
    /// <summary>The setting is off: no library and no driver is loaded.</summary>
    public const string Off = "off";

    /// <summary>The setting is on and the library is being opened (this can take a few seconds).</summary>
    public const string Starting = "starting";

    /// <summary>The library is open and sensors are read.</summary>
    public const string Running = "running";

    /// <summary>The library is open but the kernel driver is not installed, so only the sensors that need no driver are read (drives, graphics cards).</summary>
    public const string DriverMissing = "driver_missing";

    /// <summary>The library could not be opened; <see cref="HardwareSensorStatus.Message"/> says why.</summary>
    public const string Failed = "failed";

    /// <summary>The platform has no hardware sensors.</summary>
    public const string Unsupported = "unsupported";
}

/// <summary>Response of <c>GET /api/v1/hardware-sensors</c>: what became of the optional hardware sensors (T707, ADR 0002).</summary>
/// <param name="Supported">False on a platform without hardware sensors; the setting is kept but nothing is read.</param>
/// <param name="Enabled">Whether the setting is on.</param>
/// <param name="State">One of <see cref="HardwareSensorStates"/>.</param>
/// <param name="Driver">The kernel driver that the library needs for processor and mainboard sensors, for the UI warning (<c>PawnIO</c>).</param>
/// <param name="DriverInstalled">Whether that driver is installed; null while the setting is off (the agent does not look).</param>
/// <param name="SensorCount">The number of sensors that were found; 0 while the setting is off.</param>
/// <param name="Message">An explanation when the state is <c>failed</c> or <c>driver_missing</c>.</param>
public sealed record HardwareSensorStatus(
    bool Supported,
    bool Enabled,
    string State,
    string Driver,
    bool? DriverInstalled,
    int SensorCount,
    string? Message = null);

/// <summary>Response of <c>GET /api/v1/status</c>.</summary>
/// <param name="Version">The agent version.</param>
/// <param name="AgentId">The persistent agent id.</param>
/// <param name="Name">The agent name.</param>
/// <param name="StartedAt">When the agent started.</param>
/// <param name="UptimeSeconds">How long the agent has been running.</param>
/// <param name="Platform">The platform.</param>
/// <param name="Session">The user session.</param>
/// <param name="Link">QoffeeLink.</param>
/// <param name="Paused">True while every run is rejected with <c>paused</c>.</param>
/// <param name="ActiveRuns">The number of runs that have not finished.</param>
/// <param name="ManifestRev">The revision of the manifest.</param>
/// <param name="ConfigProblems">The number of configuration files that are skipped because they are invalid.</param>
/// <param name="Hub">The hub link (T905); absent from an agent without hub support.</param>
public sealed record AgentStatus(
    string Version,
    string AgentId,
    string Name,
    DateTimeOffset StartedAt,
    long UptimeSeconds,
    PlatformStatus Platform,
    SessionStatus Session,
    LinkStatus Link,
    bool Paused,
    int ActiveRuns,
    string ManifestRev,
    int ConfigProblems,
    HubLinkStatus? Hub = null);
