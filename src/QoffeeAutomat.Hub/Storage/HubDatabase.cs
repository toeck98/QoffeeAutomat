using Microsoft.Data.Sqlite;
using QoffeeAutomat.Hub.Hosting;

namespace QoffeeAutomat.Hub.Storage;

/// <summary>
/// The hub database <c>hub.db</c> (PLAN.md D-013, D-032, T902): one SQLite file with a single connection, opened and migrated to
/// <see cref="HubSchema.LatestVersion"/> by <see cref="Open"/>. Every operation runs under a lock, so the stores that use it need no
/// further synchronisation. The connection is not pooled, so disposing the database releases the file at once (a temporary directory can
/// be deleted afterwards, also on Windows). The file is created with mode 0600 on Linux; the write-ahead log keeps readers of the
/// file (a backup) from blocking the hub and its <c>-wal</c> and <c>-shm</c> files get the mode of the database file.
/// </summary>
public sealed class HubDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private HubDatabase(SqliteConnection connection, string path, int initialVersion, int version)
    {
        _connection = connection;
        Path = path;
        InitialSchemaVersion = initialVersion;
        SchemaVersion = version;
    }

    /// <summary>The full path of the database file.</summary>
    public string Path { get; }

    /// <summary>The schema version the file had before it was opened; 0 for a new database.</summary>
    public int InitialSchemaVersion { get; }

    /// <summary>The schema version now; the latest one after <see cref="Open"/>.</summary>
    public int SchemaVersion { get; }

    /// <summary>Opens the database, creating the file when needed, and migrates it to the latest schema.</summary>
    /// <param name="path">The database file; its directory must exist.</param>
    /// <returns>The open database.</returns>
    /// <exception cref="HubStartupException">The database is newer than this hub or a migration step failed.</exception>
    public static HubDatabase Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full = System.IO.Path.GetFullPath(path);
        HubDataDirectory.EnsurePrivateFile(full);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };

        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            HubSql.Execute(connection, null, "PRAGMA journal_mode = WAL;");
            HubSql.Execute(connection, null, "PRAGMA synchronous = NORMAL;");
            HubSql.Execute(connection, null, "PRAGMA foreign_keys = ON;");
            HubSql.Execute(connection, null, "PRAGMA busy_timeout = 5000;");

            int initial = HubMigrator.ReadVersion(connection);
            int version = HubMigrator.Migrate(connection);
            return new HubDatabase(connection, full, initial, version);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Runs work with the connection while holding the lock.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="work">The work; it must not keep the connection.</param>
    /// <param name="cancellationToken">Cancels waiting for the lock.</param>
    /// <returns>What the work returned.</returns>
    public async Task<T> QueryAsync<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return work(_connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs work with the connection while holding the lock.</summary>
    /// <param name="work">The work; it must not keep the connection.</param>
    /// <param name="cancellationToken">Cancels waiting for the lock.</param>
    /// <returns>A task that completes when the work is done.</returns>
    public Task ExecuteAsync(Action<SqliteConnection> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        return QueryAsync(
            connection =>
            {
                work(connection);
                return 0;
            },
            cancellationToken);
    }

    /// <summary>Runs work in a transaction, while holding the lock: it commits when the work returns and rolls back when it throws.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="work">The work; it must not keep the connection or the transaction.</param>
    /// <param name="cancellationToken">Cancels waiting for the lock.</param>
    /// <returns>What the work returned.</returns>
    public Task<T> InTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        return QueryAsync(
            connection =>
            {
                using SqliteTransaction transaction = connection.BeginTransaction();
                T result = work(connection, transaction);
                transaction.Commit();
                return result;
            },
            cancellationToken);
    }

    /// <summary>Runs work with the connection while holding the lock, synchronously. Only for start-up code.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="work">The work; it must not keep the connection.</param>
    /// <returns>What the work returned.</returns>
    public T Query<T>(Func<SqliteConnection, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return work(_connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the connection after pending work finished.</summary>
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connection.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }
}
