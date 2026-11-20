using System.Globalization;
using Microsoft.Data.Sqlite;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Security;

/// <summary>A local account of the hub (without its password hash).</summary>
/// <param name="Id">The id: 32 lower-case hex digits.</param>
/// <param name="Username">The sign-in name; unique, compared without regard to case.</param>
/// <param name="Role">One of <see cref="HubRoles"/>.</param>
/// <param name="Disabled">A disabled account cannot sign in; its sessions end.</param>
/// <param name="CreatedAt">When the account was created.</param>
/// <param name="UpdatedAt">When the role or the disabled flag last changed.</param>
/// <param name="PasswordChangedAt">When the password was last set.</param>
/// <param name="LastLoginAt">The last successful sign-in, or null.</param>
public sealed record HubUser(
    string Id,
    string Username,
    string Role,
    bool Disabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset PasswordChangedAt,
    DateTimeOffset? LastLoginAt);

/// <summary>An account together with its salted password hash. The hash never leaves the security code.</summary>
/// <param name="User">The account.</param>
/// <param name="PasswordHash">The stored hash, see <see cref="QoffeeAutomat.Core.Security.PasswordHasher"/>.</param>
public sealed record HubUserRecord(HubUser User, string PasswordHash)
{
    /// <summary>Hides the hash from the generated <c>ToString</c> so that it does not end up in logs.</summary>
    /// <returns>A text without the hash.</returns>
    public override string ToString() => nameof(HubUserRecord) + " { Username = " + User.Username + " }";
}

/// <summary>The outcome of a change to an account.</summary>
public enum UserChangeResult
{
    /// <summary>The change was made.</summary>
    Done,

    /// <summary>There is no such account.</summary>
    NotFound,

    /// <summary>The change would leave the hub without an enabled administrator.</summary>
    LastAdministrator,
}

/// <summary>The table <c>users</c> of <c>hub.db</c>. Every method is one atomic database operation.</summary>
public sealed class UserStore
{
    private const int SqliteConstraint = 19;

    private const string Columns = "id, username, password_hash, role, disabled, created_at, updated_at, password_changed_at, last_login_at";

    private readonly HubDatabase _database;

    /// <summary>Creates the store.</summary>
    /// <param name="database">The migrated database.</param>
    public UserStore(HubDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
    }

    /// <summary>Counts the accounts.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The number of accounts.</returns>
    public Task<int> CountAsync(CancellationToken cancellationToken)
    {
        return _database.QueryAsync(
            connection => Convert.ToInt32(HubSql.Scalar(connection, null, "SELECT COUNT(*) FROM users;"), CultureInfo.InvariantCulture),
            cancellationToken);
    }

    /// <summary>Lists the accounts ordered by name.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The accounts.</returns>
    public Task<IReadOnlyList<HubUserRecord>> ListAsync(CancellationToken cancellationToken)
    {
        return _database.QueryAsync<IReadOnlyList<HubUserRecord>>(
            connection =>
            {
                var users = new List<HubUserRecord>();
                using SqliteCommand command = HubSql.Command(connection, null, "SELECT " + Columns + " FROM users ORDER BY username COLLATE NOCASE;");
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    users.Add(Read(reader));
                }

                return users;
            },
            cancellationToken);
    }

    /// <summary>Finds an account by id.</summary>
    /// <param name="id">The id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The account, or null.</returns>
    public Task<HubUserRecord?> FindByIdAsync(string id, CancellationToken cancellationToken)
    {
        return _database.QueryAsync(
            connection => FindOne(connection, null, "SELECT " + Columns + " FROM users WHERE id = @value;", id),
            cancellationToken);
    }

    /// <summary>Finds an account by its sign-in name, without regard to case.</summary>
    /// <param name="username">The name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The account, or null.</returns>
    public Task<HubUserRecord?> FindByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        return _database.QueryAsync(
            connection => FindOne(connection, null, "SELECT " + Columns + " FROM users WHERE username = @value COLLATE NOCASE;", username),
            cancellationToken);
    }

    /// <summary>Adds an account.</summary>
    /// <param name="record">The account.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when the account was added; false when the name is taken.</returns>
    public Task<bool> TryCreateAsync(HubUserRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        return _database.QueryAsync(connection => Insert(connection, record, onlyWhenEmpty: false), cancellationToken);
    }

    /// <summary>Adds the first account, but only while the hub has no account at all (first-run setup). Two setups at once cannot both succeed.</summary>
    /// <param name="record">The account.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when the account was added; false when an account already exists.</returns>
    public Task<bool> TryCreateFirstAsync(HubUserRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        return _database.QueryAsync(connection => Insert(connection, record, onlyWhenEmpty: true), cancellationToken);
    }

    /// <summary>Changes the role and/or the disabled flag. The hub never ends up without an enabled administrator.</summary>
    /// <param name="id">The account.</param>
    /// <param name="role">The new role, or null to keep it.</param>
    /// <param name="disabled">The new flag, or null to keep it.</param>
    /// <param name="now">The time of the change.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The outcome.</returns>
    public Task<UserChangeResult> UpdateAsync(string id, string? role, bool? disabled, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return _database.InTransactionAsync(
            (connection, transaction) =>
            {
                HubUserRecord? current = FindOne(connection, transaction, "SELECT " + Columns + " FROM users WHERE id = @value;", id);
                if (current is null)
                {
                    return UserChangeResult.NotFound;
                }

                string newRole = role ?? current.User.Role;
                bool newDisabled = disabled ?? current.User.Disabled;
                bool wasActiveAdministrator = current.User.Role == HubRoles.Admin && !current.User.Disabled;
                bool staysActiveAdministrator = newRole == HubRoles.Admin && !newDisabled;
                if (wasActiveAdministrator && !staysActiveAdministrator && CountOtherActiveAdministrators(connection, transaction, id) == 0)
                {
                    return UserChangeResult.LastAdministrator;
                }

                HubSql.Execute(
                    connection,
                    transaction,
                    "UPDATE users SET role = @role, disabled = @disabled, updated_at = @now WHERE id = @id;",
                    ("@role", newRole),
                    ("@disabled", newDisabled ? 1 : 0),
                    ("@now", HubSql.ToStored(now)),
                    ("@id", id));
                return UserChangeResult.Done;
            },
            cancellationToken);
    }

    /// <summary>Replaces the password hash.</summary>
    /// <param name="id">The account.</param>
    /// <param name="passwordHash">The new hash.</param>
    /// <param name="now">The time of the change.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when the account exists.</returns>
    public Task<bool> SetPasswordHashAsync(string id, string passwordHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return _database.QueryAsync(
            connection => HubSql.Execute(
                connection,
                null,
                "UPDATE users SET password_hash = @hash, password_changed_at = @now WHERE id = @id;",
                ("@hash", passwordHash),
                ("@now", HubSql.ToStored(now)),
                ("@id", id)) > 0,
            cancellationToken);
    }

    /// <summary>Deletes an account. The hub never ends up without an enabled administrator.</summary>
    /// <param name="id">The account.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The outcome.</returns>
    public Task<UserChangeResult> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        return _database.InTransactionAsync(
            (connection, transaction) =>
            {
                HubUserRecord? current = FindOne(connection, transaction, "SELECT " + Columns + " FROM users WHERE id = @value;", id);
                if (current is null)
                {
                    return UserChangeResult.NotFound;
                }

                bool activeAdministrator = current.User.Role == HubRoles.Admin && !current.User.Disabled;
                if (activeAdministrator && CountOtherActiveAdministrators(connection, transaction, id) == 0)
                {
                    return UserChangeResult.LastAdministrator;
                }

                HubSql.Execute(connection, transaction, "DELETE FROM users WHERE id = @id;", ("@id", id));
                return UserChangeResult.Done;
            },
            cancellationToken);
    }

    /// <summary>Records a successful sign-in.</summary>
    /// <param name="id">The account.</param>
    /// <param name="now">The time of the sign-in.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the time is stored.</returns>
    public Task TouchLoginAsync(string id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return _database.ExecuteAsync(
            connection => HubSql.Execute(connection, null, "UPDATE users SET last_login_at = @now WHERE id = @id;", ("@now", HubSql.ToStored(now)), ("@id", id)),
            cancellationToken);
    }

    private static bool Insert(SqliteConnection connection, HubUserRecord record, bool onlyWhenEmpty)
    {
        HubUser user = record.User;
        (string, object?)[] values =
        [
            ("@id", user.Id),
            ("@username", user.Username),
            ("@hash", record.PasswordHash),
            ("@role", user.Role),
            ("@disabled", user.Disabled ? 1 : 0),
            ("@created", HubSql.ToStored(user.CreatedAt)),
            ("@updated", HubSql.ToStored(user.UpdatedAt)),
            ("@changed", HubSql.ToStored(user.PasswordChangedAt)),
        ];

        try
        {
            int changed = onlyWhenEmpty
                ? HubSql.Execute(
                    connection,
                    null,
                    "INSERT INTO users (id, username, password_hash, role, disabled, created_at, updated_at, password_changed_at) "
                    + "SELECT @id, @username, @hash, @role, @disabled, @created, @updated, @changed WHERE NOT EXISTS (SELECT 1 FROM users);",
                    values)
                : HubSql.Execute(
                    connection,
                    null,
                    "INSERT INTO users (id, username, password_hash, role, disabled, created_at, updated_at, password_changed_at) "
                    + "VALUES (@id, @username, @hash, @role, @disabled, @created, @updated, @changed);",
                    values);
            return changed > 0;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == SqliteConstraint)
        {
            return false;
        }
    }

    private static int CountOtherActiveAdministrators(SqliteConnection connection, SqliteTransaction transaction, string excludedId)
    {
        object? count = HubSql.Scalar(
            connection,
            transaction,
            "SELECT COUNT(*) FROM users WHERE role = 'admin' AND disabled = 0 AND id <> @id;",
            ("@id", excludedId));
        return Convert.ToInt32(count, CultureInfo.InvariantCulture);
    }

    private static HubUserRecord? FindOne(SqliteConnection connection, SqliteTransaction? transaction, string sql, string value)
    {
        using SqliteCommand command = HubSql.Command(connection, transaction, sql, ("@value", value));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static HubUserRecord Read(SqliteDataReader reader)
    {
        var user = new HubUser(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(3),
            reader.GetInt64(4) != 0,
            HubSql.FromStored(reader.GetInt64(5)),
            HubSql.FromStored(reader.GetInt64(6)),
            HubSql.FromStored(reader.GetInt64(7)),
            reader.IsDBNull(8) ? null : HubSql.FromStored(reader.GetInt64(8)));
        return new HubUserRecord(user, reader.GetString(2));
    }
}
