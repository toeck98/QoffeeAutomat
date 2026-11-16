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
/// <param name="Logging">Logging settings.</param>
/// <param name="Retention">Retention settings.</param>
/// <param name="Revision">Revision, incremented on every save.</param>
/// <param name="UpdatedAt">Time of the last save.</param>
/// <param name="Origin">Where the document was authored.</param>
public sealed record AgentSettingsDocument(
    int SchemaVersion,
    string Name,
    int? UiPort = null,
    int? LinkPort = null,
    bool? RemoteUi = null,
    bool? Zeroconf = null,
    LoggingSettings? Logging = null,
    RetentionSettings? Retention = null,
    int? Revision = null,
    DateTimeOffset? UpdatedAt = null,
    string? Origin = null) : IConfigDocument
{
    /// <summary>The port of the UI listener, or the default.</summary>
    [JsonIgnore]
    public int EffectiveUiPort => UiPort ?? AgentSettingsDefaults.UiPort;

    /// <summary>Whether the zeroconf advertisement is on (the default).</summary>
    [JsonIgnore]
    public bool EffectiveZeroconf => Zeroconf ?? true;

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

/// <summary>Default values of the agent settings (D-018, O-03).</summary>
public static class AgentSettingsDefaults
{
    /// <summary>Default port of the loopback UI and API listener.</summary>
    public const int UiPort = 8480;

    /// <summary>Default port of the QoffeeLink listener.</summary>
    public const int LinkPort = 8481;
}
