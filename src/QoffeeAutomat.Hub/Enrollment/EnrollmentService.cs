using Microsoft.Data.Sqlite;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Enrollment;

/// <summary>Limits of the enrollment endpoint.</summary>
public sealed record HubEnrollmentOptions
{
    /// <summary>The number of seconds after which a pending agent repeats its request (<c>retry_after</c>).</summary>
    public int RetryAfterSeconds { get; init; } = 30;

    /// <summary>The most agents that may wait for approval at the same time; more requests are answered with 429.</summary>
    public int MaxPendingAgents { get; init; } = 500;

    /// <summary>The largest body of an enroll request that is read.</summary>
    public int MaxBodyBytes { get; init; } = 16 * 1024;
}

/// <summary>What became of an enroll request.</summary>
public enum EnrollStatus
{
    /// <summary>The agent is enrolled and gets a credential.</summary>
    Enrolled,

    /// <summary>The agent waits for approval.</summary>
    Pending,

    /// <summary>The token is unknown, revoked, expired or used up.</summary>
    InvalidToken,

    /// <summary>An administrator rejected the agent.</summary>
    Rejected,

    /// <summary>The request is malformed.</summary>
    BadRequest,

    /// <summary>Too many agents wait for approval.</summary>
    TooManyPending,
}

/// <summary>The outcome of <see cref="EnrollmentService.EnrollAsync"/>.</summary>
/// <param name="Status">What became of the request.</param>
/// <param name="AgentId">The agent id, when the request was well formed.</param>
/// <param name="Credential">The credential; only for <see cref="EnrollStatus.Enrolled"/>.</param>
/// <param name="Notable">True when the request changed something (a new enrollment, the hand-over of a credential), false for a pending agent that only asks again.</param>
/// <param name="Message">What is wrong, for the failure statuses.</param>
public sealed record EnrollOutcome(EnrollStatus Status, string? AgentId, string? Credential, bool Notable, string? Message)
{
    /// <summary>Hides the credential from the generated <c>ToString</c> so that it does not end up in logs.</summary>
    /// <returns>A text without the credential.</returns>
    public override string ToString() => nameof(EnrollOutcome) + " { Status = " + Status + ", AgentId = " + AgentId + " }";
}

/// <summary>
/// The enrollment of an agent (PLAN.md S20 "Enrollment and trust" step 3, T904). The use of the token and the change of the agent run in one
/// transaction, so two requests can never share the last use of a token.
/// <list type="bullet">
/// <item>A new agent id with a usable token: the agent is enrolled and receives a credential, or, when the token requires approval, becomes pending without one.</item>
/// <item>The same agent id again with a usable token: the credential is replaced and the old one revoked. Without a valid token nothing changes.</item>
/// <item>A pending agent asks again with the token it enrolled with: it stays pending and no further use of the token is counted. After approval the same request returns the credential.</item>
/// </list>
/// </summary>
public sealed class EnrollmentService
{
    private const int MaxMacs = 16;

    private readonly HubDatabase _database;
    private readonly TimeProvider _time;
    private readonly HubEnrollmentOptions _options;

    /// <summary>Creates the service.</summary>
    /// <param name="database">The hub database.</param>
    /// <param name="time">The clock.</param>
    /// <param name="options">The limits.</param>
    public EnrollmentService(HubDatabase database, TimeProvider time, HubEnrollmentOptions options)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);

        _database = database;
        _time = time;
        _options = options;
    }

    /// <summary>The number of seconds after which a pending agent repeats its request.</summary>
    public int RetryAfterSeconds => _options.RetryAfterSeconds;

    /// <summary>Handles an enroll request.</summary>
    /// <param name="request">The request body.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The outcome.</returns>
    public Task<EnrollOutcome> EnrollAsync(EnrollRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!AgentSecrets.LooksLikeToken(request.Token))
        {
            return Task.FromResult(new EnrollOutcome(EnrollStatus.BadRequest, null, null, false, "The enrollment token is malformed."));
        }

        string? error = Validate(request.Agent, out AgentEnrollmentWrite? candidate);
        if (error is not null || candidate is null)
        {
            return Task.FromResult(new EnrollOutcome(EnrollStatus.BadRequest, null, null, false, error));
        }

        string tokenHash = AgentSecrets.Hash(request.Token);
        DateTimeOffset now = _time.GetUtcNow();
        return _database.InTransactionAsync(
            (connection, transaction) => Enroll(connection, transaction, tokenHash, candidate, now),
            cancellationToken);
    }

    private static EnrollOutcome Failure(EnrollStatus status, string agentId, string message) => new(status, agentId, null, false, message);

    private EnrollOutcome Enroll(SqliteConnection connection, SqliteTransaction transaction, string tokenHash, AgentEnrollmentWrite candidate, DateTimeOffset now)
    {
        EnrollmentTokenInfo? token = EnrollmentTokenStore.FindByHash(connection, transaction, tokenHash);
        if (token is null)
        {
            return Failure(EnrollStatus.InvalidToken, candidate.Id, "The enrollment token is not valid.");
        }

        AgentRecord? existing = AgentStore.Find(connection, transaction, candidate.Id);
        AgentEnrollmentWrite write = candidate with { Now = now, TokenId = token.Id };

        // The agent asks again for the enrollment that it started with this token: nothing is counted.
        if (existing is not null && existing.TokenId == token.Id && existing.Status != AgentStatuses.Enrolled)
        {
            switch (existing.Status)
            {
                case AgentStatuses.Pending:
                    AgentStore.UpdateReportedInfo(connection, transaction, write);
                    return new EnrollOutcome(EnrollStatus.Pending, existing.Id, null, false, null);
                case AgentStatuses.Rejected:
                    return Failure(EnrollStatus.Rejected, existing.Id, "An administrator rejected this agent.");
                default:
                    string handedOver = AgentSecrets.Create();
                    AgentStore.UpdateReportedInfo(connection, transaction, write);
                    AgentStore.IssueCredential(connection, transaction, existing.Id, AgentSecrets.Hash(handedOver), now);
                    return new EnrollOutcome(EnrollStatus.Enrolled, existing.Id, handedOver, true, null);
            }
        }

        if (!token.IsUsable(now))
        {
            return Failure(EnrollStatus.InvalidToken, candidate.Id, "The enrollment token is not valid.");
        }

        bool needsApproval = token.RequiresApproval;
        if (needsApproval && existing?.Status != AgentStatuses.Pending
            && AgentStore.Count(connection, transaction, AgentStatuses.Pending) >= _options.MaxPendingAgents)
        {
            return Failure(EnrollStatus.TooManyPending, candidate.Id, "Too many agents are waiting for approval.");
        }

        if (!EnrollmentTokenStore.TryConsume(connection, transaction, token.Id, now))
        {
            return Failure(EnrollStatus.InvalidToken, candidate.Id, "The enrollment token is not valid.");
        }

        IReadOnlyList<string> tags = existing is null ? token.Tags : AgentTags.Union(existing.Tags, token.Tags);
        if (existing is not null)
        {
            // The agent has lost its credential or is set up again: whatever it held before is no longer accepted.
            AgentStore.RevokeAll(connection, transaction, existing.Id, now, "replaced");
        }

        string? credential = needsApproval ? null : AgentSecrets.Create();
        AgentStore.WriteEnrollment(
            connection,
            transaction,
            write with
            {
                Tags = tags,
                Status = needsApproval ? AgentStatuses.Pending : AgentStatuses.Enrolled,
                CredentialHash = credential is null ? null : AgentSecrets.Hash(credential),
            });
        return new EnrollOutcome(needsApproval ? EnrollStatus.Pending : EnrollStatus.Enrolled, candidate.Id, credential, true, null);
    }

    private static string? Validate(AgentInfo agent, out AgentEnrollmentWrite? write)
    {
        write = null;
        string id = agent.Id?.Trim() ?? string.Empty;
        if (id.Length is 0 or > 64 || id.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return "The agent id must have 1 to 64 characters without white space.";
        }

        string? name = Text(agent.Name, 128);
        string? hostname = Text(agent.Hostname, 255);
        string? os = Text(agent.Os, 32);
        string? arch = Text(agent.Arch, 32);
        string? version = Text(agent.Version, 64);
        if (name is null || hostname is null || os is null || arch is null || version is null)
        {
            return "The agent name, host name, os, arch and version must be present and short, without control characters.";
        }

        string? osVersion = null;
        if (!string.IsNullOrWhiteSpace(agent.OsVersion))
        {
            osVersion = Text(agent.OsVersion, 64);
            if (osVersion is null)
            {
                return "The os version is too long or has control characters.";
            }
        }

        var macs = new List<string>();
        foreach (string mac in agent.Macs ?? [])
        {
            string? text = Text(mac, 32);
            if (text is null || macs.Count >= MaxMacs)
            {
                return "At most 16 MAC addresses of at most 32 characters are allowed.";
            }

            macs.Add(text);
        }

        string? uiUrl = null;
        if (agent.UiUrl is not null)
        {
            if (!agent.UiUrl.IsAbsoluteUri
                || (agent.UiUrl.Scheme != Uri.UriSchemeHttps && agent.UiUrl.Scheme != Uri.UriSchemeHttp)
                || agent.UiUrl.OriginalString.Length > 512)
            {
                return "The UI address must be an http or https address of at most 512 characters.";
            }

            uiUrl = agent.UiUrl.AbsoluteUri;
        }

        write = new AgentEnrollmentWrite(id, name, hostname, os, osVersion, arch, version, macs, uiUrl, [], AgentStatuses.Pending, default, null, string.Empty, null);
        return null;
    }

    private static string? Text(string? value, int maxLength)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length is 0 || trimmed.Length > maxLength || trimmed.Any(char.IsControl) ? null : trimmed;
    }
}
