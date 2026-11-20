using Microsoft.Data.Sqlite;

namespace QoffeeAutomat.Hub.Storage;

/// <summary>
/// Small helpers for statements on <c>hub.db</c>. Statements are constants (or migration scripts that are part of the product);
/// values always travel as parameters, never as text in a statement.
/// </summary>
public static class HubSql
{
    /// <summary>Creates a command with a statement and parameters.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">The transaction the command belongs to, or null.</param>
    /// <param name="sql">The statement; a constant, values travel as parameters.</param>
    /// <param name="parameters">Pairs of parameter name (with <c>@</c>) and value; null becomes SQL NULL.</param>
    /// <returns>The command; the caller disposes it.</returns>
    public static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);

        SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100 // Every caller passes a constant statement or a migration script of the product; values travel as parameters.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.Transaction = transaction;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    /// <summary>Runs a statement that returns no rows.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">The transaction, or null.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="parameters">The parameters.</param>
    /// <returns>The number of rows that changed.</returns>
    public static int Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    /// <summary>Runs a query that returns one value.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">The transaction, or null.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="parameters">The parameters.</param>
    /// <returns>The first column of the first row, or null when there is no row or the value is NULL.</returns>
    public static object? Scalar(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = Command(connection, transaction, sql, parameters);
        object? value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    /// <summary>Converts a time to the unix milliseconds that the database stores.</summary>
    /// <param name="time">The time.</param>
    /// <returns>The milliseconds since 1970-01-01T00:00:00Z.</returns>
    public static long ToStored(DateTimeOffset time) => time.ToUnixTimeMilliseconds();

    /// <summary>Converts stored unix milliseconds back to a time.</summary>
    /// <param name="stored">The stored value.</param>
    /// <returns>The time in UTC.</returns>
    public static DateTimeOffset FromStored(long stored) => DateTimeOffset.FromUnixTimeMilliseconds(stored);
}
