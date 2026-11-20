namespace QoffeeAutomat.Contracts.Api;

/// <summary>The values of <see cref="HubLinkStatus.State"/> (PLAN.md S20, T905).</summary>
public static class HubLinkStates
{
    /// <summary>The agent has not joined a hub, or has left it.</summary>
    public const string NotJoined = "not_joined";

    /// <summary>The agent enrolled with a token that needs approval; an administrator of the hub has to accept it.</summary>
    public const string Pending = "pending";

    /// <summary>The agent is joined and the hub link is open.</summary>
    public const string Connected = "connected";

    /// <summary>The agent is joined but the hub link is not open (the hub cannot be reached, the certificate does not match, the agent waits to retry).</summary>
    public const string Disconnected = "disconnected";

    /// <summary>The hub revoked the credential; the agent deleted it and does not dial until it is joined again.</summary>
    public const string Revoked = "revoked";
}

/// <summary>What the agent knows about its hub link: the answer of <c>GET /api/v1/hub</c> and the member <c>hub</c> of <c>GET /api/v1/status</c>.</summary>
/// <param name="State">One of <see cref="HubLinkStates"/>.</param>
/// <param name="ManagementTunnel">Whether the management tunnel is allowed (the local switch); on by default. It is only announced to a hub while it is on.</param>
/// <param name="HubId">The id of the hub; absent while the agent is not joined.</param>
/// <param name="HubName">The name of the hub.</param>
/// <param name="Url">The address of the hub, without credentials.</param>
/// <param name="Detail">Why the link is in its state, for example the reason of the last failed attempt; never contains a credential.</param>
/// <param name="ConfigGeneration">The generation of the hub configuration that is applied; 0 when none (T908).</param>
/// <param name="LocalConfigLocked">True while the hub has locked the local configuration: the UI shows hub documents only and disables editing (T908).</param>
/// <param name="DirectLinkDenied">True while the hub denies direct Home Assistant links (T908).</param>
public sealed record HubLinkStatus(
    string State,
    bool ManagementTunnel,
    string? HubId = null,
    string? HubName = null,
    string? Url = null,
    string? Detail = null,
    long ConfigGeneration = 0,
    bool LocalConfigLocked = false,
    bool DirectLinkDenied = false);

/// <summary>Body of <c>POST /api/v1/hub/join</c>.</summary>
/// <param name="Enrollment">The enrollment string <c>qahub1.&lt;base64url&gt;</c> that the hub shows. It contains a token, so it is handled like a password.</param>
public sealed record HubJoinRequest(string Enrollment);

/// <summary>Body of <c>POST /api/v1/hub/leave</c>.</summary>
/// <param name="Documents">What becomes of documents that the hub distributed: <c>convert</c> (they become local documents) or <c>remove</c> (the default). A convert keeps the hub's commands, sensors, scripts, modes and metric overrides as local documents; the hub variables are dropped.</param>
public sealed record HubLeaveRequest(string? Documents = null);

/// <summary>Body of <c>PATCH /api/v1/hub</c>.</summary>
/// <param name="ManagementTunnel">True lets the hub use the management tunnel; false switches it off locally.</param>
public sealed record HubSettingsRequest(bool ManagementTunnel);
