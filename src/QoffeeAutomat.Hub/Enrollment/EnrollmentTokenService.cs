using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Enrollment;

/// <summary>An enrollment token without its secret (PLAN.md S20 "Enrollment and trust" step 1).</summary>
/// <param name="Id">The id of the token.</param>
/// <param name="Name">A label that the administrator chose.</param>
/// <param name="Prefix">The first characters of the secret, so that an administrator can tell tokens apart.</param>
/// <param name="Tags">The tags that agents enrolled with this token get.</param>
/// <param name="RequiresApproval">True when an administrator has to accept every agent that enrolls with this token.</param>
/// <param name="MaxUses">How many enrollments the token allows.</param>
/// <param name="Uses">How many enrollments have used the token.</param>
/// <param name="CreatedBy">The user that created the token.</param>
/// <param name="CreatedAt">When the token was created.</param>
/// <param name="ExpiresAt">When the token expires, or null when it does not.</param>
/// <param name="LastUsedAt">When the token was last used, or null.</param>
/// <param name="RevokedAt">When the token was revoked, or null.</param>
public sealed record EnrollmentTokenInfo(
    string Id,
    string Name,
    string Prefix,
    IReadOnlyList<string> Tags,
    bool RequiresApproval,
    int MaxUses,
    int Uses,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt)
{
    /// <summary>Tells whether the token can enroll one more agent at a time.</summary>
    /// <param name="now">The current time.</param>
    /// <returns>True when the token is not revoked, not expired and has uses left.</returns>
    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now) && Uses < MaxUses;
}

/// <summary>A new enrollment token with its secret, which exists only in this object and in the answer of the call that created it.</summary>
/// <param name="Info">The token.</param>
/// <param name="Secret">The secret that goes into the enrollment string.</param>
public sealed record IssuedEnrollmentToken(EnrollmentTokenInfo Info, string Secret)
{
    /// <summary>Hides the secret from the generated <c>ToString</c> so that it does not end up in logs.</summary>
    /// <returns>A text without the secret.</returns>
    public override string ToString() => nameof(IssuedEnrollmentToken) + " { Id = " + Info.Id + " }";
}

/// <summary>The statements on the table <c>enrollment_tokens</c>, for callers that hold a transaction.</summary>
internal static class EnrollmentTokenStore
{
    private const string Columns = "id, name, token_prefix, tags, requires_approval, max_uses, uses, created_by, created_at, expires_at, last_used_at, revoked_at";

    public static EnrollmentTokenInfo? FindByHash(SqliteConnection connection, SqliteTransaction? transaction, string hash)
    {
        using SqliteCommand command = HubSql.Command(connection, transaction, "SELECT " + Columns + " FROM enrollment_tokens WHERE token_hash = @hash;", ("@hash", hash));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public static EnrollmentTokenInfo? FindById(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using SqliteCommand command = HubSql.Command(connection, transaction, "SELECT " + Columns + " FROM enrollment_tokens WHERE id = @id;", ("@id", id));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public static List<EnrollmentTokenInfo> List(SqliteConnection connection)
    {
        var tokens = new List<EnrollmentTokenInfo>();
        using SqliteCommand command = HubSql.Command(connection, null, "SELECT " + Columns + " FROM enrollment_tokens ORDER BY created_at DESC, id;");
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            tokens.Add(Read(reader));
        }

        return tokens;
    }

    public static void Insert(SqliteConnection connection, SqliteTransaction transaction, EnrollmentTokenInfo info, string hash)
    {
        HubSql.Execute(
            connection,
            transaction,
            "INSERT INTO enrollment_tokens (id, name, token_hash, token_prefix, tags, requires_approval, max_uses, uses, created_by, created_at, expires_at) "
            + "VALUES (@id, @name, @hash, @prefix, @tags, @approval, @maxUses, 0, @createdBy, @createdAt, @expiresAt);",
            ("@id", info.Id),
            ("@name", info.Name),
            ("@hash", hash),
            ("@prefix", info.Prefix),
            ("@tags", JsonSerializer.Serialize(info.Tags.ToArray(), AgentStorageJsonContext.Default.StringArray)),
            ("@approval", info.RequiresApproval ? 1 : 0),
            ("@maxUses", info.MaxUses),
            ("@createdBy", info.CreatedBy),
            ("@createdAt", HubSql.ToStored(info.CreatedAt)),
            ("@expiresAt", info.ExpiresAt is { } expiry ? HubSql.ToStored(expiry) : (object?)null));
    }

    /// <summary>Counts one use of a token. Only succeeds while the token has uses left, so two requests can never share the last use.</summary>
    public static bool TryConsume(SqliteConnection connection, SqliteTransaction transaction, string id, DateTimeOffset now)
    {
        return HubSql.Execute(
            connection,
            transaction,
            "UPDATE enrollment_tokens SET uses = uses + 1, last_used_at = @now WHERE id = @id AND uses < COALESCE(max_uses, 9223372036854775807);",
            ("@now", HubSql.ToStored(now)),
            ("@id", id)) > 0;
    }

    public static void Revoke(SqliteConnection connection, SqliteTransaction transaction, string id, DateTimeOffset now)
    {
        HubSql.Execute(
            connection,
            transaction,
            "UPDATE enrollment_tokens SET revoked_at = COALESCE(revoked_at, @now) WHERE id = @id;",
            ("@now", HubSql.ToStored(now)),
            ("@id", id));
    }

    private static EnrollmentTokenInfo Read(SqliteDataReader reader)
    {
        return new EnrollmentTokenInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            JsonSerializer.Deserialize(reader.GetString(3), AgentStorageJsonContext.Default.StringArray) ?? [],
            reader.GetInt64(4) != 0,
            reader.IsDBNull(5) ? int.MaxValue : checked((int)reader.GetInt64(5)),
            checked((int)reader.GetInt64(6)),
            reader.GetString(7),
            HubSql.FromStored(reader.GetInt64(8)),
            reader.IsDBNull(9) ? null : HubSql.FromStored(reader.GetInt64(9)),
            reader.IsDBNull(10) ? null : HubSql.FromStored(reader.GetInt64(10)),
            reader.IsDBNull(11) ? null : HubSql.FromStored(reader.GetInt64(11)));
    }
}

/// <summary>
/// Creates, lists and revokes enrollment tokens (PLAN.md S20 "Hub security" item 5). A token has an optional expiry, a maximum number of uses,
/// tags for the agents it enrolls and an approval flag. The secret is shown once, when the token is created; the hub stores only its SHA-256 hash.
/// </summary>
public sealed class EnrollmentTokenService
{
    private const int PrefixLength = 6;

    private readonly HubDatabase _database;
    private readonly TimeProvider _time;

    /// <summary>Creates the service.</summary>
    /// <param name="database">The hub database.</param>
    /// <param name="time">The clock.</param>
    public EnrollmentTokenService(HubDatabase database, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(time);

        _database = database;
        _time = time;
    }

    /// <summary>Lists all tokens, newest first, including revoked, expired and used-up ones.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The tokens, without secrets.</returns>
    public Task<IReadOnlyList<EnrollmentTokenInfo>> ListAsync(CancellationToken cancellationToken)
    {
        return _database.QueryAsync<IReadOnlyList<EnrollmentTokenInfo>>(EnrollmentTokenStore.List, cancellationToken);
    }

    /// <summary>Finds a token by its id.</summary>
    /// <param name="id">The id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token, or null.</returns>
    public Task<EnrollmentTokenInfo?> FindAsync(string id, CancellationToken cancellationToken)
    {
        return _database.QueryAsync(connection => EnrollmentTokenStore.FindById(connection, null, id), cancellationToken);
    }

    /// <summary>Creates a token.</summary>
    /// <param name="name">The label.</param>
    /// <param name="tags">The tags for the agents; already normalised.</param>
    /// <param name="requiresApproval">Whether agents wait for approval.</param>
    /// <param name="maxUses">How many agents may enroll with the token.</param>
    /// <param name="expiresAt">When the token expires, or null.</param>
    /// <param name="createdBy">The user that creates the token.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token with its secret.</returns>
    public async Task<IssuedEnrollmentToken> CreateAsync(
        string name,
        IReadOnlyList<string> tags,
        bool requiresApproval,
        int maxUses,
        DateTimeOffset? expiresAt,
        string createdBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tags);

        string secret = AgentSecrets.Create();
        var info = new EnrollmentTokenInfo(
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            name,
            secret[..PrefixLength],
            tags,
            requiresApproval,
            maxUses,
            0,
            createdBy,
            _time.GetUtcNow(),
            expiresAt,
            null,
            null);
        string hash = AgentSecrets.Hash(secret);

        await _database.InTransactionAsync(
            (connection, transaction) =>
            {
                EnrollmentTokenStore.Insert(connection, transaction, info, hash);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
        return new IssuedEnrollmentToken(info, secret);
    }

    /// <summary>Revokes a token. Agents that have already enrolled keep working; no new agent can use the token.</summary>
    /// <param name="id">The id of the token.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token after the change, or null when there is no such token.</returns>
    public Task<EnrollmentTokenInfo?> RevokeAsync(string id, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return _database.InTransactionAsync<EnrollmentTokenInfo?>(
            (connection, transaction) =>
            {
                EnrollmentTokenStore.Revoke(connection, transaction, id, now);
                return EnrollmentTokenStore.FindById(connection, transaction, id);
            },
            cancellationToken);
    }
}
