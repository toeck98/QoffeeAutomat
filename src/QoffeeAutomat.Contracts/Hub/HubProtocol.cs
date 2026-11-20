using System.Collections.Frozen;

namespace QoffeeAutomat.Contracts.Hub;

/// <summary>Constants of the hub extensions of QoffeeLink (PLAN.md S20, task T901).</summary>
public static class HubProtocol
{
    /// <summary>Default port of the hub (PLAN.md O-03).</summary>
    public const int DefaultPort = 8490;

    /// <summary>Path of the enrollment endpoint (HTTPS POST, enrollment token in the body).</summary>
    public const string EnrollPath = "/hub/v1/enroll";

    /// <summary>Path of the agent link (WebSocket, agent credential as bearer token). The agent dials out.</summary>
    public const string AgentLinkPath = "/hub/v1/agent";

    /// <summary>Path of the pairing endpoint of the fleet link (HTTPS POST, pairing code).</summary>
    public const string PairPath = "/hub/v1/pair";

    /// <summary>Path of the fleet link (WebSocket, fleet token as bearer token).</summary>
    public const string FleetLinkPath = "/hub/v1/fleet";

    /// <summary>Prefix of an enrollment string: <c>qahub1.&lt;base64url(JSON)&gt;</c>.</summary>
    public const string EnrollmentStringPrefix = "qahub1.";

    /// <summary>Length of an ECDSA P-256 signature in IEEE P1363 form (r followed by s).</summary>
    public const int SignatureBytes = 64;
}

/// <summary>The optional features of the hub extensions, announced in <c>hello.features</c> and <c>client_hello.features</c>.</summary>
public static class HubFeatures
{
    /// <summary>Desired-state configuration distribution (agent link).</summary>
    public const string ConfigSync = "config_sync";

    /// <summary>Tunnel to the management API of the agent (agent link).</summary>
    public const string MgmtTunnel = "mgmt_tunnel";

    /// <summary>One connection for all agents of a hub (fleet link). Announced by the hub in its hello.</summary>
    public const string Fleet = "fleet";
}

/// <summary>The values of the envelope field <c>t</c> that the hub extensions add (PLAN.md S20).</summary>
public static class HubMessageTypes
{
    /// <summary>Hub to agent request: the signed desired state.</summary>
    public const string ConfigDesired = "config_desired";

    /// <summary>Hub to agent request: the documents the agent lacks.</summary>
    public const string ConfigDocuments = "config_documents";

    /// <summary>Agent to hub: outcome of applying a generation.</summary>
    public const string ConfigStatus = "config_status";

    /// <summary>Hub to agent request: a call of the management API.</summary>
    public const string MgmtRequest = "mgmt_request";

    /// <summary>Hub to agent request: open a Server-Sent-Events endpoint.</summary>
    public const string MgmtStreamOpen = "mgmt_stream_open";

    /// <summary>Agent to hub: one event of an open stream.</summary>
    public const string MgmtStreamData = "mgmt_stream_data";

    /// <summary>Either direction: a stream ended.</summary>
    public const string MgmtStreamClose = "mgmt_stream_close";

    /// <summary>Hub to agent request: the new credential.</summary>
    public const string CredentialRotate = "credential_rotate";

    /// <summary>Hub to agent: the agent is removed from the hub.</summary>
    public const string Unenroll = "unenroll";

    /// <summary>Client to hub request (fleet link): list the agents.</summary>
    public const string GetAgents = "get_agents";

    /// <summary>Hub to client (fleet link): an agent entered the scope.</summary>
    public const string AgentAdded = "agent_added";

    /// <summary>Hub to client (fleet link): an agent left the scope.</summary>
    public const string AgentRemoved = "agent_removed";

    /// <summary>Hub to client (fleet link): an agent went online or offline.</summary>
    public const string AgentStatus = "agent_status";
}

/// <summary>Error codes that the hub adds to those of QoffeeLink v1 (PLAN.md S20). Receivers must tolerate unknown codes.</summary>
public static class HubErrorCodes
{
    /// <summary>The agent id is unknown or outside the scope of the fleet token.</summary>
    public const string UnknownAgent = "unknown_agent";

    /// <summary>The agent is not connected to the hub.</summary>
    public const string AgentOffline = "agent_offline";

    /// <summary>The caller may not do this: a fleet token out of scope, a tunnel that is switched off, or a role that is too low.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>The credential was revoked. Sent in the HTTP 401 response of the agent link, not in a frame.</summary>
    public const string Revoked = "revoked";

    /// <summary>All error codes added by the hub.</summary>
    public static FrozenSet<string> All { get; } = new[]
    {
        UnknownAgent,
        AgentOffline,
        Forbidden,
        Revoked,
    }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>The kinds of document that <c>config_sync</c> distributes.</summary>
public static class DocumentKinds
{
    /// <summary>A command document.</summary>
    public const string Command = "command";

    /// <summary>A custom sensor document.</summary>
    public const string Sensor = "sensor";

    /// <summary>A script; its body is the script text.</summary>
    public const string Script = "script";

    /// <summary>A mode group document.</summary>
    public const string Mode = "mode";

    /// <summary>The metric overrides document.</summary>
    public const string MetricOverrides = "metric_overrides";

    /// <summary>Key/value pairs exposed to every run as <c>QA_HUBVAR_&lt;NAME&gt;</c>.</summary>
    public const string Variables = "variables";

    /// <summary>All document kinds.</summary>
    public static FrozenSet<string> All { get; } = new[]
    {
        Command,
        Sensor,
        Script,
        Mode,
        MetricOverrides,
        Variables,
    }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>The values of <c>policy.local_config</c> (D-029).</summary>
public static class LocalConfigPolicies
{
    /// <summary>The local layer stays active and editable.</summary>
    public const string Allow = "allow";

    /// <summary>The local layer is inactive and local changes are rejected.</summary>
    public const string Locked = "locked";
}

/// <summary>The values of <c>policy.direct_link</c> (D-029).</summary>
public static class DirectLinkPolicies
{
    /// <summary>The agent still accepts direct pairing and link clients.</summary>
    public const string Allow = "allow";

    /// <summary>The agent stops advertising, rejects pairing and closes direct link clients.</summary>
    public const string Deny = "deny";
}

/// <summary>The values of <c>config_status.state</c>.</summary>
public static class ConfigStates
{
    /// <summary>The generation is active.</summary>
    public const string Applied = "applied";

    /// <summary>The generation was rejected; the previous generation stays active.</summary>
    public const string Failed = "failed";
}

/// <summary>The values of the item <c>state</c> in <c>config_status.results</c>.</summary>
public static class ConfigItemStates
{
    /// <summary>The document is part of the active configuration.</summary>
    public const string Applied = "applied";

    /// <summary>The platform does not support the document's action or sensor type.</summary>
    public const string SkippedUnsupported = "skipped_unsupported";

    /// <summary>The document did not pass the validators.</summary>
    public const string Invalid = "invalid";
}

/// <summary>The roles of the hub user behind a tunnelled request.</summary>
public static class TunnelRoles
{
    /// <summary>May only issue GET requests.</summary>
    public const string Viewer = "viewer";

    /// <summary>Additionally the run and test endpoints.</summary>
    public const string Operator = "operator";

    /// <summary>Everything; hub documents stay read-only.</summary>
    public const string Admin = "admin";
}

/// <summary>The values of <c>power_state</c> in the fleet link: a <see cref="Link.ByeReasons"/> value or <see cref="Unknown"/>.</summary>
public static class PowerStates
{
    /// <summary>No <c>bye</c> was seen.</summary>
    public const string Unknown = "unknown";
}

/// <summary>The values of <c>state</c> in the response of the enrollment endpoint.</summary>
public static class EnrollStates
{
    /// <summary>The agent is enrolled; the response carries the credential.</summary>
    public const string Enrolled = "enrolled";

    /// <summary>An administrator has to accept the agent; the response carries <c>retry_after</c>.</summary>
    public const string Pending = "pending";
}
