using System.Text.Json;
using QoffeeAutomat.Contracts.Hub;

namespace QoffeeAutomat.Core.Config.HubLayer;

/// <summary>A document of the hub layer as the signed item list names it, with what the agent did with it.</summary>
/// <param name="Kind">One of <see cref="DocumentKinds"/>.</param>
/// <param name="Id">The document id (for scripts the file name).</param>
/// <param name="Hash">The hash that the hub signed (lower-case hex SHA-256).</param>
/// <param name="State">One of <see cref="QoffeeAutomat.Contracts.Api.HubConfigItemStates"/>: <c>active</c>, or <c>skipped_unsupported</c> when the document is not stored.</param>
public sealed record HubLayerItem(string Kind, string Id, string Hash, string State);

/// <summary>
/// What the hub layer holds (PLAN.md S20, D-028): the applied generation, when it was applied, the policy that came with it and the item list. A layer that
/// never received a generation has generation 0 and the default policy, which restricts nothing.
/// </summary>
/// <param name="Generation">The applied generation; 0 when none.</param>
/// <param name="AppliedAt">When the generation was applied; null when none.</param>
/// <param name="Policy">The policy of the generation.</param>
/// <param name="Items">The items, ordered by kind and id in ordinal order.</param>
/// <param name="LastError">Why the last generation of the hub was rejected; kept in memory only and cleared by the next applied generation.</param>
public sealed record HubLayerState(
    long Generation,
    DateTimeOffset? AppliedAt,
    PolicyInfo Policy,
    IReadOnlyList<HubLayerItem> Items,
    string? LastError = null)
{
    /// <summary>The state of an agent that no hub manages: no generation, nothing restricted.</summary>
    public static HubLayerState None { get; } = new(
        0,
        null,
        new PolicyInfo(LocalConfigPolicies.Allow, DirectLinkPolicies.Allow),
        []);

    /// <summary>True while a generation of the hub is applied.</summary>
    public bool IsActive => Generation > 0;

    /// <summary>True when the hub locked the local configuration: the local layer is inactive and local writes are refused.</summary>
    public bool LocalConfigLocked => IsActive && string.Equals(Policy.LocalConfig, LocalConfigPolicies.Locked, StringComparison.Ordinal);

    /// <summary>True when the hub denies direct links: no zeroconf, no pairing, no direct clients.</summary>
    public bool DirectLinkDenied => IsActive && string.Equals(Policy.DirectLink, DirectLinkPolicies.Deny, StringComparison.Ordinal);
}

/// <summary>One entry of a generation that the agent is asked to apply.</summary>
/// <param name="Kind">One of <see cref="DocumentKinds"/>.</param>
/// <param name="Id">The document id.</param>
/// <param name="Hash">The hash from the signed item list.</param>
/// <param name="Body">The document body; null keeps the document of the current generation that has the same kind, id and hash.</param>
public sealed record HubGenerationEntry(string Kind, string Id, string Hash, JsonElement? Body = null);

/// <summary>A generation that passed the signature and hash checks, ready for validation and storage.</summary>
/// <param name="Generation">The generation number.</param>
/// <param name="Policy">The policy.</param>
/// <param name="Entries">Every item of the generation.</param>
public sealed record HubGeneration(long Generation, PolicyInfo Policy, IReadOnlyList<HubGenerationEntry> Entries);

/// <summary>The outcome of <see cref="IHubConfigLayer.ApplyAsync"/>.</summary>
/// <param name="Applied">True when the generation is active now; false when nothing changed.</param>
/// <param name="Results">The state of every item. After a failed apply the invalid items say why; the others are reported as they would have been.</param>
/// <param name="Error">A short reason when <paramref name="Applied"/> is false; never contains secrets.</param>
public sealed record HubApplyResult(bool Applied, IReadOnlyList<ConfigItemResult> Results, string? Error = null);

/// <summary>What <see cref="IHubConfigLayer.RemoveAsync"/> does with the documents of the hub.</summary>
public enum HubLeaveMode
{
    /// <summary>The hub documents are deleted.</summary>
    Remove,

    /// <summary>The commands, sensors, modes, metric overrides and scripts become local documents (variables are dropped).</summary>
    Convert,
}

/// <summary>The outcome of <see cref="IHubConfigLayer.RemoveAsync"/>.</summary>
/// <param name="Removed">True when a hub layer existed.</param>
/// <param name="Converted">The number of documents that became local documents.</param>
/// <param name="Skipped">Documents that could not become local documents because the local layer has one with the same id; the local one stays.</param>
public sealed record HubRemoveResult(bool Removed, int Converted, IReadOnlyList<HubDocumentRef> Skipped);

/// <summary>Names a document of the hub layer.</summary>
/// <param name="Kind">One of <see cref="DocumentKinds"/>.</param>
/// <param name="Id">The document id.</param>
public sealed record HubDocumentRef(string Kind, string Id);
