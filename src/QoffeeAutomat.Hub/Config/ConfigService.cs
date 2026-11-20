using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Validation;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Security;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Config;

/// <summary>What a computation changes for one agent: the agent, its new desired state and the generation it had.</summary>
internal sealed record PlannedChange(ConfigAgent Agent, ComputedDesiredState State, AgentGeneration? Stored);

/// <summary>What publishing or republishing a profile would do; built without writing anything.</summary>
internal sealed record PublishPlan(
    ProfileRow Profile,
    string ContentText,
    int Revision,
    bool NewRevision,
    bool Unchanged,
    List<string> SystemScripts,
    List<PlannedChange> Changes);

/// <summary>
/// The configuration service of the hub (PLAN.md S06, S20, T907): the library of documents with their revisions, profiles with a draft and immutable
/// published revisions, assignments to targets, and the desired state of every agent with its generation and signature.
/// <para>
/// A change that can alter what agents should have (a publish, a rollback, an assignment, deleting a profile) recomputes the desired state of every
/// enrolled agent in the same transaction. An agent gets a new generation only when the hash of its content (policy and items) differs from the
/// one the hub last told it about; the first generation is only created once an agent has something other than the empty default state. The state is
/// signed with the hub key (<see cref="HubSigningKeyStore"/>, <see cref="DesiredStateSigner"/>) and the signature is kept, so it is identical every
/// time it is sent. The service never sends anything: the gateway (T909) reads <see cref="GetGenerationAsync"/> and <see cref="LoadDocumentsAsync"/>.
/// </para>
/// </summary>
public sealed class ConfigService
{
    /// <summary>The longest display name of a profile.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The highest and (negated) lowest priority of a profile.</summary>
    public const int MaxPriority = 10_000;

    /// <summary>The most library documents in one profile.</summary>
    public const int MaxItems = 500;

    /// <summary>The longest note of a publication.</summary>
    public const int MaxNoteLength = 500;

    /// <summary>The most variables in one profile.</summary>
    public const int MaxVariables = 256;

    private static readonly string[] LocalConfigValues = [LocalConfigPolicies.Allow, LocalConfigPolicies.Locked, DesiredStateComputer.Inherit];
    private static readonly string[] DirectLinkValues = [DirectLinkPolicies.Allow, DirectLinkPolicies.Deny, DesiredStateComputer.Inherit];
    private static readonly string[] OsFilterValues = [PlatformActionSupport.WindowsOs, PlatformActionSupport.LinuxOs];

    private readonly HubDatabase _database;
    private readonly IConfigAgentSource _agents;
    private readonly HubSigningKeyStore _signer;
    private readonly TimeProvider _time;
    private readonly object _listenerGate = new();
    private Action<IReadOnlyList<string>>[] _listeners = [];

    /// <summary>Creates the service.</summary>
    /// <param name="database">The hub database.</param>
    /// <param name="agents">The agents that configuration is computed for.</param>
    /// <param name="signer">The key that signs desired states.</param>
    /// <param name="time">The clock.</param>
    public ConfigService(HubDatabase database, IConfigAgentSource agents, HubSigningKeyStore signer, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(time);

        _database = database;
        _agents = agents;
        _signer = signer;
        _time = time;
    }

    // Notification ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Registers a listener that is called, after the change is committed, with the ids of the agents that got a new generation (T909: the rollout service pushes
    /// the new desired state to those that are online). The listener runs on the thread that made the change; it must return quickly and not throw.
    /// </summary>
    /// <param name="listener">The listener.</param>
    /// <returns>A handle; dispose it to stop the notifications.</returns>
    public IDisposable OnGenerationsChanged(Action<IReadOnlyList<string>> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        lock (_listenerGate)
        {
            _listeners = [.. _listeners, listener];
        }

        return new ListenerRegistration(this, listener);
    }

    private void Announce(IReadOnlyList<AgentChange> changes)
    {
        if (changes.Count == 0)
        {
            return;
        }

        Action<IReadOnlyList<string>>[] listeners = Volatile.Read(ref _listeners);
        if (listeners.Length == 0)
        {
            return;
        }

        IReadOnlyList<string> agents = [.. changes.Select(static change => change.AgentId)];
        foreach (Action<IReadOnlyList<string>> listener in listeners)
        {
            try
            {
                listener(agents);
            }
#pragma warning disable CA1031 // A listener belongs to another service; its failure must not turn a committed change into an error.
            catch (Exception)
#pragma warning restore CA1031
            {
                // The listener logs its own failures.
            }
        }
    }

    private void RemoveListener(Action<IReadOnlyList<string>> listener)
    {
        lock (_listenerGate)
        {
            _listeners = [.. _listeners.Where(existing => existing != listener)];
        }
    }

    private sealed class ListenerRegistration(ConfigService service, Action<IReadOnlyList<string>> listener) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                service.RemoveListener(listener);
            }
        }
    }

    // Library -----------------------------------------------------------------------------------------------------------------------

    /// <summary>Lists the library documents that are not deleted.</summary>
    /// <param name="kind">Only documents of this kind, or null for all.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The current revision of each document without its body.</returns>
    public Task<IReadOnlyList<LibraryDocumentSummary>> ListLibraryAsync(string? kind, CancellationToken cancellationToken)
    {
        return _database.QueryAsync<IReadOnlyList<LibraryDocumentSummary>>(
            connection => ConfigStorage.ListLibrary(connection, null, kind).Select(ToSummary).ToList(),
            cancellationToken);
    }

    /// <summary>Reads a library document.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="id">The library id.</param>
    /// <param name="revision">The revision, or null for the current one. An explicit revision is also readable after the document was deleted.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The document with its body, or null.</returns>
    public Task<LibraryDocumentInfo?> GetLibraryDocumentAsync(string kind, string id, int? revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(id);

        return _database.QueryAsync<LibraryDocumentInfo?>(
            connection =>
            {
                LibraryRow? row = ConfigStorage.FindLibrary(connection, null, kind, id, revision);
                return row is null || (revision is null && row.DeletedAt is not null) ? null : ToInfo(row, null);
            },
            cancellationToken);
    }

    /// <summary>Lists the revisions of a library document.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="id">The library id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The history, newest first; null when there is no such document.</returns>
    public Task<LibraryRevisionList?> ListLibraryRevisionsAsync(string kind, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(id);

        return _database.QueryAsync<LibraryRevisionList?>(
            connection =>
            {
                LibraryRow? row = ConfigStorage.FindLibrary(connection, null, kind, id);
                return row is null || row.DeletedAt is not null
                    ? null
                    : new LibraryRevisionList(kind, id, ConfigStorage.ListLibraryRevisions(connection, null, kind, id));
            },
            cancellationToken);
    }

    /// <summary>
    /// Saves a library document. The document is validated by the validators of the agent; a valid document whose content equals the current
    /// revision adds no revision (<see cref="LibraryDocumentInfo.Changed"/> is false), otherwise it becomes the next revision.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="id">The library id.</param>
    /// <param name="body">The document.</param>
    /// <param name="user">The user that saves it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The saved revision; <see cref="ConfigStatus.Created"/> for the first revision of a document.</returns>
    public Task<ConfigResult<LibraryDocumentInfo>> SaveLibraryDocumentAsync(
        string kind,
        string id,
        JsonElement body,
        string user,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(user);

        DateTimeOffset now = _time.GetUtcNow();
        return _database.InTransactionAsync(
            (connection, transaction) =>
            {
                if (!DocumentKinds.All.Contains(kind))
                {
                    return ConfigResults.NotFound<LibraryDocumentInfo>("unknown_kind", "The kind is not one of command, sensor, script, mode, metric_overrides and variables.");
                }

                LibraryValidation validation = LibraryDocuments.Normalize(kind, id, body, commandId => FindCommand(connection, transaction, commandId));
                if (!validation.IsValid)
                {
                    return ConfigResults.Invalid<LibraryDocumentInfo>(validation.Errors);
                }

                string contentHash = DocumentHasher.Hash(kind, validation.Body);
                LibraryRow? current = ConfigStorage.FindLibrary(connection, transaction, kind, id);
                if (current is { DeletedAt: null } && string.Equals(current.ContentHash, contentHash, StringComparison.Ordinal))
                {
                    return ConfigResults.Ok(ToInfo(current, false));
                }

                int revision = (current?.Revision ?? 0) + 1;
                JsonElement stamped = LibraryDocuments.Stamp(kind, validation.Body, revision, now);
                string hash = DocumentHasher.Hash(kind, stamped);
                ConfigStorage.SaveLibraryRevision(connection, transaction, kind, id, revision, stamped, hash, contentHash, validation.Name, user, now);

                LibraryRow saved = ConfigStorage.FindLibrary(connection, transaction, kind, id, revision)
                    ?? throw new InvalidOperationException("The saved library revision cannot be read.");
                return current is null ? ConfigResults.Created(ToInfo(saved, true)) : ConfigResults.Ok(ToInfo(saved, true));
            },
            cancellationToken);
    }

    /// <summary>
    /// Deletes a library document: it disappears from the list and cannot be added to a profile, but its revisions stay, because published profile
    /// revisions pin them. A document that is part of the draft of a profile cannot be deleted.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="id">The library id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when deleted.</returns>
    public Task<ConfigResult<bool>> DeleteLibraryDocumentAsync(string kind, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(id);

        DateTimeOffset now = _time.GetUtcNow();
        return _database.InTransactionAsync(
            (connection, transaction) =>
            {
                LibraryRow? row = ConfigStorage.FindLibrary(connection, transaction, kind, id);
                if (row is null || row.DeletedAt is not null)
                {
                    return ConfigResults.NotFound<bool>("unknown_document", "There is no such library document.");
                }

                List<string> users = [.. ConfigStorage.ListProfiles(connection, transaction)
                    .Where(profile => profile.Draft.Items.Any(item => item.Kind == kind && item.Id == id))
                    .Select(static profile => profile.Id)];
                if (users.Count > 0)
                {
                    return ConfigResults.Conflict<bool>("in_use", "The document is part of the draft of the profile(s) " + string.Join(", ", users) + ". Remove it there first.");
                }

                ConfigStorage.MarkLibraryDeleted(connection, transaction, kind, id, now);
                return ConfigResults.Ok(true);
            },
            cancellationToken);
    }

    // Profiles ----------------------------------------------------------------------------------------------------------------------

    /// <summary>Lists the profiles.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The profiles with their drafts, ordered by id.</returns>
    public Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(CancellationToken cancellationToken)
    {
        return _database.QueryAsync<IReadOnlyList<ProfileInfo>>(
            connection => ConfigStorage.ListProfiles(connection, null).Select(row => ToProfileInfo(connection, null, row)).ToList(),
            cancellationToken);
    }

    /// <summary>Reads a profile.</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The profile, or null.</returns>
    public Task<ProfileInfo?> GetProfileAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        return _database.QueryAsync<ProfileInfo?>(
            connection =>
            {
                ProfileRow? row = ConfigStorage.FindProfile(connection, null, id);
                return row is null ? null : ToProfileInfo(connection, null, row);
            },
            cancellationToken);
    }

    /// <summary>Creates a profile with a draft. Nothing is published, so no agent is affected.</summary>
    /// <param name="request">The id and the draft.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The profile.</returns>
    public Task<ConfigResult<ProfileInfo>> CreateProfileAsync(ProfileDraftRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        DateTimeOffset now = _time.GetUtcNow();
        return _database.InTransactionAsync(
            (connection, transaction) =>
            {
                if (!ScriptNames.IsValidDocumentId(request.Id))
                {
                    return ConfigResults.Invalid<ProfileInfo>("/id", ValidationCodes.Invalid, "The id must match ^[a-z][a-z0-9_]{0,62}$.");
                }

                if (ConfigStorage.FindProfile(connection, transaction, request.Id!) is not null)
                {
                    return ConfigResults.Conflict<ProfileInfo>("duplicate", "A profile with this id exists.");
                }

                (ProfileContent? content, List<ValidationError> errors) = NormalizeDraft(connection, transaction, request);
                if (content is null)
                {
                    return ConfigResults.Invalid<ProfileInfo>(errors);
                }

                ConfigStorage.InsertProfile(connection, transaction, request.Id!, ConfigStorage.WriteContent(content), now);
                ProfileRow created = ConfigStorage.FindProfile(connection, transaction, request.Id!)
                    ?? throw new InvalidOperationException("The created profile cannot be read.");
                return ConfigResults.Created(ToProfileInfo(connection, transaction, created));
            },
            cancellationToken);
    }

    /// <summary>Replaces the draft of a profile. Published revisions and agents are not affected until the next publish.</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="request">The new draft.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The profile.</returns>
    public Task<ConfigResult<ProfileInfo>> UpdateProfileAsync(string id, ProfileDraftRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(request);

        DateTimeOffset now = _time.GetUtcNow();
        return _database.InTransactionAsync(
            (connection, transaction) =>
            {
                if (ConfigStorage.FindProfile(connection, transaction, id) is null)
                {
                    return ConfigResults.NotFound<ProfileInfo>("unknown_profile", "There is no such profile.");
                }

                if (request.Id is not null && !string.Equals(request.Id, id, StringComparison.Ordinal))
                {
                    return ConfigResults.Invalid<ProfileInfo>("/id", ValidationCodes.Conflict, "The id of a profile cannot be changed.");
                }

                (ProfileContent? content, List<ValidationError> errors) = NormalizeDraft(connection, transaction, request);
                if (content is null)
                {
                    return ConfigResults.Invalid<ProfileInfo>(errors);
                }

                ConfigStorage.UpdateDraft(connection, transaction, id, ConfigStorage.WriteContent(content), now);
                ProfileRow updated = ConfigStorage.FindProfile(connection, transaction, id)
                    ?? throw new InvalidOperationException("The updated profile cannot be read.");
                return ConfigResults.Ok(ToProfileInfo(connection, transaction, updated));
            },
            cancellationToken);
    }

    /// <summary>Deletes a profile with its revisions and assignments. The desired state of every agent is recomputed.</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agents that got a new generation.</returns>
    public async Task<ConfigResult<IReadOnlyList<AgentChange>>> DeleteProfileAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        List<ConfigAgent> agents = await ListAgentsAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _time.GetUtcNow();
        ConfigResult<IReadOnlyList<AgentChange>> deleted = await _database.InTransactionAsync(
            (connection, transaction) =>
            {
                if (ConfigStorage.FindProfile(connection, transaction, id) is null)
                {
                    return ConfigResults.NotFound<IReadOnlyList<AgentChange>>("unknown_profile", "There is no such profile.");
                }

                ConfigStorage.DeleteProfile(connection, transaction, id);
                return ConfigResults.Ok<IReadOnlyList<AgentChange>>(RecomputeCore(connection, transaction, agents, agents, now));
            },
            cancellationToken).ConfigureAwait(false);
        if (deleted.IsSuccess)
        {
            Announce(deleted.Value!);
        }

        return deleted;
    }

    /// <summary>Lists the published revisions of a profile.</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The revisions, newest first; null when there is no such profile.</returns>
    public Task<ProfileRevisionList?> ListProfileRevisionsAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        return _database.QueryAsync<ProfileRevisionList?>(
            connection =>
            {
                ProfileRow? profile = ConfigStorage.FindProfile(connection, null, id);
                return profile is null
                    ? null
                    : new ProfileRevisionList(
                        id,
                        ConfigStorage.ListProfileRevisions(connection, null, id).Select(row => ToRevisionSummary(row, profile.ActiveRevision)).ToList());
            },
            cancellationToken);
    }

    /// <summary>Reads one published revision of a profile.</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="revision">The revision.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The revision with its frozen content, or null.</returns>
    public Task<ProfileRevisionInfo?> GetProfileRevisionAsync(string id, int revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        return _database.QueryAsync<ProfileRevisionInfo?>(
            connection =>
            {
                ProfileRow? profile = ConfigStorage.FindProfile(connection, null, id);
                ProfileRevisionRow? row = profile is null ? null : ConfigStorage.FindProfileRevision(connection, null, id, revision);
                return row is null ? null : new ProfileRevisionInfo(id, ToRevisionSummary(row, profile!.ActiveRevision), row.Content);
            },
            cancellationToken);
    }

    // Assignments -------------------------------------------------------------------------------------------------------------------

    /// <summary>Lists the assignments.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The assignments.</returns>
    public Task<IReadOnlyList<AssignmentInfo>> ListAssignmentsAsync(CancellationToken cancellationToken)
    {
        return _database.QueryAsync<IReadOnlyList<AssignmentInfo>>(
            connection => ConfigStorage.ListAssignments(connection, null),
            cancellationToken);
    }

    /// <summary>
    /// Assigns a profile to a target. The assignment takes effect at once: the desired state of every enrolled agent is recomputed and an agent whose
    /// content changed gets a new generation.
    /// </summary>
    /// <param name="profileId">The profile.</param>
    /// <param name="target"><c>all</c>, <c>tag:&lt;tag&gt;</c> or <c>agent:&lt;id&gt;</c>.</param>
    /// <param name="user">The user that creates the assignment.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The assignment and the agents that got a new generation.</returns>
    public async Task<ConfigResult<CreatedAssignment>> CreateAssignmentAsync(
        string? profileId,
        string? target,
        string user,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var errors = new List<ValidationError>();
        if (string.IsNullOrEmpty(profileId))
        {
            errors.Add(new ValidationError("/profileId", ValidationCodes.Required, "The profile is required."));
        }

        if (!AssignmentTarget.TryParse(target?.Trim(), out AssignmentTarget? parsed, out string? targetError))
        {
            errors.Add(new ValidationError("/target", ValidationCodes.Invalid, targetError ?? "The target is not valid."));
        }

        if (errors.Count > 0 || parsed is null)
        {
            return ConfigResults.Invalid<CreatedAssignment>(errors);
        }

        string targetText = parsed.Text;
        string profile = profileId!;
        List<ConfigAgent> agents = await ListAgentsAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _time.GetUtcNow();
        ConfigResult<CreatedAssignment> created = await _database.InTransactionAsync(
            (connection, transaction) =>
            {
                if (ConfigStorage.FindProfile(connection, transaction, profile) is null)
                {
                    return ConfigResults.Invalid<CreatedAssignment>("/profileId", ValidationCodes.UnknownReference, "There is no such profile.");
                }

                if (ConfigStorage.AssignmentExists(connection, transaction, profile, targetText))
                {
                    return ConfigResults.Conflict<CreatedAssignment>("duplicate", "This profile is already assigned to this target.");
                }

                var assignment = new AssignmentInfo(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), profile, targetText, now, user);
                ConfigStorage.InsertAssignment(connection, transaction, assignment);
                List<AgentChange> changes = RecomputeCore(connection, transaction, agents, agents, now);
                return ConfigResults.Created(new CreatedAssignment(assignment, changes));
            },
            cancellationToken).ConfigureAwait(false);
        if (created.IsSuccess)
        {
            Announce(created.Value!.Agents);
        }

        return created;
    }

    /// <summary>Removes an assignment. The desired state of every enrolled agent is recomputed.</summary>
    /// <param name="id">The assignment id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agents that got a new generation.</returns>
    public async Task<ConfigResult<IReadOnlyList<AgentChange>>> DeleteAssignmentAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        List<ConfigAgent> agents = await ListAgentsAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _time.GetUtcNow();
        ConfigResult<IReadOnlyList<AgentChange>> deleted = await _database.InTransactionAsync(
            (connection, transaction) =>
            {
                if (ConfigStorage.FindAssignment(connection, transaction, id) is null)
                {
                    return ConfigResults.NotFound<IReadOnlyList<AgentChange>>("unknown_assignment", "There is no such assignment.");
                }

                ConfigStorage.DeleteAssignment(connection, transaction, id);
                return ConfigResults.Ok<IReadOnlyList<AgentChange>>(RecomputeCore(connection, transaction, agents, agents, now));
            },
            cancellationToken).ConfigureAwait(false);
        if (deleted.IsSuccess)
        {
            Announce(deleted.Value!);
        }

        return deleted;
    }

    // Publish, preview, rollback ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Shows what publishing the draft of a profile, or republishing one of its revisions, would do, without changing anything: the agents whose
    /// desired state would change, with the documents and policy members that differ, and whether the content needs the role admin.
    /// </summary>
    /// <param name="profileId">The profile.</param>
    /// <param name="revision">An earlier revision to republish, or null for the draft.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The preview.</returns>
    public async Task<ConfigResult<PublishPreview>> PreviewAsync(string profileId, int? revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profileId);

        List<ConfigAgent> agents = await ListAgentsAsync(cancellationToken).ConfigureAwait(false);
        return await _database.QueryAsync(
            connection =>
            {
                ConfigResult<PublishPlan> built = BuildPlan(connection, null, profileId, revision, agents);
                return built.IsSuccess
                    ? ConfigResults.Ok(ToPreview(built.Value!))
                    : ConfigResults.Fail<PublishPreview, PublishPlan>(built);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes the draft of a profile as the next immutable revision (or, with <paramref name="revision"/>, republishes an earlier revision, which
    /// is the roll-back) and recomputes the desired state of every enrolled agent. A publish whose content equals what is in effect creates no
    /// revision and no generation. Content with system scripts needs the role admin.
    /// </summary>
    /// <param name="profileId">The profile.</param>
    /// <param name="revision">An earlier revision to republish, or null to publish the draft.</param>
    /// <param name="note">A note for the history.</param>
    /// <param name="user">The user that publishes.</param>
    /// <param name="role">The role of that user (<see cref="HubRoles"/>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The revision in effect and the agents that got a new generation.</returns>
    public async Task<ConfigResult<PublishResult>> PublishAsync(
        string profileId,
        int? revision,
        string? note,
        string user,
        string role,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(role);

        if (note is { Length: > MaxNoteLength })
        {
            return ConfigResults.Invalid<PublishResult>("/note", ValidationCodes.MaxLength, "The note must not be longer than 500 characters.");
        }

        List<ConfigAgent> agents = await ListAgentsAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _time.GetUtcNow();
        ConfigResult<PublishResult> published = await _database.InTransactionAsync(
            (connection, transaction) =>
            {
                ConfigResult<PublishPlan> built = BuildPlan(connection, transaction, profileId, revision, agents);
                if (!built.IsSuccess)
                {
                    return ConfigResults.Fail<PublishResult, PublishPlan>(built);
                }

                PublishPlan plan = built.Value!;
                if (plan.SystemScripts.Count > 0 && !string.Equals(role, HubRoles.Admin, StringComparison.Ordinal))
                {
                    return ConfigResults.Forbidden<PublishResult>(
                        "admin_required",
                        "The profile contains system scripts (" + string.Join(", ", plan.SystemScripts) + "). Only an administrator may publish it.");
                }

                if (plan.Unchanged)
                {
                    return ConfigResults.Ok(new PublishResult(profileId, plan.Revision, false, true, []));
                }

                if (plan.NewRevision)
                {
                    ConfigStorage.InsertProfileRevision(connection, transaction, profileId, plan.Revision, plan.ContentText, user, note, now);
                }

                ConfigStorage.SetActiveRevision(connection, transaction, profileId, plan.Revision);
                List<AgentChange> applied = Persist(connection, transaction, plan.Changes, now);
                ConfigStorage.PruneGenerations(connection, transaction, KnownAgentIds(agents));
                return ConfigResults.Ok(new PublishResult(profileId, plan.Revision, plan.NewRevision, false, applied));
            },
            cancellationToken).ConfigureAwait(false);
        if (published.IsSuccess)
        {
            Announce(published.Value!.Agents);
        }

        return published;
    }

    // Desired state and generations -------------------------------------------------------------------------------------------------

    /// <summary>Shows the desired state that the profiles yield for an agent now, with the generation the agent was last told about.</summary>
    /// <param name="agentId">The agent.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The desired state, or null when there is no such agent.</returns>
    public async Task<AgentDesiredInfo?> GetDesiredStateAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentId);

        List<ConfigAgent> agents = await ListAgentsAsync(cancellationToken).ConfigureAwait(false);
        ConfigAgent? match = agents.Find(candidate => string.Equals(candidate.Id, agentId, StringComparison.Ordinal));
        if (match is null)
        {
            return null;
        }

        ConfigAgent agent = match;
        return await _database.QueryAsync<AgentDesiredInfo?>(
            connection =>
            {
                List<ProfileSnapshot> snapshots = LoadSnapshots(connection, null, null);
                List<ProfileAssignment> assignments = LoadAssignments(connection, null);
                ComputedDesiredState state = DesiredStateComputer.Compute(agent, snapshots, assignments);
                AgentGeneration? stored = ConfigStorage.FindGeneration(connection, null, agentId);
                string baseline = stored?.ContentHash ?? DesiredStateHash.Empty(agentId);
                return new AgentDesiredInfo(
                    agentId,
                    stored?.Generation ?? 0,
                    string.Equals(state.ContentHash(agentId), baseline, StringComparison.Ordinal),
                    state.Policy,
                    state.Profiles,
                    [.. state.Documents.Select(static document => new DesiredItemInfo(document.Kind, document.Id, document.Hash, document.ProfileId, document.ProfileRevision))],
                    [.. state.Warnings.Select(ToWarningInfo)],
                    stored?.UpdatedAt);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the generation the hub last created for an agent.</summary>
    /// <param name="agentId">The agent.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The generation, or null while no profile has ever reached the agent.</returns>
    public Task<AgentGeneration?> GetGenerationAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentId);

        return _database.QueryAsync<AgentGeneration?>(connection => ConfigStorage.FindGeneration(connection, null, agentId), cancellationToken);
    }

    /// <summary>
    /// Recomputes the desired state of one agent and creates a new generation when its content changed; for a newly enrolled agent and after its
    /// tags changed. Then returns the current generation.
    /// </summary>
    /// <param name="agentId">The agent.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The generation, or null when no profile reaches the agent and none ever did.</returns>
    public async Task<AgentGeneration?> EnsureGenerationAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentId);

        await RecomputeAgentAsync(agentId, cancellationToken).ConfigureAwait(false);
        return await GetGenerationAsync(agentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Recomputes the desired state of every enrolled agent; an agent whose content changed gets a new generation.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agents that got a new generation.</returns>
    public async Task<IReadOnlyList<AgentChange>> RecomputeAsync(CancellationToken cancellationToken)
    {
        List<ConfigAgent> agents = await ListAgentsAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _time.GetUtcNow();
        IReadOnlyList<AgentChange> changes = await _database.InTransactionAsync<IReadOnlyList<AgentChange>>(
            (connection, transaction) => RecomputeCore(connection, transaction, agents, agents, now),
            cancellationToken).ConfigureAwait(false);
        Announce(changes);
        return changes;
    }

    /// <summary>Recomputes the desired state of one agent; the agent gets a new generation when its content changed.</summary>
    /// <param name="agentId">The agent.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The change, or null when nothing changed or there is no such agent.</returns>
    public async Task<AgentChange?> RecomputeAgentAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentId);

        List<ConfigAgent> agents = await ListAgentsAsync(cancellationToken).ConfigureAwait(false);
        List<ConfigAgent> targets = agents.FindAll(agent => string.Equals(agent.Id, agentId, StringComparison.Ordinal));
        DateTimeOffset now = _time.GetUtcNow();
        List<AgentChange> changes = await _database.InTransactionAsync(
            (connection, transaction) => RecomputeCore(connection, transaction, targets, agents, now),
            cancellationToken).ConfigureAwait(false);
        Announce(changes);
        return changes.Count > 0 ? changes[0] : null;
    }

    /// <summary>Reads the generation the hub last created for every agent that has one.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The generations by agent id.</returns>
    public Task<IReadOnlyDictionary<string, AgentGeneration>> ListGenerationsAsync(CancellationToken cancellationToken)
    {
        return _database.QueryAsync<IReadOnlyDictionary<string, AgentGeneration>>(
            connection => ConfigStorage.ListGenerations(connection, null),
            cancellationToken);
    }

    /// <summary>Reads the bodies of the documents of a generation, from the copies the hub keeps of every document that it ever announced.</summary>
    /// <param name="items">The items of the generation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The documents; an item whose body is unknown is left out.</returns>
    public Task<IReadOnlyList<HubDocument>> LoadDocumentsAsync(IReadOnlyList<DesiredItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        return _database.QueryAsync<IReadOnlyList<HubDocument>>(
            connection =>
            {
                var documents = new List<HubDocument>();
                foreach (DesiredItem item in items)
                {
                    if (ConfigStorage.FindBlob(connection, null, item.Kind, item.Id, item.Hash) is { } document)
                    {
                        documents.Add(document);
                    }
                }

                return documents;
            },
            cancellationToken);
    }

    // Planning and persisting -------------------------------------------------------------------------------------------------------

    private async Task<List<ConfigAgent>> ListAgentsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ConfigAgent> agents = await _agents.ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. agents];
    }

    private static HashSet<string> KnownAgentIds(List<ConfigAgent> agents)
    {
        return [.. agents.Select(static agent => agent.Id)];
    }

    private List<AgentChange> RecomputeCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        List<ConfigAgent> targets,
        List<ConfigAgent> all,
        DateTimeOffset now)
    {
        List<ProfileSnapshot> snapshots = LoadSnapshots(connection, transaction, null);
        List<ProfileAssignment> assignments = LoadAssignments(connection, transaction);
        Dictionary<string, AgentGeneration> generations = ConfigStorage.ListGenerations(connection, transaction);
        List<PlannedChange> planned = ComputeChanges(targets, snapshots, assignments, generations);
        List<AgentChange> applied = Persist(connection, transaction, planned, now);
        ConfigStorage.PruneGenerations(connection, transaction, KnownAgentIds(all));
        return applied;
    }

    private static List<PlannedChange> ComputeChanges(
        List<ConfigAgent> agents,
        List<ProfileSnapshot> snapshots,
        List<ProfileAssignment> assignments,
        Dictionary<string, AgentGeneration> generations)
    {
        var changes = new List<PlannedChange>();
        foreach (ConfigAgent agent in agents)
        {
            if (!string.Equals(agent.Status, AgentStatuses.Enrolled, StringComparison.Ordinal))
            {
                continue;
            }

            ComputedDesiredState state = DesiredStateComputer.Compute(agent, snapshots, assignments);
            generations.TryGetValue(agent.Id, out AgentGeneration? stored);

            // An agent that was never told anything has the empty default state; only a different state is a change.
            string baseline = stored?.ContentHash ?? DesiredStateHash.Empty(agent.Id);
            if (!string.Equals(state.ContentHash(agent.Id), baseline, StringComparison.Ordinal))
            {
                changes.Add(new PlannedChange(agent, state, stored));
            }
        }

        return changes;
    }

    private List<AgentChange> Persist(SqliteConnection connection, SqliteTransaction transaction, List<PlannedChange> planned, DateTimeOffset now)
    {
        var applied = new List<AgentChange>();
        foreach (PlannedChange change in planned)
        {
            long generation = (change.Stored?.Generation ?? 0) + 1;
            var desired = new DesiredState(change.Agent.Id, generation, change.State.Policy, change.State.Items);
            string signature = _signer.Sign(desired);
            ConfigStorage.SaveGeneration(
                connection,
                transaction,
                new AgentGeneration(change.Agent.Id, generation, change.State.ContentHash(change.Agent.Id), change.State.Policy, change.State.Items, signature, now));
            foreach (DesiredDocument document in change.State.Documents)
            {
                ConfigStorage.SaveBlob(connection, transaction, document.Kind, document.Hash, document.Body);
            }

            applied.Add(ToAgentChange(change, generation));
        }

        return applied;
    }

    private static ConfigResult<PublishPlan> BuildPlan(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string profileId,
        int? revision,
        List<ConfigAgent> agents)
    {
        ProfileRow? profile = ConfigStorage.FindProfile(connection, transaction, profileId);
        if (profile is null)
        {
            return ConfigResults.NotFound<PublishPlan>("unknown_profile", "There is no such profile.");
        }

        ProfileRevisionRow? active = profile.ActiveRevision is { } activeRevision
            ? ConfigStorage.FindProfileRevision(connection, transaction, profileId, activeRevision)
            : null;

        ProfileContent content;
        string contentText;
        int target;
        bool newRevision;
        bool unchanged;
        if (revision is { } requested)
        {
            ProfileRevisionRow? row = ConfigStorage.FindProfileRevision(connection, transaction, profileId, requested);
            if (row is null)
            {
                return ConfigResults.NotFound<PublishPlan>("unknown_revision", "The profile has no such revision.");
            }

            content = row.Content;
            contentText = row.ContentText;
            target = requested;
            newRevision = false;
            unchanged = profile.ActiveRevision == requested;
        }
        else
        {
            content = profile.Draft;
            contentText = profile.DraftText;
            bool same = active is not null && string.Equals(active.ContentText, profile.DraftText, StringComparison.Ordinal);
            target = same ? active!.Revision : profile.LatestRevision + 1;
            newRevision = !same;
            unchanged = same;
        }

        List<ResolvedDocument> documents = ResolveDocuments(connection, transaction, content);
        var replacement = new ProfileSnapshot(profileId, target, content.Name, content.Priority, content.OsFilter, documents, content.Variables, content.Policy);
        List<ProfileSnapshot> snapshots = LoadSnapshots(connection, transaction, replacement);

        IEnumerable<JsonElement> commands = ConfigStorage.ListLibrary(connection, transaction, DocumentKinds.Command).Select(static row => row.Body)
            .Concat(snapshots.SelectMany(static snapshot => snapshot.Documents).Where(static document => document.Kind == DocumentKinds.Command).Select(static document => document.Body));
        List<string> systemScripts = SystemScripts.Find(documents, SystemScripts.SystemScriptNames(commands));

        List<PlannedChange> changes = unchanged
            ? []
            : ComputeChanges(
                agents,
                snapshots,
                LoadAssignments(connection, transaction),
                ConfigStorage.ListGenerations(connection, transaction));
        return ConfigResults.Ok(new PublishPlan(profile, contentText, target, newRevision, unchanged, systemScripts, changes));
    }

    private static List<ResolvedDocument> ResolveDocuments(SqliteConnection connection, SqliteTransaction? transaction, ProfileContent content)
    {
        var documents = new List<ResolvedDocument>();
        foreach (ProfileItemRef item in content.Items)
        {
            LibraryRow row = ConfigStorage.FindLibrary(connection, transaction, item.Kind, item.Id, item.Revision ?? 0)
                ?? throw new InvalidOperationException(
                    "The library revision " + item.Kind + "/" + item.Id + "@" + (item.Revision ?? 0).ToString(CultureInfo.InvariantCulture) + " of a stored profile is missing.");
            documents.Add(new ResolvedDocument(row.Kind, row.Id, row.Revision, row.Hash, row.Body));
        }

        return documents;
    }

    /// <summary>The active revisions of all profiles, with <paramref name="replacement"/> standing in for the profile it belongs to.</summary>
    private static List<ProfileSnapshot> LoadSnapshots(SqliteConnection connection, SqliteTransaction? transaction, ProfileSnapshot? replacement)
    {
        var snapshots = new List<ProfileSnapshot>();
        foreach (ProfileRow profile in ConfigStorage.ListProfiles(connection, transaction))
        {
            if (replacement is not null && string.Equals(profile.Id, replacement.ProfileId, StringComparison.Ordinal))
            {
                continue;
            }

            if (profile.ActiveRevision is not { } active
                || ConfigStorage.FindProfileRevision(connection, transaction, profile.Id, active) is not { } revision)
            {
                continue;
            }

            ProfileContent content = revision.Content;
            snapshots.Add(new ProfileSnapshot(
                profile.Id,
                revision.Revision,
                content.Name,
                content.Priority,
                content.OsFilter,
                ResolveDocuments(connection, transaction, content),
                content.Variables,
                content.Policy));
        }

        if (replacement is not null)
        {
            snapshots.Add(replacement);
        }

        return snapshots;
    }

    private static List<ProfileAssignment> LoadAssignments(SqliteConnection connection, SqliteTransaction? transaction)
    {
        return [.. ConfigStorage.ListAssignments(connection, transaction).Select(static assignment => new ProfileAssignment(assignment.Id, assignment.ProfileId, assignment.Target))];
    }

    private static CommandDocument? FindCommand(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        LibraryRow? row = ConfigStorage.FindLibrary(connection, transaction, DocumentKinds.Command, id);
        if (row is null || row.DeletedAt is not null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(row.Body, ConfigJsonContext.Default.CommandDocument);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Draft validation --------------------------------------------------------------------------------------------------------------

    private static (ProfileContent? Content, List<ValidationError> Errors) NormalizeDraft(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ProfileDraftRequest request)
    {
        var errors = new List<ValidationError>();

        string name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl))
        {
            errors.Add(new ValidationError("/name", ValidationCodes.Invalid, "The name must have 1 to 100 characters and no control characters."));
        }

        int priority = request.Priority ?? 0;
        if (priority is < -MaxPriority or > MaxPriority)
        {
            errors.Add(new ValidationError("/priority", ValidationCodes.Range, "The priority must be between -10000 and 10000."));
        }

        string? osFilter = string.IsNullOrWhiteSpace(request.OsFilter) ? null : request.OsFilter.Trim().ToLowerInvariant();
        if (osFilter is not null && !OsFilterValues.Contains(osFilter, StringComparer.Ordinal))
        {
            errors.Add(new ValidationError("/osFilter", ValidationCodes.Invalid, "The filter must be windows or linux."));
        }

        List<ProfileItemRef> items = NormalizeItems(connection, transaction, request.Items ?? [], errors);
        SortedDictionary<string, string> variables = NormalizeVariables(request.Variables, errors);

        string localConfig = request.Policy?.LocalConfig ?? DesiredStateComputer.Inherit;
        if (!LocalConfigValues.Contains(localConfig, StringComparer.Ordinal))
        {
            errors.Add(new ValidationError("/policy/localConfig", ValidationCodes.Invalid, "localConfig must be allow, locked or inherit."));
        }

        string directLink = request.Policy?.DirectLink ?? DesiredStateComputer.Inherit;
        if (!DirectLinkValues.Contains(directLink, StringComparer.Ordinal))
        {
            errors.Add(new ValidationError("/policy/directLink", ValidationCodes.Invalid, "directLink must be allow, deny or inherit."));
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var content = new ProfileContent(
            name,
            priority,
            osFilter,
            items,
            new Dictionary<string, string>(variables, StringComparer.Ordinal),
            new ProfilePolicy(localConfig, directLink));
        return (content, errors);
    }

    private static List<ProfileItemRef> NormalizeItems(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<ProfileItemRef> requested,
        List<ValidationError> errors)
    {
        var items = new List<ProfileItemRef>();
        if (requested.Count > MaxItems)
        {
            errors.Add(new ValidationError("/items", ValidationCodes.Invalid, "A profile has at most 500 documents."));
            return items;
        }

        var seen = new HashSet<(string Kind, string Id)>();
        bool hasMetricOverrides = false;
        for (int index = 0; index < requested.Count; index++)
        {
            ProfileItemRef? item = requested[index];
            string path = JsonPointer.Index("/items", index);
            if (item is null || string.IsNullOrEmpty(item.Kind) || string.IsNullOrEmpty(item.Id))
            {
                errors.Add(new ValidationError(path, ValidationCodes.Required, "An item needs a kind and an id."));
                continue;
            }

            if (!DocumentKinds.All.Contains(item.Kind))
            {
                errors.Add(new ValidationError(JsonPointer.Member(path, "kind"), ValidationCodes.Invalid, "The kind is not one of command, sensor, script, mode, metric_overrides and variables."));
                continue;
            }

            if (!seen.Add((item.Kind, item.Id)))
            {
                errors.Add(new ValidationError(path, ValidationCodes.Duplicate, "The document is in the profile twice."));
                continue;
            }

            if (item.Kind == DocumentKinds.MetricOverrides)
            {
                if (hasMetricOverrides)
                {
                    errors.Add(new ValidationError(path, ValidationCodes.Conflict, "An agent has one metric override document, so a profile can contain only one."));
                    continue;
                }

                hasMetricOverrides = true;
            }

            LibraryRow? row = ConfigStorage.FindLibrary(connection, transaction, item.Kind, item.Id);
            if (row is null || row.DeletedAt is not null)
            {
                errors.Add(new ValidationError(path, ValidationCodes.UnknownReference, "There is no such library document."));
                continue;
            }

            int pinned = item.Revision ?? row.Revision;
            if (ConfigStorage.FindLibrary(connection, transaction, item.Kind, item.Id, pinned) is null)
            {
                errors.Add(new ValidationError(JsonPointer.Member(path, "revision"), ValidationCodes.UnknownReference, "The document has no such revision."));
                continue;
            }

            items.Add(new ProfileItemRef(item.Kind, item.Id, pinned));
        }

        return [.. items.OrderBy(static item => item.Kind, StringComparer.Ordinal).ThenBy(static item => item.Id, StringComparer.Ordinal)];
    }

    private static SortedDictionary<string, string> NormalizeVariables(IReadOnlyDictionary<string, string>? requested, List<ValidationError> errors)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (requested is null)
        {
            return variables;
        }

        if (requested.Count > MaxVariables)
        {
            errors.Add(new ValidationError("/variables", ValidationCodes.Invalid, "A profile has at most 256 variables."));
            return variables;
        }

        foreach ((string variable, string? value) in requested)
        {
            string path = JsonPointer.Member("/variables", variable);
            if (!LibraryDocuments.IsValidVariableName(variable))
            {
                errors.Add(new ValidationError(path, ValidationCodes.Invalid, "A variable name must match ^[A-Za-z][A-Za-z0-9_]{0,63}$."));
            }
            else if (value is null)
            {
                errors.Add(new ValidationError(path, ValidationCodes.TypeMismatch, "The value of a variable must be a string."));
            }
            else if (value.Length > LibraryDocuments.MaxVariableValueLength)
            {
                errors.Add(new ValidationError(path, ValidationCodes.MaxLength, "The value of a variable must not be longer than 4096 characters."));
            }
            else
            {
                variables[variable] = value;
            }
        }

        return variables;
    }

    // Mapping -----------------------------------------------------------------------------------------------------------------------

    private static LibraryDocumentSummary ToSummary(LibraryRow row)
    {
        return new LibraryDocumentSummary(row.Kind, row.Id, row.Name, row.Revision, row.Hash, row.DocumentCreatedAt, row.RevisionCreatedAt);
    }

    private static LibraryDocumentInfo ToInfo(LibraryRow row, bool? changed)
    {
        return new LibraryDocumentInfo(row.Kind, row.Id, row.Name, row.Revision, row.Hash, row.RevisionCreatedAt, row.CreatedBy, row.Body, changed);
    }

    private static ProfileInfo ToProfileInfo(SqliteConnection connection, SqliteTransaction? transaction, ProfileRow row)
    {
        bool unpublished = true;
        if (row.ActiveRevision is { } active
            && ConfigStorage.FindProfileRevision(connection, transaction, row.Id, active) is { } revision)
        {
            unpublished = !string.Equals(revision.ContentText, row.DraftText, StringComparison.Ordinal);
        }

        return new ProfileInfo(row.Id, row.Draft, row.ActiveRevision, row.LatestRevision, unpublished, row.CreatedAt, row.UpdatedAt);
    }

    private static ProfileRevisionSummary ToRevisionSummary(ProfileRevisionRow row, int? activeRevision)
    {
        return new ProfileRevisionSummary(row.Revision, row.PublishedAt, row.PublishedBy, row.Note, row.Revision == activeRevision);
    }

    private static PublishPreview ToPreview(PublishPlan plan)
    {
        bool system = plan.SystemScripts.Count > 0;
        return new PublishPreview(
            plan.Profile.Id,
            plan.Revision,
            plan.NewRevision,
            plan.Unchanged,
            system,
            plan.SystemScripts,
            system,
            [.. plan.Changes.Select(change => ToAgentChange(change, (change.Stored?.Generation ?? 0) + 1))]);
    }

    private static AgentChange ToAgentChange(PlannedChange change, long generation)
    {
        IReadOnlyList<DesiredItem> before = change.Stored?.Items ?? [];
        PolicyInfo beforePolicy = change.Stored?.Policy ?? DesiredStateComputer.DefaultPolicy;
        return new AgentChange(
            change.Agent.Id,
            change.Agent.Name,
            change.Stored?.Generation ?? 0,
            generation,
            DesiredStateDiff.Documents(before, change.State.Items),
            DesiredStateDiff.Policy(beforePolicy, change.State.Policy),
            [.. change.State.Warnings.Select(ToWarningInfo)]);
    }

    private static ConfigWarningInfo ToWarningInfo(ConfigWarning warning)
    {
        return new ConfigWarningInfo(warning.Code, warning.Kind, warning.Id, warning.Message, warning.Profile, warning.OtherProfile);
    }
}

/// <summary>The differences between two desired states, for the preview and the result of a publish.</summary>
public static class DesiredStateDiff
{
    /// <summary>Compares two item lists.</summary>
    /// <param name="before">The items the agent has.</param>
    /// <param name="after">The items it would get.</param>
    /// <returns>The documents that were added, removed or changed, sorted by kind and id.</returns>
    public static List<DocumentChange> Documents(IReadOnlyList<DesiredItem> before, IReadOnlyList<DesiredItem> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var old = new Dictionary<(string Kind, string Id), string>();
        foreach (DesiredItem item in before)
        {
            old[(item.Kind, item.Id)] = item.Hash;
        }

        var changes = new List<DocumentChange>();
        var current = new HashSet<(string Kind, string Id)>();
        foreach (DesiredItem item in after)
        {
            current.Add((item.Kind, item.Id));
            if (!old.TryGetValue((item.Kind, item.Id), out string? oldHash))
            {
                changes.Add(new DocumentChange(item.Kind, item.Id, "added", null, item.Hash));
            }
            else if (!string.Equals(oldHash, item.Hash, StringComparison.Ordinal))
            {
                changes.Add(new DocumentChange(item.Kind, item.Id, "changed", oldHash, item.Hash));
            }
        }

        foreach (DesiredItem item in before)
        {
            if (!current.Contains((item.Kind, item.Id)))
            {
                changes.Add(new DocumentChange(item.Kind, item.Id, "removed", item.Hash, null));
            }
        }

        changes.Sort(static (left, right) =>
        {
            int byKind = string.CompareOrdinal(left.Kind, right.Kind);
            return byKind != 0 ? byKind : string.CompareOrdinal(left.Id, right.Id);
        });
        return changes;
    }

    /// <summary>Compares two policies.</summary>
    /// <param name="before">The policy the agent has.</param>
    /// <param name="after">The policy it would get.</param>
    /// <returns>The members that differ.</returns>
    public static List<PolicyDifference> Policy(PolicyInfo before, PolicyInfo after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var differences = new List<PolicyDifference>();
        if (!string.Equals(before.LocalConfig, after.LocalConfig, StringComparison.Ordinal))
        {
            differences.Add(new PolicyDifference("local_config", before.LocalConfig, after.LocalConfig));
        }

        if (!string.Equals(before.DirectLink, after.DirectLink, StringComparison.Ordinal))
        {
            differences.Add(new PolicyDifference("direct_link", before.DirectLink, after.DirectLink));
        }

        return differences;
    }
}
