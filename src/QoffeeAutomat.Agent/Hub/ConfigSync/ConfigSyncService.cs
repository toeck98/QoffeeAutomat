using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Link;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Config.HubLayer;
using QoffeeAutomat.Core.Secrets;

namespace QoffeeAutomat.Agent.Hub.ConfigSync;

/// <summary>
/// The agent side of the feature <c>config_sync</c> (PLAN.md S20, D-027, T908): receives the desired state from the hub, verifies its signature with the key
/// pinned at enrollment and refuses anything that is not newer than the applied generation, asks only for the documents it lacks, checks every document against
/// the signed hash, hands the generation to the hub layer (which validates every document with the normal validators and replaces the layer atomically) and
/// reports the outcome with <c>config_status</c>. A generation that fails in any step changes nothing: the previous generation stays active.
/// </summary>
public sealed class ConfigSyncService
{
    /// <summary>The most items that one desired state may list; a longer list is refused.</summary>
    public const int MaxItems = 4096;

    private readonly IConfigStore _config;
    private readonly HubStateStore _state;
    private readonly AgentIdentity _identity;
    private readonly ILogger<ConfigSyncService> _logger;
    private readonly object _gate = new();
    private Session? _session;

    /// <summary>Creates the service.</summary>
    /// <param name="config">The configuration store; its hub layer receives the generations.</param>
    /// <param name="state">The stored hub state, which holds the pinned signing key.</param>
    /// <param name="identity">The identity of the agent; the signature binds a desired state to its id.</param>
    /// <param name="logger">The logger.</param>
    public ConfigSyncService(IConfigStore config, HubStateStore state, AgentIdentity identity, ILogger<ConfigSyncService> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _state = state;
        _identity = identity;
        _logger = logger;
    }

    /// <summary>The connection is established: remembers what the hub announced and sends the status of the applied generation (generation 0 and no results when there is none).</summary>
    /// <param name="peerFeatures">The features of <c>client_hello</c>.</param>
    /// <param name="context">The connection.</param>
    public void OnEstablished(IReadOnlyList<string> peerFeatures, ILinkExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(peerFeatures);
        ArgumentNullException.ThrowIfNull(context);

        bool negotiated = peerFeatures.Contains(HubFeatures.ConfigSync, StringComparer.Ordinal);
        lock (_gate)
        {
            _session = new Session(context, negotiated);
        }

        if (negotiated)
        {
            SendStatus(context, CurrentStatus());
        }
    }

    /// <summary>The connection ended: a generation that was waiting for its documents is dropped. The hub sends the desired state again on the next connection.</summary>
    /// <param name="context">The connection.</param>
    public void OnClosed(ILinkExtensionContext context)
    {
        lock (_gate)
        {
            if (_session is not null && ReferenceEquals(_session.Context, context))
            {
                _session = null;
            }
        }
    }

    /// <summary>Handles <c>config_desired</c> and <c>config_documents</c>.</summary>
    /// <param name="message">The frame.</param>
    /// <param name="context">The connection.</param>
    /// <param name="cancellationToken">Fires when the connection ends.</param>
    /// <returns>True when the frame is a message of this feature (and was answered); false for any other type.</returns>
    public async ValueTask<bool> TryHandleAsync(UnknownMessage message, ILinkExtensionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);

        if (message.Type is not (HubMessageTypes.ConfigDesired or HubMessageTypes.ConfigDocuments))
        {
            return false;
        }

        Session? session;
        lock (_gate)
        {
            session = _session is not null && ReferenceEquals(_session.Context, context) ? _session : null;
        }

        if (session is null || !session.Negotiated)
        {
            // A message of a feature that the hub did not announce.
            Fail(context, message.Raw, ErrorCodes.BadRequest, "The feature config_sync was not announced in client_hello.");
            return true;
        }

        if (message.Type == HubMessageTypes.ConfigDesired)
        {
            await HandleDesiredAsync(message, session, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await HandleDocumentsAsync(message, session, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    // ------------------------------------------------------------------
    // config_desired
    // ------------------------------------------------------------------

    private async Task HandleDesiredAsync(UnknownMessage message, Session session, CancellationToken cancellationToken)
    {
        ILinkExtensionContext context = session.Context;
        ConfigDesiredMessage? desired = Read(message.Raw, HubJsonContext.Default.ConfigDesiredMessage);
        if (desired is null)
        {
            Fail(context, message.Raw, ErrorCodes.BadRequest, "The config_desired request is malformed.");
            return;
        }

        // A newer desired state replaces one that still waits for its documents, whatever happens to the new one.
        session.Pending = null;

        if (desired.Generation < 1 || desired.Items.Count > MaxItems)
        {
            Reject(context, desired.Id, Math.Max(0, desired.Generation), "The desired state has an invalid generation or too many items.");
            return;
        }

        bool signed = await VerifyAsync(desired, cancellationToken).ConfigureAwait(false);
        if (!signed)
        {
            Reject(context, desired.Id, desired.Generation, "The signature of the desired state is not valid for the key of this hub; nothing was changed.");
            return;
        }

        long applied = _config.Hub.State.Generation;
        if (desired.Generation <= applied)
        {
            // Replays and old states: nothing to fetch, nothing to change (a generation lower than or equal to the applied one is ignored).
            HubLog.ConfigIgnored(_logger, desired.Generation, applied);
            context.Send(ResultMessage.Success(desired.Id, NeedData([])));
            SendStatus(context, CurrentStatus());
            return;
        }

        IHubConfigLayer layer = _config.Hub;
        DocumentRef[] need =
        [
            .. desired.Items
                .Where(item => !layer.CanCarry(item.Kind, item.Id, item.Hash))
                .Select(item => new DocumentRef(item.Kind, item.Id)),
        ];
        context.Send(ResultMessage.Success(desired.Id, NeedData(need)));

        if (need.Length == 0)
        {
            await ApplyAsync(context, desired.Generation, desired.Policy, desired.Items, new Dictionary<(string, string), HubDocument>(), cancellationToken).ConfigureAwait(false);
            return;
        }

        session.Pending = new PendingGeneration(
            desired.Generation,
            desired.Policy,
            desired.Items,
            [.. need.Select(reference => (reference.Kind, reference.Id))]);
    }

    private async Task<bool> VerifyAsync(ConfigDesiredMessage desired, CancellationToken cancellationToken)
    {
        HubStoredState? stored;
        try
        {
            stored = await _state.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SecretStoreException)
        {
            return false;
        }

        if (stored is null || !HubSigningKey.TryImportPublicKey(stored.PublicKey, out ECDsa? key))
        {
            return false;
        }

        using (key)
        {
            return DesiredStateSigner.Verify(_identity.Id, desired, key);
        }
    }

    // ------------------------------------------------------------------
    // config_documents
    // ------------------------------------------------------------------

    private async Task HandleDocumentsAsync(UnknownMessage message, Session session, CancellationToken cancellationToken)
    {
        ILinkExtensionContext context = session.Context;
        ConfigDocumentsMessage? frame = Read(message.Raw, HubJsonContext.Default.ConfigDocumentsMessage);
        if (frame is null)
        {
            Fail(context, message.Raw, ErrorCodes.BadRequest, "The config_documents request is malformed.");
            return;
        }

        PendingGeneration? pending = session.Pending;
        if (pending is null || pending.Generation != frame.Generation)
        {
            context.Send(ResultMessage.Failure(frame.Id, ErrorCodes.BadRequest, "No desired state of this generation is waiting for documents."));
            return;
        }

        foreach (HubDocument document in frame.Documents)
        {
            DesiredItem? item = pending.Items.FirstOrDefault(candidate =>
                string.Equals(candidate.Kind, document.Kind, StringComparison.Ordinal)
                && string.Equals(candidate.Id, document.Id, StringComparison.Ordinal));
            if (item is null || !pending.Needed.Contains((document.Kind, document.Id)))
            {
                Abort(session, frame.Id, pending, document.Kind, document.Id, "The document is not part of the signed desired state or was not asked for.");
                return;
            }

            if (!string.Equals(document.Hash, item.Hash, StringComparison.Ordinal) || !DocumentHasher.Matches(document))
            {
                Abort(session, frame.Id, pending, document.Kind, document.Id, "The hash of the document does not match the signed hash.");
                return;
            }

            pending.Received.TryAdd((document.Kind, document.Id), document);
        }

        if (!frame.Final)
        {
            context.Send(ResultMessage.Success(frame.Id, LinkValues.EmptyObject));
            return;
        }

        (string Kind, string Id)[] missing = [.. pending.Needed.Where(key => !pending.Received.ContainsKey(key))];
        if (missing.Length > 0)
        {
            session.Pending = null;
            ConfigItemResult[] results =
            [
                .. missing.Select(key => new ConfigItemResult(key.Kind, key.Id, ConfigItemStates.Invalid, ["The document was not sent."])),
            ];
            context.Send(ResultMessage.Failure(frame.Id, ErrorCodes.BadRequest, "The last config_documents frame came but documents are still missing."));
            RecordFailure(context, pending.Generation, "Documents of the desired state were not sent; nothing was changed.", results);
            return;
        }

        context.Send(ResultMessage.Success(frame.Id, LinkValues.EmptyObject));
        session.Pending = null;
        await ApplyAsync(context, pending.Generation, pending.Policy, pending.Items, pending.Received, cancellationToken).ConfigureAwait(false);
    }

    private void Abort(Session session, long requestId, PendingGeneration pending, string kind, string id, string reason)
    {
        session.Pending = null;
        session.Context.Send(ResultMessage.Failure(requestId, ErrorCodes.BadRequest, reason));
        RecordFailure(
            session.Context,
            pending.Generation,
            reason + " Nothing was changed.",
            [new ConfigItemResult(kind, id, ConfigItemStates.Invalid, [reason])]);
    }

    // ------------------------------------------------------------------
    // Applying and reporting
    // ------------------------------------------------------------------

    private async Task ApplyAsync(
        ILinkExtensionContext context,
        long generation,
        PolicyInfo policy,
        IReadOnlyList<DesiredItem> items,
        IReadOnlyDictionary<(string Kind, string Id), HubDocument> received,
        CancellationToken cancellationToken)
    {
        HubGenerationEntry[] entries =
        [
            .. items.Select(item => new HubGenerationEntry(
                item.Kind,
                item.Id,
                item.Hash,
                received.TryGetValue((item.Kind, item.Id), out HubDocument? document) ? document.Body : (JsonElement?)null)),
        ];

        HubApplyResult result = await _config.Hub.ApplyAsync(new HubGeneration(generation, policy, entries), cancellationToken).ConfigureAwait(false);
        if (result.Applied)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                int appliedCount = result.Results.Count(item => item.State == ConfigItemStates.Applied);
                HubLog.ConfigApplied(
                    _logger,
                    generation,
                    appliedCount,
                    policy.LocalConfig,
                    policy.DirectLink);
            }

            SendStatus(context, new ConfigStatusMessage(generation, ConfigStates.Applied, result.Results));
            return;
        }

        RecordFailure(context, generation, result.Error ?? "The generation was rejected.", result.Results);
    }

    private void Reject(ILinkExtensionContext context, long requestId, long generation, string reason)
    {
        context.Send(ResultMessage.Failure(requestId, ErrorCodes.BadRequest, reason));
        RecordFailure(context, generation, reason, []);
    }

    private void RecordFailure(ILinkExtensionContext context, long generation, string reason, IReadOnlyList<ConfigItemResult> results)
    {
        HubLog.ConfigRejected(_logger, generation, reason);
        _config.Hub.RecordError(reason);
        SendStatus(context, new ConfigStatusMessage(generation, ConfigStates.Failed, results));
    }

    private ConfigStatusMessage CurrentStatus()
    {
        HubLayerState state = _config.Hub.State;
        return new ConfigStatusMessage(
            state.Generation,
            ConfigStates.Applied,
            [
                .. state.Items.Select(item => new ConfigItemResult(
                    item.Kind,
                    item.Id,
                    string.Equals(item.State, QoffeeAutomat.Contracts.Api.HubConfigItemStates.SkippedUnsupported, StringComparison.Ordinal)
                        ? ConfigItemStates.SkippedUnsupported
                        : ConfigItemStates.Applied)),
            ]);
    }

    private static void SendStatus(ILinkExtensionContext context, ConfigStatusMessage status)
    {
        // The base protocol does not know the hub messages; a frame of an unknown type is written as it is.
        JsonElement raw = JsonSerializer.SerializeToElement(status, HubJsonContext.Default.ConfigStatusMessage);
        _ = context.Send(new UnknownMessage(HubMessageTypes.ConfigStatus, raw));
    }

    private static JsonElement NeedData(IReadOnlyList<DocumentRef> need)
    {
        return JsonSerializer.SerializeToElement(new ConfigDesiredResultData(need), HubJsonContext.Default.ConfigDesiredResultData);
    }

    private static T? Read<T>(JsonElement raw, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return raw.Deserialize(typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Fail(ILinkExtensionContext context, JsonElement raw, string code, string message)
    {
        if (raw.ValueKind == JsonValueKind.Object
            && raw.TryGetProperty("id", out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out long id)
            && id >= 0)
        {
            context.Send(ResultMessage.Failure(id, code, message));
        }
    }

    private sealed class Session
    {
        public Session(ILinkExtensionContext context, bool negotiated)
        {
            Context = context;
            Negotiated = negotiated;
        }

        public ILinkExtensionContext Context { get; }

        public bool Negotiated { get; }

        public PendingGeneration? Pending { get; set; }
    }

    private sealed class PendingGeneration
    {
        public PendingGeneration(long generation, PolicyInfo policy, IReadOnlyList<DesiredItem> items, HashSet<(string Kind, string Id)> needed)
        {
            Generation = generation;
            Policy = policy;
            Items = items;
            Needed = needed;
        }

        public long Generation { get; }

        public PolicyInfo Policy { get; }

        public IReadOnlyList<DesiredItem> Items { get; }

        public HashSet<(string Kind, string Id)> Needed { get; }

        public Dictionary<(string Kind, string Id), HubDocument> Received { get; } = [];
    }
}
