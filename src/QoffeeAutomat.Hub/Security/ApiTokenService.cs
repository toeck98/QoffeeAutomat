using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Security;

/// <summary>An API token as the hub knows it. The secret itself is never stored, only its SHA-256 hash.</summary>
/// <param name="Id">The id of the token, for management and the audit log.</param>
/// <param name="Name">A name that says what the token is for.</param>
/// <param name="Role">The role the token acts with, one of <see cref="HubRoles"/>.</param>
/// <param name="Prefix">The first characters of the secret, to recognise a token.</param>
/// <param name="CreatedBy">The name of the account that created it.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="ExpiresAt">When it stops working, or null for no expiry.</param>
/// <param name="LastUsedAt">When it was last used (to the granularity of <see cref="HubAuthOptions.TokenUseGranularity"/>), or null.</param>
/// <param name="RevokedAt">When it was revoked, or null.</param>
public sealed record ApiTokenInfo(
    string Id,
    string Name,
    string Role,
    string Prefix,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt)
{
    /// <summary>Tells whether the token still works.</summary>
    /// <param name="now">The current time.</param>
    /// <returns>True when it is neither revoked nor expired.</returns>
    public bool IsActive(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}

/// <summary>A new API token: the record and the secret, which is shown to the caller once and cannot be read again.</summary>
/// <param name="Info">The token record.</param>
/// <param name="Secret">The secret that the client sends as <c>Authorization: Bearer &lt;secret&gt;</c>.</param>
public sealed record IssuedApiToken(ApiTokenInfo Info, string Secret)
{
    /// <summary>Hides the secret from the generated <c>ToString</c> so that it does not end up in logs.</summary>
    /// <returns>A text without the secret.</returns>
    public override string ToString() => nameof(IssuedApiToken) + " { Id = " + Info.Id + " }";
}

/// <summary>
/// API tokens (PLAN.md S20 "Hub security" items 1 and 3): 32 random bytes with the prefix <c>qht_</c>, shown once, stored as a
/// SHA-256 hash, with a role, an optional expiry and individual revocation. A token authenticates as <c>token:&lt;name&gt;</c> with its role.
/// </summary>
public sealed class ApiTokenService
{
    /// <summary>The prefix of every token secret; it makes a leaked token recognisable for secret scanners.</summary>
    public const string SecretPrefix = "qht_";

    private const int SecretBytes = 32;
    private const int PrefixLength = 8;
    private const string Columns = "id, name, role, token_prefix, created_by, created_at, expires_at, last_used_at, revoked_at";

    private readonly HubDatabase _database;
    private readonly TimeProvider _time;
    private readonly HubAuthOptions _options;

    /// <summary>Creates the service.</summary>
    /// <param name="database">The migrated database.</param>
    /// <param name="time">The clock.</param>
    /// <param name="options">The limits.</param>
    public ApiTokenService(HubDatabase database, TimeProvider time, HubAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);

        _database = database;
        _time = time;
        _options = options;
    }

    /// <summary>Lists all tokens, the newest first, including revoked and expired ones.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The tokens.</returns>
    public Task<IReadOnlyList<ApiTokenInfo>> ListAsync(CancellationToken cancellationToken)
    {
        return _database.QueryAsync<IReadOnlyList<ApiTokenInfo>>(
            connection =>
            {
                var tokens = new List<ApiTokenInfo>();
                using SqliteCommand command = HubSql.Command(connection, null, "SELECT " + Columns + " FROM api_tokens ORDER BY created_at DESC, id;");
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    tokens.Add(Read(reader));
                }

                return tokens;
            },
            cancellationToken);
    }

    /// <summary>Creates a token.</summary>
    /// <param name="name">The name; the caller has validated it.</param>
    /// <param name="role">The role; the caller has validated it.</param>
    /// <param name="expiresAt">When the token stops working, or null.</param>
    /// <param name="createdBy">The name of the account that creates it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token record and its secret.</returns>
    public async Task<IssuedApiToken> CreateAsync(string name, string role, DateTimeOffset? expiresAt, string createdBy, CancellationToken cancellationToken)
    {
        string secret = SecretPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));
        var info = new ApiTokenInfo(
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            name,
            role,
            secret[..PrefixLength],
            createdBy,
            _time.GetUtcNow(),
            expiresAt,
            null,
            null);

        await _database.ExecuteAsync(
            connection => HubSql.Execute(
                connection,
                null,
                "INSERT INTO api_tokens (id, name, role, token_hash, token_prefix, created_by, created_at, expires_at) "
                + "VALUES (@id, @name, @role, @hash, @prefix, @createdBy, @createdAt, @expiresAt);",
                ("@id", info.Id),
                ("@name", info.Name),
                ("@role", info.Role),
                ("@hash", Hash(secret)),
                ("@prefix", info.Prefix),
                ("@createdBy", info.CreatedBy),
                ("@createdAt", HubSql.ToStored(info.CreatedAt)),
                ("@expiresAt", info.ExpiresAt is { } expiry ? HubSql.ToStored(expiry) : (object?)null)),
            cancellationToken).ConfigureAwait(false);
        return new IssuedApiToken(info, secret);
    }

    /// <summary>Revokes a token. A revoked token stays in the list, so the audit log still points at something.</summary>
    /// <param name="id">The id of the token.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token as it is now, or null when there is no such token. Revoking a revoked token changes nothing.</returns>
    public Task<ApiTokenInfo?> RevokeAsync(string id, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return _database.InTransactionAsync(
            (connection, transaction) =>
            {
                HubSql.Execute(
                    connection,
                    transaction,
                    "UPDATE api_tokens SET revoked_at = COALESCE(revoked_at, @now) WHERE id = @id;",
                    ("@now", HubSql.ToStored(now)),
                    ("@id", id));
                return FindById(connection, transaction, id);
            },
            cancellationToken);
    }

    /// <summary>Finds a token by id.</summary>
    /// <param name="id">The id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token, or null.</returns>
    public Task<ApiTokenInfo?> FindAsync(string id, CancellationToken cancellationToken)
    {
        return _database.QueryAsync(connection => FindById(connection, null, id), cancellationToken);
    }

    /// <summary>Authenticates a request by the secret of an API token.</summary>
    /// <param name="secret">The secret from the <c>Authorization</c> header.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token, or null when the secret is unknown, revoked or expired.</returns>
    public async Task<ApiTokenInfo?> AuthenticateAsync(string secret, CancellationToken cancellationToken)
    {
        if (!LooksLikeSecret(secret))
        {
            return null;
        }

        string hash = Hash(secret);
        DateTimeOffset now = _time.GetUtcNow();
        return await _database.QueryAsync(
            connection =>
            {
                ApiTokenInfo? token = null;
                using (SqliteCommand command = HubSql.Command(connection, null, "SELECT " + Columns + " FROM api_tokens WHERE token_hash = @hash;", ("@hash", hash)))
                using (SqliteDataReader reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        token = Read(reader);
                    }
                }

                if (token is null || !token.IsActive(now))
                {
                    return null;
                }

                if (token.LastUsedAt is null || now - token.LastUsedAt >= _options.TokenUseGranularity)
                {
                    HubSql.Execute(connection, null, "UPDATE api_tokens SET last_used_at = @now WHERE id = @id;", ("@now", HubSql.ToStored(now)), ("@id", token.Id));
                    token = token with { LastUsedAt = now };
                }

                return token;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tells whether a string has the shape of a token secret: the prefix and 43 base64url characters.</summary>
    /// <param name="secret">The candidate.</param>
    /// <returns>True when the shape is right; nothing is said about whether the token exists.</returns>
    public static bool LooksLikeSecret(string? secret)
    {
        const int base64Length = 43;
        if (secret is null || secret.Length != SecretPrefix.Length + base64Length || !secret.StartsWith(SecretPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char character in secret.AsSpan(SecretPrefix.Length))
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static ApiTokenInfo? FindById(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using SqliteCommand command = HubSql.Command(connection, transaction, "SELECT " + Columns + " FROM api_tokens WHERE id = @id;", ("@id", id));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static ApiTokenInfo Read(SqliteDataReader reader)
    {
        return new ApiTokenInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            HubSql.FromStored(reader.GetInt64(5)),
            reader.IsDBNull(6) ? null : HubSql.FromStored(reader.GetInt64(6)),
            reader.IsDBNull(7) ? null : HubSql.FromStored(reader.GetInt64(7)),
            reader.IsDBNull(8) ? null : HubSql.FromStored(reader.GetInt64(8)));
    }
}
