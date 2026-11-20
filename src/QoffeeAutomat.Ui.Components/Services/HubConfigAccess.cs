using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Hub;

namespace QoffeeAutomat.Ui.Components.Services;

/// <summary>
/// What the pages need to know about the hub's hold on the configuration (PLAN.md S20, T908): whether a document comes from the hub, whether a local document
/// is kept but not used, and whether editing is switched off. A page that could not read the state (an older agent, a failed request) passes null and
/// behaves as it always did: nothing is read-only.
/// </summary>
public static class HubConfigAccess
{
    /// <summary>True when the hub is managing this agent (a generation is applied).</summary>
    /// <param name="hub">The state; null when it is unknown.</param>
    /// <returns>True when the hub layer exists.</returns>
    public static bool IsManaged(HubConfigState? hub) => hub is { Active: true };

    /// <summary>True when the hub locked the local configuration: no local document can be created, changed or deleted.</summary>
    /// <param name="hub">The state; null when it is unknown.</param>
    /// <returns>True when the local layer is inactive and read-only.</returns>
    public static bool IsLocked(HubConfigState? hub) => hub is { LocalConfigLocked: true };

    /// <summary>True when the hub denies direct links: pairing is switched off.</summary>
    /// <param name="hub">The state; null when it is unknown.</param>
    /// <returns>True when the agent refuses direct Home Assistant connections.</returns>
    public static bool IsDirectLinkDenied(HubConfigState? hub) => hub is { DirectLinkDenied: true };

    /// <summary>True when a document was distributed by the hub.</summary>
    /// <param name="origin">The <c>origin</c> member of the document.</param>
    /// <returns>True for the origin <c>hub</c>.</returns>
    public static bool IsHubDocument(string? origin) => string.Equals(origin, DocumentOrigins.Hub, StringComparison.Ordinal);

    /// <summary>True when a document cannot be edited here: it comes from the hub, or the hub locked the local configuration.</summary>
    /// <param name="hub">The state; null when it is unknown.</param>
    /// <param name="origin">The <c>origin</c> member of the document.</param>
    /// <returns>True when saving and deleting would be refused.</returns>
    public static bool IsReadOnly(HubConfigState? hub, string? origin) => IsLocked(hub) || IsHubDocument(origin);

    /// <summary>The local documents of one kind that are kept but not used.</summary>
    /// <param name="hub">The state; null when it is unknown.</param>
    /// <param name="kind">One of <see cref="DocumentKinds"/>.</param>
    /// <returns>The shadowed documents, ordered by id; empty when there are none.</returns>
    public static IReadOnlyList<HubShadowedItem> ShadowedOf(HubConfigState? hub, string kind)
    {
        return hub is null
            ? []
            : [.. hub.Shadowed.Where(item => string.Equals(item.Kind, kind, StringComparison.Ordinal)).OrderBy(item => item.Id, StringComparer.Ordinal)];
    }

    /// <summary>The reason why a local document is not used, or null when it is in use.</summary>
    /// <param name="hub">The state; null when it is unknown.</param>
    /// <param name="kind">One of <see cref="DocumentKinds"/>.</param>
    /// <param name="id">The document id.</param>
    /// <returns>One of <see cref="HubShadowReasons"/>, or null.</returns>
    public static string? ShadowReason(HubConfigState? hub, string kind, string id)
    {
        return hub?.Shadowed.FirstOrDefault(item =>
            string.Equals(item.Kind, kind, StringComparison.Ordinal) && string.Equals(item.Id, id, StringComparison.Ordinal))?.Reason;
    }
}
