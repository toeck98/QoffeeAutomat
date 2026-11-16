using Microsoft.Data.Sqlite;

namespace QoffeeAutomat.Core.History;

/// <summary>
/// One SQLite database file with a single connection. Every operation runs under a lock, so the
/// stores that use it need no further synchronisation. The connection is not pooled, so disposing the
/// database releases the file at once (a temporary directory can be deleted afterwards, also on
/// Windows). The write-ahead log keeps readers of the file (a backup, a diagnostics bundle) from
/// blocking the agent.
/// </summary>
internal sealed class SqliteDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private SqliteDatabase(SqliteConnection connection)
    {
        _connection = connection;
    }

    /// <summary>Opens the database, creating the file, its directory and the schema when needed.</summary>
    /// <param name="path">The database file.</param>
    /// <param name="schema">SQL that creates the tables and indexes; it must be idempotent (<c>IF NOT EXISTS</c>).</param>
    /// <returns>The open database.</returns>
    public static SqliteDatabase Open(string path, string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrEmpty(schema);

        string full = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

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
            Execute(connection, "PRAGMA journal_mode = WAL;");
            Execute(connection, "PRAGMA synchronous = NORMAL;");
            Execute(connection, schema);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return new SqliteDatabase(connection);
    }

    /// <summary>Runs work with the connection while holding the lock.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="work">The work; it must not keep the connection.</param>
    /// <param name="cancellationToken">Cancels waiting for the lock.</param>
    /// <returns>What the work returned.</returns>
    public async Task<T> QueryAsync<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        await _gate.WaitAsync(cancellationToken);
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

    /// <summary>Creates a command with a constant statement and the given parameters.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="sql">The statement; always a constant, values travel as parameters.</param>
    /// <param name="parameters">Pairs of parameter name (with <c>@</c>) and value; null becomes SQL NULL.</param>
    /// <returns>The command; the caller disposes it.</returns>
    public static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100 // Every caller passes a constant statement; values travel as parameters.
        command.CommandText = sql;
#pragma warning restore CA2100
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100 // Only constant statements are passed (pragmas and the schema of the stores).
        command.CommandText = sql;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }
}
