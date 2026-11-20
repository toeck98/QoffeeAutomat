using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Contracts.Hub;

/// <summary>The hub user on whose behalf a tunnelled request is made; the agent writes it to its audit log.</summary>
/// <param name="User">The hub user name.</param>
/// <param name="Role">One of <see cref="TunnelRoles"/>.</param>
public sealed record TunnelPrincipal(string User, string Role);

/// <summary>
/// Hub to agent request (feature <c>mgmt_tunnel</c>): one call of the management API. The result data is
/// <see cref="MgmtRequestResultData"/>.
/// </summary>
/// <param name="Id">Request id chosen by the hub.</param>
/// <param name="Method">GET, POST, PUT, PATCH or DELETE.</param>
/// <param name="Path">A path of the management API; starts with <c>/api/v1/</c> and has no query string.</param>
/// <param name="Principal">The hub user.</param>
/// <param name="Query">Decoded query parameters; absent when there are none.</param>
/// <param name="Body">The request body as a JSON value; absent when there is none.</param>
public sealed record MgmtRequestMessage(
    long Id,
    string Method,
    string Path,
    TunnelPrincipal Principal,
    IReadOnlyDictionary<string, string>? Query = null,
    JsonElement? Body = null) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => HubMessageTypes.MgmtRequest;
}

/// <summary>The data of the result of <c>mgmt_request</c>.</summary>
/// <param name="Status">The HTTP status of the tunnelled call.</param>
/// <param name="Body">The response body as a JSON value; absent when there is none.</param>
public sealed record MgmtRequestResultData(int Status, JsonElement? Body = null);

/// <summary>
/// Hub to agent request (feature <c>mgmt_tunnel</c>): open a Server-Sent-Events endpoint. The result data is
/// <see cref="MgmtStreamOpenResultData"/>.
/// </summary>
/// <param name="Id">Request id chosen by the hub.</param>
/// <param name="Path">The path of the endpoint; starts with <c>/api/v1/</c>.</param>
/// <param name="Principal">The hub user.</param>
/// <param name="Query">Decoded query parameters of the endpoint (for example the metrics of <c>/api/v1/metrics/stream</c>); absent when there are none.</param>
public sealed record MgmtStreamOpenMessage(
    long Id,
    string Path,
    TunnelPrincipal Principal,
    IReadOnlyDictionary<string, string>? Query = null) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => HubMessageTypes.MgmtStreamOpen;
}

/// <summary>The data of the result of <c>mgmt_stream_open</c>.</summary>
/// <param name="Sid">The id of the stream, chosen by the agent.</param>
public sealed record MgmtStreamOpenResultData(string Sid);

/// <summary>Agent to hub (feature <c>mgmt_tunnel</c>): one Server-Sent-Event of an open stream.</summary>
/// <param name="Sid">The stream id.</param>
/// <param name="Event">The event name.</param>
/// <param name="Data">The data field of the event as text.</param>
public sealed record MgmtStreamDataMessage(string Sid, string Event, string Data) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => HubMessageTypes.MgmtStreamData;
}

/// <summary>Either direction (feature <c>mgmt_tunnel</c>): a stream ended.</summary>
/// <param name="Sid">The stream id.</param>
public sealed record MgmtStreamCloseMessage(string Sid) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => HubMessageTypes.MgmtStreamClose;
}

/// <summary>
/// Hub to agent request (always available on the agent link): the new credential. The old one stays valid until the
/// agent has answered with an ok result. The frame holds a secret: do not compress it and do not log it.
/// </summary>
/// <param name="Id">Request id chosen by the hub.</param>
/// <param name="Credential">The new credential, 32 random bytes as base64url without padding.</param>
public sealed record CredentialRotateMessage(long Id, string Credential) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => HubMessageTypes.CredentialRotate;

    /// <summary>Hides the credential from the generated <c>ToString</c> so that it does not end up in logs.</summary>
    /// <returns>A text without the credential.</returns>
    public override string ToString() =>
        nameof(CredentialRotateMessage) + " { Id = " + Id.ToString(CultureInfo.InvariantCulture) + " }";
}

/// <summary>Hub to agent (always available on the agent link): the agent is removed from the hub.</summary>
/// <param name="Reason">An open vocabulary, for example <c>removed</c>.</param>
public sealed record UnenrollMessage(string Reason) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => HubMessageTypes.Unenroll;
}
