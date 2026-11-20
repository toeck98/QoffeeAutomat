using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Gateway;

/// <summary>
/// Stores the events that agents report over the agent link (PLAN.md S07 <c>event</c>, S20, T906) in the table <c>agent_events</c>. Every agent keeps at most
/// <see cref="GatewayOptions.MaxEventsPerAgent"/> events; storing a new one removes the oldest beyond that. The table follows the agent: removing the agent from
/// the registry removes its events (a foreign key). Statements are constants and values travel as parameters.
/// </summary>
public sealed class AgentEventStore
{
    /// <summary>The longest event type that is stored; a longer type is cut.</summary>
    public const int MaxTypeLength = 128;

    private readonly HubDatabase _database;
    private readonly GatewayOptions _options;

    /// <summary>Creates the store.</summary>
    /// <param name="database">The hub database.</param>
    /// <param name="options">The limits.</param>
    public AgentEventStore(HubDatabase database, GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);

        _database = database;
        _options = options;
    }

    /// <summary>Stores an event and removes the oldest events of the agent beyond the limit.</summary>
    /// <param name="agentId">The agent that reported it; it must exist in the registry.</param>
    /// <param name="type">The event type.</param>
    /// <param name="reportedAt">The time that the agent reported.</param>
    /// <param name="receivedAt">When the hub received it.</param>
    /// <param name="data">The event data, or null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The stored event.</returns>
    /// <exception cref="SqliteException">The agent does not exist or the database failed.</exception>
    public Task<StoredAgentEvent> AppendAsync(
        string agentId,
        string type,
        DateTimeOffset reportedAt,
        DateTimeOffset receivedAt,
        JsonElement? data,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentNullException.ThrowIfNull(type);

        string storedType = type.Length > MaxTypeLength ? type[..MaxTypeLength] : type;
        JsonElement? storedData = LimitData(data);
        string? text = storedData is { } element ? element.GetRawText() : null;
        return _database.InTransactionAsync(
            (connection, transaction) =>
            {
                HubSql.Execute(
                    connection,
                    transaction,
                    "INSERT INTO agent_events (agent_id, at, received_at, type, data) VALUES (@agent, @at, @received, @type, @data);",
                    ("@agent", agentId),
                    ("@at", HubSql.ToStored(reportedAt)),
                    ("@received", HubSql.ToStored(receivedAt)),
                    ("@type", storedType),
                    ("@data", text));
                long id = Convert.ToInt64(HubSql.Scalar(connection, transaction, "SELECT last_insert_rowid();"), System.Globalization.CultureInfo.InvariantCulture);

                // Keep the newest N: remove everything at or below the id that sits N rows from the top.
                HubSql.Execute(
                    connection,
                    transaction,
                    "DELETE FROM agent_events WHERE agent_id = @agent AND id <= "
                    + "(SELECT id FROM agent_events WHERE agent_id = @agent ORDER BY id DESC LIMIT 1 OFFSET @keep);",
                    ("@agent", agentId),
                    ("@keep", (long)_options.MaxEventsPerAgent));
                return new StoredAgentEvent(id, agentId, reportedAt, receivedAt, storedType, storedData);
            },
            cancellationToken);
    }

    /// <summary>Reads the newest events of an agent.</summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="limit">The most events to return.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The events, the newest first.</returns>
    public Task<IReadOnlyList<StoredAgentEvent>> RecentAsync(string agentId, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        long count = Math.Clamp(limit, 0, _options.MaxEventsPerAgent);
        return _database.QueryAsync<IReadOnlyList<StoredAgentEvent>>(
            connection =>
            {
                var events = new List<StoredAgentEvent>();
                using SqliteCommand command = HubSql.Command(
                    connection,
                    null,
                    "SELECT id, at, received_at, type, data FROM agent_events WHERE agent_id = @agent ORDER BY id DESC LIMIT @limit;",
                    ("@agent", agentId),
                    ("@limit", count));
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    events.Add(new StoredAgentEvent(
                        reader.GetInt64(0),
                        agentId,
                        HubSql.FromStored(reader.GetInt64(1)),
                        HubSql.FromStored(reader.GetInt64(2)),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : ParseData(reader.GetString(4))));
                }

                return events;
            },
            cancellationToken);
    }

    /// <summary>Counts the stored events of an agent.</summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The number of events.</returns>
    public Task<long> CountAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        return _database.QueryAsync(
            connection => HubSql.Scalar(connection, null, "SELECT COUNT(*) FROM agent_events WHERE agent_id = @agent;", ("@agent", agentId)) is long number ? number : 0L,
            cancellationToken);
    }

    private JsonElement? LimitData(JsonElement? data)
    {
        if (data is not { } element || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        return Encoding.UTF8.GetByteCount(element.GetRawText()) > _options.MaxEventDataBytes ? null : element;
    }

    private static JsonElement? ParseData(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
