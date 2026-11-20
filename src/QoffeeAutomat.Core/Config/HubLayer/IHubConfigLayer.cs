namespace QoffeeAutomat.Core.Config.HubLayer;

/// <summary>
/// The hub layer of the configuration (PLAN.md S20, D-028): the documents that a hub distributed with <c>config_sync</c>, kept under
/// <c>config/hub/</c>. It is read-only for everybody but the sync service, and it comes before the local layer in the effective view, so on equal
/// kind and id the hub document wins and the local one is kept but shadowed. The layer changes only as a whole generation, atomically.
/// </summary>
public interface IHubConfigLayer : IConfigLayer
{
    /// <summary>The applied generation, its policy and its items.</summary>
    HubLayerState State { get; }

    /// <summary>The hub variables: name to value. The environment of every run gets them as <c>QA_HUBVAR_&lt;NAME&gt;</c> (see <see cref="HubVariables"/>).</summary>
    IReadOnlyDictionary<string, string> Variables { get; }

    /// <summary>The files of the layer that were skipped because they are damaged.</summary>
    IReadOnlyList<ConfigProblem> Problems { get; }

    /// <summary>Raised after the state changed: a generation was applied, the layer was removed, or an error was recorded. The handler must not throw.</summary>
    event EventHandler? StateChanged;

    /// <summary>Whether the layer has an active document of a kind and id. Metric overrides use <see cref="ConfigIds.MetricOverrides"/>, scripts their file name.</summary>
    /// <param name="kind">The kind; settings never are in the hub layer.</param>
    /// <param name="id">The document id.</param>
    /// <returns>True when the effective view serves the hub's document.</returns>
    bool Owns(ConfigKind kind, string id);

    /// <summary>
    /// Whether the layer can keep a document of the applied generation when the next generation lists it with the same hash: it is stored and readable,
    /// or it was skipped because the platform cannot run it. The sync service asks the hub only for the documents that this says no to.
    /// </summary>
    /// <param name="kind">The wire kind, see <see cref="QoffeeAutomat.Contracts.Hub.DocumentKinds"/>.</param>
    /// <param name="id">The document id.</param>
    /// <param name="hash">The hash from the signed item list.</param>
    /// <returns>True when no body is needed.</returns>
    bool CanCarry(string kind, string id, string hash);

    /// <summary>
    /// Validates a generation with the normal validators and stores it as the hub layer in one step. Every new document is validated (and checked against
    /// the platform); one invalid document fails the generation and the layer stays as it was. A generation that is not newer than the applied one is refused.
    /// </summary>
    /// <param name="generation">The generation; bodies were checked against the signed hashes by the caller.</param>
    /// <param name="cancellationToken">Cancels the call; a cancelled apply leaves the layer as it was.</param>
    /// <returns>The outcome with the state of every item.</returns>
    Task<HubApplyResult> ApplyAsync(HubGeneration generation, CancellationToken cancellationToken);

    /// <summary>Remembers why a generation was rejected, so that the UI can show it. Cleared by the next applied generation.</summary>
    /// <param name="message">A short reason without secrets; null clears it.</param>
    void RecordError(string? message);

    /// <summary>
    /// Deletes the hub layer and returns to the local configuration (leaving the hub, unenroll, revoked). With <see cref="HubLeaveMode.Convert"/> the
    /// documents of the hub first become local documents; a local document with the same id is not overwritten.
    /// </summary>
    /// <param name="mode">What happens to the documents.</param>
    /// <param name="local">The local layer that receives converted documents.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What was done.</returns>
    Task<HubRemoveResult> RemoveAsync(HubLeaveMode mode, ILocalConfigLayer local, CancellationToken cancellationToken);
}
