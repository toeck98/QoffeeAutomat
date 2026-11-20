namespace QoffeeAutomat.Contracts.Api;

/// <summary>The values of <see cref="HubConfigItem.State"/> (PLAN.md S20, T908).</summary>
public static class HubConfigItemStates
{
    /// <summary>The document is part of the hub layer and takes part in the effective configuration.</summary>
    public const string Active = "active";

    /// <summary>The platform does not support the action or sensor type; the document was not stored.</summary>
    public const string SkippedUnsupported = "skipped_unsupported";
}

/// <summary>The values of <see cref="HubShadowedItem.Reason"/>.</summary>
public static class HubShadowReasons
{
    /// <summary>A hub document with the same kind and id is active; the local document is kept but not used.</summary>
    public const string HubDocument = "hub_document";

    /// <summary>The hub locked the local configuration; no local document of this kind is used.</summary>
    public const string LocalConfigLocked = "local_config_locked";
}

/// <summary>The problem codes (<c>ApiProblem.Code</c>) with which local writes are refused while a hub manages the configuration.</summary>
public static class HubConfigProblemCodes
{
    /// <summary>403: the hub locked the local configuration (<c>policy.local_config: locked</c>).</summary>
    public const string LocalConfigLocked = "local_config_locked";

    /// <summary>409: the document comes from the hub and is read-only on the agent.</summary>
    public const string HubDocument = "hub_document";

    /// <summary>403: the hub denies direct links (<c>policy.direct_link: deny</c>): pairing and direct clients are refused.</summary>
    public const string DirectLinkDenied = "direct_link_denied";
}

/// <summary>The policy the hub set for this agent (D-029).</summary>
/// <param name="LocalConfig"><c>allow</c> or <c>locked</c>.</param>
/// <param name="DirectLink"><c>allow</c> or <c>deny</c>.</param>
public sealed record HubConfigPolicy(string LocalConfig, string DirectLink);

/// <summary>A document of the hub layer.</summary>
/// <param name="Kind">One of <c>command</c>, <c>sensor</c>, <c>script</c>, <c>mode</c>, <c>metric_overrides</c>, <c>variables</c>.</param>
/// <param name="Id">The document id.</param>
/// <param name="Hash">The hash that the hub signed (lower-case hex SHA-256).</param>
/// <param name="State">One of <see cref="HubConfigItemStates"/>.</param>
public sealed record HubConfigItem(string Kind, string Id, string Hash, string State);

/// <summary>A local document that is kept but not used.</summary>
/// <param name="Kind">One of <c>command</c>, <c>sensor</c>, <c>script</c>, <c>mode</c>, <c>metric_overrides</c>.</param>
/// <param name="Id">The document id (for metric overrides <c>metrics</c>, for scripts the file name).</param>
/// <param name="Reason">One of <see cref="HubShadowReasons"/>.</param>
public sealed record HubShadowedItem(string Kind, string Id, string Reason);

/// <summary>What the hub configured on this agent: the answer of <c>GET /api/v1/hub/config</c>.</summary>
/// <param name="Active">True while a generation of the hub is applied (the hub layer exists).</param>
/// <param name="Generation">The applied generation; 0 when none.</param>
/// <param name="AppliedAt">When the generation was applied; absent when none.</param>
/// <param name="Policy">The policy in effect; <c>allow</c> and <c>allow</c> without a hub layer.</param>
/// <param name="LocalConfigLocked">True when local changes to commands, sensors, scripts, modes and metric overrides are refused and the local layer is inactive.</param>
/// <param name="DirectLinkDenied">True when the agent does not advertise itself, refuses pairing and has no direct link clients.</param>
/// <param name="Items">The documents of the hub layer, ordered by kind and id.</param>
/// <param name="Shadowed">The local documents that are not used, ordered by kind and id.</param>
/// <param name="Variables">The names of the hub variables, which every run receives as <c>QA_HUBVAR_&lt;NAME&gt;</c>; the values are not shown.</param>
/// <param name="LastError">Why the last generation of the hub was rejected; absent when it was applied or none arrived.</param>
public sealed record HubConfigState(
    bool Active,
    long Generation,
    DateTimeOffset? AppliedAt,
    HubConfigPolicy Policy,
    bool LocalConfigLocked,
    bool DirectLinkDenied,
    IReadOnlyList<HubConfigItem> Items,
    IReadOnlyList<HubShadowedItem> Shadowed,
    IReadOnlyList<string> Variables,
    string? LastError = null);
