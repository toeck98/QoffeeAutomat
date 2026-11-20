using System.Globalization;
using Microsoft.Data.Sqlite;
using QoffeeAutomat.Hub.Hosting;

namespace QoffeeAutomat.Hub.Storage;

/// <summary>Runs the steps of <see cref="HubSchema"/> that a database has not seen yet (T902).</summary>
public static class HubMigrator
{
    /// <summary>Reads the schema version of a database.</summary>
    /// <param name="connection">An open connection.</param>
    /// <returns>The value of <c>PRAGMA user_version</c>; 0 for a new database.</returns>
    public static int ReadVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        object? value = HubSql.Scalar(connection, null, "PRAGMA user_version;");
        return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Migrates a database forward.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="targetVersion">The version to reach; null migrates to <see cref="HubSchema.LatestVersion"/>. A smaller number is for tests of upgrades.</param>
    /// <returns>The version of the database afterwards.</returns>
    /// <exception cref="HubStartupException">The database is newer than this hub, or a step failed (nothing of the failed step remains).</exception>
    public static int Migrate(SqliteConnection connection, int? targetVersion = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        int target = targetVersion ?? HubSchema.LatestVersion;
        if (target < 0 || target > HubSchema.LatestVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(targetVersion), target, "The target is not a version of the schema.");
        }

        int current = ReadVersion(connection);
        if (current > HubSchema.LatestVersion)
        {
            throw new HubStartupException(
                $"The database has schema version {current.ToString(CultureInfo.InvariantCulture)}, but this hub only knows up to "
                + $"{HubSchema.LatestVersion.ToString(CultureInfo.InvariantCulture)}. It was written by a newer hub; update the hub instead of starting an old one.");
        }

        foreach (HubMigration step in HubSchema.Migrations)
        {
            if (step.Version <= current || step.Version > target)
            {
                continue;
            }

            Apply(connection, step);
            current = step.Version;
        }

        return current;
    }

    private static void Apply(SqliteConnection connection, HubMigration step)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        try
        {
            HubSql.Execute(connection, transaction, step.Sql);

            // PRAGMA takes no parameters; the number comes from the product's own list of steps.
            HubSql.Execute(connection, transaction, "PRAGMA user_version = " + step.Version.ToString(CultureInfo.InvariantCulture) + ";");
            transaction.Commit();
        }
        catch (SqliteException exception)
        {
            transaction.Rollback();
            throw new HubStartupException(
                $"The database migration to version {step.Version.ToString(CultureInfo.InvariantCulture)} ({step.Description}) failed: {exception.Message}",
                exception);
        }
    }
}
