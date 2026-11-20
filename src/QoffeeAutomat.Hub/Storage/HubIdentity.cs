using System.Globalization;
using Microsoft.Data.Sqlite;

namespace QoffeeAutomat.Hub.Storage;

/// <summary>
/// Who this hub is (PLAN.md S20): the persistent id, which is the unique id of the Home Assistant config entry and part of the
/// enrollment, and the name that people see.
/// </summary>
/// <param name="Id">The hub id: 32 lower-case hex digits, created once and never changed.</param>
/// <param name="Name">The display name.</param>
/// <param name="CreatedAt">When the hub was created.</param>
public sealed record HubIdentity(string Id, string Name, DateTimeOffset CreatedAt);

/// <summary>Reads and creates the identity of the hub in the table <c>hub_meta</c>.</summary>
public static class HubIdentityStore
{
    /// <summary>The name of a hub that was started without a name.</summary>
    public const string DefaultName = "QoffeeAutomat Hub";

    /// <summary>The longest name.</summary>
    public const int MaxNameLength = 64;

    private const string IdKey = "hub_id";
    private const string NameKey = "hub_name";
    private const string CreatedKey = "created_at";

    /// <summary>Loads the identity, or creates it on the first start. The id never changes; the name is only used for a new hub.</summary>
    /// <param name="database">The migrated database.</param>
    /// <param name="initialName">The name for a new hub; null or white space picks <see cref="DefaultName"/>.</param>
    /// <param name="time">The clock.</param>
    /// <returns>The identity.</returns>
    public static HubIdentity LoadOrCreate(HubDatabase database, string? initialName, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(time);

        DateTimeOffset now = time.GetUtcNow();
        string id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        string name = NormalizeName(initialName);

        return database.Query(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();

            // INSERT OR IGNORE: a value that exists is never replaced, so the id survives every restart.
            const string insert = "INSERT OR IGNORE INTO hub_meta (key, value) VALUES (@key, @value);";
            HubSql.Execute(connection, transaction, insert, ("@key", IdKey), ("@value", id));
            HubSql.Execute(connection, transaction, insert, ("@key", NameKey), ("@value", name));
            HubSql.Execute(connection, transaction, insert, ("@key", CreatedKey), ("@value", HubSql.ToStored(now).ToString(CultureInfo.InvariantCulture)));

            HubIdentity identity = new(
                Read(connection, transaction, IdKey),
                Read(connection, transaction, NameKey),
                HubSql.FromStored(long.Parse(Read(connection, transaction, CreatedKey), NumberStyles.None, CultureInfo.InvariantCulture)));
            transaction.Commit();
            return identity;
        });
    }

    /// <summary>Trims a name and limits its length; an empty name becomes <see cref="DefaultName"/>.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The name to store.</returns>
    public static string NormalizeName(string? name)
    {
        string trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return DefaultName;
        }

        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength].TrimEnd() : trimmed;
    }

    private static string Read(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        object? value = HubSql.Scalar(connection, transaction, "SELECT value FROM hub_meta WHERE key = @key;", ("@key", key));
        return value as string ?? throw new InvalidOperationException("The hub database has no value for " + key + ".");
    }
}
