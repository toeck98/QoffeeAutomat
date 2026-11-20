using System.Text.Json;
using System.Text.Json.Serialization;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Contracts.Hub;

/// <summary>The effective policy of an agent (D-029); the hub resolves <c>inherit</c> before it signs.</summary>
/// <param name="LocalConfig">One of <see cref="LocalConfigPolicies"/>.</param>
/// <param name="DirectLink">One of <see cref="DirectLinkPolicies"/>.</param>
public sealed record PolicyInfo(string LocalConfig, string DirectLink);

/// <summary>An entry of the signed item list: a document and the hash of its body.</summary>
/// <param name="Kind">One of <see cref="DocumentKinds"/>.</param>
/// <param name="Id">The document id.</param>
/// <param name="Hash">Lower-case hex SHA-256 (see <see cref="DocumentHasher"/>).</param>
public sealed record DesiredItem(string Kind, string Id, string Hash);

/// <summary>Names a document without its content.</summary>
/// <param name="Kind">One of <see cref="DocumentKinds"/>.</param>
/// <param name="Id">The document id.</param>
public sealed record DocumentRef(string Kind, string Id);

/// <summary>A document with its body.</summary>
/// <param name="Kind">One of <see cref="DocumentKinds"/>.</param>
/// <param name="Id">The document id.</param>
/// <param name="Hash">Lower-case hex SHA-256 of the body.</param>
/// <param name="Body">A JSON object, for scripts a JSON string holding the script text.</param>
public sealed record HubDocument(string Kind, string Id, string Hash, JsonElement Body);

/// <summary>
/// Hub to agent request (feature <c>config_sync</c>): the signed desired state. The signature covers the canonical
/// JSON of <see cref="DesiredState"/>; the agent id is not in the frame because it is the agent's own id.
/// </summary>
/// <param name="Id">Request id chosen by the hub.</param>
/// <param name="Generation">Generation, increasing per agent; the first one is 1.</param>
/// <param name="Policy">The policy.</param>
/// <param name="Items">The items, sorted by kind and id in ordinal order; the order is part of the signature.</param>
/// <param name="Sig">Base64 ECDSA P-256 signature (see <see cref="DesiredStateSigner"/>).</param>
public sealed record ConfigDesiredMessage(
    long Id,
    long Generation,
    PolicyInfo Policy,
    IReadOnlyList<DesiredItem> Items,
    string Sig) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => HubMessageTypes.ConfigDesired;
}

/// <summary>The data of the result of <c>config_desired</c>: the documents the agent lacks.</summary>
/// <param name="Need">The documents to send in <c>config_documents</c>; empty when everything is cached.</param>
public sealed record ConfigDesiredResultData(IReadOnlyList<DocumentRef> Need);

/// <summary>
/// Hub to agent request (feature <c>config_sync</c>): documents the agent asked for. The set is split over several
/// frames when it exceeds the frame limit; the last frame has <c>Final</c> true. The result data is an empty object.
/// </summary>
/// <param name="Id">Request id chosen by the hub.</param>
/// <param name="Generation">The generation the documents belong to.</param>
/// <param name="Documents">The documents.</param>
/// <param name="Final">Whether this is the last frame of the generation.</param>
public sealed record ConfigDocumentsMessage(
    long Id,
    long Generation,
    IReadOnlyList<HubDocument> Documents,
    bool Final) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => HubMessageTypes.ConfigDocuments;
}

/// <summary>The outcome of one document in <see cref="ConfigStatusMessage"/>.</summary>
/// <param name="Kind">One of <see cref="DocumentKinds"/>.</param>
/// <param name="Id">The document id.</param>
/// <param name="State">One of <see cref="ConfigItemStates"/>.</param>
/// <param name="Errors">Validation errors or the reason for skipping; absent when there are none.</param>
public sealed record ConfigItemResult(
    string Kind,
    string Id,
    string State,
    IReadOnlyList<string>? Errors = null);

/// <summary>
/// Agent to hub (feature <c>config_sync</c>): the outcome of applying a generation. Also sent once after every
/// handshake with the last applied generation (0 and no results when none was applied yet). A notification: it has no
/// request id.
/// </summary>
/// <param name="Generation">The generation; 0 when none was applied.</param>
/// <param name="State">One of <see cref="ConfigStates"/>.</param>
/// <param name="Results">The state of each document.</param>
public sealed record ConfigStatusMessage(
    long Generation,
    string State,
    IReadOnlyList<ConfigItemResult> Results) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => HubMessageTypes.ConfigStatus;
}

/// <summary>
/// The content of the signature of a desired state: the canonical JSON of this record is signed (PLAN.md S20,
/// D-027). <see cref="AgentId"/> binds the state to one agent.
/// </summary>
/// <param name="AgentId">The id of the agent the state is for.</param>
/// <param name="Generation">The generation.</param>
/// <param name="Policy">The policy.</param>
/// <param name="Items">The items in the order of the frame.</param>
public sealed record DesiredState(
    string AgentId,
    long Generation,
    PolicyInfo Policy,
    IReadOnlyList<DesiredItem> Items)
{
    /// <summary>Sorts items by kind and then by id in ordinal order, the order the hub signs.</summary>
    /// <param name="items">The items.</param>
    /// <returns>The sorted items.</returns>
    public static IReadOnlyList<DesiredItem> SortItems(IEnumerable<DesiredItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items
            .OrderBy(static item => item.Kind, StringComparer.Ordinal)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }
}
