using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QoffeeAutomat.Core.History;

namespace QoffeeAutomat.Core.Events;

/// <summary>Persistence of the recent events (PLAN.md D-013, <c>GET /events</c>).</summary>
public interface IEventStore
{
    /// <summary>Stores an event.</summary>
    /// <param name="type">The event type.</param>
    /// <param name="timestamp">When it happened.</param>
    /// <param name="data">Optional data.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id of the new row.</returns>
    Task<long> AddAsync(string type, DateTimeOffset timestamp, JsonElement? data, CancellationToken cancellationToken);

    /// <summary>Reads recent events.</summary>
    /// <param name="limit">The most events to return.</param>
    /// <param name="type">Only events of this type, or null for all.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The events, newest first.</returns>
    Task<IReadOnlyList<AgentEvent>> GetRecentAsync(int limit, string? type, CancellationToken cancellationToken);
}

/// <summary>Settings of <see cref="SqliteEventStore"/>.</summary>
public sealed class EventStoreOptions
{
    /// <summary>The default for <see cref="MaxEvents"/>.</summary>
    public const int DefaultMaxEvents = 1000;

    /// <summary>The default for <see cref="MaxAge"/>.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(30);

    /// <summary>The most events that are kept; the oldest are removed first.</summary>
    public int MaxEvents { get; init; } = DefaultMaxEvents;

    /// <summary>Events older than this are removed.</summary>
    public TimeSpan MaxAge { get; init; } = DefaultMaxAge;

    /// <summary>The table is pruned after this many inserts (and when it is opened).</summary>
    public int PruneEvery { get; init; } = 50;
}

/// <summary>The recent events in a SQLite table, pruned by age and count.</summary>
public sealed class SqliteEventStore : IEventStore, IDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS events (
            id   INTEGER PRIMARY KEY AUTOINCREMENT,
            type TEXT    NOT NULL,
            ts   INTEGER NOT NULL,
            data TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_events_type ON events (type, id);
        CREATE INDEX IF NOT EXISTS ix_events_ts ON events (ts);
        """;

    private readonly SqliteDatabase _database;
    private readonly TimeProvider _time;
    private readonly EventStoreOptions _options;
    private int _insertsSincePrune;

    /// <summary>Opens the store, creating the database file when it does not exist.</summary>
    /// <param name="databasePath">The SQLite file.</param>
    /// <param name="time">The clock for the age limit.</param>
    /// <param name="options">Retention; null uses the defaults.</param>
    public SqliteEventStore(string databasePath, TimeProvider time, EventStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(time);

        _time = time;
        _options = options ?? new EventStoreOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxEvents, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.PruneEvery, 1, nameof(options));
        if (_options.MaxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxAge must be positive.");
        }

        _database = SqliteDatabase.Open(databasePath, Schema);
        _database.ExecuteAsync(Prune, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public Task<long> AddAsync(string type, DateTimeOffset timestamp, JsonElement? data, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);

        string? json = data is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } element
            ? element.GetRawText()
            : null;
        long unixMilliseconds = timestamp.ToUnixTimeMilliseconds();

        return _database.QueryAsync(
            connection =>
            {
                using var insert = SqliteDatabase.Command(
                    connection,
                    "INSERT INTO events (type, ts, data) VALUES (@type, @ts, @data); SELECT last_insert_rowid();",
                    ("@type", type),
                    ("@ts", unixMilliseconds),
                    ("@data", json));
                long id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);

                if (++_insertsSincePrune >= _options.PruneEvery)
                {
                    Prune(connection);
                }

                return id;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentEvent>> GetRecentAsync(int limit, string? type, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return _database.QueryAsync<IReadOnlyList<AgentEvent>>(
            connection =>
            {
                using var select = SqliteDatabase.Command(
                    connection,
                    "SELECT id, type, ts, data FROM events WHERE (@type IS NULL OR type = @type) ORDER BY id DESC LIMIT @limit;",
                    ("@type", type),
                    ("@limit", (long)limit));
                using var reader = select.ExecuteReader();

                var events = new List<AgentEvent>();
                while (reader.Read())
                {
                    JsonElement? data = null;
                    if (!reader.IsDBNull(3))
                    {
                        using JsonDocument document = JsonDocument.Parse(reader.GetString(3));
                        data = document.RootElement.Clone();
                    }

                    events.Add(new AgentEvent(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                        data));
                }

                return events;
            },
            cancellationToken);
    }

    /// <summary>Closes the database.</summary>
    public void Dispose()
    {
        _database.Dispose();
    }

    private void Prune(SqliteConnection connection)
    {
        _insertsSincePrune = 0;
        long cutoff = (_time.GetUtcNow() - _options.MaxAge).ToUnixTimeMilliseconds();

        using var byAge = SqliteDatabase.Command(connection, "DELETE FROM events WHERE ts < @cutoff;", ("@cutoff", cutoff));
        byAge.ExecuteNonQuery();

        using var byCount = SqliteDatabase.Command(
            connection,
            "DELETE FROM events WHERE id NOT IN (SELECT id FROM events ORDER BY id DESC LIMIT @max);",
            ("@max", (long)_options.MaxEvents));
        byCount.ExecuteNonQuery();
    }
}
