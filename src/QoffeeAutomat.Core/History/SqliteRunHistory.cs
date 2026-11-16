using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace QoffeeAutomat.Core.History;

/// <summary>Settings of <see cref="SqliteRunHistory"/>.</summary>
public sealed class RunHistoryOptions
{
    /// <summary>The default for <see cref="MaxAge"/>, 30 days (PLAN.md S06 settings <c>retention.runHistoryDays</c>).</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(30);

    /// <summary>The default for <see cref="MaxRuns"/>.</summary>
    public const int DefaultMaxRuns = 10_000;

    /// <summary>The default for <see cref="MaxOutputChars"/>.</summary>
    public const int DefaultMaxOutputChars = 8 * 1024;

    /// <summary>Runs that started longer ago than this are removed.</summary>
    public TimeSpan MaxAge { get; init; } = DefaultMaxAge;

    /// <summary>The most runs that are kept; the oldest are removed first.</summary>
    public int MaxRuns { get; init; } = DefaultMaxRuns;

    /// <summary>The most characters of standard output, and separately of standard error, that are stored per run.</summary>
    public int MaxOutputChars { get; init; } = DefaultMaxOutputChars;

    /// <summary>The retention rules are applied after this many inserts (and when the history is opened).</summary>
    public int PruneEvery { get; init; } = 100;
}

/// <summary>The run history in a SQLite table <c>runs</c> with retention by age and by count.</summary>
public sealed class SqliteRunHistory : IRunHistory, IDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS runs (
            seq              INTEGER PRIMARY KEY AUTOINCREMENT,
            id               TEXT    NOT NULL UNIQUE,
            command          TEXT    NOT NULL,
            source           TEXT    NOT NULL,
            client_id        TEXT,
            user_id          TEXT,
            variables        TEXT    NOT NULL,
            started_at       INTEGER NOT NULL,
            duration_ms      INTEGER NOT NULL,
            exit_code        INTEGER,
            error_code       TEXT,
            stdout           TEXT    NOT NULL,
            stderr           TEXT    NOT NULL,
            output_truncated INTEGER NOT NULL,
            parent_run_id    TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_runs_command ON runs (command, seq);
        CREATE INDEX IF NOT EXISTS ix_runs_started ON runs (started_at);
        """;

    private const string Columns =
        "id, command, source, client_id, user_id, variables, started_at, duration_ms, exit_code, error_code, stdout, stderr, output_truncated, parent_run_id";

    private readonly SqliteDatabase _database;
    private readonly TimeProvider _time;
    private readonly RunHistoryOptions _options;
    private int _insertsSincePrune;

    /// <summary>Opens the history, creating the database file when it does not exist.</summary>
    /// <param name="databasePath">The SQLite file.</param>
    /// <param name="time">The clock for the age limit.</param>
    /// <param name="options">Retention and output limit; null uses the defaults.</param>
    public SqliteRunHistory(string databasePath, TimeProvider time, RunHistoryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(time);

        _time = time;
        _options = options ?? new RunHistoryOptions();
        if (_options.MaxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxAge must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxRuns, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxOutputChars, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.PruneEvery, 1, nameof(options));

        _database = SqliteDatabase.Open(databasePath, Schema);
        _database.ExecuteAsync(connection => Prune(connection), CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public Task AddAsync(RunRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        (string stdout, bool stdoutCut) = Truncate(record.StandardOutput, _options.MaxOutputChars);
        (string stderr, bool stderrCut) = Truncate(record.StandardError, _options.MaxOutputChars);
        bool truncated = record.OutputTruncated || stdoutCut || stderrCut;
        string variables = record.Variables.ValueKind == JsonValueKind.Object ? record.Variables.GetRawText() : "{}";

        return _database.ExecuteAsync(
            connection =>
            {
                using SqliteCommand insert = SqliteDatabase.Command(
                    connection,
                    "INSERT INTO runs (" + Columns + ") VALUES (@id, @command, @source, @client, @user, @variables, @started, @duration, @exit, @error, @stdout, @stderr, @truncated, @parent);",
                    ("@id", record.Id),
                    ("@command", record.Command),
                    ("@source", record.Source),
                    ("@client", record.ClientId),
                    ("@user", record.UserId),
                    ("@variables", variables),
                    ("@started", record.StartedAt.ToUnixTimeMilliseconds()),
                    ("@duration", (long)record.Duration.TotalMilliseconds),
                    ("@exit", record.ExitCode),
                    ("@error", record.ErrorCode),
                    ("@stdout", stdout),
                    ("@stderr", stderr),
                    ("@truncated", truncated ? 1L : 0L),
                    ("@parent", record.ParentRunId));
                insert.ExecuteNonQuery();

                if (++_insertsSincePrune >= _options.PruneEvery)
                {
                    Prune(connection);
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<RunRecord?> GetAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return _database.QueryAsync<RunRecord?>(
            connection =>
            {
                using SqliteCommand select = SqliteDatabase.Command(
                    connection,
                    "SELECT " + Columns + " FROM runs WHERE id = @id;",
                    ("@id", id));
                using SqliteDataReader reader = select.ExecuteReader();
                return reader.Read() ? ReadRecord(reader) : null;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RunRecord>> QueryAsync(RunQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1, nameof(query));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, 1000, nameof(query));
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset, nameof(query));

        return _database.QueryAsync<IReadOnlyList<RunRecord>>(
            connection =>
            {
                using SqliteCommand select = SqliteDatabase.Command(
                    connection,
                    "SELECT " + Columns + " FROM runs"
                    + " WHERE (@command IS NULL OR command = @command)"
                    + " AND (@source IS NULL OR source = @source)"
                    + " AND (@failed = 0 OR error_code IS NOT NULL OR exit_code IS NULL OR exit_code <> 0)"
                    + " ORDER BY seq DESC LIMIT @limit OFFSET @offset;",
                    ("@command", query.Command),
                    ("@source", query.Source),
                    ("@failed", query.FailedOnly ? 1L : 0L),
                    ("@limit", (long)query.Limit),
                    ("@offset", (long)query.Offset));
                using SqliteDataReader reader = select.ExecuteReader();

                var runs = new List<RunRecord>();
                while (reader.Read())
                {
                    runs.Add(ReadRecord(reader));
                }

                return runs;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        return _database.QueryAsync(Prune, cancellationToken);
    }

    /// <summary>Closes the database.</summary>
    public void Dispose()
    {
        _database.Dispose();
    }

    private static (string Text, bool Cut) Truncate(string text, int limit)
    {
        if (text.Length <= limit)
        {
            return (text, false);
        }

        // Do not end in the middle of a surrogate pair.
        int length = char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
        return (text[..length], true);
    }

    private static RunRecord ReadRecord(SqliteDataReader reader)
    {
        using JsonDocument variables = JsonDocument.Parse(reader.GetString(5));
        return new RunRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            variables.RootElement.Clone(),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
            TimeSpan.FromMilliseconds(reader.GetInt64(7)),
            reader.IsDBNull(8) ? null : reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetInt64(12) != 0,
            reader.IsDBNull(13) ? null : reader.GetString(13));
    }

    private int Prune(SqliteConnection connection)
    {
        _insertsSincePrune = 0;
        long cutoff = (_time.GetUtcNow() - _options.MaxAge).ToUnixTimeMilliseconds();

        using SqliteCommand byAge = SqliteDatabase.Command(connection, "DELETE FROM runs WHERE started_at < @cutoff;", ("@cutoff", cutoff));
        int removed = byAge.ExecuteNonQuery();

        using SqliteCommand byCount = SqliteDatabase.Command(
            connection,
            "DELETE FROM runs WHERE seq NOT IN (SELECT seq FROM runs ORDER BY seq DESC LIMIT @max);",
            ("@max", (long)_options.MaxRuns));
        removed += byCount.ExecuteNonQuery();

        return removed;
    }
}
