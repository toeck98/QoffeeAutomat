using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Security;

/// <summary>The values of the <c>result</c> of an audit entry.</summary>
public static class AuditResults
{
    /// <summary>The request did what it was meant to do.</summary>
    public const string Success = "success";

    /// <summary>The request was allowed to try but failed: bad input, a missing object, a conflict, a wrong password, an error.</summary>
    public const string Failure = "failure";

    /// <summary>The request was refused: not signed in, role too low, missing anti-forgery header, too many attempts.</summary>
    public const string Denied = "denied";
}

/// <summary>An entry of the audit log as it is stored.</summary>
/// <param name="Id">The sequence number; entries are ordered by it.</param>
/// <param name="At">When the request finished.</param>
/// <param name="User">The user name, <c>token:&lt;name&gt;</c> for an API token, the name that was tried for a failed sign-in, or <c>anonymous</c>.</param>
/// <param name="Role">The role of the caller, or null when nobody was authenticated.</param>
/// <param name="AuthMethod">One of <see cref="AuthMethods"/>.</param>
/// <param name="Action">What was done: the name of the operation, for example <c>createUser</c>.</param>
/// <param name="Method">The HTTP method.</param>
/// <param name="Target">What the action was applied to, for example <c>user:3f2a...</c>, or null.</param>
/// <param name="Agents">The ids of the agents that the action affected; empty when it affected none.</param>
/// <param name="Result">One of <see cref="AuditResults"/>.</param>
/// <param name="Status">The HTTP status of the answer.</param>
/// <param name="RemoteAddress">The client address as the hub saw it, or null.</param>
public sealed record AuditEntry(
    long Id,
    DateTimeOffset At,
    string User,
    string? Role,
    string AuthMethod,
    string Action,
    string Method,
    string? Target,
    IReadOnlyList<string> Agents,
    string Result,
    int Status,
    string? RemoteAddress);

/// <summary>An entry that is about to be written; the log adds the time and the sequence number.</summary>
/// <param name="User">The user name.</param>
/// <param name="Role">The role, or null.</param>
/// <param name="AuthMethod">One of <see cref="AuthMethods"/>.</param>
/// <param name="Action">The name of the operation.</param>
/// <param name="Method">The HTTP method.</param>
/// <param name="Target">What the action was applied to, or null.</param>
/// <param name="Agents">The affected agents.</param>
/// <param name="Result">One of <see cref="AuditResults"/>.</param>
/// <param name="Status">The HTTP status.</param>
/// <param name="RemoteAddress">The client address, or null.</param>
public sealed record NewAuditEntry(
    string User,
    string? Role,
    string AuthMethod,
    string Action,
    string Method,
    string? Target,
    IReadOnlyList<string> Agents,
    string Result,
    int Status,
    string? RemoteAddress);

/// <summary>A query of the audit log. All filters are optional and combine with AND.</summary>
public sealed record AuditQuery
{
    /// <summary>The default number of entries of a page.</summary>
    public const int DefaultLimit = 100;

    /// <summary>The largest page.</summary>
    public const int MaxLimit = 500;

    /// <summary>Only entries of this user (without regard to case).</summary>
    public string? User { get; init; }

    /// <summary>Only entries of this action.</summary>
    public string? Action { get; init; }

    /// <summary>Only entries with this result.</summary>
    public string? Result { get; init; }

    /// <summary>Only entries that affected this agent.</summary>
    public string? Agent { get; init; }

    /// <summary>Only entries at or after this time.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Only entries before this time.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Only entries with a smaller sequence number (the cursor of the next page).</summary>
    public long? Before { get; init; }

    /// <summary>The page size, <see cref="DefaultLimit"/> when 0 or negative, at most <see cref="MaxLimit"/>.</summary>
    public int Limit { get; init; } = DefaultLimit;

    /// <summary>The page size that is used: <see cref="Limit"/> within 1 and <see cref="MaxLimit"/>.</summary>
    public int EffectiveLimit => Limit <= 0 ? DefaultLimit : Math.Min(Limit, MaxLimit);
}

/// <summary>The JSON of the list of affected agents in the column <c>agents</c>.</summary>
[JsonSerializable(typeof(string[]))]
internal sealed partial class AuditJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The audit log of the hub (PLAN.md S20 "Hub security" item 2, T903): who (user or token), when, which action, on what, which
/// agents were affected and with what result. It is append only for the API; the hub removes entries older than
/// <see cref="HubAuthOptions.AuditRetention"/> and the oldest ones beyond <see cref="HubAuthOptions.AuditMaxEntries"/>. Values never contain
/// passwords, tokens or request bodies.
/// </summary>
public sealed class AuditLog
{
    private const string Columns = "id, at, user_name, role, auth_method, action, method, target, agents, result, status, remote_address";

    private readonly HubDatabase _database;
    private readonly TimeProvider _time;
    private readonly HubAuthOptions _options;
    private int _sincePrune;

    /// <summary>Creates the log.</summary>
    /// <param name="database">The migrated database.</param>
    /// <param name="time">The clock.</param>
    /// <param name="options">The retention limits.</param>
    public AuditLog(HubDatabase database, TimeProvider time, HubAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);

        _database = database;
        _time = time;
        _options = options;
    }

    /// <summary>Appends an entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the entry is stored.</returns>
    public async Task AppendAsync(NewAuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        long at = HubSql.ToStored(_time.GetUtcNow());
        string agents = JsonSerializer.Serialize(entry.Agents.ToArray(), AuditJsonContext.Default.StringArray);
        await _database.ExecuteAsync(
            connection => HubSql.Execute(
                connection,
                null,
                "INSERT INTO audit_log (at, user_name, role, auth_method, action, method, target, agents, result, status, remote_address) "
                + "VALUES (@at, @user, @role, @auth, @action, @method, @target, @agents, @result, @status, @remote);",
                ("@at", at),
                ("@user", entry.User),
                ("@role", entry.Role),
                ("@auth", entry.AuthMethod),
                ("@action", entry.Action),
                ("@method", entry.Method),
                ("@target", entry.Target),
                ("@agents", agents),
                ("@result", entry.Result),
                ("@status", entry.Status),
                ("@remote", entry.RemoteAddress)),
            cancellationToken).ConfigureAwait(false);

        if (Interlocked.Increment(ref _sincePrune) >= Math.Max(1, _options.AuditPruneEvery))
        {
            Interlocked.Exchange(ref _sincePrune, 0);
            await PruneAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads a page of the log, the newest entry first.</summary>
    /// <param name="query">The filters and the page.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The entries.</returns>
    public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var conditions = new List<string>();
        var parameters = new List<(string Name, object? Value)>();
        if (query.User is not null)
        {
            conditions.Add("user_name = @user COLLATE NOCASE");
            parameters.Add(("@user", query.User));
        }

        if (query.Action is not null)
        {
            conditions.Add("action = @action");
            parameters.Add(("@action", query.Action));
        }

        if (query.Result is not null)
        {
            conditions.Add("result = @result");
            parameters.Add(("@result", query.Result));
        }

        if (query.Agent is not null)
        {
            // The agents are stored as a JSON array of strings: search for the JSON form of the id, which is exact (no wildcards).
            string json = JsonSerializer.Serialize(new[] { query.Agent }, AuditJsonContext.Default.StringArray);
            conditions.Add("instr(agents, @agent) > 0");
            parameters.Add(("@agent", json[1..^1]));
        }

        if (query.From is { } from)
        {
            conditions.Add("at >= @from");
            parameters.Add(("@from", HubSql.ToStored(from)));
        }

        if (query.To is { } to)
        {
            conditions.Add("at < @to");
            parameters.Add(("@to", HubSql.ToStored(to)));
        }

        if (query.Before is { } before)
        {
            conditions.Add("id < @before");
            parameters.Add(("@before", before));
        }

        parameters.Add(("@limit", query.EffectiveLimit));

        // The conditions are constant fragments; every value travels as a parameter.
        string sql = "SELECT " + Columns + " FROM audit_log"
            + (conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty)
            + " ORDER BY id DESC LIMIT @limit;";
        (string Name, object? Value)[] values = [.. parameters];

        return _database.QueryAsync<IReadOnlyList<AuditEntry>>(
            connection =>
            {
                var entries = new List<AuditEntry>();
                using SqliteCommand command = HubSql.Command(connection, null, sql, values);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    entries.Add(Read(reader));
                }

                return entries;
            },
            cancellationToken);
    }

    /// <summary>Counts the entries.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The number of entries.</returns>
    public Task<long> CountAsync(CancellationToken cancellationToken)
    {
        return _database.QueryAsync(
            connection => Convert.ToInt64(HubSql.Scalar(connection, null, "SELECT COUNT(*) FROM audit_log;"), CultureInfo.InvariantCulture),
            cancellationToken);
    }

    /// <summary>Removes entries beyond the retention limits.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the entries are removed.</returns>
    public Task PruneAsync(CancellationToken cancellationToken)
    {
        long cutoff = HubSql.ToStored(_time.GetUtcNow() - _options.AuditRetention);
        int max = Math.Max(1, _options.AuditMaxEntries);
        return _database.InTransactionAsync(
            (connection, transaction) =>
            {
                HubSql.Execute(connection, transaction, "DELETE FROM audit_log WHERE at < @cutoff;", ("@cutoff", cutoff));
                HubSql.Execute(
                    connection,
                    transaction,
                    "DELETE FROM audit_log WHERE id <= (SELECT id FROM audit_log ORDER BY id DESC LIMIT 1 OFFSET @max);",
                    ("@max", max));
                return 0;
            },
            cancellationToken);
    }

    private static AuditEntry Read(SqliteDataReader reader)
    {
        string[] agents = JsonSerializer.Deserialize(reader.GetString(8), AuditJsonContext.Default.StringArray) ?? [];
        return new AuditEntry(
            reader.GetInt64(0),
            HubSql.FromStored(reader.GetInt64(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            agents,
            reader.GetString(9),
            reader.GetInt32(10),
            reader.IsDBNull(11) ? null : reader.GetString(11));
    }
}
