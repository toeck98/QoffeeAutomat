using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Fleet;

/// <summary>A fleet token without its secret (PLAN.md S20 "Fleet link").</summary>
/// <param name="Id">The id of the token; the fleet link and the audit log name a token by it.</param>
/// <param name="Name">A label: the name that the client gave when it paired, or the name of the pairing code.</param>
/// <param name="Prefix">The first characters of the secret, so that an administrator can tell tokens apart.</param>
/// <param name="Scope">What the token may see and control.</param>
/// <param name="ClientKind">The kind of client that paired (<c>homeassistant</c> or <c>tool</c>).</param>
/// <param name="ClientName">The name that the client gave when it paired.</param>
/// <param name="CreatedBy">The user that created the pairing code, or <c>pairing</c> when the token was created otherwise.</param>
/// <param name="CreatedAt">When the token was issued.</param>
/// <param name="LastUsedAt">When a client last opened the fleet link with the token; null when never.</param>
/// <param name="RevokedAt">When the token was revoked; null while it works.</param>
public sealed record FleetTokenInfo(
    string Id,
    string Name,
    string Prefix,
    FleetScope Scope,
    string ClientKind,
    string ClientName,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt)
{
    /// <summary>True while the token has not been revoked.</summary>
    public bool Active => RevokedAt is null;
}

/// <summary>A new fleet token with its secret, which exists only in this object and in the answer of the pairing request that created it.</summary>
/// <param name="Info">The token.</param>
/// <param name="Secret">The secret that the client presents as bearer token.</param>
public sealed record IssuedFleetToken(FleetTokenInfo Info, string Secret)
{
    /// <summary>Hides the secret from the generated <c>ToString</c> so that it does not end up in logs.</summary>
    /// <returns>A text without the secret.</returns>
    public override string ToString() => nameof(IssuedFleetToken) + " { Id = " + Info.Id + " }";
}

/// <summary>The outcome of checking the bearer token of a fleet link request.</summary>
public enum FleetTokenOutcome
{
    /// <summary>The token is known and has not been revoked.</summary>
    Accepted,

    /// <summary>The token is not known.</summary>
    Unknown,

    /// <summary>The token was revoked.</summary>
    Revoked,
}

/// <summary>The result of <see cref="FleetTokenService.AuthenticateAsync"/>.</summary>
/// <param name="Outcome">What became of the token.</param>
/// <param name="Token">The token when <paramref name="Outcome"/> is <see cref="FleetTokenOutcome.Accepted"/> or <see cref="FleetTokenOutcome.Revoked"/>.</param>
public sealed record FleetTokenResult(FleetTokenOutcome Outcome, FleetTokenInfo? Token);

/// <summary>The statements on the table <c>fleet_tokens</c>, for callers that hold a transaction.</summary>
internal static class FleetTokenStore
{
    private const string Columns = "id, name, token_prefix, scope, tags, client_kind, client_name, created_by, created_at, last_used_at, revoked_at";

    public static FleetTokenInfo? FindByHash(SqliteConnection connection, SqliteTransaction? transaction, string hash)
    {
        using SqliteCommand command = HubSql.Command(connection, transaction, "SELECT " + Columns + " FROM fleet_tokens WHERE token_hash = @hash;", ("@hash", hash));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public static FleetTokenInfo? FindById(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using SqliteCommand command = HubSql.Command(connection, transaction, "SELECT " + Columns + " FROM fleet_tokens WHERE id = @id;", ("@id", id));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public static List<FleetTokenInfo> List(SqliteConnection connection)
    {
        var tokens = new List<FleetTokenInfo>();
        using SqliteCommand command = HubSql.Command(connection, null, "SELECT " + Columns + " FROM fleet_tokens ORDER BY created_at DESC, id;");
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            tokens.Add(Read(reader));
        }

        return tokens;
    }

    public static void Insert(SqliteConnection connection, SqliteTransaction transaction, FleetTokenInfo info, string hash)
    {
        HubSql.Execute(
            connection,
            transaction,
            "INSERT INTO fleet_tokens (id, name, token_hash, token_prefix, scope, tags, client_kind, client_name, created_by, created_at) "
            + "VALUES (@id, @name, @hash, @prefix, @scope, @tags, @kind, @clientName, @createdBy, @createdAt);",
            ("@id", info.Id),
            ("@name", info.Name),
            ("@hash", hash),
            ("@prefix", info.Prefix),
            ("@scope", info.Scope.Type),
            ("@tags", JsonSerializer.Serialize(info.Scope.Tags.ToArray(), AgentStorageJsonContext.Default.StringArray)),
            ("@kind", info.ClientKind),
            ("@clientName", info.ClientName),
            ("@createdBy", info.CreatedBy),
            ("@createdAt", HubSql.ToStored(info.CreatedAt)));
    }

    public static void Touch(SqliteConnection connection, SqliteTransaction? transaction, string id, DateTimeOffset now)
    {
        HubSql.Execute(
            connection,
            transaction,
            "UPDATE fleet_tokens SET last_used_at = @now WHERE id = @id;",
            ("@now", HubSql.ToStored(now)),
            ("@id", id));
    }

    public static void Revoke(SqliteConnection connection, SqliteTransaction transaction, string id, DateTimeOffset now)
    {
        HubSql.Execute(
            connection,
            transaction,
            "UPDATE fleet_tokens SET revoked_at = COALESCE(revoked_at, @now) WHERE id = @id;",
            ("@now", HubSql.ToStored(now)),
            ("@id", id));
    }

    private static FleetTokenInfo Read(SqliteDataReader reader)
    {
        IReadOnlyList<string> tags = JsonSerializer.Deserialize(reader.GetString(4), AgentStorageJsonContext.Default.StringArray) ?? [];
        return new FleetTokenInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            FleetScope.From(reader.GetString(3), tags),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            HubSql.FromStored(reader.GetInt64(8)),
            reader.IsDBNull(9) ? null : HubSql.FromStored(reader.GetInt64(9)),
            reader.IsDBNull(10) ? null : HubSql.FromStored(reader.GetInt64(10)));
    }
}

/// <summary>
/// Issues, lists, revokes and authenticates fleet tokens (PLAN.md S20 "Fleet link", "Hub security" item 3). A token is 32 random bytes as base64url; the hub keeps only
/// its SHA-256 hash, and the secret is shown once, in the answer to the pairing request. A token has a scope: every agent, or the agents that carry one of a set of tags.
/// A revoked token stays in the list, so that the answer to a client that still presents it can say that it was revoked.
/// </summary>
public sealed class FleetTokenService
{
    private const int PrefixLength = 6;

    private readonly HubDatabase _database;
    private readonly TimeProvider _time;

    /// <summary>Creates the service.</summary>
    /// <param name="database">The hub database.</param>
    /// <param name="time">The clock.</param>
    public FleetTokenService(HubDatabase database, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(time);

        _database = database;
        _time = time;
    }

    /// <summary>Lists all tokens, newest first, including revoked ones.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The tokens, without secrets.</returns>
    public Task<IReadOnlyList<FleetTokenInfo>> ListAsync(CancellationToken cancellationToken)
    {
        return _database.QueryAsync<IReadOnlyList<FleetTokenInfo>>(FleetTokenStore.List, cancellationToken);
    }

    /// <summary>Finds a token by its id.</summary>
    /// <param name="id">The id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token, or null.</returns>
    public Task<FleetTokenInfo?> FindAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        return _database.QueryAsync(connection => FleetTokenStore.FindById(connection, null, id), cancellationToken);
    }

    /// <summary>Issues a token.</summary>
    /// <param name="name">The label.</param>
    /// <param name="scope">What the token may see and control.</param>
    /// <param name="clientKind">The kind of client that pairs.</param>
    /// <param name="clientName">The name that the client gave.</param>
    /// <param name="createdBy">The user that created the pairing code.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token with its secret.</returns>
    public async Task<IssuedFleetToken> IssueAsync(
        string name,
        FleetScope scope,
        string clientKind,
        string clientName,
        string createdBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(clientKind);
        ArgumentNullException.ThrowIfNull(clientName);
        ArgumentNullException.ThrowIfNull(createdBy);

        string secret = AgentSecrets.Create();
        var info = new FleetTokenInfo(
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            name,
            secret[..PrefixLength],
            scope,
            clientKind,
            clientName,
            createdBy,
            _time.GetUtcNow(),
            null,
            null);
        string hash = AgentSecrets.Hash(secret);

        await _database.InTransactionAsync(
            (connection, transaction) =>
            {
                FleetTokenStore.Insert(connection, transaction, info, hash);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
        return new IssuedFleetToken(info, secret);
    }

    /// <summary>Revokes a token. The caller closes the open connections of the token (<see cref="FleetService.OnTokenRevoked"/>).</summary>
    /// <param name="id">The id of the token.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token after the change, or null when there is no such token.</returns>
    public Task<FleetTokenInfo?> RevokeAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        DateTimeOffset now = _time.GetUtcNow();
        return _database.InTransactionAsync<FleetTokenInfo?>(
            (connection, transaction) =>
            {
                FleetTokenStore.Revoke(connection, transaction, id, now);
                return FleetTokenStore.FindById(connection, transaction, id);
            },
            cancellationToken);
    }

    /// <summary>Checks the bearer token of a request that wants to open the fleet link. An accepted token gets its last-used time updated.</summary>
    /// <param name="secret">The bearer token of the request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The outcome and, for known tokens, the token.</returns>
    public Task<FleetTokenResult> AuthenticateAsync(string? secret, CancellationToken cancellationToken)
    {
        if (!AgentSecrets.LooksLikeCredential(secret))
        {
            return Task.FromResult(new FleetTokenResult(FleetTokenOutcome.Unknown, null));
        }

        string hash = AgentSecrets.Hash(secret!);
        DateTimeOffset now = _time.GetUtcNow();
        return _database.InTransactionAsync(
            (connection, transaction) =>
            {
                FleetTokenInfo? found = FleetTokenStore.FindByHash(connection, transaction, hash);
                if (found is null)
                {
                    return new FleetTokenResult(FleetTokenOutcome.Unknown, null);
                }

                if (!found.Active)
                {
                    return new FleetTokenResult(FleetTokenOutcome.Revoked, found);
                }

                FleetTokenStore.Touch(connection, transaction, found.Id, now);
                return new FleetTokenResult(FleetTokenOutcome.Accepted, found with { LastUsedAt = now });
            },
            cancellationToken);
    }
}
