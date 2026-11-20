using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Agents;

/// <summary>The outcome of checking an agent credential.</summary>
public enum AgentCredentialOutcome
{
    /// <summary>The credential belongs to an enrolled agent.</summary>
    Accepted,

    /// <summary>The credential is not known.</summary>
    Unknown,

    /// <summary>The credential was revoked: the agent was removed, or the credential was replaced.</summary>
    Revoked,

    /// <summary>The credential belongs to an agent that is not (or no longer) enrolled. The agent may not open the link.</summary>
    NotApproved,
}

/// <summary>The result of <see cref="AgentRegistry.AuthenticateAsync"/>.</summary>
/// <param name="Outcome">What became of the credential.</param>
/// <param name="Agent">The agent when <paramref name="Outcome"/> is <see cref="AgentCredentialOutcome.Accepted"/> or <see cref="AgentCredentialOutcome.NotApproved"/>.</param>
public sealed record AgentCredentialResult(AgentCredentialOutcome Outcome, AgentRecord? Agent);

/// <summary>The outcome of an administrator's decision about an agent.</summary>
public enum AgentDecision
{
    /// <summary>The change was made.</summary>
    Done,

    /// <summary>There is no such agent.</summary>
    NotFound,

    /// <summary>The agent is in a status in which the change is not possible.</summary>
    WrongState,
}

/// <summary>The result of <see cref="AgentRegistry.BeginRotationAsync"/>.</summary>
/// <param name="Decision">Whether a rotation was started.</param>
/// <param name="Credential">The new credential, for the gateway to send to the agent; null unless <paramref name="Decision"/> is <see cref="AgentDecision.Done"/>.</param>
public sealed record RotationStart(AgentDecision Decision, string? Credential)
{
    /// <summary>Hides the credential from the generated <c>ToString</c> so that it does not end up in logs.</summary>
    /// <returns>A text without the credential.</returns>
    public override string ToString() => nameof(RotationStart) + " { Decision = " + Decision + " }";
}

/// <summary>
/// The agent registry (PLAN.md S20, T904): the agents, their tags and credentials. The hub keeps only hashes of credentials. A credential that is
/// replaced or whose agent is removed is remembered as revoked, so that a later connection with it is answered with HTTP 401 and the code
/// <c>revoked</c>. A credential that is rotated stays valid until the agent has used the new one: connecting with the new credential is the
/// proof that the agent has it.
/// </summary>
public sealed class AgentRegistry
{
    private readonly HubDatabase _database;
    private readonly TimeProvider _time;

    /// <summary>Creates the registry.</summary>
    /// <param name="database">The hub database.</param>
    /// <param name="time">The clock.</param>
    public AgentRegistry(HubDatabase database, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(time);

        _database = database;
        _time = time;
    }

    /// <summary>Lists the agents, the most recently enrolled first.</summary>
    /// <param name="status">Only the agents with this status (<see cref="AgentStatuses"/>), or null for all.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agents.</returns>
    public Task<IReadOnlyList<AgentRecord>> ListAsync(string? status, CancellationToken cancellationToken)
    {
        return _database.QueryAsync<IReadOnlyList<AgentRecord>>(connection => AgentStore.List(connection, status), cancellationToken);
    }

    /// <summary>Finds an agent.</summary>
    /// <param name="id">The agent id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agent, or null.</returns>
    public Task<AgentRecord?> FindAsync(string id, CancellationToken cancellationToken)
    {
        return _database.QueryAsync(connection => AgentStore.Find(connection, null, id), cancellationToken);
    }

    /// <summary>Replaces the tags of an agent.</summary>
    /// <param name="id">The agent id.</param>
    /// <param name="tags">The new tags; validated and normalised by the caller (<see cref="AgentTags.TryNormalize"/>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agent after the change, or null when there is no such agent.</returns>
    public Task<AgentRecord?> SetTagsAsync(string id, IReadOnlyList<string> tags, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tags);

        return _database.InTransactionAsync<AgentRecord?>(
            (connection, transaction) =>
            {
                return AgentStore.SetTags(connection, transaction, id, tags) == 0 ? null : AgentStore.Find(connection, transaction, id);
            },
            cancellationToken);
    }

    /// <summary>Accepts a pending agent. It receives its credential with its next enroll request.</summary>
    /// <param name="id">The agent id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The decision outcome.</returns>
    public Task<AgentDecision> ApproveAsync(string id, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return Decide(id, AgentStatuses.Approved, now, cancellationToken);
    }

    /// <summary>Rejects a pending agent. Its next enroll request is answered with the code <c>rejected</c>.</summary>
    /// <param name="id">The agent id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The decision outcome.</returns>
    public Task<AgentDecision> RejectAsync(string id, CancellationToken cancellationToken)
    {
        return Decide(id, AgentStatuses.Rejected, null, cancellationToken);
    }

    /// <summary>Removes an agent and revokes its credentials.</summary>
    /// <param name="id">The agent id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agent that was removed, or null when there was none.</returns>
    public Task<AgentRecord?> RemoveAsync(string id, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return _database.InTransactionAsync<AgentRecord?>(
            (connection, transaction) =>
            {
                AgentRecord? agent = AgentStore.Find(connection, transaction, id);
                if (agent is null)
                {
                    return null;
                }

                AgentStore.RevokeAll(connection, transaction, id, now, "removed");
                AgentStore.Delete(connection, transaction, id);
                return agent;
            },
            cancellationToken);
    }

    /// <summary>
    /// Starts the rotation of the credential of an enrolled agent: creates the new credential and keeps only its hash. The old credential stays
    /// valid until the agent connects with the new one or <see cref="CompleteRotationAsync"/> is called. A rotation that is already under way is replaced.
    /// </summary>
    /// <param name="id">The agent id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The outcome with the new credential, for the gateway to send in <c>credential_rotate</c>.</returns>
    public Task<RotationStart> BeginRotationAsync(string id, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return _database.InTransactionAsync(
            (connection, transaction) =>
            {
                AgentRecord? agent = AgentStore.Find(connection, transaction, id);
                if (agent is null)
                {
                    return new RotationStart(AgentDecision.NotFound, null);
                }

                if (agent.Status != AgentStatuses.Enrolled || agent.CredentialIssuedAt is null)
                {
                    return new RotationStart(AgentDecision.WrongState, null);
                }

                (string? _, string? oldPending) = AgentStore.CredentialHashes(connection, transaction, id);
                AgentStore.Revoke(connection, transaction, oldPending, id, now, "rotation replaced");

                string credential = AgentSecrets.Create();
                AgentStore.SetPendingCredential(connection, transaction, id, AgentSecrets.Hash(credential), now);
                return new RotationStart(AgentDecision.Done, credential);
            },
            cancellationToken);
    }

    /// <summary>Completes a rotation: the new credential becomes the current one and the old one is revoked. Does nothing when no rotation is under way.</summary>
    /// <param name="id">The agent id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that ends when the change is stored.</returns>
    public Task CompleteRotationAsync(string id, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return _database.InTransactionAsync(
            (connection, transaction) =>
            {
                AgentStore.PromotePendingCredential(connection, transaction, id, now);
                return 0;
            },
            cancellationToken);
    }

    /// <summary>Abandons a rotation that was not delivered: the new credential is revoked and the old one stays.</summary>
    /// <param name="id">The agent id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that ends when the change is stored.</returns>
    public Task CancelRotationAsync(string id, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return _database.InTransactionAsync(
            (connection, transaction) =>
            {
                (string? _, string? pending) = AgentStore.CredentialHashes(connection, transaction, id);
                AgentStore.Revoke(connection, transaction, pending, id, now, "rotation cancelled");
                AgentStore.ClearPendingCredential(connection, transaction, id);
                return 0;
            },
            cancellationToken);
    }

    /// <summary>
    /// Checks the credential of an agent that wants to open the agent link, before the WebSocket upgrade. An accepted credential updates the
    /// last-seen time; a credential that was issued by a rotation completes the rotation.
    /// </summary>
    /// <param name="credential">The bearer token of the request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The outcome and, for known credentials, the agent.</returns>
    public Task<AgentCredentialResult> AuthenticateAsync(string? credential, CancellationToken cancellationToken)
    {
        if (!AgentSecrets.LooksLikeCredential(credential))
        {
            return Task.FromResult(new AgentCredentialResult(AgentCredentialOutcome.Unknown, null));
        }

        string hash = AgentSecrets.Hash(credential!);
        DateTimeOffset now = _time.GetUtcNow();
        return _database.InTransactionAsync(
            (connection, transaction) =>
            {
                (AgentRecord Agent, bool IsPendingCredential)? found = AgentStore.FindByCredential(connection, transaction, hash);
                if (found is null)
                {
                    return new AgentCredentialResult(
                        AgentStore.IsRevoked(connection, transaction, hash) ? AgentCredentialOutcome.Revoked : AgentCredentialOutcome.Unknown,
                        null);
                }

                AgentRecord agent = found.Value.Agent;
                if (agent.Status != AgentStatuses.Enrolled)
                {
                    return new AgentCredentialResult(AgentCredentialOutcome.NotApproved, agent);
                }

                if (found.Value.IsPendingCredential)
                {
                    AgentStore.PromotePendingCredential(connection, transaction, agent.Id, now);
                }

                AgentStore.TouchSeen(connection, transaction, agent.Id, now);
                return new AgentCredentialResult(AgentCredentialOutcome.Accepted, AgentStore.Find(connection, transaction, agent.Id));
            },
            cancellationToken);
    }

    /// <summary>Records that an agent was seen, for example when its link ends.</summary>
    /// <param name="id">The agent id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that ends when the time is stored.</returns>
    public Task MarkSeenAsync(string id, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return _database.ExecuteAsync(connection => AgentStore.TouchSeen(connection, null, id, now), cancellationToken);
    }

    private Task<AgentDecision> Decide(string id, string to, DateTimeOffset? approvedAt, CancellationToken cancellationToken)
    {
        return _database.InTransactionAsync(
            (connection, transaction) =>
            {
                if (AgentStore.SetStatus(connection, transaction, id, AgentStatuses.Pending, to, approvedAt) > 0)
                {
                    return AgentDecision.Done;
                }

                return AgentStore.Find(connection, transaction, id) is null ? AgentDecision.NotFound : AgentDecision.WrongState;
            },
            cancellationToken);
    }
}
